using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DBToRestAPI.Tests;

/// <summary>
/// The patterns that find each request input's placeholders: on the endpoint, else in regex.xml
/// (under &lt;regex&gt;), else the default. The query string, form and route values read a key at the
/// root of the configuration before regex.xml's.
/// </summary>
public class RegexOverrideTests
{
    private const string Pipes = @"(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)";
    private const string Brackets = @"(?<open_marker>\[\[)(?<param>.*?)?(?<close_marker>\]\])";

    // The pattern each source of a request's values is read with.
    private static async Task<List<string?>> PatternsFor(Dictionary<string, string?> settings)
    {
        settings["queries:ep:route"] = "items/{{id}}";
        settings["queries:ep:query"] = "SELECT 1";
        var root = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var config = new Mock<IEncryptedConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns<string>(key => root.GetSection(key));
        config.Setup(c => c[It.IsAny<string>()]).Returns<string>(key => root[key]);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?q=1");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream("f=1"u8.ToArray());
        context.Items["section"] = root.GetSection("queries:ep");
        context.Items["route"] = "items/7";
        context.Items["route_parameters"] = new Dictionary<string, string> { ["id"] = "7" };
        var services = new ServiceCollection();
        services.AddScoped<TempFilesTracker>();
        context.RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider;

        var builder = new ParametersBuilder(new HttpContextAccessor { HttpContext = context }, config.Object,
            NullLogger<ParametersBuilder>.Instance);
        return (await builder.GetParamsAsync())!.Select(p => p.QueryParamsRegex).ToList();
    }

    [Theory]
    [InlineData("regex:query_string_variables_pattern")]
    [InlineData("regex:form_data_variables_pattern")]
    [InlineData("regex:form_variables_pattern")]
    [InlineData("regex:route_variables_pattern")]
    public async Task APatternInRegexXml_IsRead(string key)
    {
        Assert.DoesNotContain(Pipes, await PatternsFor(new()));
        Assert.Single(await PatternsFor(new() { [key] = Pipes }), Pipes);
    }

    [Theory]
    [InlineData("query_string_variables_pattern")]
    [InlineData("form_data_variables_pattern")]
    [InlineData("route_variables_pattern")]
    public async Task AKeyAtTheRoot_WinsOverRegexXml_AndTheEndpointsElementOverBoth(string name)
    {
        var both = new Dictionary<string, string?> { [name] = Brackets, [$"regex:{name}"] = Pipes };
        var patterns = await PatternsFor(both);
        Assert.Contains(Brackets, patterns);
        Assert.DoesNotContain(Pipes, patterns);

        both[$"queries:ep:{name}"] = @"(?<open_marker>%%)(?<param>.*?)?(?<close_marker>%%)";
        patterns = await PatternsFor(both);
        Assert.Contains(@"(?<open_marker>%%)(?<param>.*?)?(?<close_marker>%%)", patterns);
        Assert.DoesNotContain(Brackets, patterns);
    }

    [Fact]
    public void TheShippedRegexXml_SetsTheDefaults()
    {
        // So reading it changes nothing: a source's pattern decides which others it is read with.
        var regex = new ConfigurationBuilder()
            .AddXmlFile(Path.Combine(AppContext.BaseDirectory, "config", "regex.xml"), optional: false)
            .Build()
            .GetSection("regex");

        Assert.Equal(DefaultRegex.DefaultJsonVariablesPattern, regex["json_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultQueryStringPattern, regex["query_string_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultHeadersPattern, regex["headers_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultRouteVariablesPattern, regex["route_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultFormDataVariablesPattern, regex["form_data_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultAuthVariablesPattern, regex["auth_variables_pattern"]);
        Assert.Equal(DefaultRegex.DefaultHttpVariablesPattern, regex["http_variable_pattern"]);
    }
}
