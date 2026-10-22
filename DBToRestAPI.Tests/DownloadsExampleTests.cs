using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// docs/topics/10-file-downloads.md, "Complete example: owner-only download", on SQL Server, with
/// the store, tables and create endpoint of the uploads example, which the page builds on. As the
/// page says, the create endpoint gets an &lt;authorize&gt; so that owner_id holds the submitter.
/// </summary>
public class DownloadsExampleTests(DownloadsExampleTests.Engine engine) : IClassFixture<DownloadsExampleTests.Engine>
{
    public sealed class Engine : SqlServerExampleEngine
    {
        public FakeIdp Idp { get; }

        public Engine() : base(
            new SqlServerExample(("docs/topics/09-file-uploads.md", "Complete example"), ("docs/topics/10-file-downloads.md", "Complete example")),
            folder => new()
            {
                ["file_management:local_file_store:support:base_path"] = Path.Combine(folder, "support") + Path.DirectorySeparatorChar,
                ["authorize:providers:my_provider:authority"] = "https://idp.test",
                ["authorize:providers:my_provider:audience"] = "api://docs",
                ["queries:create_support_request:authorize:provider"] = "my_provider",
            })
        {
            Idp = new FakeIdp(Http, "https://idp.test", "api://docs");
        }
    }

    private string Token(string user) => engine.Idp.Issue(new() { ["sub"] = user });

    /// <summary>A support request by <paramref name="user"/> with one PDF, and that file's id.</summary>
    private async Task<(Guid FileId, byte[] Content)> UploadAsync(string user)
    {
        var content = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Jane Doe"), "full_name" },
            { new StringContent("jane@example.com"), "email" },
            { new StringContent("billing"), "category" },
            { new StringContent("Invoice question"), "message" },
            { new StringContent("""[{"name":"invoice.pdf"}]"""), "attachments" },
            { new ByteArrayContent(content), "file", "invoice.pdf" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "support-requests") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        using var response = await engine.Client.SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToString());

        var requestId = Guid.Parse(body.GetProperty("id").GetString()!);
        var fileId = (Guid)engine.Example.Scalar($"SELECT id FROM support_request_files WHERE request_id = '{requestId}'")!;
        return (fileId, content);
    }

    private async Task<HttpResponseMessage> DownloadAsync(string id, string? user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"support-files/{id}");
        if (user != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        return await engine.Client.SendAsync(request);
    }

    [SqlServerFact]
    public async Task TheOwner_GetsTheFile_WithItsTypeAndName()
    {
        var (fileId, content) = await UploadAsync("owner-1");

        using var response = await DownloadAsync(fileId.ToString(), "owner-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment; filename=invoice.pdf; filename*=UTF-8''invoice.pdf", response.Content.Headers.ContentDisposition?.ToString());
    }

    [SqlServerTheory]
    [InlineData("another user")]
    [InlineData("an unknown id")]
    [InlineData("an id that isn't a GUID")]
    public async Task AnyoneElse_GetsTheSame404(string who)
    {
        var (fileId, _) = await UploadAsync("owner-2");
        var (id, user) = who switch
        {
            "another user" => (fileId.ToString(), "someone-else"),
            "an unknown id" => (Guid.NewGuid().ToString(), "owner-2"),
            _ => ("not-a-guid", "owner-2"),
        };

        using var response = await DownloadAsync(id, user);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.StartsWith("No file record found to download for route ", body.GetProperty("message").GetString());
    }

    [SqlServerFact]
    public async Task APlainLink_WithoutTheToken_Is401()
    {
        var (fileId, _) = await UploadAsync("owner-3");

        using var response = await DownloadAsync(fileId.ToString(), user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
