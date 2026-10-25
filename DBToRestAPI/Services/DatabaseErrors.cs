using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace DBToRestAPI.Services;

/// <summary>
/// What a database error means for the response: a status the query raised on purpose, a
/// timeout, a database the engine couldn't reach, or any other failure.
/// </summary>
/// <remarks>
/// Most providers are loaded only when a connection uses them (DbConnectionFactory), so their
/// exceptions are recognised by type name and read by reflection: naming their types here would
/// load every provider's assembly. SQL Server's and SQLite's are always loaded.
/// </remarks>
internal static partial class DatabaseErrors
{
    // How far down an exception's InnerException chain to look: a query chain wraps the driver's
    // exception once ("Query n of m failed"), and an unreachable database once more.
    private const int MaxDepth = 8;

    /// <summary>The SQLite function a query calls to raise a status: <c>raise_error(50404, 'Not found')</c>.</summary>
    internal const string SqliteRaiseFunction = "raise_error";

    /// <summary>
    /// The HttpContext item that holds the final query's connection while its result is streamed,
    /// so that Step6 can tell a connection the database closed while the rest was read.
    /// </summary>
    internal const string StreamedConnectionItem = "streamed_db_connection";

    // SQLITE_CONSTRAINT_TRIGGER, from RAISE() in a trigger, and SQLITE_CONSTRAINT_FUNCTION, which
    // raise_error() sets. A SQLite error that repeats a caller's value has neither.
    private const int SqliteConstraintTrigger = 1811;
    private const int SqliteConstraintFunction = 1043;

    // OleDb's DB_E_ABORTLIMIT: the command timeout expired.
    private const int OleDbAbortLimit = unchecked((int)0x80040E31);

    /// <summary>
    /// Finds a status the query raised on purpose: an error numbered 50000 + status, for a status
    /// from 200 to 599. Any other number, RAISERROR's 50000 included, is not a status.
    /// </summary>
    /// <param name="exception">The exception, searched with its inner exceptions.</param>
    /// <param name="errorNumber">The error's number, 50000 + status.</param>
    /// <param name="errorMessage">The error's own text, without the driver's prefixes.</param>
    internal static bool TryGetCustomDbError(Exception? exception, out int errorNumber, out string errorMessage)
    {
        var depth = 0;
        for (var ex = exception; ex != null && depth < MaxDepth; ex = ex.InnerException, depth++)
        {
            if (TryReadRaisedError(ex, out errorNumber, out errorMessage))
                return true;
        }
        errorNumber = 0;
        errorMessage = string.Empty;
        return false;
    }

    /// <summary>
    /// The status of the generic error response, for an exception that isn't a raised status:
    /// 503 for a database the engine couldn't reach, 504 for a database timeout, else 400.
    /// </summary>
    internal static int GenericErrorStatus(Exception exception)
    {
        if (Find<DatabaseUnavailableException>(exception) != null)
            return StatusCodes.Status503ServiceUnavailable;
        if (IsDatabaseError(exception) && IsTimeout(exception))
            return StatusCodes.Status504GatewayTimeout;
        return StatusCodes.Status400BadRequest;
    }

    /// <summary>True when the exception, or one it wraps, comes from a database.</summary>
    internal static bool IsDatabaseError(Exception exception) =>
        Find<DbException>(exception) != null || Find<DatabaseUnavailableException>(exception) != null;

    /// <summary>
    /// True when the database closed an open connection during the query (SQL Server after a fatal
    /// error, PostgreSQL's terminated session): the database is unavailable, not the query wrong. A
    /// timeout stays a timeout, whatever it did to the connection.
    /// </summary>
    internal static bool LostTheConnection(Exception exception, DbConnection? connection) =>
        connection is TrackedDbConnection { WasOpened: true, State: ConnectionState.Closed or ConnectionState.Broken }
        && !IsTimeout(exception);

