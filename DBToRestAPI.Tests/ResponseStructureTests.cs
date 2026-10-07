using DBToRestAPI.Controllers;
using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// From 1.7.8 the documented values of <c>response_structure</c> are <c>array</c> and <c>file</c>,
/// and leaving the tag out gives the row-count shape. <c>single</c> and <c>auto</c> are read as that
/// shape: <c>single</c> returned the first row of any result, and was mostly set on queries that
/// return one row anyway. A global <c>response_structure</c> under <c>settings</c> is no longer read.
/// A route that still sets <c>single</c> or <c>auto</c>, a value the engine doesn't know, and a
/// global value are reported when the routes load.
/// </summary>
public class ResponseStructureTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfigurationRoot Config(Dictionary<string, string?> data)
        => new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]          // a blank value counts as none
    [InlineData(" ", "auto")]
    [InlineData("single", "auto")]
    [InlineData("Single", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("ARRAY", "array")]
    [InlineData("file", "file")]
    [InlineData("singel", "singel")]  // unknown values are kept, and answer 500
    public void ResolveResponseStructure(string? route, string expected)
    {
        Assert.Equal(expected, ApiController.ResolveResponseStructure(route));
    }

    [Fact]
    public void Single_IsReportedOnceAtLoad()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:get_item:route"] = "items/{{id}}",
            ["queries:get_item:response_structure"] = "single",
            ["queries:list:route"] = "items",
            ["queries:list:response_structure"] = "array",
            ["queries:download:route"] = "files/{{id}}",
            ["queries:download:response_structure"] = "File",
            ["queries:plain:route"] = "plain",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("`queries:get_item`", entry.Message);
        Assert.Contains("read as the default", entry.Message);
        Assert.Contains("TOP 1, LIMIT 1", entry.Message);
        Assert.Contains("Then remove the tag.", entry.Message);
    }

    [Fact]
    public void Auto_IsReported()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = "Auto",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("which is the default", message);
        Assert.Contains("Remove the tag.", message);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("auto")]
    public void SingleOrAuto_OnACountQueryRoute_IsToldTheTagIsIgnored(string value)
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = value,
            ["queries:ep:count_query"] = "SELECT 1",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("a route with a count_query ignores", message);
        Assert.DoesNotContain("several rows", message);
    }

    [Fact]
    public void UnknownValue_IsReported()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = "singel",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("<response_structure>singel</response_structure>", message);
        Assert.Contains("answers 500", message);
    }

    [Fact]
    public void UnknownValue_OnACountQueryRoute_IsNotReported()
    {
        // A count query's route ignores response_structure, so the value does no harm there.
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = "paged",
            ["queries:ep:count_query"] = "SELECT 1",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("single")]
    [InlineData("objects")]
    public void GlobalValue_IsReportedAsNotRead(string value)
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["response_structure"] = value,
            ["queries:ep:route"] = "ep",
        });

        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains($"The global <response_structure>{value}</response_structure>", message);
        Assert.Contains("is not read from 1.7.8", message);
    }

    [Fact]
    public void GlobalValue_IsReportedOnceAcrossReloads()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["response_structure"] = "array",
            ["queries:ep:route"] = "ep",
        });
        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);
        Assert.Single(log.Entries);

        config.Reload();                                   // same configuration loaded again
        Assert.Single(log.Entries);

        config["response_structure"] = "file";
        config.Reload();                                   // a different global value
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void Single_CountQueryRemoved_IsReportedAgainWithTheNewAdvice()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = "single",
            ["queries:ep:count_query"] = "SELECT 1",
        });
        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);
        Assert.Contains("count_query ignores", Assert.Single(log.Entries).Message);

        config["queries:ep:count_query"] = null;
        config.Reload();

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("TOP 1, LIMIT 1", log.Entries[1].Message);
    }

    [Fact]
    public void Reload_RepeatsTheWarningOnlyWhenTheValueComesBack()
    {
        var log = new ListLogger<QueryRouteResolver>();
        var config = Config(new()
        {
            ["queries:ep:route"] = "ep",
            ["queries:ep:response_structure"] = "single",
        });
        _ = new QueryRouteResolver(new TestEncryptedConfiguration(config), log);
        Assert.Single(log.Entries);

        config.Reload();                                   // same configuration loaded again
        Assert.Single(log.Entries);

        config["queries:ep:response_structure"] = "singel";
        config.Reload();                                   // a different mistake
        Assert.Equal(2, log.Entries.Count);

        config["queries:ep:response_structure"] = "array";
        config.Reload();                                   // fixed
        Assert.Equal(2, log.Entries.Count);

        config["queries:ep:response_structure"] = "single";
        config.Reload();                                   // back again
        Assert.Equal(3, log.Entries.Count);
    }
}
