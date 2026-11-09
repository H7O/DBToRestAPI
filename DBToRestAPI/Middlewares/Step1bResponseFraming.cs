using DBToRestAPI.Services;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace DBToRestAPI.Middlewares;

/// <summary>
/// Adds a route's <c>response_prefix</c> / <c>response_suffix</c> bytes around its answer body (see
/// <see cref="ResponseFramingIndex"/>). Runs right after <see cref="Step1ServiceTypeChecks"/>, which puts the
/// route's section in <c>context.Items["section"]</c>, so the answers of every later step are covered: errors,
/// gateway answers, query results and files.
/// <para>
/// When no route uses them, this step only reads one flag and passes the request on, without an async
/// state machine or an allocation. Answers stay plain in the cache: the bytes are added on the way out.
/// </para>
/// </summary>
public class Step1bResponseFraming(
    RequestDelegate next,
    ILogger<Step1bResponseFraming> logger,
    ResponseFramingIndex framingIndex)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<Step1bResponseFraming> _logger = logger;
    private readonly ResponseFramingIndex _framingIndex = framingIndex;

    public Task InvokeAsync(HttpContext context)
    {
        if (!_framingIndex.InUse) return _next(context);
        var framing = _framingIndex.For(context.Items.TryGetValue("section", out var s) ? s as IConfigurationSection : null);
        if (framing.IsNone) return _next(context);
        return InvokeFramedAsync(context, framing);
    }

    private async Task InvokeFramedAsync(HttpContext context, ResponseFraming framing)
    {
        if (framing.Error is not null)
        {
            _logger.LogError("{Time}: {Message}. Answering 500 for `{Route}`.",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), LogText.Escape(framing.Error),
                context.Items.TryGetValue("route", out var r) ? r : "unknown");
            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(new { success = false, message = "This endpoint's response_prefix / response_suffix setting is invalid." })
                {
                    StatusCode = 500
                });
            return;
        }

        // The answer goes out whole and uncompressed, so the bytes can be added to it: a gateway target
        // that got these would answer a part of it (206) or gzip it, and either passes through unframed.
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");
        context.Request.Headers.Remove("Accept-Encoding");

        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        var body = new ResponseFramingBody(original, context.Response, framing);
        context.Features.Set<IHttpResponseBodyFeature>(body);
        context.Response.OnStarting(static state =>
        {
            ((ResponseFramingBody)state).AdjustContentLength();
            return Task.CompletedTask;
        }, body);
        try
        {
            await _next(context);
            await body.FinishAsync();
        }
        finally
        {
            context.Features.Set(original);
        }
    }
}