    /// <summary>
    /// True when the database didn't answer in time: the command timeout expired, or the database
    /// gave up waiting for a lock or stopped the statement for its own timeout.
    /// </summary>
    internal static bool IsTimeout(Exception? exception)
    {
        var depth = 0;
        for (var ex = exception; ex != null && depth < MaxDepth; ex = ex.InnerException, depth++)
        {
            switch (ex)
            {
                // Npgsql's command timeout, under its NpgsqlException.
                case TimeoutException:
                    return true;
                // -2: the command timeout. 1222: SET LOCK_TIMEOUT expired.
                case SqlException sql when sql.Errors.Cast<SqlError>().Any(e => e.Number is -2 or 1222):
                    return true;
                // SQLITE_BUSY or SQLITE_LOCKED: SQLite waits for the lock, and Microsoft.Data.Sqlite
                // retries, until the command timeout expires, while another connection holds it.
                case SqliteException { SqliteErrorCode: 5 or 6 }:
                    return true;
                // 57014 query_canceled: PostgreSQL's statement_timeout, DB2's interrupt. 55P03
                // lock_not_available: PostgreSQL's lock_timeout, and a NOWAIT lock. 25P04: PostgreSQL's
                // transaction_timeout, which also closes the connection.
                case DbException { SqlState: "57014" or "55P03" or "25P04" }:
                    return true;
            }
            switch (ex.GetType().FullName)
            {
                // -1: MySqlConnector's command timeout. 1205: innodb_lock_wait_timeout. 3024:
                // max_execution_time. 3572: a NOWAIT lock. 1969: MariaDB's max_statement_time.
                case "MySqlConnector.MySqlException" when Property(ex, "Number") is -1 or 1205 or 3024 or 3572 or 1969:
                // ORA-01013, which the command timeout raises. ORA-00054: a NOWAIT lock. ORA-30006: a
                // WAIT n lock that timed out.
                case "Oracle.ManagedDataAccess.Client.OracleException" when Property(ex, "Number") is 1013 or 54 or 30006:
                // SQL0952N: the command timeout interrupted the statement. SQL0911N and SQL0913N with a
                // timeout's reason code: 68, a lock wait (LUW); 00C9008E, a lock wait (z/OS); 80, a
                // statement timeout (SQL0913N); 3059, an index rebuild (SQL0911N). Reason code 2 and
                // 00C90088 are deadlocks.
                case "IBM.Data.Db2.DB2Exception" when Errors(ex).Any(e => Property(e, "SQLState") is "57014"
                    || Property(e, "NativeError") is -952
                    || (Property(e, "NativeError") is -911 && HasReasonCode(e, "68", "00C9008E", "3059"))
                    || (Property(e, "NativeError") is -913 && HasReasonCode(e, "68", "00C9008E", "80"))):
                // HYT00: query timeout expired. HYT01: connection timeout expired. 57014 and 55P03 as
                // above, from the database behind the driver.
                case "System.Data.Odbc.OdbcException" when Errors(ex).Any(IsTimeoutThroughDriver):
                case "System.Data.OleDb.OleDbException" when ((DbException)ex).ErrorCode == OleDbAbortLimit
                    || Errors(ex).Any(IsTimeoutThroughDriver):
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Adds <c>raise_error(number, message)</c> to a SQLite connection. SQLite accepts RAISE() only
    /// in a trigger; this function raises a status from any statement.
    /// </summary>
    internal static void AddSqliteRaiseError(SqliteConnection connection) =>
        connection.CreateFunction<long?, string?, object?>(SqliteRaiseFunction, (number, message) =>
            throw (IsStatusNumber(number)
                ? new SqliteException($"[{number}] {message}", SqliteConstraintFunction)
                : new SqliteException(
                    $"{SqliteRaiseFunction}() takes 50000 + an HTTP status from 200 to 599, such as 50404; got {number?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}",
                    1)));

    // A DB2 message's reason code, which it quotes: Reason code "68".
    private static bool HasReasonCode(object error, params string[] codes) =>
        Property(error, "Message") is string message
        && codes.Any(code => message.Contains($"\"{code}\"", StringComparison.Ordinal));

    // One error of an ODBC or OLE DB exception that reports a timeout.
    private static bool IsTimeoutThroughDriver(object error) =>
        Property(error, "SQLState") is "HYT00" or "HYT01" or "57014" or "55P03" || IsSqlServerLockTimeout(error);

    // SQL Server's lock timeout (1222) through ODBC, whose SQLSTATE is a plain 42000. The number is
    // SQL Server's only when a SQL Server driver reports it: MySQL's 1222 is another error.
    private static bool IsSqlServerLockTimeout(object error) =>
        Property(error, "NativeError") is 1222
        && Property(error, "Source") is string source
        && (source.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)
            || source.Contains("msodbcsql", StringComparison.OrdinalIgnoreCase)
            || source.Contains("sqlncli", StringComparison.OrdinalIgnoreCase)
            || source.Contains("sqlsrv32", StringComparison.OrdinalIgnoreCase));

    // One driver's exception, if it reports a status the query raised.
    private static bool TryReadRaisedError(Exception ex, out int number, out string message)
    {
        number = 0;
        message = string.Empty;
        switch (ex)
        {
            // THROW 50404, 'Not found', 1;
            case SqlException sql:
                foreach (SqlError error in sql.Errors)
                {
                    if (IsStatusNumber(error.Number))
                    {
                        (number, message) = (error.Number, error.Message);
                        return true;
                    }
                }
                return false;
            // RAISE(ABORT, '[50404] Not found') in a trigger, or raise_error(50404, 'Not found').
            case SqliteException sqlite:
                return sqlite.SqliteExtendedErrorCode is SqliteConstraintTrigger or SqliteConstraintFunction
                    && TryReadBracketed(SqliteText(sqlite.Message), out number, out message);
        }

        switch (ex.GetType().FullName)
        {
            case "Npgsql.PostgresException":
            {
                var sqlState = ((DbException)ex).SqlState;
                var text = Property(ex, "MessageText") as string ?? ex.Message;
                // RAISE EXCEPTION 'Not found' USING ERRCODE = '50404';
                if (TryParseNumber(sqlState, out number))
                {
                    message = text;
                    return true;
                }
                // RAISE EXCEPTION '[50404] Not found'; (SQLSTATE P0001, raise_exception). No other
                // SQLSTATE is read for the brackets: a cast error repeats the value it couldn't cast.
                return sqlState == "P0001" && TryReadBracketed(text, out number, out message);
            }
            // SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';
            case "MySqlConnector.MySqlException":
                if (Property(ex, "Number") is int mysql && IsStatusNumber(mysql))
                {
                    (number, message) = (mysql, ex.Message);
                    return true;
                }
                return false;
            // RAISE_APPLICATION_ERROR(-20404, 'Not found'); The driver reports 20404, and the message
            // "ORA-20404: Not found" followed by the error stack's lines.
            case "Oracle.ManagedDataAccess.Client.OracleException":
                if (Property(ex, "Number") is int oracle and ((>= 20000 and <= 20999) or (>= -20999 and <= -20000)))
                {
                    var code = Math.Abs(oracle);
                    if (!IsStatusNumber(30000 + code))
                        return false;
                    (number, message) = (30000 + code, OracleText(ex.Message, code));
                    return true;
                }
                return false;
            case "IBM.Data.Db2.DB2Exception":
                foreach (var error in Errors(ex))
                {
                    var text = Db2Text(Property(error, "Message") as string ?? ex.Message);
                    // SIGNAL SQLSTATE '50404' SET MESSAGE_TEXT = 'Not found';
                    if (TryParseNumber(Property(error, "SQLState") as string, out number))
                    {
                        message = text;
                        return true;
                    }
                    // SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found'; (SQL0438N,
                    // an error the application raised).
                    if (Property(error, "NativeError") is -438 && TryReadBracketed(text, out number, out message))
                        return true;
                }
                return false;
            // Through ODBC or OLE DB: the database's own number (SQL Server's THROW, MySQL's
            // MYSQL_ERRNO), or a SQLSTATE that is the number (PostgreSQL's ERRCODE, DB2's SIGNAL).
            case "System.Data.Odbc.OdbcException":
            case "System.Data.OleDb.OleDbException":
                foreach (var error in Errors(ex))
                {
                    if (Property(error, "NativeError") is int native && IsStatusNumber(native))
                        number = native;
                    else if (!TryParseNumber(Property(error, "SQLState") as string, out number))
                        continue;
                    var text = Property(error, "Message") as string ?? ex.Message;
                    // ODBC writes [vendor][driver][data source] before the database's text; OLE DB
                    // writes the text alone.
                    message = ex.GetType().FullName == "System.Data.Odbc.OdbcException"
                        ? WithoutDriverTags(text)
                        : text;
                    return true;
                }
                return false;
        }
        return false;
    }

    // 50000 + a status from 200 to 599. A smaller one would send an invalid status line (RAISERROR
    // reports 50000), and one from 600 is not an HTTP status.
    private static bool IsStatusNumber(long? number) => number is >= 50200 and <= 50599;

    // A five-digit SQLSTATE that is a status number: "50404".
    private static bool TryParseNumber(string? sqlState, out int number)
    {
        number = 0;
        return sqlState is { Length: 5 }
            && sqlState.All(char.IsAsciiDigit)
            && int.TryParse(sqlState, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && IsStatusNumber(number);
    }

    // A [nnnnn] that starts the text, when it is a status number, and the text after it. Only at the
    // start: a raised message that carries a caller's value later on can't let the caller pick the
    // status.
    private static bool TryReadBracketed(string text, out int number, out string message)
    {
        number = 0;
        message = string.Empty;
        var match = BracketedNumber().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var found)
            || !IsStatusNumber(found))
            return false;
        number = found;
        message = text.Remove(match.Index, match.Length).Trim();
        return true;
    }

    // Microsoft.Data.Sqlite writes "SQLite Error 19: 'text'."
    private static string SqliteText(string message)
    {
        var match = SqliteMessage().Match(message);
        return match.Success ? match.Groups["text"].Value : message;
    }

    // "ORA-20404: Not found", followed by the error stack: the text on the line of that error.
    private static string OracleText(string message, int code)
    {
        foreach (Match match in OracleLine().Matches(message))
        {
            if (int.TryParse(match.Groups["code"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var found) && found == code)
                return match.Groups["text"].Value.Trim();
        }
        return message;
    }

    // SQL0438N's diagnostic text is quoted: ... with diagnostic text: "Not found".  SQLSTATE=50404
    private static string Db2Text(string message)
    {
        var match = QuotedText().Match(message);
        return match.Success ? match.Groups["text"].Value : WithoutDriverTags(message);
    }

    // "[Microsoft][ODBC Driver 17 for SQL Server][SQL Server]Not found" -> "Not found". At most three
    // tags, as ODBC writes [vendor][driver][data source], so a message that starts with a [label] of
    // its own keeps it through such a driver. A driver that writes fewer tags (FreeTDS writes two) loses
    // that label.
    private static string WithoutDriverTags(string message) => DriverTags().Replace(message, string.Empty).Trim();

    private static T? Find<T>(Exception? exception) where T : Exception
    {
        var depth = 0;
        for (var ex = exception; ex != null && depth < MaxDepth; ex = ex.InnerException, depth++)
        {
            if (ex is T found)
                return found;
        }
        return null;
    }

    // A driver's error collection (Errors), whatever its type.
    private static IEnumerable<object> Errors(Exception ex) =>
        Property(ex, "Errors") is IEnumerable errors ? errors.Cast<object?>().OfType<object>() : [];

    // A public property, read by name. Mapping an error must not throw from the catch that maps it,
    // so a property that can't be read counts as missing.
    private static object? Property(object target, string name)
    {
        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*\[(\d{5})\]")]
    private static partial Regex BracketedNumber();

    [GeneratedRegex(@"^SQLite Error -?\d+: '(?<text>.*)'\.$", RegexOptions.Singleline)]
    private static partial Regex SqliteMessage();

    [GeneratedRegex(@"ORA-(?<code>\d{5}):(?<text>[^\r\n]*)")]
    private static partial Regex OracleLine();

    [GeneratedRegex("\"(?<text>.*)\"", RegexOptions.Singleline)]
    private static partial Regex QuotedText();

    [GeneratedRegex(@"^(?:\s*\[[^\]]*\]){1,3}")]
    private static partial Regex DriverTags();
}

/// <summary>
/// The engine couldn't connect to a database, or the database closed the connection during the
/// query. The caller gets 503 with the generic message.
/// </summary>
internal sealed class DatabaseUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
