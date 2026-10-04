using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using static DBToRestAPI.Tests.UploadTestHarness;

namespace DBToRestAPI.Tests;

/// <summary>
/// Pins how a request whose parameters can't be read is answered: an invalid upload (bad file name,
/// extension, count, content or metadata shape) is a 400 that says why, in both JSON and multipart
/// form; a body over the size limit keeps its 413; anything else is a JSON 500 with the error code.
/// Before 1.7.3 these escaped to Kestrel as an empty 500, and a multipart upload with an invalid file
/// (or an oversized form) was silently dropped while the request ran on with every form field null.
/// </summary>
public class ParametersErrorMappingTests
{
    // ── JSON uploads ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"attachments":[{"name":"a\\b.txt","content_base64":"eA=="}]}""", "a\\b.txt")]
    [InlineData("""{"attachments":[{"name":"run.exe","content_base64":"eA=="}]}""", "not permitted")]
    [InlineData("""{"attachments":[{"name":"..txt","content_base64":"eA=="}]}""", "..txt")]
    [InlineData("""{"attachments":[{"name":"a.txt","content_base64":"eA=="},{"name":"b.txt","content_base64":"eA=="},{"name":"c.txt","content_base64":"eA=="}]}""", "maximum allowed limit of 2")]
    [InlineData("""{"attachments":{"name":"a.txt","content_base64":"eA=="}}""", "must be an array")]
    [InlineData("""{"attachments":[{"name":"a.txt","content_base64":"data:text/plain;base64,eA=="}]}""", "not valid base64")]
    [InlineData("""[{"name":"a.txt","content_base64":"eA=="}]""", "must be a JSON object")]
    public async Task InvalidJsonUpload_Is400WithTheReason(string json, string expectedInMessage)
    {
        AssertRefused(await RunAsync(JsonRequest(json)), 400, expectedInMessage);
    }

    [Fact]
    public async Task ValidJsonUpload_HasNoError()
    {
        var (parameters, error) = await RunAsync(JsonRequest("""{"attachments":[{"name":"a.txt","content_base64":"eA=="}]}"""));

        Assert.Null(error);
        Assert.NotNull(parameters);
    }

    // ── multipart uploads ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidMultipartUpload_Is400InsteadOfBeingDropped()
    {
        AssertRefused(await RunAsync(FormRequest(Form("""[{"name":"run.exe"}]""", ("run.exe", "x")))), 400, "not permitted");
    }

    [Fact]
    public async Task MalformedMultipartMetadata_Is400InsteadOfBeingDropped()
    {
        AssertRefused(await RunAsync(FormRequest(Form("not json", ("a.txt", "x")))), 400, "must be a JSON array");
    }

    [Fact]
    public async Task MultipartBodyOverTheSizeLimit_Is413InsteadOfBeingDropped()
    {
        var tooLarge = new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        AssertRefused(await RunAsync(FormRequest(FormThatThrows(tooLarge))), 413, "could not be read");
    }

    [Fact]
    public async Task MultipartFormOverAFormOptionsLimit_Is400InsteadOfBeingDropped()
    {
        var overLimit = new InvalidDataException("Form value length limit 4194304 exceeded.");

        AssertRefused(await RunAsync(FormRequest(FormThatThrows(overLimit))), 400, "form data could not be read");
    }

    [Fact]
    public async Task FormContentTypeWithNoBody_IsReadAsNoParameters()
    {
        // Some clients send a form content type on a GET; 1.7.3 ran such requests normally.
        var context = FormRequest(FormThatThrows(new IOException("should not be read")));
        context.Request.Method = "GET";
        context.Request.ContentLength = 0;

        var (parameters, error) = await RunAsync(context);

        Assert.Null(error);
        Assert.NotNull(parameters);
    }

    [Fact]
    public async Task FormThatCannotBeRead_Is400()
    {
        var truncated = new IOException("Unexpected end of Stream, the content may have already been read by another component.");

        AssertRefused(await RunAsync(FormRequest(FormThatThrows(truncated))), 400, "form data could not be read");
    }

    [Theory]
    [InlineData("temp folder")]
    [InlineData("disk full")]
    public async Task DiskFaultWhileTheFormIsBuffered_Is500_NotBlamedOnTheCaller(string fault)
    {
        Exception ex = fault == "temp folder"
            ? new DirectoryNotFoundException("Could not find a part of the path.")
            : new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));

        AssertRefused(await RunAsync(FormRequest(FormThatThrows(ex))), 500, "test-error-code");
    }

    [Fact]
    public async Task CallerWhoDisconnectedMidUpload_GetsACancelled400_NotAServerError()
    {
        var context = FormRequest(FormThatThrows(new IOException("The client reset the request stream.")));
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;

        AssertRefused(await RunAsync(context), 400, "Request was cancelled");
    }

    // ── file name rules and server-side failures ─────────────────────────────────────────────────

    [Fact]
    public void FileNameThatIsNotValidUnicode_IsAValidationError()
    {
        var ex = Assert.Throws<RequestValidationException>(() => ParametersBuilder.ValidateAndGetNormalizeFileName("a\uD800.txt"));
        Assert.Contains("not valid Unicode", ex.Message);
    }

    [Fact]
    public async Task UnexpectedFailureWhileSavingAPart_Is500InsteadOfBeingDropped()
    {
        // A part without headers, so reading its content type throws. It stands in for any server-side
        // failure while a part is saved (a disk error, say), which used to drop every form field and
        // let the request report success.
        var brokenPart = new FormFile(new MemoryStream([120]), 0, 1, "file", "a.txt");

        AssertRefused(await RunAsync(FormRequest(FormWithFiles("""[{"name":"a.txt"}]""", brokenPart))), 500, "test-error-code");
    }

    [Fact]
    public async Task ServerSideFailure_IsJson500WithTheErrorCode()
    {
        // No route section in the request: a setup problem, not something the caller did.
        AssertRefused(await RunAsync(JsonRequest("""{"a":1}"""), withSection: false), 500, "test-error-code");
    }
}
