using System.Data.Common;
using System.Text.Json;
using Com.H.Data.Common;
using DBToRestAPI.Controllers;
using DBToRestAPI.Middlewares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DBToRestAPI.Tests;

/// <summary>
/// Errors a database raises after it has returned rows: SQL Server's THROW after a SELECT, or a
/// later statement in the batch that fails.
///
/// Com.H.Data.Common 10.1.0.9 closed the reader once the first result set's rows ran out, and
/// closing discards such an error, so the request succeeded and its uploads were kept. 10.1.0.10
/// moves through the rest of the batch once every row has been read, and throws the error. The
/// engine maps it wherever it surfaces: in the controller, or in Step6 when MVC was already
/// writing a streamed result. File and count paths take the first row by reading two (1.7.8):
/// a result of one row is read to its end, and a result of many isn't read past the second.
/// </summary>
public class LateDbErrorTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public LateDbErrorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var command = _connection.CreateCommand();
        // SQLite raises a custom error only from a trigger; the engine maps its [50404] message.
        command.CommandText = """
            CREATE TABLE raise_after (x INTEGER);
            CREATE TRIGGER raise_after_trg BEFORE INSERT ON raise_after
            BEGIN
              SELECT RAISE(ABORT, '[50404] Category not found');
            END;
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("SELECT 1 AS a; INSERT INTO raise_after VALUES (1);")]
    [InlineData("SELECT 1 AS a WHERE 1 = 0; INSERT INTO raise_after VALUES (1);")]
    public async Task FirstRow_OneRowOrNone_ThrowsAnErrorRaisedAfterIt(string sql)
    {
        await using var result = await _connection.ExecuteQueryAsync(sql);

        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => ApiController.FirstRowAsync(result, CancellationToken.None));

        Assert.True(ApiController.TryGetCustomDbError(ex, out var number, out _));
        Assert.Equal(50404, number);
    }

    [Fact]
    public async Task FirstRow_ManyRows_StopsWithoutReadingTheRest()
    {
        // A query that should return one row returns a million. Reading them all would reach the
        // error raised after them; taking the first row by reading two never gets there.
        await using var result = await _connection.ExecuteQueryAsync(
            "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 1000000) "
            + "SELECT i AS a FROM r; INSERT INTO raise_after VALUES (1);");

        var first = await ApiController.FirstRowAsync(result, CancellationToken.None);

        Assert.Equal(1L, (long)first!.a);
    }

    [Fact]
    public async Task FirstRow_NoRows_ReturnsNull()
    {
        await using var result = await _connection.ExecuteQueryAsync("SELECT 1 AS a WHERE 1 = 0;");

        Assert.Null(await ApiController.FirstRowAsync(result, CancellationToken.None));
        Assert.Null(await ApiController.FirstRowAsync(null, CancellationToken.None));
    }

    [Fact]
    public void LateError_RaisedStatus_GetsTheControllersBody()
    {
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            new SqliteException("[50404] Category not found", 19), debugMode: false, "generic");

        Assert.Equal(404, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Category not found", body.GetProperty("message").GetString());
        Assert.Equal(404, body.GetProperty("error_number").GetInt32());
    }

    [Fact]
    public void LateError_OtherDatabaseError_IsTheGeneric400()
    {
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            new SqliteException("no such table: missing", 1), debugMode: false, "generic message");

        Assert.Equal(400, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("generic message", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("stack_trace", out _));
    }

    [Fact]
    public void LateError_OtherDatabaseError_InDebugMode_HasTheDetails()
    {
        // A streamed result's error reaches Step6 as the driver's own exception, not wrapped in
        // the controller's "Query n of m failed" exception.
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            new SqliteException("no such table: missing", 1), debugMode: true, "generic");

        Assert.Equal(500, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Contains("no such table", body.GetProperty("message").GetString());
        Assert.True(body.TryGetProperty("stack_trace", out _));
    }

    [Fact]
    public void LateError_WrappedDatabaseError_IsStillADatabaseError()
    {
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            new InvalidOperationException("Query 1 of 1 failed", new SqliteException("no such table: missing", 1)),
            debugMode: false, "generic");

        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public void LateError_NotADatabaseError_IsStillThe500()
    {
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            new InvalidOperationException("serializer bug"), debugMode: false, "generic");

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("An unexpected error occurred processing your request",
            JsonSerializer.SerializeToElement(result.Value).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Library_UnnamedSingleColumn_IsABareValue()
    {
        // The library side of the chain guard: with 10.1.0.10 an unnamed column comes back as the
        // bare value, not an object. PipelineTests checks that the chain keeps such a value out of
        // the next query's {{column}} sources.
        await using var result = await _connection.ExecuteQueryAsync("SELECT '{\"id\": 99}' AS \"\";");

        var rows = new List<object?>();
        await foreach (var row in result) rows.Add((object?)row);

        Assert.Single(rows);
        Assert.IsType<string>(rows[0]);
        Assert.False(rows[0] is IDictionary<string, object>);
    }
}
