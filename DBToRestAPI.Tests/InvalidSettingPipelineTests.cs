using System.Net;
using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Tests;

/// <summary>
/// A setting the engine reads as a number or as true/false, holding something else: the request gets a
/// 500 that names the setting, its value and the route, as an invalid buffer_rows does. Each route's
/// query would answer 404, so a 500 also shows that it never ran.
/// </summary>
public class InvalidSettingPipelineTests(InvalidSettingPipelineTests.Engine engine) : IClassFixture<InvalidSettingPipelineTests.Engine>
{
    private const string Raise = "INSERT INTO raise_after VALUES (1);";

    public sealed class Engine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        return new()
        {
            ["file_management:local_file_store:is_store:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,

            ["queries:is_timeout:route"] = "is/timeout",
            ["queries:is_timeout:db_command_timeout"] = "soon",
            ["queries:is_timeout:query"] = Raise,

            ["queries:is_status:route"] = "is/status",
            ["queries:is_status:success_status_code"] = "created",
            ["queries:is_status:query"] = Raise,

            ["queries:is_query_timeout:route"] = "is/query_timeout",
            ["queries:is_query_timeout:query"] = Raise,
            ["queries:is_query_timeout:query:db_command_timeout"] = "later",

            ["queries:is_cache:route"] = "is/cache",
            ["queries:is_cache:cache:memory:duration_in_milliseconds"] = "a minute",
            ["queries:is_cache:query"] = Raise,

            ["queries:is_upload:route"] = "is/upload",
            ["queries:is_upload:verb"] = "POST",
            ["queries:is_upload:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:is_upload:file_management:stores"] = "is_store",
            ["queries:is_upload:file_management:max_number_of_files"] = "ten",
            ["queries:is_upload:query"] = Raise,

            ["queries:is_authorize:route"] = "is/authorize",
            ["queries:is_authorize:authorize:enabled"] = "yes",
            ["queries:is_authorize:query"] = Raise,

            ["routes:is_gateway:route"] = "is/gateway",
            ["routes:is_gateway:url"] = "https://upstream.example/data",
            ["routes:is_gateway:target_route_timeout_seconds"] = "slow",
        };
    });

    [Theory]
    [InlineData("is/timeout", "db_command_timeout", "soon", "a whole number")]
    [InlineData("is/status", "success_status_code", "created", "a whole number")]
    [InlineData("is/query_timeout", "query:db_command_timeout", "later", "a whole number")]
    [InlineData("is/cache", "cache:memory:duration_in_milliseconds", "a minute", "a whole number")]
    [InlineData("is/authorize", "authorize:enabled", "yes", "true or false")]
    [InlineData("is/gateway", "target_route_timeout_seconds", "slow", "a whole number")]
    public async Task AnInvalidSetting_IsA500_ThatNamesIt(string route, string setting, string value, string expected)
    {
        using var response = await engine.Client.GetAsync(route);

        await AssertInvalidAsync(response, route, setting, value, expected);
        Assert.DoesNotContain(engine.Http.Requests, r => r.Uri.Host == "upstream.example");
    }

    [Fact]
    public async Task AnInvalidUploadLimit_IsA500_AndNothingIsStored()
    {
        using var response = await engine.Client.PostAsync("is/upload", new StringContent(
            """{"attachments": [{"name": "a.txt", "content_base64": "YQ=="}]}""", Encoding.UTF8, "application/json"));

        await AssertInvalidAsync(response, "is/upload", "file_management:max_number_of_files", "ten", "a whole number");
        Assert.Empty(Directory.GetFiles(Path.Combine(engine.Folder, "store"), "*", SearchOption.AllDirectories));
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string route, string setting, string value, string expected)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.StartsWith($"Invalid {setting} `{value}` for route `{route}`: use {expected}", body.GetProperty("message").GetString());
    }
}
