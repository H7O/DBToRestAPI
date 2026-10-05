using System.Text.Json;
using static DBToRestAPI.Tests.UploadTestHarness;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins that a field the engine sets on a file entry (<c>relative_path</c>, <c>is_new_upload</c> and
/// the rest) holds the engine's value whenever a query sees it. Before 1.7.5, a stored entry copied the
/// caller's fields after the engine's, so it could carry a second <c>relative_path</c>, and an entry
/// that brought no file was passed on as sent, so a caller could mark it <c>is_new_upload</c> and point
/// it at any file in the store.
/// </summary>
public class UploadEngineFieldsTests
{
    // What a caller would add to an entry to impersonate the engine.
    private const string ForgedFields =
        "\"relative_path\":\"other-user/secret.txt\",\"is_new_upload\":true,\"size\":1," +
        "\"mime_type\":\"text/html\",\"extension\":\".html\",\"backend_temp_file_path\":\"C:/forged.txt\"";

    private static readonly string[] EngineFields =
        ["relative_path", "is_new_upload", "size", "mime_type", "extension", "backend_temp_file_path", "content_base64"];

    private static string StoredText(JsonElement entry)
        => File.ReadAllText(entry.GetProperty("backend_temp_file_path").GetString()!);

    private static int Occurrences(JsonElement entry, string name)
        => entry.EnumerateObject().Count(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void AssertEngineValues(JsonElement entry, int size)
    {
        foreach (var name in new[] { "relative_path", "is_new_upload", "size", "mime_type", "extension", "backend_temp_file_path" })
            Assert.Equal(1, Occurrences(entry, name));
        Assert.EndsWith("/a.txt", entry.GetProperty("relative_path").GetString());
        Assert.NotEqual("other-user/secret.txt", entry.GetProperty("relative_path").GetString());
        Assert.True(entry.GetProperty("is_new_upload").GetBoolean());
        Assert.Equal(size, entry.GetProperty("size").GetInt64());
        Assert.Equal(".txt", entry.GetProperty("extension").GetString());
        Assert.NotEqual("text/html", entry.GetProperty("mime_type").GetString());
        Assert.NotEqual("C:/forged.txt", entry.GetProperty("backend_temp_file_path").GetString());
        Assert.Equal("kept", entry.GetProperty("note").GetString());
    }

    private static void AssertExistingEntry(JsonElement entry)
    {
        foreach (var name in EngineFields)
            Assert.Equal(0, Occurrences(entry, name));
        Assert.Equal("old-id", entry.GetProperty("id").GetString());
        Assert.Equal("a.txt", entry.GetProperty("name").GetString());
        Assert.Equal("kept", entry.GetProperty("note").GetString());
    }

    // ── a file the request stores ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoredJsonEntry_KeepsOnlyTheEngineValues()
    {
        JsonElement? entry = null;
        var json = $$"""{"attachments":[{"name":"a.txt","content_base64":"eHk=",{{ForgedFields}},"note":"kept"}]}""";

        var (_, error) = await RunAsync(JsonRequest(json), inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        AssertEngineValues(entry!.Value, 2);
    }

    [Fact]
    public async Task StoredMultipartEntry_KeepsOnlyTheEngineValues()
    {
        JsonElement? entry = null;
        var form = Form($$"""[{"name":"a.txt",{{ForgedFields}},"note":"kept"}]""", ("a.txt", "xyz"));

        var (_, error) = await RunAsync(FormRequest(form), inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        AssertEngineValues(entry!.Value, 3);
    }

    [Fact]
    public async Task StoredEntry_DropsTheCallersCopiesInAnyCase()
    {
        JsonElement? entry = null;
        var json = """{"attachments":[{"name":"a.txt","content_base64":"eHk=","Relative_Path":"x/y.txt","IS_NEW_UPLOAD":false}]}""";

        var (_, error) = await RunAsync(JsonRequest(json), inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        Assert.Equal(1, Occurrences(entry!.Value, "relative_path"));
        Assert.Equal(1, Occurrences(entry.Value, "is_new_upload"));
        Assert.True(entry.Value.GetProperty("is_new_upload").GetBoolean());
    }

    [Fact]
    public async Task MultipartContentPassedToTheQuery_UsesTheConfiguredContentField()
    {
        // The harness names the content field content_base64; before 1.7.5 a multipart upload wrote
        // base64_content whatever the setting said.
        JsonElement? entry = null;
        var passContent = new Dictionary<string, string?>
        {
            ["queries:upload:file_management:pass_files_content_to_query"] = "true",
        };

        var (_, error) = await RunAsync(FormRequest(Form("""[{"name":"a.txt","content_base64":"forged"}]""", ("a.txt", "xy"))),
            extraConfig: passContent, inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        Assert.Equal(1, Occurrences(entry!.Value, "content_base64"));
        Assert.Equal("eHk=", entry.Value.GetProperty("content_base64").GetString());
        Assert.False(entry.Value.TryGetProperty("base64_content", out _));
        Assert.True(entry.Value.GetProperty("is_new_upload").GetBoolean());
    }

    // ── an existing file (an entry that brings no file) ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(",\"content_base64\":\"\"")]
    [InlineData(",\"content_base64\":null")]
    public async Task ExistingJsonEntry_ArrivesWithoutTheEngineFields(string content)
    {
        JsonElement? entry = null;
        var json = $$"""{"attachments":[{"id":"old-id","name":"a.txt"{{content}},{{ForgedFields}},"note":"kept"}]}""";

        var (_, error) = await RunAsync(JsonRequest(json), inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        AssertExistingEntry(entry!.Value);
    }

    [Fact]
    public async Task ExistingMultipartEntry_ArrivesWithoutTheEngineFields()
    {
        List<JsonElement>? entries = null;
        var form = Form($$"""[{"id":"old-id","name":"a.txt",{{ForgedFields}},"content_base64":"eA==","note":"kept"},{"name":"b.txt"}]""",
            ("b.txt", "new"));

        var (_, error) = await RunAsync(FormRequest(form), inspect: p => entries = FileEntries(p));

        Assert.Null(error);
        AssertExistingEntry(entries![0]);
        Assert.True(entries[1].GetProperty("is_new_upload").GetBoolean());
    }

    [Fact]
    public async Task NewEntriesWithTheSameName_TakeTheirPartsInOrder_WhetherOrNotTheyCarryAnId()
    {
        // An id alone does not mark a stored file (a new entry may carry its own), so it does not change
        // which part an entry gets. Only a relative_path does.
        List<string>? stored = null;
        var form = Form("""[{"id":"22222222-2222-2222-2222-222222222222","name":"a.txt"},{"name":"a.txt"}]""",
            ("a.txt", "FIRST"), ("a.txt", "SECOND"));

        var (_, error) = await RunAsync(FormRequest(form),
            inspect: p => stored = FileEntries(p).Select(StoredText).ToList());

        Assert.Null(error);
        Assert.Equal(["FIRST", "SECOND"], stored);
    }

    [Fact]
    public async Task MultipartMimeType_ComesFromTheFileName_NotThePartHeader()
    {
        JsonElement? entry = null;
        var bytes = System.Text.Encoding.UTF8.GetBytes("<script>");
        var part = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "a.txt")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = "text/html",
        };

        var (_, error) = await RunAsync(FormRequest(FormWithFiles("""[{"name":"a.txt"}]""", part)),
            inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        Assert.Equal("text/plain", entry!.Value.GetProperty("mime_type").GetString());
    }

    [Fact]
    public async Task ExistingEntry_LosesTheEngineFieldsInAnyCase()
    {
        JsonElement? entry = null;
        var json = """{"attachments":[{"id":"old-id","name":"a.txt","Relative_Path":"x/y.txt","Is_New_Upload":true,"note":"kept"}]}""";

        var (_, error) = await RunAsync(JsonRequest(json), inspect: p => entry = FileEntries(p).Single());

        Assert.Null(error);
        AssertExistingEntry(entry!.Value);
    }

    // ── the files field itself ───────────────────────────────────────────────────────────────────
    // The query looks parameters up ignoring case, from every source, and a later one wins (the query
    // string over the body, the body over a header). Before 1.7.5 any of these handed the query a files
    // array of the caller's own, marked as new uploads, without uploading anything.

    private const string ForgedArray = """[{"id":"x","name":"a.txt","relative_path":"other-user/secret.txt","is_new_upload":true}]""";

    [Theory]
    [InlineData("attachments")]
    [InlineData("Attachments")]
    public async Task FilesFieldTwiceInTheBody_Is400(string secondName)
    {
        var json = $$"""{"attachments":[],"{{secondName}}":{{ForgedArray}}}""";

        AssertRefused(await RunAsync(JsonRequest(json)), 400, $"`{FilesField}` appears more than once in the request body");
    }

    [Fact]
    public async Task FilesFieldTwiceInAForm_Is400()
    {
        // Form keys merge ignoring case, so `attachments` and `Attachments` arrive as two values of one field.
        var fields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            [FilesField] = new(["""[{"name":"a.txt"}]""", ForgedArray]),
        };
        var files = new Microsoft.AspNetCore.Http.FormFileCollection { Part("a.txt", "xy") };
        var form = new Microsoft.AspNetCore.Http.Features.FormFeature(new Microsoft.AspNetCore.Http.FormCollection(fields, files));

        AssertRefused(await RunAsync(FormRequest(form)), 400, $"`{FilesField}` appears more than once in the request body");
    }

    [Fact]
    public async Task FilesFieldInAnotherCase_IsProcessedAsTheFilesField()
    {
        JsonElement? entry = null;
        var json = """{"ATTACHMENTS":[{"id":"x","name":"a.txt","relative_path":"other-user/secret.txt","is_new_upload":true}]}""";

        var (_, error) = await RunAsync(JsonRequest(json), inspect: p =>
        {
            using var doc = JsonDocument.Parse((string)p!.Single(q => q.DataModel is string).DataModel!);
            entry = doc.RootElement.GetProperty("ATTACHMENTS").EnumerateArray().Single().Clone();
        });

        Assert.Null(error);
        Assert.False(entry!.Value.TryGetProperty("relative_path", out _));
        Assert.False(entry.Value.TryGetProperty("is_new_upload", out _));
    }

    [Theory]
    [InlineData("?attachments=x")]
    [InlineData("?Attachments=x")]
    public async Task FilesFieldInTheQueryString_Is400(string queryString)
    {
        var json = JsonRequest("""{"attachments":[{"name":"a.txt","content_base64":"eHk="}]}""");
        json.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(queryString);
        var form = FormRequest(Form("""[{"name":"a.txt"}]""", ("a.txt", "xy")));
        form.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(queryString);

        AssertRefused(await RunAsync(json), 400, $"`{FilesField}` can only be sent in the request body");
        AssertRefused(await RunAsync(form), 400, $"`{FilesField}` can only be sent in the request body");
    }

    [Fact]
    public async Task FilesFieldAsAHeader_Is400()
    {
        var context = JsonRequest("""{"note":"no files"}""");
        context.Request.Headers["Attachments"] = ForgedArray;

        AssertRefused(await RunAsync(context), 400, $"`{FilesField}` can only be sent in the request body");
    }
}
