// Stand-ins for the exceptions of drivers whose own can't be built in a test (no public
// constructor) or need a database to throw. The engine recognises them by type name and reads
// them by property name, so each has the driver's full name and the properties the engine reads.
// The test project also references the real drivers, so these names conflict with them on
// purpose: within this file the stand-ins win (CS0436).
#pragma warning disable CS0436

using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace DBToRestAPI.Tests
{
    internal static class FakeDrivers
    {
        public static Exception Oracle(int number, string message) =>
            new global::Oracle.ManagedDataAccess.Client.OracleException(number, message);

        public static Exception MySql(int number, string message) =>
            new global::MySqlConnector.MySqlException(number, message);

        public static Exception Db2(string sqlState, int nativeError, string message) =>
            new global::IBM.Data.Db2.DB2Exception($"ERROR [{sqlState}] {message}",
                new global::IBM.Data.Db2.DB2Error(message, sqlState, nativeError));

        public static Exception Odbc(string sqlState, int nativeError, string message, string source = "msodbcsql17.dll") =>
            new global::System.Data.Odbc.OdbcException($"ERROR [{sqlState}] {message}",
                new global::System.Data.Odbc.OdbcError(message, sqlState, nativeError, source));

        public static Exception OleDb(int errorCode, string sqlState, int nativeError, string message) =>
            new global::System.Data.OleDb.OleDbException(message, errorCode,
                new global::System.Data.OleDb.OleDbError(message, sqlState, nativeError, "Microsoft OLE DB Driver 19 for SQL Server"));

        /// <summary>
        /// A real SqlException, built with the driver's internal constructors (it has no public one).
        /// </summary>
        public static SqlException SqlServer(params (int Number, string Message)[] errors)
        {
            var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
            var errorConstructor = typeof(SqlError).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)],
                null)!;
            var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var (number, message) in errors)
                add.Invoke(collection, [errorConstructor.Invoke([number, (byte)1, (byte)16, "server", message, "", 1, null])]);
            var create = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, null,
                [typeof(SqlErrorCollection), typeof(string)], null)!;
            return (SqlException)create.Invoke(null, [collection, "16.0"])!;
        }
    }
}

namespace Oracle.ManagedDataAccess.Client
{
    public sealed class OracleException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }
}

namespace MySqlConnector
{
    public sealed class MySqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }
}

namespace IBM.Data.Db2
{
    public sealed class DB2Error(string message, string sqlState, int nativeError)
    {
        public string Message { get; } = message;
        public string SQLState { get; } = sqlState;
        public int NativeError { get; } = nativeError;
    }

    public sealed class DB2Exception(string message, params DB2Error[] errors) : DbException(message)
    {
        public IReadOnlyList<DB2Error> Errors { get; } = errors;
    }
}

namespace System.Data.Odbc
{
    public sealed class OdbcError(string message, string sqlState, int nativeError, string source)
    {
        public string Message { get; } = message;
        public string SQLState { get; } = sqlState;
        public int NativeError { get; } = nativeError;
        public string Source { get; } = source;
    }

    public sealed class OdbcException(string message, params OdbcError[] errors) : DbException(message)
    {
        public IReadOnlyList<OdbcError> Errors { get; } = errors;
    }
}

namespace System.Data.OleDb
{
    public sealed class OleDbError(string message, string sqlState, int nativeError, string source)
    {
        public string Message { get; } = message;
        public string SQLState { get; } = sqlState;
        public int NativeError { get; } = nativeError;
        public string Source { get; } = source;
    }

    public sealed class OleDbException(string message, int errorCode, params OleDbError[] errors) : DbException(message, errorCode)
    {
        public IReadOnlyList<OleDbError> Errors { get; } = errors;
    }
}
