using System.Net;
using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/09-file-uploads.md, "Complete example: a form with fields and files", on SQL Server:
/// each row of its "What the client gets back" table that a request can produce.
/// </summary>
public class UploadsExampleTests(UploadsExampleTests.Engine engine) : IClassFixture<UploadsExampleTests.Engine>
{
    public sealed class Engine() : SqlServerExampleEngine(new SqlServerExample(("docs/topics/09-file-uploads.md", "Complete example")), folder => new()
    {
        // The page's store, in the engine's temporary folder.
        ["file_management:local_file_store:support:base_path"] = Path.Combine(folder, "support") + Path.DirectorySeparatorChar,
    })
    {
        public string Store => Path.Combine(Folder, "support");
    }

    private int StoredFiles() => Directory.Exists(engine.Store) ? Directory.GetFiles(engine.Store, "*", SearchOption.AllDirectories).Length : 0;

    /// <summary>The page's form: its fields, the files field, and one part per file.</summary>
    private static MultipartFormDataContent Form(string? category = "billing", string? entries = null, params (string Name, int Bytes)[] files)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Jane Doe"), "full_name" },
            { new StringContent("jane@example.com"), "email" },
            { new StringContent("Invoice question"), "message" },
        };
        if (category != null)
            form.Add(new StringContent(category), "category");
        form.Add(new StringContent(entries ?? JsonSerializer.Serialize(files.Select(f => new { name = f.Name }))), "attachments");
        foreach (var (name, bytes) in files)
            form.Add(new ByteArrayContent(new byte[bytes]), "file", name);
        return form;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(MultipartFormDataContent form)
    {
        using var response = await engine.Client.PostAsync("support-requests", form);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    [SqlServerFact]
    public async Task Success_Is201_WithTheRequestAndItsFileCount()
    {
        var before = StoredFiles();

        var (status, body) = await PostAsync(Form(files: [("invoice.pdf", 100), ("receipt.png", 200)]));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(2, body.GetProperty("file_count").GetInt32());
        var id = body.GetProperty("id").GetString();
        Assert.Equal(2, engine.Example.Scalar($"SELECT COUNT(*) FROM support_request_files WHERE request_id = '{Guid.Parse(id!)}'"));
        Assert.Equal(before + 2, StoredFiles());
    }

    public static TheoryData<string, string?, string?, (string, int)[], string, bool> Refusals() => new()
    {
        { "A mandatory field missing", null, null, [("invoice.pdf", 10)], "Missing mandatory parameters: category", false },
        { "A THROW 50400 in the query", "billing", "[]", [], "Attach at least one file.", true },
        { "Wrong extension", "billing", null, [("tool.exe", 10)], "File extension `.exe` is not permitted.", false },
        { "File too large", "billing", null, [("big.pdf", 5242881)], "File `big.pdf` exceeds the maximum allowed size of 5242880 bytes", false },
        { "More than 3 files", "billing", null, [("a.pdf", 1), ("b.pdf", 1), ("c.pdf", 1), ("d.pdf", 1)], "Number of files exceeds the maximum allowed limit of 3", false },
        { "A file part with no entry", "billing", """[{"name":"invoice.pdf"}]""", [("invoice.pdf", 10), ("x.pdf", 10)], "Uploaded file `x.pdf` has no entry in `attachments`.", false },
        // The table's rule for every row: the request's stored files are deleted.
        { "A THROW after the files were stored", "refunds", null, [("invoice.pdf", 10)], "Category must be billing, technical or other.", true },
    };

    [SqlServerTheory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusal_Is400_WithThePagesMessage_AndKeepsNoFile(string @case, string? category, string? entries, (string, int)[] files, string message, bool raised)
    {
        var before = StoredFiles();

        var (status, body) = await PostAsync(Form(category, entries, files));

        Assert.True(HttpStatusCode.BadRequest == status, $"{@case}: {status} {body}");
        Assert.Equal(message, body.GetProperty("message").GetString());
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(raised, body.TryGetProperty("error_number", out var number) && number.GetInt32() == 400);
        Assert.Equal(before, StoredFiles());
    }
}
