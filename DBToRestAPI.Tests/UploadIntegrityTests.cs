using System.Text.Json;
using static DBToRestAPI.Tests.UploadTestHarness;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins that an upload stores exactly what the caller sent. Before 1.7.4, base64 without its trailing
/// <c>=</c> padding lost its last one or two bytes, two multipart parts with the same name were both
/// stored with the first part's bytes, and a part no metadata entry named was silently ignored.
/// </summary>
public class UploadIntegrityTests
{
    private static byte[] StoredBytes(JsonElement entry)
        => File.ReadAllBytes(entry.GetProperty("backend_temp_file_path").GetString()!);

    private static string StoredText(JsonElement entry)
        => System.Text.Encoding.UTF8.GetString(StoredBytes(entry));

    private static string JsonUpload(string base64)
        => JsonSerializer.Serialize(new { attachments = new[] { new { name = "a.txt", content_base64 = base64 } } });

    // ── base64 content ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("eA==", "x")]
    [InlineData("eA=", "x")]
    [InlineData("eA", "x")]
    [InlineData("eHk=", "xy")]
    [InlineData("eHk", "xy")]
    [InlineData("eHl6", "xyz")]
    [InlineData("eHl6eA", "xyzx")]
    [InlineData("eH\nk", "xy")]
    [InlineData("eH k=\r\n", "xy")]
    [InlineData("eHk=\f", "xy")]
    [InlineData("eH\fk=", "xy")]
    [InlineData("eHk\v", "xy")]
    public async Task Base64_WithOrWithoutPadding_StoresExactlyTheSentBytes(string base64, string expected)
    {
        string? stored = null;

        var (_, error) = await RunAsync(JsonRequest(JsonUpload(base64)),
            inspect: p => stored = StoredText(FileEntries(p).Single()));

        Assert.Null(error);
        Assert.Equal(expected, stored);
    }

    [Fact]
    public async Task LongUnpaddedBase64_AcrossDecodeChunks_StoresExactlyTheSentBytes()
    {
        // 10,001 bytes: well past the decoder's 4,096-character chunks, and a length whose base64 needs
        // padding, which is then stripped.
        var bytes = Enumerable.Range(0, 10_001).Select(i => (byte)(i * 31 % 256)).ToArray();
        byte[]? stored = null;

        var (_, error) = await RunAsync(JsonRequest(JsonUpload(Convert.ToBase64String(bytes).TrimEnd('='))),
            inspect: p => stored = StoredBytes(FileEntries(p).Single()));

        Assert.Null(error);
        Assert.Equal(bytes, stored);
    }

    [Theory]
    [InlineData("e")]
    [InlineData("eHl6e")]
    public async Task Base64_WithALengthNoBase64CanHave_Is400(string base64)
    {
        AssertRefused(await RunAsync(JsonRequest(JsonUpload(base64))), 400, "not valid base64");
    }

    [Fact]
    public async Task UnpaddedBase64_PassedToTheQuery_ArrivesPaddedWithTheRightSize()
    {
        List<JsonElement>? entries = null;
        var passContent = new Dictionary<string, string?>
        {
            ["queries:upload:file_management:pass_files_content_to_query"] = "true",
        };

        var (_, error) = await RunAsync(JsonRequest(JsonUpload("eHk")), extraConfig: passContent,
            inspect: p => entries = FileEntries(p));

        Assert.Null(error);
        var entry = Assert.Single(entries!);
        Assert.Equal("eHk=", entry.GetProperty("content_base64").GetString());
        Assert.Equal(2, entry.GetProperty("size").GetInt64());
    }

    [Theory]
    [InlineData("eHl6e")]
    [InlineData("data:text/plain;base64,eA==")]
    [InlineData("eHk=")]
    public async Task ContentPassedToTheQuery_ThatIsNotPlainUnpaddedBase64_IsPassedUnchanged(string content)
    {
        List<JsonElement>? entries = null;
        var passContent = new Dictionary<string, string?>
        {
            ["queries:upload:file_management:pass_files_content_to_query"] = "true",
        };

        var (_, error) = await RunAsync(JsonRequest(JsonUpload(content)), extraConfig: passContent,
            inspect: p => entries = FileEntries(p));

        Assert.Null(error);
        Assert.Equal(content, Assert.Single(entries!).GetProperty("content_base64").GetString());
    }

    [Fact]
    public async Task LongBase64WithSparseWhitespace_StoresExactlyTheSentBytes()
    {
        // FromBase64Transform, used before 1.7.4, threw on this valid content: a lone newline shifted
        // its leftover characters past the end of its buffer at the 4096-character chunk boundary.
        var bytes = Enumerable.Range(0, 9_000).Select(i => (byte)(i * 7 % 251)).ToArray();
        byte[]? stored = null;

        var (_, error) = await RunAsync(JsonRequest(JsonUpload(Convert.ToBase64String(bytes).Insert(100, "\n"))),
            inspect: p => stored = StoredBytes(FileEntries(p).Single()));

        Assert.Null(error);
        Assert.Equal(bytes, stored);
    }

    // ── multipart parts and their metadata entries ───────────────────────────────────────────────

