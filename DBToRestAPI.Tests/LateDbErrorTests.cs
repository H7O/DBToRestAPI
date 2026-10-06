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
/// engine then has to read to the end (single, file and count paths took one row and closed) and
/// map the error wherever it surfaces: in the controller, or in Step6 when MVC was already
/// writing a streamed result.
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

    private const string ThreeRowsThenRaise =
        "SELECT 1 AS a UNION ALL SELECT 2 UNION ALL SELECT 3; INSERT INTO raise_after VALUES (1);";

    [Fact]
    public async Task FirstRowReadingToEnd_ThrowsAnErrorRaisedAfterTheRows()
    {
        await using var result = await _connection.ExecuteQueryAsync(ThreeRowsThenRaise);

        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => ApiController.FirstRowReadingToEndAsync(result, CancellationToken.None));

        Assert.True(ApiController.TryGetCustomDbError(ex, out var number, out _));
        Assert.Equal(50404, number);
    }

    [Fact]
    public async Task FirstRow_TakenAndClosed_LosesTheError()
    {
        // What the single, file and count paths did: the error never reaches the caller.
        await using var result = await _connection.ExecuteQueryAsync(ThreeRowsThenRaise);

        var first = result.AsEnumerable().FirstOrDefault();

        Assert.NotNull(first);
    }

    [Fact]
    public async Task FirstRowReadingToEnd_ReturnsTheFirstRow()
    {
        await using var result = await _connection.ExecuteQueryAsync(
            "SELECT 1 AS a UNION ALL SELECT 2 UNION ALL SELECT 3;");

        var first = await ApiController.FirstRowReadingToEndAsync(result, CancellationToken.None);

        Assert.Equal(1L, (long)first!.a);
    }

    [Fact]
    public async Task FirstRowReadingToEnd_NoRows_ReturnsNull()
    {
        await using var result = await _connection.ExecuteQueryAsync("SELECT 1 AS a WHERE 1 = 0;");

        Assert.Null(await ApiController.FirstRowReadingToEndAsync(result, CancellationToken.None));
        Assert.Null(await ApiController.FirstRowReadingToEndAsync(null, CancellationToken.None));
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
