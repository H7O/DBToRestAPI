namespace DBToRestAPI.Middlewares;

using DBToRestAPI.Services;

/// <summary>
/// Short-circuits requests to OpenAPI spec URLs (/openapi.json, /swagger.json)
/// and Swagger UI URLs (/swagger, /swagger/index.html, /api-docs), returning
/// cached content from OpenApiDocumentBuilder.
/// Registered before Step1ServiceTypeChecks so these requests bypass the full pipeline.
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
        if (path == null)
        {
            await _next(context);
            return;
        }

        // OpenAPI JSON spec
        if (path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/swagger.json", StringComparison.OrdinalIgnoreCase))
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

        // Swagger UI
        if (path.Equals("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/swagger/index.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api-docs", StringComparison.OrdinalIgnoreCase))
        {
            if (!_builder.IsEnabled)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var html = _builder.GetSwaggerUiHtml();
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = html.Length;
            await context.Response.Body.WriteAsync(html);
            return;
        }

        await _next(context);
    }
}
