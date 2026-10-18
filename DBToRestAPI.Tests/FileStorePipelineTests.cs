using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// The whole engine with a local file store in its temporary folder: an upload is written to the
/// store and recorded by the query, removed again when the request fails after the rows, and
/// served back by a download route.
/// </summary>
public class FileStorePipelineTests(FileStorePipelineTests.StoreEngine engine) : IClassFixture<FileStorePipelineTests.StoreEngine>
{
    private const string Record =
        "INSERT INTO uploads (path) SELECT json_extract(value, '$.relative_path') FROM json_each({{attachments}}) "
        + "WHERE json_extract(value, '$.is_new_upload') = 1; "
        + "SELECT id, path FROM uploads WHERE id = last_insert_rowid();";

    public sealed class StoreEngine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        return new()
        {
            ["file_management:local_file_store:pl_store:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,

            ["queries:store_upload:route"] = "store/upload",
            ["queries:store_upload:verb"] = "POST",
            ["queries:store_upload:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:store_upload:file_management:stores"] = "pl_store",
            ["queries:store_upload:query"] = Record,

            ["queries:store_upload_raise:route"] = "store/upload_raise",
            ["queries:store_upload_raise:verb"] = "POST",
            ["queries:store_upload_raise:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:store_upload_raise:file_management:stores"] = "pl_store",
            ["queries:store_upload_raise:query"] = Record + " INSERT INTO raise_after VALUES (1);",

            // Three short rows are streamed, so the error raised after them leaves the controller.
            ["queries:store_upload_raise_rows:route"] = "store/upload_raise_rows",
            ["queries:store_upload_raise_rows:verb"] = "POST",
            ["queries:store_upload_raise_rows:response_structure"] = "array",
            ["queries:store_upload_raise_rows:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:store_upload_raise_rows:file_management:stores"] = "pl_store",
            ["queries:store_upload_raise_rows:query"] = Record.Replace(
                "WHERE id = last_insert_rowid();", "WHERE id = last_insert_rowid() UNION ALL SELECT 0, 'x' UNION ALL SELECT 0, 'y';")
                + " INSERT INTO raise_after VALUES (1);",

            ["queries:store_download:route"] = "store/download/{{id}}",
            ["queries:store_download:verb"] = "GET",
            ["queries:store_download:response_structure"] = "file",
            ["queries:store_download:file_management:store"] = "pl_store",
            ["queries:store_download:query"] = "SELECT path AS relative_path, 'note.txt' AS file_name FROM uploads WHERE id = {{id}};",

            ["queries:store_download_cached:route"] = "store/download_cached/{{id}}",
            ["queries:store_download_cached:verb"] = "GET",
            ["queries:store_download_cached:response_structure"] = "file",
            ["queries:store_download_cached:file_management:store"] = "pl_store",
            ["queries:store_download_cached:cache:memory:duration_in_milliseconds"] = "60000",
            ["queries:store_download_cached:query"] = "SELECT path AS relative_path, 'note.txt' AS file_name FROM uploads WHERE id = {{id}};",

            // The path comes from the query string here, standing in for one stored in a table.
            ["queries:store_download_path:route"] = "store/download_path",
            ["queries:store_download_path:verb"] = "GET",
            ["queries:store_download_path:response_structure"] = "file",
            ["queries:store_download_path:file_management:store"] = "pl_store",
            ["queries:store_download_path:query"] = "SELECT {{path}} AS relative_path, 'x.txt' AS file_name;",
        };
    })
    {
        public string Store => Path.Combine(Folder, "store");
    }

    private string[] StoredFiles() => Directory.GetFiles(engine.Store, "*", SearchOption.AllDirectories);

    private async Task<(int Id, string Path)> UploadAsync(HttpContent content)
    {
        using var response = await engine.Client.PostAsync("store/upload", content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var row = JsonDocument.Parse(body).RootElement;
        return (row.GetProperty("id").GetInt32(), row.GetProperty("path").GetString()!);
    }

    [Fact]
    public async Task JsonUpload_IsStored_AndDownloadsBack()
    {
        var (id, path) = await UploadAsync(new StringContent(
            """{"attachments": [{"name": "note.txt", "content_base64": "aGVsbG8="}]}""", Encoding.UTF8, "application/json"));

        Assert.EndsWith("/note.txt", path);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(engine.Store, path)));

        using var download = await engine.Client.GetAsync($"store/download/{id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("hello", await download.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("note.txt", download.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task ADownloadRouteWithCache_StillDeliversTheFile_EveryTime()
    {
        // The cache stores JSON results only, so <cache> on a download route is ignored: a cached
        // route used to answer without the file.
        var (id, _) = await UploadAsync(new StringContent(
            """{"attachments": [{"name": "note.txt", "content_base64": "aGVsbG8="}]}""", Encoding.UTF8, "application/json"));

        for (var call = 0; call < 2; call++)
        {
            using var download = await engine.Client.GetAsync($"store/download_cached/{id}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("hello", await download.Content.ReadAsStringAsync());
            Assert.Equal("note.txt", download.Content.Headers.ContentDisposition?.FileNameStar);
        }
    }

    [Theory]
    [InlineData("../outside-the-store.txt")]    // refused: outside the store
    [InlineData("2026/missing-from-store.txt")]  // inside the store, but no such file
    public async Task FileNotFound_NamesTheRouteOnly_AndLogsThePath(string storedPath)
    {
        var before = engine.Logs.Entries.Count;

        using var response = await engine.Client.GetAsync("store/download_path?path=" + Uri.EscapeDataString(storedPath));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString();
        Assert.StartsWith("File not found for route `store/download_path`", message);
        Assert.DoesNotContain(Path.GetFileName(storedPath), message);
        Assert.Contains(engine.Logs.Entries.Skip(before),
            e => e.Level == LogLevel.Warning && e.Message.Contains(Path.GetFileName(storedPath)));
    }

    [Fact]
    public async Task MultipartUpload_PairsThePartWithItsEntry_AndIsStored()
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes("from a part"));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("""[{"name": "part.txt"}]"""), "attachments" },
            { part, "file", "part.txt" },
        };

        var (_, path) = await UploadAsync(form);

        Assert.EndsWith("/part.txt", path);
        Assert.Equal("from a part", File.ReadAllText(Path.Combine(engine.Store, path)));
    }

    [Theory]
    [InlineData("store/upload_raise")]       // one row: read to the end inside the controller
    [InlineData("store/upload_raise_rows")]  // streamed: the error passes through the upload step
    public async Task ErrorAfterTheRows_RemovesTheStoredFile(string route)
    {
        var before = StoredFiles().Length;
        var logged = engine.Logs.Entries.Count;

        using var response = await engine.Client.PostAsync(route, new StringContent(
            """{"attachments": [{"name": "late.txt", "content_base64": "aGVsbG8="}]}""", Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, body.GetProperty("error_number").GetInt32());
        Assert.Equal(before, StoredFiles().Length);
        Assert.DoesNotContain(StoredFiles(), f => f.EndsWith("late.txt", StringComparison.Ordinal));

        // A status the query raised on purpose is logged once, without a stack trace.
        var entries = engine.Logs.Entries.Skip(logged).ToList();
        Assert.Single(entries, e => e.Level == LogLevel.Information && e.Message.Contains("answered 404, raised by its query"));
        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Error);
    }
}
