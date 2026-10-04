using System.Text;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;

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
    private static ParametersBuilder Build(DefaultHttpContext context, bool withSection = true)
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["queries:upload:file_management:files_json_field_or_form_field_name"] = "attachments",
            ["queries:upload:file_management:stores"] = "primary",
            ["file_management:relative_file_path_structure"] = "{{guid}}/{file{name}}",
            ["file_management:filename_field_in_payload"] = "name",
            ["file_management:base64_content_field_in_payload"] = "content_base64",
            ["file_management:permitted_file_extensions"] = ".txt",
            ["file_management:max_number_of_files"] = "2",
        }).Build();

        var config = new Mock<IEncryptedConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns<string>(key => root.GetSection(key));
        config.Setup(c => c[It.IsAny<string>()]).Returns<string>(key => root[key]);

        if (withSection) context.Items["section"] = root.GetSection("queries:upload");
        context.Items["route"] = "upload";
        var services = new ServiceCollection();
        services.AddScoped<TempFilesTracker>();
        context.RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider;

        return new ParametersBuilder(
            new HttpContextAccessor { HttpContext = context },
            config.Object,
            NullLogger<ParametersBuilder>.Instance);
    }

    /// <summary>Runs the builder, then deletes any decoded temp files, as the end of a request would.</summary>
    private static async Task<(List<Com.H.Data.Common.DbQueryParams>? Params, ObjectResult? Error)> RunAsync(
        DefaultHttpContext context, bool withSection = true)
    {
        try
        {
            return await Build(context, withSection).GetParamsOrErrorAsync("test-error-code");
        }
        finally
        {
            context.RequestServices.GetRequiredService<TempFilesTracker>().Dispose();
        }
    }

    private static DefaultHttpContext JsonRequest(string json)
    {
        var context = new DefaultHttpContext();
        context.Items["content_type"] = "application/json";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context;
    }

    private static DefaultHttpContext FormRequest(IFormFeature formFeature)
    {
        var context = new DefaultHttpContext();
        context.Items["content_type"] = "multipart/form-data";
        context.Request.Method = "POST";
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Body = new MemoryStream();
        context.Features.Set(formFeature);
        return context;
    }

    private static IFormFeature Form(string metadata, params string[] fileNames)
    {
        var files = new FormFileCollection();
        foreach (var name in fileNames)
            files.Add(new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("x")), 0, 1, "file", name));
        return new FormFeature(new FormCollection(
            new Dictionary<string, StringValues> { ["attachments"] = metadata }, files));
    }

    private static IFormFeature FormThatThrows(Exception ex)
    {
        var feature = new Mock<IFormFeature>();
        feature.SetupGet(f => f.HasFormContentType).Returns(true);
        feature.Setup(f => f.ReadFormAsync(It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        return feature.Object;
    }

    private static string Message(ObjectResult result)
        => (string)result.Value!.GetType().GetProperty("message")!.GetValue(result.Value)!;

    private static void AssertRefused(
        (List<Com.H.Data.Common.DbQueryParams>? Params, ObjectResult? Error) result, int status, string expectedInMessage)
    {
        Assert.Null(result.Params);
        Assert.NotNull(result.Error);
        Assert.Equal(status, result.Error!.StatusCode);
        Assert.Contains(expectedInMessage, Message(result.Error));
    }

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
        AssertRefused(await RunAsync(FormRequest(Form("""[{"name":"run.exe"}]""", "run.exe"))), 400, "not permitted");
    }

    [Fact]
    public async Task MalformedMultipartMetadata_Is400InsteadOfBeingDropped()
    {
        AssertRefused(await RunAsync(FormRequest(Form("not json", "a.txt"))), 400, "must be a JSON array");
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

    // ── file name rules and server-side failures ─────────────────────────────────────────────────

    [Fact]
    public void FileNameThatIsNotValidUnicode_IsAValidationError()
    {
        var ex = Assert.Throws<RequestValidationException>(() => ParametersBuilder.ValidateAndGetNormalizeFileName("a\uD800.txt"));
        Assert.Contains("not valid Unicode", ex.Message);
    }

    [Fact]
    public async Task ServerSideFailure_IsJson500WithTheErrorCode()
    {
        // No route section in the request: a setup problem, not something the caller did.
        AssertRefused(await RunAsync(JsonRequest("""{"a":1}"""), withSection: false), 500, "test-error-code");
    }
}
