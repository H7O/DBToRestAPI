// The load generator for benchmarks/run.ps1. Three commands:
//
//   setup  --sqlite <file> [--sqlserver <server>] [--rows N]
//          Creates the benchmark data: a SQLite file and, with --sqlserver, the DbToRestApiBench database.
//   fetch  --url <url> [--method GET|POST] [--body <json>]
//          Prints one response's status and body (run.ps1 compares the two servers' answers with it).
//   run    --server <label> --db <label> --scenario <label> --url <template> [--method GET|POST] [--body <template>]
//          --concurrency N --warmup S --duration S [--rows N] [--pid P] [--affinity <hex mask>] --out <file.jsonl>
//          Sends requests from N connections for S seconds after a warmup, and appends one JSON line of results.
//
// In a template, {id} is an id from 1 to --rows, {after} an id to start a page after, and {n} a number unique
// to the request.
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

var options = Options.Parse(args.Skip(1));
switch (args.FirstOrDefault())
{
    case "setup": await Setup.RunAsync(options); break;
    case "fetch": await Fetch.RunAsync(options); break;
    case "run": await Load.RunAsync(options); break;
    default:
        Console.Error.WriteLine("usage: LoadGenerator setup|fetch|run [--option value ...]");
        return 1;
}
return 0;

sealed class Options(Dictionary<string, string> values)
{
    public static Options Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal)) { name = arg[2..]; values[name] = "true"; }
            else if (name is not null) { values[name] = arg; name = null; }
        }
        return new Options(values);
    }

    public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required");
    public int Int(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
}

static class Setup
{
    static readonly string[] Cities = ["Amman", "Berlin", "Cairo", "Dubai", "Lisbon", "Nairobi", "Oslo", "Seoul", "Toronto", "Lima"];

    static (string Name, string Email, string City, DateTime CreatedAt) Contact(int id) =>
        ($"Contact {id}", $"contact{id}@example.com", Cities[id % Cities.Length], new DateTime(2026, 1, 1).AddMinutes(id));

    public static async Task RunAsync(Options options)
    {
        var rows = options.Int("rows", 10000);
        await SqliteAsync(options.Require("sqlite"), rows);
        if (options.Get("sqlserver") is { } server) await SqlServerAsync(server, rows);
    }

    static async Task SqliteAsync(string path, int rows)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Delete(file);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE contacts (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL, city TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE writes (id INTEGER PRIMARY KEY, name TEXT NOT NULL, amount REAL NOT NULL);
            """);
        await using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO contacts VALUES ($id, $name, $email, $city, $created_at)";
            var parameters = new[] { "$id", "$name", "$email", "$city", "$created_at" }.Select(n => insert.Parameters.AddWithValue(n, DBNull.Value)).ToArray();
            for (var id = 1; id <= rows; id++)
            {
                var c = Contact(id);
                parameters[0].Value = id;
                parameters[1].Value = c.Name;
                parameters[2].Value = c.Email;
                parameters[3].Value = c.City;
                parameters[4].Value = c.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                await insert.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        Console.WriteLine($"sqlite: {rows} contacts in {path}");
    }

    static async Task SqlServerAsync(string server, int rows)
    {
        await using (var master = new SqlConnection(Connections.SqlServer(server, "master")))
        {
            await master.OpenAsync();
            await ExecuteAsync(master, "IF DB_ID('DbToRestApiBench') IS NULL CREATE DATABASE DbToRestApiBench;");
            using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))", master);
            Console.WriteLine($"sqlserver-version: SQL Server {await version.ExecuteScalarAsync()}");
        }
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(Connections.SqlServer(server, "DbToRestApiBench"));
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            DROP TABLE IF EXISTS contacts;
            DROP TABLE IF EXISTS writes;
            CREATE TABLE contacts (id int PRIMARY KEY, name nvarchar(100) NOT NULL, email nvarchar(200) NOT NULL, city nvarchar(100) NOT NULL, created_at datetime2(0) NOT NULL);
            CREATE TABLE writes (id int IDENTITY PRIMARY KEY, name nvarchar(100) NOT NULL, amount float NOT NULL);
            """);
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("email", typeof(string));
        table.Columns.Add("city", typeof(string));
        table.Columns.Add("created_at", typeof(DateTime));
        for (var id = 1; id <= rows; id++)
        {
            var c = Contact(id);
            table.Rows.Add(id, c.Name, c.Email, c.City, c.CreatedAt);
        }
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "contacts" };
        await bulk.WriteToServerAsync(table);
        Console.WriteLine($"sqlserver: {rows} contacts in DbToRestApiBench on {server}");
    }

    static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

static class Connections
{
    public static string SqlServer(string server, string database) =>
        new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = database, IntegratedSecurity = true, TrustServerCertificate = true }.ConnectionString;
}

static class Fetch
{
    public static async Task RunAsync(Options options)
    {
        using var client = new HttpClient();
        using var request = Load.Request(options.Get("method") ?? "GET", options.Require("url"), options.Get("body"));
        using var response = await client.SendAsync(request);
        Console.WriteLine((int)response.StatusCode);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
    }
}

