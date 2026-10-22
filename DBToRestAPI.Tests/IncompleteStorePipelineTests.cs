using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// An upload store missing a setting it needs (an SFTP store's host, username or password, a local
/// store's base_path) fails like a store that can't be reached, before any file is written: an
/// optional one is skipped with a warning, any other fails the request. It was skipped without a
/// word, so the request succeeded and its query recorded files no store had.
/// </summary>
public class IncompleteStorePipelineTests(IncompleteStorePipelineTests.Engine engine) : IClassFixture<IncompleteStorePipelineTests.Engine>
{
    private const string Record =
        "INSERT INTO uploads (path) SELECT json_extract(value, '$.relative_path') FROM json_each({{attachments}}) "
        + "WHERE json_extract(value, '$.is_new_upload') = 1; "
        + "SELECT COUNT(*) AS stored FROM uploads;";

    public sealed class Engine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        var settings = new Dictionary<string, string?>
        {
            ["file_management:local_file_store:inc_ok:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,
            ["file_management:local_file_store:inc_no_base:optional"] = "false",
            ["file_management:sftp_file_store:inc_no_password:host"] = "sftp.example",
            ["file_management:sftp_file_store:inc_no_password:username"] = "uploader",
            ["file_management:sftp_file_store:inc_no_password_optional:host"] = "sftp.example",
            ["file_management:sftp_file_store:inc_no_password_optional:username"] = "uploader",
            ["file_management:sftp_file_store:inc_no_password_optional:optional"] = "true",
        };
        foreach (var (route, stores) in new[]
                 {
                     ("required_sftp", "inc_ok,inc_no_password"),
                     ("optional_sftp", "inc_ok,inc_no_password_optional"),
                     ("required_local", "inc_ok,inc_no_base"),
                 })
        {
            settings[$"queries:inc_{route}:route"] = $"inc/{route}";
            settings[$"queries:inc_{route}:verb"] = "POST";
            settings[$"queries:inc_{route}:file_management:files_json_field_or_form_field_name"] = "attachments";
            settings[$"queries:inc_{route}:file_management:stores"] = stores;
            settings[$"queries:inc_{route}:query"] = Record;
        }
        return settings;
    });

    private Task<HttpResponseMessage> UploadAsync(string route, string fileName) => engine.Client.PostAsync(route, new StringContent(
        $$"""{"attachments": [{"name": "{{fileName}}", "content_base64": "YWJj"}]}""", Encoding.UTF8, "application/json"));

    private bool Stored(string fileName)
        => Directory.GetFiles(Path.Combine(engine.Folder, "store"), fileName, SearchOption.AllDirectories).Length > 0;

    [Theory]
    [InlineData("inc/required_sftp", "required_sftp.txt", "SFTP store 'inc_no_password' has no `password`")]
    [InlineData("inc/required_local", "required_local.txt", "local store 'inc_no_base' has no `base_path`")]
    public async Task ARequiredStoreMissingASetting_FailsTheRequest_BeforeAFileIsWritten(string route, string fileName, string logged)
    {
        using var response = await UploadAsync(route, fileName);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(Stored(fileName));
        Assert.Contains(engine.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(logged, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnOptionalStoreMissingASetting_IsSkipped_WithAWarning()
    {
        using var response = await UploadAsync("inc/optional_sftp", "optional_sftp.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Stored("optional_sftp.txt"));
        Assert.Contains(engine.Logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("SFTP store 'inc_no_password_optional' has no `password`"));
    }
}
