// The baseline for benchmarks/run.ps1: the benchmark's endpoints written by hand, with ADO.NET and
// System.Text.Json, running the same SQL as benchmarks/engine/sql.xml and answering the same JSON.
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var sqlite = builder.Configuration["Bench:Sqlite"] ?? throw new InvalidOperationException("Bench:Sqlite is required");
var sqlServer = builder.Configuration["Bench:SqlServer"];

DbConnection Sqlite() => new SqliteConnection(sqlite);
DbConnection SqlServer() => new SqlConnection(sqlServer ?? throw new InvalidOperationException("Bench:SqlServer is not set"));

// SQLite
app.MapGet("/bench/sqlite/ping", async () =>
    new { ok = await ScalarAsync<long>(Sqlite(), "SELECT 1 AS ok;") });

app.MapGet("/bench/sqlite/contacts/{id:int}", async (int id) =>
    (await QueryAsync(Sqlite(),
        "SELECT id, name, email, city, created_at FROM contacts WHERE id = $id;",
        c => Add(c, "$id", id), SqliteContact.Read)) is [var row] ? Results.Ok(row) : Results.NoContent());

app.MapGet("/bench/sqlite/contacts", async (int after, int take) =>
    await QueryAsync(Sqlite(),
        "SELECT id, name, email, city, created_at FROM contacts WHERE id > $after ORDER BY id LIMIT $take;",
        c => { Add(c, "$after", after); Add(c, "$take", take); }, SqliteContact.Read));

app.MapPost("/bench/sqlite/writes", async (WriteRequest write) =>
    new { id = await ScalarAsync<long>(Sqlite(),
        "INSERT INTO writes (name, amount) VALUES ($name, $amount); SELECT last_insert_rowid() AS id;",
        c => { Add(c, "$name", write.name); Add(c, "$amount", write.amount); }) });

// SQL Server
app.MapGet("/bench/sqlserver/ping", async () =>
    new { ok = await ScalarAsync<int>(SqlServer(), "SELECT 1 AS ok;") });

app.MapGet("/bench/sqlserver/contacts/{id:int}", async (int id) =>
    (await QueryAsync(SqlServer(),
        "SELECT id, name, email, city, created_at FROM contacts WHERE id = @id;",
        c => Add(c, "@id", id), SqlServerContact.Read)) is [var row] ? Results.Ok(row) : Results.NoContent());

app.MapGet("/bench/sqlserver/contacts", async (int after, int take) =>
    await QueryAsync(SqlServer(),
        "SELECT TOP (@take) id, name, email, city, created_at FROM contacts WHERE id > @after ORDER BY id;",
        c => { Add(c, "@after", after); Add(c, "@take", take); }, SqlServerContact.Read));

app.MapPost("/bench/sqlserver/writes", async (WriteRequest write) =>
    new { id = await ScalarAsync<int>(SqlServer(),
        "INSERT INTO writes (name, amount) OUTPUT INSERTED.id VALUES (@name, @amount);",
        c => { Add(c, "@name", write.name); Add(c, "@amount", write.amount); }) });

app.Run();

static void Add(DbCommand command, string name, object value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value;
    command.Parameters.Add(parameter);
}

static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, Action<DbCommand>? bind = null)
{
    await using (connection)
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}

static async Task<List<T>> QueryAsync<T>(DbConnection connection, string sql, Action<DbCommand> bind, Func<DbDataReader, T> read)
{
    await using (connection)
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(read(reader));
        return rows;
    }
}

record WriteRequest(string name, double amount);

record SqliteContact(long id, string name, string email, string city, string created_at)
{
    public static SqliteContact Read(DbDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
}

record SqlServerContact(int id, string name, string email, string city, DateTime created_at)
{
    public static SqlServerContact Read(DbDataReader r) =>
        new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDateTime(4));
}
