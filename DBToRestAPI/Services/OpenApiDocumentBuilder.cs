using Com.H.Threading;
using DBToRestAPI.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DBToRestAPI.Services;

/// <summary>
/// Builds an OpenAPI 3.0.3 JSON specification from the configured query endpoints.
/// Monitors the "queries" configuration section via ChangeToken and rebuilds the
/// cached document automatically when endpoints change.
///
/// Visibility rules (secure by default):
///   - Global openapi:enabled = true  → all endpoints included unless locally disabled
///   - Global openapi:enabled = false → only endpoints with local openapi:enabled = true
///   - If no endpoints are visible → spec is empty, middleware returns 404
///
/// Per-endpoint enrichment tags (summary, description, tags, response_schema) live
/// under each endpoint's &lt;openapi&gt; child node.
/// </summary>
public class OpenApiDocumentBuilder
{
    private byte[] _cachedDocument = [];
    private byte[] _cachedSwaggerUiHtml = [];
    private bool _enabled;
    private readonly IEncryptedConfiguration _configuration;
    private readonly ILogger<OpenApiDocumentBuilder> _logger;
    private readonly AtomicGate _reloadingGate = new();

    private static readonly Regex _routeParamRegex = new(
        @"\{\{(?<name>[^}]+)\}\}",
        RegexOptions.Compiled);

    public OpenApiDocumentBuilder(
        IEncryptedConfiguration configuration,
        ILogger<OpenApiDocumentBuilder> logger)
    {
        _configuration = configuration;
        _logger = logger;
        Rebuild();
        ChangeToken.OnChange(
            () => _configuration.GetReloadToken(),
            Rebuild);
    }

    /// <summary>
    /// Returns the cached OpenAPI JSON document as a byte array.
    /// Empty if disabled or no endpoints configured.
    /// </summary>
    public byte[] GetDocument() => _cachedDocument;

    /// <summary>
    /// Returns the cached Swagger UI HTML page as a byte array.
    /// Empty if disabled.
    /// </summary>
    public byte[] GetSwaggerUiHtml() => _cachedSwaggerUiHtml;

    /// <summary>
    /// Whether OpenAPI is enabled in the current configuration.
    /// </summary>
    public bool IsEnabled => _enabled;

