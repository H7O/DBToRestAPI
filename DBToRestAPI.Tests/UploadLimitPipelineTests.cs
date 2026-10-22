using System.Net;
using System.Text;

namespace DBToRestAPI.Tests;

/// <summary>
/// An upload limit below 1 is no limit, on a route or at the root. A global 0 rejected every file:
/// only a route's fell back.
/// </summary>
public class UploadLimitPipelineTests(UploadLimitPipelineTests.Engine engine) : IClassFixture<UploadLimitPipelineTests.Engine>
{
    private const string Record =
        "INSERT INTO uploads (path) SELECT json_extract(value, '$.relative_path') FROM json_each({{attachments}}) "
        + "WHERE json_extract(value, '$.is_new_upload') = 1; "
        + "SELECT COUNT(*) AS stored FROM uploads;";

    public sealed class Engine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        return new()
        {
            ["file_management:local_file_store:ul_store:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,
            ["file_management:max_number_of_files"] = "0",
            ["file_management:max_file_size_in_bytes"] = "0",

            ["queries:ul_upload:route"] = "ul/upload",
            ["queries:ul_upload:verb"] = "POST",
            ["queries:ul_upload:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:ul_upload:file_management:stores"] = "ul_store",
            ["queries:ul_upload:query"] = Record,

            ["queries:ul_one:route"] = "ul/one",
            ["queries:ul_one:verb"] = "POST",
            ["queries:ul_one:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:ul_one:file_management:stores"] = "ul_store",
            ["queries:ul_one:file_management:max_number_of_files"] = "1",
            ["queries:ul_one:query"] = Record,
        };
    });

    private static StringContent Files(int count) => new(
        "{\"attachments\": [" + string.Join(", ", Enumerable.Range(1, count).Select(i => $"{{\"name\": \"f{i}.txt\", \"content_base64\": \"YWJj\"}}")) + "]}",
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task AGlobalZero_IsNoLimit()
    {
        using var response = await engine.Client.PostAsync("ul/upload", Files(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARoutesLimit_StillApplies()
    {
        using var response = await engine.Client.PostAsync("ul/one", Files(2));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Number of files exceeds the maximum allowed limit of 1", body);
    }
}