static class Load
{
    public static HttpRequestMessage Request(string method, string url, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    public static async Task RunAsync(Options options)
    {
        if (options.Get("affinity") is { } affinity)
            Process.GetCurrentProcess().ProcessorAffinity = (nint)long.Parse(affinity, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var concurrency = options.Int("concurrency", 32);
        var rows = options.Int("rows", 10000);
        var method = options.Get("method") ?? "GET";
        var url = options.Require("url");
        var body = options.Get("body");
        var server = options.Get("pid") is { } pid ? Process.GetProcessById(int.Parse(pid, CultureInfo.InvariantCulture)) : null;

        using var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = concurrency,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        var counter = 0L;
        string Fill(string template, Random random)
        {
            if (!template.Contains('{')) return template;
            return template
                .Replace("{id}", random.Next(1, rows + 1).ToString(CultureInfo.InvariantCulture))
                .Replace("{after}", random.Next(0, Math.Max(1, rows - 1000)).ToString(CultureInfo.InvariantCulture))
                .Replace("{n}", Interlocked.Increment(ref counter).ToString(CultureInfo.InvariantCulture));
        }

        async Task<Phase> PhaseAsync(TimeSpan length)
        {
            var end = Stopwatch.GetTimestamp() + (long)(length.TotalSeconds * Stopwatch.Frequency);
            var workers = Enumerable.Range(0, concurrency).Select(worker => Task.Run(async () =>
            {
                var random = new Random(worker * 7919 + 17);
                var latencies = new List<long>(1 << 16);
                long errors = 0, bytes = 0;
                while (Stopwatch.GetTimestamp() < end)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        using var request = Request(method, Fill(url, random), body is null ? null : Fill(body, random));
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        await using var stream = await response.Content.ReadAsStreamAsync();
                        var counting = new CountingStream();
                        await stream.CopyToAsync(counting);
                        bytes += counting.Length;
                        if (!response.IsSuccessStatusCode) errors++;
                    }
                    catch (Exception)
                    {
                        errors++;
                    }
                    latencies.Add(Stopwatch.GetTimestamp() - start);
                }
                return (latencies, errors, bytes);
            })).ToArray();
            var results = await Task.WhenAll(workers);
            return new Phase(results.SelectMany(r => r.latencies).ToArray(), results.Sum(r => r.errors), results.Sum(r => r.bytes));
        }

        await PhaseAsync(TimeSpan.FromSeconds(options.Int("warmup", 3)));

        server?.Refresh();
        var cpuBefore = server?.TotalProcessorTime ?? TimeSpan.Zero;
        var started = Stopwatch.GetTimestamp();
        var phase = await PhaseAsync(TimeSpan.FromSeconds(options.Int("duration", 10)));
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        server?.Refresh();
        var cpu = ((server?.TotalProcessorTime ?? TimeSpan.Zero) - cpuBefore).TotalSeconds;

        Array.Sort(phase.Latencies);
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        double Percentile(double p) => phase.Latencies.Length == 0 ? 0 : Ms(phase.Latencies[Math.Min(phase.Latencies.Length - 1, (int)(p * phase.Latencies.Length))]);
        var requests = phase.Latencies.Length;

        var result = new Dictionary<string, object?>
        {
            ["server"] = options.Get("server"),
            ["db"] = options.Get("db"),
            ["scenario"] = options.Get("scenario"),
            ["concurrency"] = concurrency,
            ["requests"] = requests,
            ["errors"] = phase.Errors,
            ["rps"] = Math.Round(requests / seconds, 1),
            ["mean_ms"] = Math.Round(requests == 0 ? 0 : phase.Latencies.Average(Ms), 3),
            ["p50_ms"] = Math.Round(Percentile(0.50), 3),
            ["p90_ms"] = Math.Round(Percentile(0.90), 3),
            ["p99_ms"] = Math.Round(Percentile(0.99), 3),
            ["max_ms"] = Math.Round(requests == 0 ? 0 : Ms(phase.Latencies[^1]), 3),
            ["bytes_per_response"] = requests == 0 ? 0 : phase.Bytes / requests,
            ["server_cpu_pct"] = server is null ? null : Math.Round(cpu / seconds * 100, 1),
            ["server_cpu_us_per_request"] = server is null || requests == 0 ? null : Math.Round(cpu * 1_000_000 / requests, 1),
            ["server_working_set_mb"] = server is null ? null : Math.Round(server.WorkingSet64 / 1048576.0, 1),
            ["server_private_mb"] = server is null ? null : Math.Round(server.PrivateMemorySize64 / 1048576.0, 1),
            ["server_peak_working_set_mb"] = server is null ? null : Math.Round(server.PeakWorkingSet64 / 1048576.0, 1),
        };
        var line = JsonSerializer.Serialize(result);
        await File.AppendAllTextAsync(options.Require("out"), line + Environment.NewLine);
        Console.WriteLine(line);
    }

    sealed record Phase(long[] Latencies, long Errors, long Bytes);

    /// <summary>Reads a response to its end without keeping it.</summary>
    sealed class CountingStream : Stream
    {
        long _length;
        public override long Length => _length;
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