    private void Rebuild()
    {
        try
        {
            if (!_reloadingGate.TryOpen()) return;

            var globalEnabled = string.Equals(
                _configuration.GetSection("openapi")?.GetValue<string>("enabled"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            // Check if any endpoint has local openapi:enabled = true
            var anyLocalEnabled = false;
            if (!globalEnabled)
            {
                var querySections = _configuration.GetSection("queries");
                if (querySections != null && querySections.Exists())
                {
                    foreach (var qs in querySections.GetChildren())
                    {
                        foreach (var ep in ExpandDuplicateXmlSections(qs))
                        {
                            if (string.Equals(
                                ep.GetSection("openapi")?.GetValue<string>("enabled"),
                                "true",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                anyLocalEnabled = true;
                                break;
                            }
                        }
                        if (anyLocalEnabled) break;
                    }
                }
            }

            _enabled = globalEnabled || anyLocalEnabled;

            if (!_enabled)
            {
                _cachedDocument = [];
                _cachedSwaggerUiHtml = [];
                return;
            }

            var doc = BuildDocument(globalEnabled);
            _cachedDocument = JsonSerializer.SerializeToUtf8Bytes(doc,
                new JsonSerializerOptions { WriteIndented = true });

            var title = _configuration.GetSection("openapi")?.GetValue<string>("title")
                ?? "DBToRestAPI";
            _cachedSwaggerUiHtml = System.Text.Encoding.UTF8.GetBytes(BuildSwaggerUiHtml(title));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild OpenAPI document — keeping previous version");
        }
        finally
        {
            _reloadingGate.TryClose();
        }
    }
    private static string BuildSwaggerUiHtml(string title)
    {
        var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
        return
$$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{{encodedTitle}} - Swagger UI</title>
  <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css">
  <style>
    html { box-sizing: border-box; overflow-y: scroll; }
    *, *::before, *::after { box-sizing: inherit; }
    body { margin: 0; background: #fafafa; }
  </style>
</head>
<body>
  <div id="swagger-ui"></div>
  <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
  <script>
    SwaggerUIBundle({
      url: '/openapi.json',
      dom_id: '#swagger-ui',
      deepLinking: true,
      presets: [
        SwaggerUIBundle.presets.apis,
        SwaggerUIBundle.SwaggerUIStandalonePreset
      ],
      layout: 'BaseLayout'
    });
  </script>
</body>
</html>
""";
    }
    private Dictionary<string, object> BuildDocument(bool globalEnabled)
    {
        var openApiSection = _configuration.GetSection("openapi");
        var title = openApiSection?.GetValue<string>("title") ?? "DBToRestAPI";
        var version = openApiSection?.GetValue<string>("version") ?? "1.0.0";
        var description = openApiSection?.GetValue<string>("description") ?? "Auto-generated API specification";

        var paths = new Dictionary<string, object>();
        var hasApiKey = false;
        var hasBearer = false;

        var querySections = _configuration.GetSection("queries");
        if (querySections != null && querySections.Exists())
        {
            foreach (var querySection in querySections.GetChildren())
            {
                foreach (var endpointSection in ExpandDuplicateXmlSections(querySection))
                {
                    // Per-endpoint visibility check
                    var localOpenApi = endpointSection.GetSection("openapi");
                    var localEnabledStr = localOpenApi?.GetValue<string>("enabled");

                    if (globalEnabled)
                    {
                        // Global on: skip only if explicitly disabled locally
                        if (string.Equals(localEnabledStr, "false", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    else
                    {
                        // Global off: include only if explicitly enabled locally
                        if (!string.Equals(localEnabledStr, "true", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    var route = endpointSection.GetValue<string>("route")
                        ?? (int.TryParse(endpointSection.Key, out _) ? querySection.Key : endpointSection.Key);
                    if (string.IsNullOrWhiteSpace(route)) continue;

                    var result = BuildPathItem(endpointSection, route, querySection.Key);
                    if (result == null) continue;

                    var (openApiPath, pathItem, usesApiKey, usesBearer) = result.Value;
                    hasApiKey |= usesApiKey;
                    hasBearer |= usesBearer;

                    if (paths.ContainsKey(openApiPath))
                    {
                        // Merge operations into existing path
                        var existing = (Dictionary<string, object>)paths[openApiPath];
                        foreach (var kvp in pathItem)
                            existing.TryAdd(kvp.Key, kvp.Value);
                    }
                    else
                    {
                        paths[openApiPath] = pathItem;
                    }
                }
            }
        }

        var doc = new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = title,
                ["version"] = version,
                ["description"] = description
            },
            ["paths"] = paths
        };

        // Build components/securitySchemes only if actually used
        if (hasApiKey || hasBearer)
        {
            var schemes = new Dictionary<string, object>();
            if (hasApiKey)
            {
                schemes["ApiKeyAuth"] = new Dictionary<string, object>
                {
                    ["type"] = "apiKey",
                    ["in"] = "header",
                    ["name"] = "x-api-key"
                };
            }
            if (hasBearer)
            {
                schemes["BearerAuth"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                    ["bearerFormat"] = "JWT"
                };
            }
            doc["components"] = new Dictionary<string, object>
            {
                ["securitySchemes"] = schemes
            };
        }

        return doc;
    }

    private (string openApiPath, Dictionary<string, object> pathItem, bool usesApiKey, bool usesBearer)?
        BuildPathItem(IConfigurationSection section, string route, string parentKey)
    {
        // Convert {{param}} to {param} for OpenAPI
        var openApiPath = "/" + _routeParamRegex.Replace(
            route.Trim('/'),
            m => "{" + m.Groups["name"].Value + "}");

        // Extract path parameter names from route
        var pathParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in _routeParamRegex.Matches(route))
            pathParams.Add(m.Groups["name"].Value);

        // Parse verbs
        var verbString = section.GetValue<string>("verb");
        var verbs = new List<string>();
        if (!string.IsNullOrWhiteSpace(verbString))
        {
            verbs.AddRange(verbString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => v.ToLowerInvariant()));
        }
        else
        {
            verbs.Add("get");
            verbs.Add("post");
            verbs.Add("put");
            verbs.Add("delete");
            verbs.Add("patch");
        }

        // Parse mandatory parameters
        var mandatoryStr = section.GetValue<string>("mandatory_parameters");
        var mandatoryParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(mandatoryStr))
        {
            foreach (var p in mandatoryStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mandatoryParams.Add(p);
        }

        // Non-path mandatory params
        var nonPathMandatory = mandatoryParams.Where(p => !pathParams.Contains(p)).ToList();

        // Response info
        var successCode = section.GetValue<string>("success_status_code") ?? "200";
        var responseStructure = section.GetValue<string>("response_structure")?.ToLowerInvariant();

        // Security
        var apiKeysCollection = section.GetValue<string>("api_keys_collections");
        var authorize = section.GetSection("authorize");
        var usesApiKey = !string.IsNullOrWhiteSpace(apiKeysCollection);
        var usesBearer = authorize != null && authorize.Exists();

        // Optional enrichment tags — read from the local <openapi> child node
        var localOpenApi = section.GetSection("openapi");
        var summary = localOpenApi?.GetValue<string>("summary");
        var description = localOpenApi?.GetValue<string>("description");
        var tagsStr = localOpenApi?.GetValue<string>("tags");
        var responseSchemaStr = localOpenApi?.GetValue<string>("response_schema");

        // Defaults
        var nodeName = int.TryParse(section.Key, out _) ? parentKey : section.Key;
        summary ??= nodeName.Replace('_', ' ');

        List<string> tags;
        if (!string.IsNullOrWhiteSpace(tagsStr))
        {
            tags = tagsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else
        {
            var firstSegment = route.Trim('/').Split('/').FirstOrDefault() ?? nodeName;
            // Don't use a route parameter as tag name
            if (_routeParamRegex.IsMatch(firstSegment))
                firstSegment = nodeName;
            tags = [firstSegment];
        }

        // Host, cache, count_query hints for description
        var host = section.GetValue<string>("host");
        var hasCache = section.GetSection("cache").Exists();
        var hasCountQuery = section.GetSection("count_query").Exists()
            || !string.IsNullOrWhiteSpace(section.GetValue<string>("count_query"));

        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description))
            descriptionParts.Add(description);
        if (!string.IsNullOrWhiteSpace(host))
            descriptionParts.Add($"Host: `{host}`");
        if (hasCache)
            descriptionParts.Add("Response is cached.");
        if (hasCountQuery)
            descriptionParts.Add("Supports pagination with count.");
        var fullDescription = descriptionParts.Count > 0 ? string.Join(" | ", descriptionParts) : null;

        // Build response schema
        var responseContent = BuildResponseContent(responseStructure, responseSchemaStr, hasCountQuery);

        var pathItem = new Dictionary<string, object>();
        foreach (var verb in verbs)
        {
            var operation = BuildOperation(
                verb, summary, fullDescription, tags,
                pathParams, nonPathMandatory,
                successCode, responseContent,
                usesApiKey, usesBearer);
            pathItem[verb] = operation;
        }

        return (openApiPath, pathItem, usesApiKey, usesBearer);
    }

    private static Dictionary<string, object> BuildOperation(
        string verb,
        string summary,
        string? description,
        List<string> tags,
        HashSet<string> pathParams,
        List<string> nonPathMandatory,
        string successCode,
        Dictionary<string, object>? responseContent,
        bool usesApiKey,
        bool usesBearer)
    {
        var operation = new Dictionary<string, object>
        {
            ["summary"] = summary,
            ["tags"] = tags
        };

        if (description != null)
            operation["description"] = description;

        // Parameters (path + query/body)
        var parameters = new List<object>();

        foreach (var param in pathParams)
        {
            parameters.Add(new Dictionary<string, object>
            {
                ["name"] = param,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new Dictionary<string, object> { ["type"] = "string" }
            });
        }

        // For GET/DELETE: non-path params go in query
        // For POST/PUT/PATCH: non-path params go in requestBody
        var isBodyVerb = verb is "post" or "put" or "patch";

        if (!isBodyVerb && nonPathMandatory.Count > 0)
        {
            foreach (var param in nonPathMandatory)
            {
                parameters.Add(new Dictionary<string, object>
                {
                    ["name"] = param,
                    ["in"] = "query",
                    ["required"] = true,
                    ["schema"] = new Dictionary<string, object> { ["type"] = "string" }
                });
            }
        }

        if (parameters.Count > 0)
            operation["parameters"] = parameters;

        // Request body for body verbs
        if (isBodyVerb && nonPathMandatory.Count > 0)
        {
            var properties = new Dictionary<string, object>();
            foreach (var param in nonPathMandatory)
            {
                properties[param] = new Dictionary<string, object> { ["type"] = "string" };
            }

            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object>
                    {
                        ["schema"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = properties,
                            ["required"] = nonPathMandatory
                        }
                    }
                }
            };
        }

        // Responses
        var successResponse = new Dictionary<string, object>
        {
            ["description"] = "Success"
        };
        if (responseContent != null)
            successResponse["content"] = responseContent;

        operation["responses"] = new Dictionary<string, object>
        {
            [successCode] = successResponse,
            ["default"] = new Dictionary<string, object>
            {
                ["description"] = "Error response",
                ["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object>
                    {
                        ["schema"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["error_message"] = new Dictionary<string, object> { ["type"] = "string" }
                            }
                        }
                    }
                }
            }
        };

        // Security
        var security = new List<object>();
        if (usesApiKey)
            security.Add(new Dictionary<string, object> { ["ApiKeyAuth"] = Array.Empty<string>() });
        if (usesBearer)
            security.Add(new Dictionary<string, object> { ["BearerAuth"] = Array.Empty<string>() });
        if (security.Count > 0)
            operation["security"] = security;

        return operation;
    }

    private static Dictionary<string, object>? BuildResponseContent(
        string? responseStructure,
        string? responseSchemaStr,
        bool hasCountQuery)
    {
        // File download — binary stream
        if (responseStructure == "file")
        {
            return new Dictionary<string, object>
            {
                ["application/octet-stream"] = new Dictionary<string, object>
                {
                    ["schema"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["format"] = "binary"
                    }
                }
            };
        }

        // Try to parse user-provided JSON Schema
        Dictionary<string, object>? userSchema = null;
        if (!string.IsNullOrWhiteSpace(responseSchemaStr))
        {
            try
            {
                userSchema = JsonSerializer.Deserialize<Dictionary<string, object>>(responseSchemaStr);
            }
            catch
            {
                // Invalid JSON Schema — ignore silently, fall through to defaults
            }
        }

        object schema;
        if (userSchema != null)
        {
            schema = WrapSchemaForStructure(userSchema, responseStructure, hasCountQuery);
        }
        else
        {
            // Default generic schema
            var itemSchema = new Dictionary<string, object> { ["type"] = "object" };
            schema = WrapSchemaForStructure(itemSchema, responseStructure, hasCountQuery);
        }

        return new Dictionary<string, object>
        {
            ["application/json"] = new Dictionary<string, object>
            {
                ["schema"] = schema
            }
        };
    }

    private static object WrapSchemaForStructure(
        object itemSchema,
        string? responseStructure,
        bool hasCountQuery)
    {
        // Pagination envelope
        if (hasCountQuery)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["success"] = new Dictionary<string, object> { ["type"] = "boolean" },
                    ["count"] = new Dictionary<string, object> { ["type"] = "integer" },
                    ["data"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = itemSchema
                    }
                },
                ["required"] = new[] { "success", "count", "data" }
            };
        }

        if (responseStructure == "single")
            return itemSchema;

        // Default to array
        if (responseStructure is "array" or null or "auto")
        {
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = itemSchema
            };
        }

        return itemSchema;
    }

    /// <summary>
    /// Detects when an IConfiguration section represents a group of duplicate XML sibling elements.
    /// Same logic as QueryRouteResolver.ExpandDuplicateXmlSections.
    /// </summary>
    private static IEnumerable<IConfigurationSection> ExpandDuplicateXmlSections(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();

        if (children.Count > 0
            && children.All(c => int.TryParse(c.Key, out _))
            && children.Any(c => c.GetSection("query").Exists()))
        {
            foreach (var child in children)
                yield return child;
        }
        else
        {
            yield return section;
        }
    }
}
