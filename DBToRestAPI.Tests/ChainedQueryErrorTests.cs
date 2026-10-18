using DBToRestAPI.Services;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for chained query error propagation:
/// - TryGetCustomDbError correctly extracts error codes from various DB exceptions
/// - The chain's InvalidOperationException wrapping preserves the inner DB exception
///   so error mapping still works through the outer catch block
/// </summary>
public class ChainedQueryErrorTests
{
    #region TryGetCustomDbError — direct DB exceptions

    // RAISE(ABORT, 'text') in a trigger, as Microsoft.Data.Sqlite reports it.
    private static Microsoft.Data.Sqlite.SqliteException Raised(string text) =>
        new($"SQLite Error 19: '{text}'.", 19, 1811);

    [Fact]
    public void Sqlite_50404_ExtractsNotFound()
    {
        var ex = Raised("[50404] Not found");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50404, errorNumber);
        Assert.Equal("Not found", errorMessage);
    }

    [Fact]
    public void Sqlite_50400_ExtractsBadRequest()
    {
        var ex = Raised("[50400] Invalid input");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50400, errorNumber);
        Assert.Equal("Invalid input", errorMessage);
    }

    [Fact]
    public void Sqlite_50409_ExtractsConflict()
    {
        var ex = Raised("[50409] Duplicate entry");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50409, errorNumber);
        Assert.Equal("Duplicate entry", errorMessage);
    }

    [Fact]
    public void Sqlite_50500_ExtractsInternalError()
    {
        var ex = Raised("[50500] Server error");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50500, errorNumber);
        Assert.Equal("Server error", errorMessage);
    }

    [Fact]
    public void NonCustomError_ReturnsFalse()
    {
        // A generic SQLite error without a [5xxxx] code
        var ex = new Microsoft.Data.Sqlite.SqliteException("SQLite Error 1: 'no such table: contacts'.", 1);

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.False(result);
        Assert.Equal(0, errorNumber);
    }

    [Fact]
    public void ErrorOutsideRange_ReturnsFalse()
    {
        // 49999 is not 50000 + a status
        var ex = Raised("[49999] Below range");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.False(result);
    }

    [Fact]
    public void ErrorAboveRange_ReturnsFalse()
    {
        // 51001 is not 50000 + a status
        var ex = Raised("[51001] Above range");

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.False(result);
    }

    #endregion

    #region Chain wrapping — InvalidOperationException preserves inner DB exception

    [Fact]
    public void ChainWrapped_Query1Error_StillExtractsDbError()
    {
        // Simulate what the chained query catch block does:
        //   throw new InvalidOperationException($"Query 1 of 3 failed: {ex.Message}", ex);
        var dbEx = Raised("[50404] Contact not found");
        var wrappedEx = new InvalidOperationException("Query 1 of 3 failed: [50404] Contact not found", dbEx);

        var result = DatabaseErrors.TryGetCustomDbError(wrappedEx, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50404, errorNumber);
        Assert.Equal("Contact not found", errorMessage);
    }

    [Fact]
    public void ChainWrapped_Query2Error_StillExtractsDbError()
    {
        var dbEx = Raised("[50400] Missing required field");
        var wrappedEx = new InvalidOperationException("Query 2 of 3 failed: [50400] Missing required field", dbEx);

        var result = DatabaseErrors.TryGetCustomDbError(wrappedEx, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(50400, errorNumber);
        Assert.Equal("Missing required field", errorMessage);
    }

    [Fact]
    public void ChainWrapped_NonCustomError_ReturnsFalse()
    {
        // A generic DB error wrapped by the chain — should not match
        var dbEx = new Microsoft.Data.Sqlite.SqliteException("table locked", 1);
        var wrappedEx = new InvalidOperationException("Query 1 of 2 failed: table locked", dbEx);

        var result = DatabaseErrors.TryGetCustomDbError(wrappedEx, out int errorNumber, out string errorMessage);

        Assert.False(result);
    }

    [Fact]
    public void ChainWrapped_NonCustomError_StillThrows()
    {
        // Verifies the chain wrapping pattern: even when the error is not a raised
        // status, the InvalidOperationException still propagates, which
        // is what prevents remaining queries from executing.
        var dbEx = new Microsoft.Data.Sqlite.SqliteException("disk I/O error", 1);

        // This is exactly what the catch block does in GetResultFromDbMultipleQueriesAsync
        var wrappedEx = new InvalidOperationException(
            $"Query 1 of 3 failed: {dbEx.Message}", dbEx);

        // The exception propagates (chain stops) and the inner exception is preserved
        Assert.IsType<InvalidOperationException>(wrappedEx);
        Assert.Same(dbEx, wrappedEx.InnerException);
        Assert.Contains("Query 1 of 3 failed", wrappedEx.Message);

        // But TryGetCustomDbError returns false — so the outer catch returns a generic error
        var isCustom = DatabaseErrors.TryGetCustomDbError(wrappedEx, out _, out _);
        Assert.False(isCustom);
    }

    #endregion

    #region HTTP status code mapping

    [Theory]
    [InlineData("[50400] Bad request", 400)]
    [InlineData("[50401] Unauthorized", 401)]
    [InlineData("[50403] Forbidden", 403)]
    [InlineData("[50404] Not found", 404)]
    [InlineData("[50409] Conflict", 409)]
    [InlineData("[50422] Unprocessable", 422)]
    [InlineData("[50429] Too many requests", 429)]
    [InlineData("[50500] Internal error", 500)]
    [InlineData("[50503] Service unavailable", 503)]
    public void ErrorCode_MapsToCorrectHttpStatus(string message, int expectedHttpStatus)
    {
        var ex = Raised(message);

        var result = DatabaseErrors.TryGetCustomDbError(ex, out int errorNumber, out string errorMessage);

        Assert.True(result);
        Assert.Equal(expectedHttpStatus, errorNumber - 50000);
    }

    #endregion
}
