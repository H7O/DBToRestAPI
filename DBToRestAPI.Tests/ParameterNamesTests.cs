using Com.H.Data.Common;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DBToRestAPI.Tests;

/// <summary>
/// A marker holds the caller's name for an input exactly as it was sent: spaces, dashes, dots,
/// slashes. The engine never renames inputs, and Com.H.Data.Common binds each marker to a generated
/// SQL parameter name, so these characters never reach the SQL text.
/// </summary>
public class ParameterNamesTests
{
    [Theory]
    [InlineData("first name,e-mail", new[] { "first name", "e-mail" })]
    [InlineData(" a , b ,, c ", new[] { "a", "b", "c" })]
    [InlineData("a,\r\nb\nc", new[] { "a", "b", "c" })]
    [InlineData("last, first|e-mail", new[] { "last, first", "e-mail" })] // a `|` makes commas part of names
    [InlineData(" a | b\nc ", new[] { "a", "b", "c" })]
    public void MandatoryParameters_AreSplitOnCommasOrPipesAndLineBreaks(string value, string[] expected)
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["queries:ep:mandatory_parameters"] = value })
            .Build()
            .GetSection("queries:ep");

        Assert.Equal(expected, section.GetMandatoryParameters());
    }

    [Fact]
    public async Task AuthClaim_WithAUriName_ReachesTheQueryUnchanged()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var claims = new Dictionary<string, object>
        {
            ["http://schemas.microsoft.com/identity/claims/scope"] = "openid orders.write",
            ["user.email"] = "ann@example.com",
        };
        var parameters = new List<DbQueryParams>
        {
            new() { DataModel = claims, QueryParamsRegex = DefaultRegex.DefaultAuthVariablesPattern },
        };

        await using var result = await connection.ExecuteQueryAsync(
            "SELECT {auth{http://schemas.microsoft.com/identity/claims/scope}} AS scope, {auth{user.email}} AS email;",
            parameters);
        var row = Assert.Single(result.AsEnumerable().ToList());

        Assert.Equal("openid orders.write", (string)row.scope);
        Assert.Equal("ann@example.com", (string)row.email);
    }
}
