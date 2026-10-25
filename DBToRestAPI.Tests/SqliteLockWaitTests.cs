using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DBToRestAPI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// A SQLite query waits for a lock inside SQLite, in short steps, for as long as its command timeout
/// lets it run. Microsoft.Data.Sqlite leaves SQLite's busy timeout at 0 and sleeps 150 ms before
/// each retry, so with many writers each collision used to cost a request 150 ms.
/// </summary>
public class SqliteLockWaitTests(SqliteLockWaitTests.Engine engine) : IClassFixture<SqliteLockWaitTests.Engine>
{
    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:wait_default:route"] = "wait/default",
        ["queries:wait_default:verb"] = "GET",
        ["queries:wait_default:query"] = "PRAGMA busy_timeout;",

        ["queries:wait_route:route"] = "wait/route",
        ["queries:wait_route:verb"] = "GET",
        ["queries:wait_route:db_command_timeout"] = "3",
        ["queries:wait_route:query"] = "PRAGMA busy_timeout;",

        ["queries:wait_unlimited:route"] = "wait/unlimited",
        ["queries:wait_unlimited:verb"] = "GET",
        ["queries:wait_unlimited:db_command_timeout"] = "0",
        ["queries:wait_unlimited:query"] = "PRAGMA busy_timeout;",
    });

    [Theory]
    [InlineData("wait/default", 30000)]          // the driver's default command timeout
    [InlineData("wait/route", 3000)]             // the route's db_command_timeout
    [InlineData("wait/unlimited", int.MaxValue)] // 0: no limit
    public async Task AQuery_WaitsForALock_AsLongAsItsCommandTimeout(string path, int milliseconds)
    {
        using var response = await engine.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(milliseconds, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task AWriteThatFindsTheDatabaseLocked_GoesOnSoonAfterTheLockIsReleased()
    {
        // The time from the release of the lock to the end of the waiting write, seven times. Waiting
        // in SQLite, the write notices within a few milliseconds. The driver's own retries come every
        // 150 ms, and a SQLite built without usleep would sleep a second at a time.
        var folder = Path.Combine(Path.GetTempPath(), "dbtorest-lockwait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = $"Data Source={Path.Combine(folder, "lock.db")};Pooling=False";
        try
        {
            using (var setup = new SqliteConnection(connectionString))
            {
                setup.Open();
                Execute(setup, "CREATE TABLE t (x INTEGER);");
            }

            var lags = new List<TimeSpan>();
            for (var i = 0; i < 7; i++)
                lags.Add(await LagAfterReleaseAsync(connectionString));

            lags.Sort();
            Assert.True(lags[3] < TimeSpan.FromMilliseconds(60), "lags: " + string.Join(", ", lags.Select(l => $"{l.TotalMilliseconds:0} ms")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<TimeSpan> LagAfterReleaseAsync(string connectionString)
    {
        // Both are the engine's connections, as two requests' would be. The holder's exclusive lock
        // keeps the waiter from reading too, so the waiter holds nothing its COMMIT would wait for.
        using var holder = new TrackedDbConnection(new SqliteConnection(connectionString), NullLogger.Instance, "lock_hold");
        holder.Open();
        holder.WaitForLocksUpTo(10);
        Execute(holder, "BEGIN EXCLUSIVE; INSERT INTO t VALUES (0);");

        using var waiter = new TrackedDbConnection(new SqliteConnection(connectionString), NullLogger.Instance, "lock_wait");
        waiter.Open();
        waiter.WaitForLocksUpTo(10);

        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() =>
        {
            using var command = waiter.CreateCommand();
            command.CommandText = "INSERT INTO t VALUES (1);";
            writing.SetResult();
            command.ExecuteNonQuery();
            return Stopwatch.GetTimestamp();
        });

        await writing.Task;
        await Task.Delay(30);
        Assert.False(write.IsCompleted, "the write didn't wait for the lock");
        var released = Stopwatch.GetTimestamp();
        Execute(holder, "COMMIT;");
        return Stopwatch.GetElapsedTime(released, await write);
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
