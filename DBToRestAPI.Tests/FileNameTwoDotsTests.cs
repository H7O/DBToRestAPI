using DBToRestAPI.Services;

namespace DBToRestAPI.Tests;

/// <summary>
/// An uploaded file's name may contain two dots in a row (<c>Q3 2025..pdf</c>). A name is one path segment,
/// since <c>/</c> and <c>\</c> are refused, and a segment only leaves its folder when it is <c>.</c> or
/// <c>..</c> as a whole, as FileStorePath reads a stored path.
///
/// Before, any name containing <c>..</c> was refused. About 3% of the attachments one production system
/// receives are named like <c>Report 2025..pdf</c>, so moving it onto the engine would have refused them.
/// </summary>
public class FileNameTwoDotsTests
{
    private static readonly HashSet<string> Pdf = new() { ".pdf" };

    [Theory]
    [InlineData("Q3 2025..pdf")]
    [InlineData("a..b.pdf")]
    [InlineData("Report...final.pdf")]
    [InlineData("x.pdf")]
    public void TwoDotsInsideAName_AreAccepted(string name)
    {
        Assert.Equal(name, ParametersBuilder.ValidateAndGetNormalizeFileName(name, Pdf));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../x.pdf")]
    [InlineData("..\\x.pdf")]
    [InlineData("sub/../x.pdf")]
    [InlineData("..pdf")]
    [InlineData("x.pdf.")]
    [InlineData("x..")]
    [InlineData("...")]
    public void TraversalAndBadlyFormedNames_AreStillRefused(string name)
    {
        Assert.Throws<RequestValidationException>(() => ParametersBuilder.ValidateAndGetNormalizeFileName(name, null));
    }

    [Fact]
    public void ADotOnlyName_GetsTheTraversalMessage()
    {
        var e = Assert.Throws<RequestValidationException>(() => ParametersBuilder.ValidateAndGetNormalizeFileName("..", null));
        Assert.Contains("path traversal", e.Message);
    }
}
