using System.Net;

namespace DBToRestAPI.Tests;

/// <summary>
/// A route's <c>root_node</c> wraps its answer in <c>{"&lt;root_node&gt;": answer}</c>, whatever
/// shape the answer has: streamed rows (<c>array</c>, several rows without
/// <c>response_structure</c>, a count query's data), a read result (with <c>cache</c>), one row and
/// no row. A streamed answer inside the wrapper answered 500 while the wrapper was an ExpandoObject,
/// because ExpandoJsonConverter writes values synchronously.
/// </summary>
public class RootNodePipelineTests(RootNodePipelineTests.Engine engine) : IClassFixture<RootNodePipelineTests.Engine>
{
    private const string Rows = """[{"n":1,"t":"a"},{"n":2,"t":"b"},{"n":3,"t":"c"}]""";
    private const string RowsQuery = "WITH r(n, t) AS (VALUES (1, 'a'), (2, 'b'), (3, 'c')) SELECT n, t FROM r ORDER BY n;";

    public sealed class Engine() : PipelineTests.PipelineEngine(_ => new()
    {
        ["queries:rn_array:route"] = "rn/array",
        ["queries:rn_array:verb"] = "GET",
        ["queries:rn_array:response_structure"] = "array",
        ["queries:rn_array:root_node"] = "root",
        ["queries:rn_array:query"] = RowsQuery,

        ["queries:rn_array_buffered:route"] = "rn/array-buffered",
        ["queries:rn_array_buffered:verb"] = "GET",
        ["queries:rn_array_buffered:response_structure"] = "array",
        ["queries:rn_array_buffered:buffer_rows"] = "10",
        ["queries:rn_array_buffered:root_node"] = "root",
        ["queries:rn_array_buffered:query"] = RowsQuery,

        ["queries:rn_array_cached:route"] = "rn/array-cached",
        ["queries:rn_array_cached:verb"] = "GET",
        ["queries:rn_array_cached:response_structure"] = "array",
        ["queries:rn_array_cached:root_node"] = "root",
        ["queries:rn_array_cached:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:rn_array_cached:query"] = RowsQuery,

        ["queries:rn_array_empty:route"] = "rn/array-empty",
        ["queries:rn_array_empty:verb"] = "GET",
        ["queries:rn_array_empty:response_structure"] = "array",
        ["queries:rn_array_empty:root_node"] = "root",
        ["queries:rn_array_empty:query"] = "SELECT 1 AS n WHERE 1 = 0;",

        ["queries:rn_auto_rows:route"] = "rn/auto-rows",
        ["queries:rn_auto_rows:verb"] = "GET",
        ["queries:rn_auto_rows:root_node"] = "root",
        ["queries:rn_auto_rows:query"] = RowsQuery,

        ["queries:rn_auto_one:route"] = "rn/auto-one",
        ["queries:rn_auto_one:verb"] = "GET",
        ["queries:rn_auto_one:root_node"] = "root",
        ["queries:rn_auto_one:query"] = "SELECT 1 AS n, 'a' AS t;",

        ["queries:rn_auto_none:route"] = "rn/auto-none",
        ["queries:rn_auto_none:verb"] = "GET",
        ["queries:rn_auto_none:root_node"] = "root",
        ["queries:rn_auto_none:query"] = "SELECT 1 AS n WHERE 1 = 0;",

        ["queries:rn_count:route"] = "rn/count",
        ["queries:rn_count:verb"] = "GET",
        ["queries:rn_count:root_node"] = "root",
        ["queries:rn_count:count_query"] = "SELECT 3;",
        ["queries:rn_count:query"] = RowsQuery,

        ["queries:rn_count_cached:route"] = "rn/count-cached",
        ["queries:rn_count_cached:verb"] = "GET",
        ["queries:rn_count_cached:root_node"] = "root",
        ["queries:rn_count_cached:cache:memory:duration_in_milliseconds"] = "60000",
        ["queries:rn_count_cached:count_query"] = "SELECT 3;",
        ["queries:rn_count_cached:query"] = RowsQuery,
    });

    private async Task<string> GetAsync(string path)
    {
        using var response = await engine.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
        return body;
    }

    [Theory]
    [InlineData("rn/array")]
    [InlineData("rn/array-buffered")]
    [InlineData("rn/auto-rows")]
    public async Task StreamedRows_AreWrapped(string path)
    {
        Assert.Equal("{\"root\":" + Rows + "}", await GetAsync(path));
    }

    [Fact]
    public async Task CachedRows_AreWrapped_FilledAndThenHit()
    {
        Assert.Equal("{\"root\":" + Rows + "}", await GetAsync("rn/array-cached"));
        Assert.Equal("{\"root\":" + Rows + "}", await GetAsync("rn/array-cached"));
    }

    [Fact]
    public async Task NoRows_WithArray_IsAnEmptyArray()
    {
        Assert.Equal("""{"root":[]}""", await GetAsync("rn/array-empty"));
    }

    [Fact]
    public async Task OneRow_IsWrappedAsAnObject()
    {
        Assert.Equal("""{"root":{"n":1,"t":"a"}}""", await GetAsync("rn/auto-one"));
    }

    [Fact]
    public async Task NoRow_WithoutArray_IsNull()
    {
        Assert.Equal("""{"root":null}""", await GetAsync("rn/auto-none"));
    }

    [Theory]
    [InlineData("rn/count")]
    [InlineData("rn/count-cached")]
    public async Task ACountQuerysAnswer_IsWrapped(string path)
    {
        Assert.Equal("{\"root\":{\"success\":true,\"count\":3,\"data\":" + Rows + "}}", await GetAsync(path));
    }
}
