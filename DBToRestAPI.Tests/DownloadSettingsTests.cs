using System.Net;

namespace DBToRestAPI.Tests;

/// <summary>
/// A download route with a global setting the shared pipeline engine doesn't have: the pattern of
/// the {fs{...}} placeholders, set under &lt;regex&gt;.
/// </summary>
public class DownloadSettingsTests(DownloadSettingsTests.DownloadEngine engine) : IClassFixture<DownloadSettingsTests.DownloadEngine>
{
    public sealed class DownloadEngine() : PipelineTests.PipelineEngine(folder =>
    {
        Directory.CreateDirectory(Path.Combine(folder, "store"));
        File.WriteAllText(Path.Combine(folder, "store", "hello.txt"), "hello");
        return new()
        {
            ["regex:file_variables_pattern"] = @"(?<open_marker>\[fs\[)(?<param>.*?)?(?<close_marker>\]\])",
            ["file_management:local_file_store:dl_store:base_path"] = Path.Combine(folder, "store") + Path.DirectorySeparatorChar,

            ["queries:dl_named:route"] = "dl/named",
            ["queries:dl_named:verb"] = "GET",
            ["queries:dl_named:response_structure"] = "file",
            ["queries:dl_named:file_management:store"] = "dl_store",
            ["queries:dl_named:query"] = "SELECT 'hello.txt' AS relative_path, [fs[store]] || '.txt' AS file_name;",
        };
    });

    [Fact]
    public async Task TheGlobalFileVariablesPattern_IsRead()
    {
        using var response = await engine.Client.GetAsync("dl/named");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("dl_store.txt", (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"'));
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }
}
