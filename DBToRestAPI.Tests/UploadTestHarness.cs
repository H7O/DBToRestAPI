using System.Text;
using System.Text.Json;
using Com.H.Data.Common;
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
/// Builds upload requests (JSON and multipart) for an "upload" route whose files field is
/// <c>attachments</c>, runs them through <see cref="ParametersBuilder.GetParamsOrErrorAsync"/>, and
/// cleans up the decoded temp files afterwards, as the end of a real request would.
/// </summary>
internal static class UploadTestHarness
{
    public const string FilesField = "attachments";

    private static ParametersBuilder Build(DefaultHttpContext context, bool withSection, IDictionary<string, string?>? extraConfig)
    {
        var settings = new Dictionary<string, string?>
        {
            ["queries:upload:file_management:files_json_field_or_form_field_name"] = FilesField,
            ["queries:upload:file_management:stores"] = "primary",
            ["file_management:relative_file_path_structure"] = "{{guid}}/{file{name}}",
            ["file_management:filename_field_in_payload"] = "name",
            ["file_management:base64_content_field_in_payload"] = "content_base64",
            ["file_management:permitted_file_extensions"] = ".txt",
            ["file_management:max_number_of_files"] = "2",
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, string?>())
            settings[key] = value;
        var root = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

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

    /// <summary>
    /// Runs the builder. <paramref name="inspect"/> sees the result while the decoded temp files still
    /// exist; they are deleted afterwards.
    /// </summary>
    public static async Task<(List<DbQueryParams>? Params, ObjectResult? Error)> RunAsync(
        DefaultHttpContext context,
        bool withSection = true,
        IDictionary<string, string?>? extraConfig = null,
        Action<List<DbQueryParams>?>? inspect = null)
    {
        try
        {
            var result = await Build(context, withSection, extraConfig).GetParamsOrErrorAsync("test-error-code");
            inspect?.Invoke(result.Params);
            return result;
        }
        finally
        {
            context.RequestServices.GetRequiredService<TempFilesTracker>().Dispose();
        }
    }

    public static DefaultHttpContext JsonRequest(string json)
    {
        var context = new DefaultHttpContext();
        context.Items["content_type"] = "application/json";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context;
    }

    public static DefaultHttpContext FormRequest(IFormFeature formFeature)
    {
        var context = new DefaultHttpContext();
        context.Items["content_type"] = "multipart/form-data";
        context.Request.Method = "POST";
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Body = new MemoryStream();
        context.Features.Set(formFeature);
        return context;
    }

    /// <summary>A multipart form with the files metadata and one part per (name, content) pair.</summary>
    public static IFormFeature Form(string metadata, params (string Name, string Content)[] parts)
        => FormWithFiles(metadata, parts.Select(p => Part(p.Name, p.Content)).ToArray());

    /// <summary>A file part as a real multipart request carries it, with headers and a content type.</summary>
    public static IFormFile Part(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };
    }

    public static IFormFeature FormWithFiles(string metadata, params IFormFile[] files)
    {
        var collection = new FormFileCollection();
        collection.AddRange(files);
        return new FormFeature(new FormCollection(
            new Dictionary<string, StringValues> { [FilesField] = metadata }, collection));
    }

    public static IFormFeature FormThatThrows(Exception ex)
    {
        var feature = new Mock<IFormFeature>();
        feature.SetupGet(f => f.HasFormContentType).Returns(true);
        feature.Setup(f => f.ReadFormAsync(It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        return feature.Object;
    }

    /// <summary>The file entries the query would receive in the files field.</summary>
    public static List<JsonElement> FileEntries(List<DbQueryParams>? parameters)
    {
        foreach (var p in parameters ?? [])
        {
            if (p.DataModel is not string json) continue;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(FilesField, out var files)
                && files.ValueKind == JsonValueKind.Array)
                return files.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        return [];
    }

    public static string Message(ObjectResult result)
        => (string)result.Value!.GetType().GetProperty("message")!.GetValue(result.Value)!;

    public static void AssertRefused((List<DbQueryParams>? Params, ObjectResult? Error) result, int status, string expectedInMessage)
    {
        Assert.Null(result.Params);
        Assert.NotNull(result.Error);
        Assert.Equal(status, result.Error!.StatusCode);
        Assert.Contains(expectedInMessage, Message(result.Error));
    }
}
