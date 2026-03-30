namespace DBToRestAPI.Middlewares;

using DBToRestAPI.Services;

/// <summary>
/// Short-circuits requests to /openapi.json and /swagger.json, returning the
/// cached OpenAPI document from OpenApiDocumentBuilder.
/// Registered before Step1ServiceTypeChecks so spec requests bypass the full pipeline.
/// </summary>
public class OpenApiMiddleware
{
    private readonly RequestDelegate _next;
    private readonly OpenApiDocumentBuilder _builder;

    public OpenApiMiddleware(RequestDelegate next, OpenApiDocumentBuilder builder)
    {
        _next = next;
        _builder = builder;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path != null
            && (path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!_builder.IsEnabled)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var doc = _builder.GetDocument();
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = doc.Length;
            await context.Response.Body.WriteAsync(doc);
            return;
        }

        await _next(context);
    }
}
