using System.Text.Json;
using DBToRestAPI.Controllers;
using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DBToRestAPI.Tests;

/// <summary>
/// How each database's errors are read (1.7.15): a status the query raised, with the database's own
/// text and none of the driver's prefixes; 504 for a timeout; 503 for a database the engine couldn't
/// reach; the generic 400 for any other error. The exception shapes are the ones each driver was seen
/// to throw (SQL Server through SqlClient, ODBC and OLE DB; PostgreSQL; SQLite), or the ones its
/// documentation gives (MySQL, Oracle, DB2).
/// </summary>
public class DatabaseErrorsTests
{
    private static (bool Mapped, int Number, string Message) Read(Exception ex) =>
        (DatabaseErrors.TryGetCustomDbError(ex, out var number, out var message), number, message);

    [Fact]
    public void SqlServer_Throw_IsTheStatus_WithItsMessage()
    {
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.SqlServer((50404, "Not found"))));
    }

    [Fact]
    public void SqlServer_AStatusAfterAnotherError_IsFound()
    {
        // SqlException.Number is the first error's: a batch that went on after an error, then threw.
        Assert.Equal((true, 50409, "Duplicate"),
            Read(FakeDrivers.SqlServer((547, "The INSERT statement conflicted"), (50409, "Duplicate"))));
    }

    [Theory]
    [InlineData(50000, false)] // RAISERROR('text', 16, 1): an invalid status line (0) before 1.7.15
    [InlineData(50099, false)]
    [InlineData(50199, false)] // 1xx is not a final status
    [InlineData(50200, true)]
    [InlineData(50404, true)]
    [InlineData(50599, true)]
    [InlineData(50600, false)] // 6xx is not an HTTP status
    [InlineData(50999, false)]
    public void OnlyAStatusFrom200To599_IsMapped(int number, bool mapped)
    {
        Assert.Equal(mapped, Read(FakeDrivers.SqlServer((number, "text"))).Mapped);
    }

    [Fact]
    public void Sqlite_RaiseInATrigger_HasNoDriverPrefix()
    {
        // Before 1.7.15 the caller got "SQLite Error 19: ' Not found'."
        Assert.Equal((true, 50404, "Not found"),
            Read(new SqliteException("SQLite Error 19: '[50404] Not found'.", 19, 1811)));
    }

    [Theory]
    [InlineData("SQLite Error 19: 'Name already taken: [50201] alice'.", 19, 1811)] // RAISE(ABORT, 'Name already taken: ' || NEW.name)
    [InlineData("SQLite Error 19: 'Not found [50404]'.", 19, 1811)]
    [InlineData("SQLite Error 19: 'Taken: [50201] x'.", 19, 1043)]
    public void Sqlite_ABracketThatDoesNotStartTheText_IsNotAStatus(string message, int code, int extended)
    {
        // A raised message that carries a caller's value: the caller sent "[50201] alice".
        Assert.False(Read(new SqliteException(message, code, extended)).Mapped);
    }

    [Theory]
    [InlineData(1, 1)]       // an error SQLite raised itself, its text starting with a caller's value
    [InlineData(19, 2067)]   // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(19, 275)]    // SQLITE_CONSTRAINT_CHECK
    public void Sqlite_AnErrorThatIsNotARaise_IsNotAStatus_EvenWhenItStartsWithABracket(int code, int extended)
    {
        Assert.False(Read(new SqliteException("SQLite Error 1: '[50404] x'.", code, extended)).Mapped);
    }

    [Fact]
    public void Sqlite_ABracketAfterSpaces_IsTheStatus()
    {
        Assert.Equal((true, 50404, "Not found"), Read(new SqliteException("SQLite Error 19: '  [50404] Not found'.", 19, 1811)));
    }

    [Fact]
    public void Sqlite_AnErrorThatRepeatsACallersValue_IsNotAStatus()
    {
        // json_extract with a path the caller sent: the text has the brackets, but SQLite raised it.
        Assert.False(Read(new SqliteException("SQLite Error 1: 'bad JSON path: '[50404] x''.", 1)).Mapped);
    }

    [Theory]
    [InlineData("SELECT raise_error(50404, 'Not found');", 50404, "Not found")]
    [InlineData("SELECT raise_error(50409, 'Can''t book: taken');", 50409, "Can't book: taken")]
    [InlineData("SELECT raise_error('50400', NULL);", 50400, "")]
    [InlineData("SELECT CASE WHEN 1 = 0 THEN 1 ELSE raise_error(50400, 'Bad category') END;", 50400, "Bad category")]
    public async Task Sqlite_RaiseError_RaisesTheStatusFromAnyStatement(string sql, int number, string message)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        DatabaseErrors.AddSqliteRaiseError(connection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());

        Assert.Equal((true, number, message), Read(ex));
    }

    [Theory]
    [InlineData("SELECT raise_error(404, 'Not found');", "got 404")]
    [InlineData("SELECT raise_error(NULL, 'Not found');", "got NULL")]
    [InlineData("SELECT raise_error(50000, 'Not found');", "got 50000")]
    public async Task Sqlite_RaiseError_WithANumberThatIsNotAStatus_IsAnError(string sql, string got)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        DatabaseErrors.AddSqliteRaiseError(connection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());

        Assert.False(Read(ex).Mapped);
        Assert.Contains("raise_error() takes 50000 + an HTTP status from 200 to 599", ex.Message);
        Assert.Contains(got, ex.Message);
    }

    [Fact]
    public void Postgres_Errcode_IsTheStatus_WithTheMessageText()
    {
        // RAISE EXCEPTION 'Not found' USING ERRCODE = '50404';
        Assert.Equal((true, 50404, "Not found"), Read(new PostgresException("Not found", "ERROR", "ERROR", "50404")));
    }

    [Fact]
    public void Postgres_Bracketed_HasNoSqlStatePrefix()
    {
        // RAISE EXCEPTION '[50404] Not found'; Before 1.7.15 the caller got "P0001:  Not found".
        Assert.Equal((true, 50404, "Not found"), Read(new PostgresException("[50404] Not found", "ERROR", "ERROR", "P0001")));
    }

    [Theory]
    [InlineData("Room [50201] blue is taken")]                                       // RAISE EXCEPTION 'Room % is taken', n
    [InlineData("Import failed: invalid input syntax for type integer: \"[50302]\"")] // RAISE EXCEPTION 'Import failed: %', SQLERRM
    public void Postgres_ARaiseWhoseTextCarriesACallersValue_IsNotAStatus(string text)
    {
        Assert.False(Read(new PostgresException(text, "ERROR", "ERROR", "P0001")).Mapped);
    }

    [Theory]
    [InlineData("22P02")] // a cast error
    [InlineData("P0002")] // no_data_found
    [InlineData("P0004")] // ASSERT
    [InlineData("23505")] // RAISE ... USING ERRCODE = 'unique_violation'
    public void Postgres_AnErrorThatIsNotAPlainRaise_IsNotAStatus_EvenWhenItStartsWithABracket(string sqlState)
    {
        Assert.False(Read(new PostgresException("[50404] x", "ERROR", "ERROR", sqlState)).Mapped);
    }

    [Fact]
    public void Postgres_ACastErrorThatRepeatsACallersValue_IsNotAStatus()
    {
        Assert.False(Read(new PostgresException(
            "invalid input syntax for type integer: \"[50404] x\"", "ERROR", "ERROR", "22P02")).Mapped);
    }

    [Fact]
    public void MySql_Signal_IsTheStatus()
    {
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.MySql(50404, "Not found")));
        Assert.False(Read(FakeDrivers.MySql(1062, "Duplicate entry")).Mapped);
    }

    [Theory]
    [InlineData(20404)]  // what ODP.NET reports
    [InlineData(-20404)] // what RAISE_APPLICATION_ERROR is given
    public void Oracle_RaiseApplicationError_IsTheStatus_WithTheTextOfItsLine(int number)
    {
        Assert.Equal((true, 50404, "Not found"),
            Read(FakeDrivers.Oracle(number, "ORA-20404: Not found\nORA-06512: at line 1")));
    }

    [Theory]
    [InlineData(20000)] // 50000: not a status
    [InlineData(20999)] // 50999
    [InlineData(1403)]  // no data found
    public void Oracle_AnotherNumber_IsNotAStatus(int number)
    {
        Assert.False(Read(FakeDrivers.Oracle(number, $"ORA-{number:00000}: text")).Mapped);
    }

    [Fact]
    public void Db2_SignalWithTheNumberAsSqlState_IsTheStatus_WithTheDiagnosticText()
    {
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.Db2("50404", -438,
            "[IBM][DB2/LINUXX8664] SQL0438N  Application raised error or warning with diagnostic text: \"Not found\".  SQLSTATE=50404")));
    }

    [Fact]
    public void Db2_SignalWithABracketedNumber_IsTheStatus_NotTheSqlState()
    {
        // Before 1.7.15 the first [nnnnn] was read: the "ERROR [75000]" that starts the message.
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.Db2("75000", -438,
            "[IBM][DB2/LINUXX8664] SQL0438N  Application raised error or warning with diagnostic text: \"[50404] Not found\".  SQLSTATE=75000")));
    }

    [Fact]
    public void Db2_ASignalWhoseTextCarriesACallersValue_IsNotAStatus()
    {
        Assert.False(Read(FakeDrivers.Db2("75000", -438,
            "[IBM][DB2/LINUXX8664] SQL0438N  Application raised error or warning with diagnostic text: \"Bad: [50201] x\".  SQLSTATE=75000")).Mapped);
    }

    [Fact]
    public void Db2_AnotherErrorThatRepeatsACallersValue_IsNotAStatus()
    {
        Assert.False(Read(FakeDrivers.Db2("22018", -420,
            "[IBM][DB2/LINUXX8664] SQL0420N  Invalid character found in a character string argument of the function \"[50404]\".  SQLSTATE=22018")).Mapped);
    }

    [Fact]
    public void Odbc_SqlServerThrow_IsTheStatus_WithoutTheDriverTags()
    {
        Assert.Equal((true, 50404, "Not found"),
            Read(FakeDrivers.Odbc("42000", 50404, "[Microsoft][ODBC Driver 17 for SQL Server][SQL Server]Not found")));
    }

    [Fact]
    public void Odbc_AMessageThatStartsWithItsOwnLabel_KeepsIt()
    {
        // THROW 50400, '[email] is invalid', 1; only ODBC's three tags are removed.
        Assert.Equal((true, 50400, "[email] is invalid"),
            Read(FakeDrivers.Odbc("42000", 50400, "[Microsoft][ODBC Driver 17 for SQL Server][SQL Server][email] is invalid")));
    }

    [Fact]
    public void OleDb_AMessageThatStartsWithALabel_IsKeptWhole()
    {
        // OLE DB writes the database's text alone.
        Assert.Equal((true, 50400, "[email] is invalid"), Read(FakeDrivers.OleDb(-2147217900, "42000", 50400, "[email] is invalid")));
    }

    [Fact]
    public void Odbc_ASqlStateThatIsTheNumber_IsTheStatus()
    {
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.Odbc("50404", 0, "[DB2/LINUXX8664] Not found")));
        Assert.False(Read(FakeDrivers.Odbc("42000", 50000, "[Microsoft][SQL Server]Category is invalid")).Mapped);
    }

    [Fact]
    public void OleDb_SqlServerThrow_IsTheStatus()
    {
        Assert.Equal((true, 50404, "Not found"), Read(FakeDrivers.OleDb(-2147217900, "42000", 50404, "Not found")));
    }

    [Fact]
    public void AStatusWrappedTwice_IsFound()
    {
        var raised = new SqliteException("SQLite Error 19: '[50404] Not found'.", 19, 1811);

        Assert.True(Read(new InvalidOperationException("Query 1 of 2 failed", new InvalidOperationException("inner", raised))).Mapped);
    }

    public static TheoryData<Exception> Timeouts => new()
    {
        FakeDrivers.SqlServer((-2, "Execution Timeout Expired.")),
        FakeDrivers.SqlServer((1222, "Lock request time out period exceeded.")),
        new SqliteException("SQLite Error 5: 'database is locked'.", 5),
        new SqliteException("SQLite Error 6: 'database table is locked'.", 6),
        new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014"),
        new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", "55P03"),
        new PostgresException("terminating connection due to transaction timeout", "FATAL", "FATAL", "25P04"),
        new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")),
        FakeDrivers.MySql(-1, "The Command Timeout expired before the operation completed."),
        FakeDrivers.MySql(1205, "Lock wait timeout exceeded"),
        FakeDrivers.MySql(3024, "Query execution was interrupted, maximum statement execution time exceeded"),
        FakeDrivers.MySql(3572, "Statement aborted because lock(s) could not be acquired immediately and NOWAIT is set."),
        FakeDrivers.Oracle(1013, "ORA-01013: user requested cancel of current operation"),
        FakeDrivers.Oracle(54, "ORA-00054: resource busy and acquire with NOWAIT specified or timeout expired"),
        FakeDrivers.Oracle(30006, "ORA-30006: resource busy; acquire with WAIT timeout expired"),
        FakeDrivers.MySql(1969, "Query execution was interrupted (max_statement_time exceeded)"),
        FakeDrivers.Db2("40001", -911, "SQL0911N  The current transaction has been rolled back because of a deadlock or timeout.  Reason code \"68\".  SQLSTATE=40001"),
        FakeDrivers.Db2("57033", -913, "SQL0913N  Unsuccessful execution caused by deadlock or timeout.  Reason code \"68\".  SQLSTATE=57033"),
        FakeDrivers.Db2("57033", -913, "SQL0913N  Unsuccessful execution caused by deadlock or timeout.  Reason code \"80\".  SQLSTATE=57033"),
        FakeDrivers.Db2("57033", -913, "SQL0913N  Unsuccessful execution caused by deadlock or timeout.  Reason code \"00C9008E\".  SQLSTATE=57033"),
        FakeDrivers.Db2("40001", -911, "SQL0911N  The current transaction has been rolled back because of a deadlock or timeout.  Reason code \"00C9008E\".  SQLSTATE=40001"),
        FakeDrivers.Db2("40001", -911, "SQL0911N  The current transaction has been rolled back because of a deadlock or timeout.  Reason code \"3059\".  SQLSTATE=40001"),
        FakeDrivers.Odbc("HYT01", 0, "[Microsoft][ODBC Driver 17 for SQL Server]Login timeout expired"),
        FakeDrivers.Odbc("55P03", 7, "ERROR: canceling statement due to lock timeout", "PSQLODBC35W.DLL"),
        FakeDrivers.Odbc("42000", 1222, "[Microsoft][SQL Server Native Client 11.0][SQL Server]Lock request time out period exceeded.", "sqlncli11.dll"),
        FakeDrivers.Odbc("42000", 1222, "[Microsoft][ODBC SQL Server Driver][SQL Server]Lock request time out period exceeded.", "SQLSRV32.DLL"),
        FakeDrivers.OleDb(unchecked((int)0x80040E31), "S1000", 0, "Query timeout expired"),
        FakeDrivers.Db2("57014", -952, "SQL0952N  Processing was cancelled due to an interrupt."),
        FakeDrivers.Odbc("HYT00", 0, "[Microsoft][ODBC Driver 17 for SQL Server]Query timeout expired"),
        FakeDrivers.Odbc("42000", 1222, "[Microsoft][ODBC Driver 17 for SQL Server][SQL Server]Lock request time out period exceeded."),
        FakeDrivers.Odbc("57014", 7, "ERROR: canceling statement due to statement timeout", "PSQLODBC35W.DLL"),
        FakeDrivers.OleDb(unchecked((int)0x80040E31), "HYT00", 0, "Query timeout expired"),
        new InvalidOperationException("Query 1 of 1 failed: Execution Timeout Expired.", FakeDrivers.SqlServer((-2, "Execution Timeout Expired."))),
    };

    [Theory]
    [MemberData(nameof(Timeouts))]
    public void ATimeout_Is504(Exception ex)
    {
        Assert.Equal(504, DatabaseErrors.GenericErrorStatus(ex));
    }

    [Theory]
    [InlineData(-911, "2")]        // a deadlock (LUW)
    [InlineData(-911, "00C90088")] // a deadlock (z/OS)
    [InlineData(-911, "80")]       // SQL0911N's 80 is a catalog member's state
    [InlineData(-913, "208")]      // a write conflict
    public void ADb2DeadlockOrConflict_IsNotATimeout(int sqlCode, string reason)
    {
        Assert.Equal(400, DatabaseErrors.GenericErrorStatus(FakeDrivers.Db2("40001", sqlCode,
            $"SQL0911N  The current transaction has been rolled back because of a deadlock or timeout.  Reason code \"{reason}\".  SQLSTATE=40001")));
    }

    [Fact]
    public void A1222ThroughAnotherDriver_IsNotATimeout()
    {
        // MySQL's 1222 is "The used SELECT statements have a different number of columns".
        Assert.Equal(400, DatabaseErrors.GenericErrorStatus(
            FakeDrivers.Odbc("21000", 1222, "[MySQL][ODBC 8.0(w) Driver][mysqld-8.0.30]The used SELECT statements have a different number of columns", "myodbc8w.dll")));
    }

    [Fact]
    public void ATimeoutThatIsNotADatabaseError_Is400()
    {
        // An http download that timed out, say: not this rule's to change.
        Assert.Equal(400, DatabaseErrors.GenericErrorStatus(new TaskCanceledException("timed out", new TimeoutException())));
    }

    [Fact]
    public void ADatabaseTheEngineCouldNotReach_Is503_EvenWhenConnectingTimedOut()
    {
        var unreachable = new InvalidOperationException("Query 1 of 1 failed: The database could not be reached",
            new DatabaseUnavailableException("The database could not be reached",
                new NpgsqlException("Failed to connect to 127.0.0.1:1", new TimeoutException("Timeout during connection attempt"))));

        Assert.Equal(503, DatabaseErrors.GenericErrorStatus(unreachable));
    }

    [Theory]
    [InlineData(8134)] // divide by zero
    [InlineData(547)]  // a constraint
    public void AnotherDatabaseError_Is400(int number)
    {
        Assert.Equal(400, DatabaseErrors.GenericErrorStatus(FakeDrivers.SqlServer((number, "text"))));
    }

    [Fact]
    public void AnErrorThatIsNotADatabaseError_Is400()
    {
        Assert.Equal(400, DatabaseErrors.GenericErrorStatus(new InvalidOperationException("Connection string 'x' not found")));
    }

    [Fact]
    public void LateError_ATimeout_IsTheGenericMessage_With504()
    {
        var result = Step6MandatoryFieldsCheck.LateErrorResponse(
            FakeDrivers.SqlServer((-2, "Execution Timeout Expired.")), debugMode: false, "generic message");

        Assert.Equal(504, result.StatusCode);
        Assert.Equal("generic message", JsonSerializer.SerializeToElement(result.Value).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null, null, 2)]
    [InlineData("", " ", 2)]
    [InlineData("0", null, 2)]
    [InlineData("1", null, 2)]
    [InlineData("2", null, 3)]
    [InlineData(" 1000 ", null, 1001)]
    [InlineData(null, "50", 51)]
    [InlineData("  ", "50", 51)]  // a blank route value counts as none
    [InlineData("0", "50", 2)]    // a route's 0 turns the global value off
    [InlineData("10", "50", 11)]
    [InlineData("2147483647", null, int.MaxValue)]
    [InlineData("99999999999", null, int.MaxValue)] // too large for an int: everything
    public void BufferRows_IsTheRowsReadFirst_LessOne(string? route, string? global, int chamberSize)
    {
        Assert.True(ApiController.TryGetChamberSize(route, global, out var size, out var invalid));
        Assert.Equal(chamberSize, size);
        Assert.Null(invalid);
    }

    [Theory]
    [InlineData("-1", null)]
    [InlineData("+3", null)]
    [InlineData("1.5", null)]
    [InlineData("1,000", null)]
    [InlineData("all", null)]
    [InlineData("１０", null)] // digits other than ASCII
    [InlineData(null, "ten")]
    public void BufferRows_ThatIsNotAWholeNumber_IsInvalid(string? route, string? global)
    {
        Assert.False(ApiController.TryGetChamberSize(route, global, out _, out var invalid));
        Assert.Equal(route ?? global, invalid);
    }
}
