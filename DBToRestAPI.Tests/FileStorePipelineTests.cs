using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

            ["queries:store_download:route"] = "store/download/{{id}}",
            ["queries:store_download:verb"] = "GET",
            ["queries:store_download:response_structure"] = "file",
            ["queries:store_download:file_management:store"] = "pl_store",
            ["queries:store_download:query"] = "SELECT path AS relative_path, 'note.txt' AS file_name FROM uploads WHERE id = {{id}};",
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

    [Fact]
    public async Task ErrorAfterTheRows_RemovesTheStoredFile()
    {
        var before = StoredFiles().Length;

        using var response = await engine.Client.PostAsync("store/upload_raise", new StringContent(
            """{"attachments": [{"name": "late.txt", "content_base64": "aGVsbG8="}]}""", Encoding.UTF8, "application/json"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, body.GetProperty("error_number").GetInt32());
        Assert.Equal(before, StoredFiles().Length);
        Assert.DoesNotContain(StoredFiles(), f => f.EndsWith("late.txt", StringComparison.Ordinal));
    }
}