    [Fact]
    public async Task TwoPartsWithTheSameName_AreStoredAsTwoFilesWithTheirOwnContent()
    {
        List<string>? stored = null;
        var form = Form("""[{"name":"image.txt"},{"name":"image.txt"}]""", ("image.txt", "one"), ("image.txt", "two"));

        var (_, error) = await RunAsync(FormRequest(form),
            inspect: p => stored = FileEntries(p).Select(StoredText).ToList());

        Assert.Null(error);
        Assert.Equal(["one", "two"], stored);
    }

    [Fact]
    public async Task EntryLeftWithoutAPart_IsKeptAsAnExistingFile()
    {
        List<JsonElement>? entries = null;
        string? firstStored = null;
        var form = Form("""[{"name":"image.txt"},{"name":"image.txt","id":"existing-id"}]""", ("image.txt", "one"));

        var (_, error) = await RunAsync(FormRequest(form), inspect: p =>
        {
            entries = FileEntries(p);
            firstStored = StoredText(entries[0]);
        });

        Assert.Null(error);
        Assert.Equal(2, entries!.Count);
        Assert.Equal("one", firstStored);
        Assert.False(entries[1].TryGetProperty("backend_temp_file_path", out _));
        Assert.Equal("existing-id", entries[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task StoredFileListedFirst_LeavesTheNewPartForTheNewEntry()
    {
        // A partial update in its natural order: the file already stored, then the newly added one,
        // both called image.txt (phones name every photo the same).
        List<JsonElement>? entries = null;
        string? newStored = null;
        var form = Form("""[{"name":"image.txt","id":"old-id","relative_path":"old/image.txt"},{"name":"image.txt"}]""",
            ("image.txt", "NEW"));

        var (_, error) = await RunAsync(FormRequest(form), inspect: p =>
        {
            entries = FileEntries(p);
            newStored = StoredText(entries[1]);
        });

        Assert.Null(error);
        Assert.Equal("old/image.txt", entries![0].GetProperty("relative_path").GetString());
        Assert.False(entries[0].TryGetProperty("backend_temp_file_path", out _));
        Assert.Equal("NEW", newStored);
    }

    [Fact]
    public async Task NamesThatDifferOnlyInCase_ArePairedWithTheirExactMatch()
    {
        List<string>? stored = null;
        var form = Form("""[{"name":"Image.txt"},{"name":"image.txt"}]""", ("image.txt", "lower"), ("Image.txt", "upper"));

        var (_, error) = await RunAsync(FormRequest(form),
            inspect: p => stored = FileEntries(p).Select(StoredText).ToList());

        Assert.Null(error);
        Assert.Equal(["upper", "lower"], stored);
    }

    [Fact]
    public async Task EntryMatchesItsPart_IgnoringCase()
    {
        string? stored = null;

        var (_, error) = await RunAsync(FormRequest(Form("""[{"name":"A.TXT"}]""", ("a.txt", "one"))),
            inspect: p => stored = StoredText(FileEntries(p).Single()));

        Assert.Null(error);
        Assert.Equal("one", stored);
    }

    [Fact]
    public async Task PartThatNoEntryNames_Is400NamingIt()
    {
        var form = Form("""[{"name":"a.txt"}]""", ("a.txt", "one"), ("b.txt", "two"));

        AssertRefused(await RunAsync(FormRequest(form)), 400, $"Uploaded file `b.txt` has no entry in `{FilesField}`");
    }

    [Fact]
    public async Task PartsWithAnEmptyFilesArray_Are400()
    {
        AssertRefused(await RunAsync(FormRequest(Form("[]", ("a.txt", "one")))), 400, "Uploaded file `a.txt` has no entry");
    }

    [Fact]
    public async Task SecondPartWithTheSameNameButOnlyOneEntry_Is400()
    {
        var form = Form("""[{"name":"image.txt"}]""", ("image.txt", "one"), ("image.txt", "two"));

        AssertRefused(await RunAsync(FormRequest(form)), 400, "Uploaded file `image.txt` has no entry");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PartsWithoutAFilesField_Are400(string? metadata)
    {
        var fields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["note"] = "hi" };
        if (metadata != null) fields[FilesField] = metadata;
        var files = new Microsoft.AspNetCore.Http.FormFileCollection { Part("z.txt", "hello") };
        var form = new Microsoft.AspNetCore.Http.Features.FormFeature(new Microsoft.AspNetCore.Http.FormCollection(fields, files));

        AssertRefused(await RunAsync(FormRequest(form)), 400, $"Uploaded file `z.txt` has no entry in `{FilesField}`");
    }

    [Fact]
    public async Task MultipartEntriesOverTheLimit_Are400BeforeAnyPairing()
    {
        // The limit in the harness is 2; the array is refused before entries and parts are paired.
        var form = Form("""[{"name":"a.txt"},{"name":"b.txt"},{"name":"c.txt"}]""", ("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));

        AssertRefused(await RunAsync(FormRequest(form)), 400, "maximum allowed limit of 2");
    }

    [Fact]
    public async Task FormWithOnlyFileParts_Is400()
    {
        var files = new Microsoft.AspNetCore.Http.FormFileCollection { Part("z.txt", "hello") };
        var form = new Microsoft.AspNetCore.Http.Features.FormFeature(new Microsoft.AspNetCore.Http.FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), files));

        AssertRefused(await RunAsync(FormRequest(form)), 400, "Uploaded file `z.txt` has no entry");
    }
}
