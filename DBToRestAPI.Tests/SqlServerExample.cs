using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration.Xml;

namespace DBToRestAPI.Tests;

/// <summary>
/// A page's complete example, run as the page shows it: its tables created in a new SQL Server
/// database, its endpoints read from the page's XML. A test then sends the page's requests and
/// checks the answers the page documents, so an example that stops working, or answers otherwise
/// than its page says, fails the build.
/// </summary>
/// <remarks>
/// The server comes from the <c>DBTOREST_TEST_SQLSERVER</c> environment variable, a connection
/// string without a database, such as <c>Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=true</c>.
/// CI runs a SQL Server container for it. Without it, these tests are reported as skipped.
/// </remarks>
public sealed partial class SqlServerExample
{
    public const string ServerVariable = "DBTOREST_TEST_SQLSERVER";

    public static string? Server => Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } server ? server : null;

    /// <summary>The database this example's tables are in, created for it.</summary>
    public string Database { get; } = "DbToRestDocs_" + Guid.NewGuid().ToString("N");

    public string ConnectionString { get; }

    /// <summary>The example's endpoints, as the configuration reader reads them.</summary>
    public Dictionary<string, string?> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="sections">
    /// Each page, from the repository's root (<c>docs/topics/09-file-uploads.md</c>), with the start of
    /// the heading of the section that holds its example. A page whose example builds on another's
    /// lists that one first.
    /// </param>
    public SqlServerExample(params (string Page, string Heading)[] sections)
    {
        var blocks = sections
            .SelectMany(s => DocsExamplesTests.Blocks(
                Section(File.ReadAllText(Path.Combine(DocsExamplesTests.Root, s.Page)).Replace("\r\n", "\n"), s.Heading)))
            .ToList();

        // An XML block whose elements hold a <query> is an endpoint, shown inside <settings><queries>.
        foreach (var (_, code, _) in blocks.Where(b => b.Lang == "xml" && b.Code.Contains("<query")))
        {
            using var xml = new MemoryStream(Encoding.UTF8.GetBytes($"<settings><queries>{code}</queries></settings>"));
            foreach (var (key, value) in XmlStreamConfigurationProvider.Read(xml, XmlDocumentDecryptor.Instance))
                Endpoints[key] = value;
        }

        ConnectionString = Server is { } server
            ? new SqlConnectionStringBuilder(server) { InitialCatalog = Database }.ConnectionString
            : "";
        if (Server is null)
            return;

        WaitForServer.Value.GetAwaiter().GetResult();
        Execute(Server, $"CREATE DATABASE [{Database}]");
        foreach (var (_, code, _) in blocks.Where(b => b.Lang == "sql" && b.Code.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)))
            Execute(ConnectionString, code);
    }

    /// <summary>
    /// SQL run in the example's database: the tables of a page that doesn't show them, built from
    /// the columns its queries use, and their rows.
    /// </summary>
    public void Execute(string sql)
    {
        if (Server is not null)
            Execute(ConnectionString, sql);
    }

    /// <summary>A query against the example's database, for a test that checks what was written.</summary>
    public object? Scalar(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    public void Drop()
    {
        if (Server is null)
            return;
        SqlConnection.ClearAllPools();
        Execute(Server, $"ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}];");
    }

    // A server in a container that has just started refuses connections for a while, so the first
    // example waits until it accepts one, up to two minutes, whatever image it runs.
    private static readonly Lazy<Task> WaitForServer = new(async () =>
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(Server);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    });

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    [GeneratedRegex(@"^(?<level>#+) ", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>The text from the heading that starts with <paramref name="heading"/> to the next heading of its level or above.</summary>
    private static string Section(string text, string heading)
    {
        var start = Heading().Matches(text).FirstOrDefault(m => text.AsSpan(m.Index + m.Length).StartsWith(heading))
            ?? throw new InvalidOperationException($"No heading `{heading}`");
        var level = start.Groups["level"].Length;
        var end = Heading().Matches(text).FirstOrDefault(m => m.Index > start.Index && m.Groups["level"].Length <= level);
        return text[start.Index..(end?.Index ?? text.Length)];
    }
}

/// <summary>
/// The engine with a page's complete example: its endpoints on the example's SQL Server database,
/// plus <paramref name="extraSettings"/> (a store, say).
/// </summary>
public abstract class SqlServerExampleEngine(SqlServerExample example, Func<string, Dictionary<string, string?>>? extraSettings = null)
    : PipelineTests.PipelineEngine(folder =>
        {
            var settings = new Dictionary<string, string?>(example.Endpoints);
            foreach (var (key, value) in extraSettings?.Invoke(folder) ?? [])
                settings[key] = value;
            return settings;
        },
        routesDatabase: (example.ConnectionString, "Microsoft.Data.SqlClient"))
{
    public SqlServerExample Example { get; } = example;

    public override void Dispose()
    {
        base.Dispose();
        Example.Drop();
    }
}

/// <summary>A test that needs SQL Server: skipped without <see cref="SqlServerExample.ServerVariable"/>.</summary>
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (SqlServerExample.Server is null) Skip = $"Set {SqlServerExample.ServerVariable} to a SQL Server to run the docs' SQL Server examples.";
    }
}

/// <summary>A theory that needs SQL Server: skipped without <see cref="SqlServerExample.ServerVariable"/>.</summary>
internal sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (SqlServerExample.Server is null) Skip = $"Set {SqlServerExample.ServerVariable} to a SQL Server to run the docs' SQL Server examples.";
    }
}
