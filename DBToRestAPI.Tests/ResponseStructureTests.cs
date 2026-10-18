using DBToRestAPI.Controllers;

namespace DBToRestAPI.Tests;

/// <summary>
/// The documented values of <c>response_structure</c> are <c>array</c> and <c>file</c>, and leaving
/// the tag out gives the row-count shape. <c>single</c> and <c>auto</c> are read as that shape.
/// </summary>
public class ResponseStructureTests
{
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
}
