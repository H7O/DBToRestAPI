using DBToRestAPI.Controllers;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
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
    private readonly Lock _reloadLock = new();
    private readonly RateLimitPolicyResolver _rateLimitResolver;

    private static readonly Regex _routeParamRegex = new(
        @"\{\{(?<name>[^}]+)\}\}",
        RegexOptions.Compiled);

    public OpenApiDocumentBuilder(
        IEncryptedConfiguration configuration,
        ILogger<OpenApiDocumentBuilder> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _rateLimitResolver = new RateLimitPolicyResolver(configuration);
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
        _reloadLock.Enter();
        try
        {
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
            _reloadLock.Exit();
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

        // Mandatory parameters, split exactly as Step6 splits them
        var mandatoryParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in section.GetMandatoryParameters() ?? [])
            mandatoryParams.Add(p);

        // Non-path mandatory params
        var nonPathMandatory = mandatoryParams.Where(p => !pathParams.Contains(p)).ToList();

        // Response info
        var successCode = section.GetValue<string>("success_status_code") ?? "200";
        var responseStructure = ApiController.ResolveResponseStructure(section.GetValue<string>("response_structure"));

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
        // A download is never cached: <cache> on it is ignored (CacheService).
        var hasCache = section.GetSection("cache").Exists() && responseStructure != "file";
        // Read as the controller reads them: a count query answers its envelope, whatever the
        // response structure, and the route's root_node, else the global one, wraps the answer.
        var hasCountQuery = !string.IsNullOrWhiteSpace(section.GetSection("count_query").Value);
        var rootNode = section.GetValue<string?>("root_node") ?? _configuration.GetValue<string?>("root_node");
        if (string.IsNullOrWhiteSpace(rootNode))
            rootNode = null;

        // An upload route has a files field, its own or the global one, as ParametersBuilder reads it.
        var filesField = FileSetting(section, "files_json_field_or_form_field_name");
        var upload = filesField == null
            ? null
            : new UploadFields(
                filesField,
                FileSetting(section, "filename_field_in_payload") ?? "name",
                FileSetting(section, "base64_content_field_in_payload") ?? "base64_content");

        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description))
            descriptionParts.Add(description);
        if (!string.IsNullOrWhiteSpace(host))
            descriptionParts.Add($"Host: `{host}`");
        if (hasCache)
            descriptionParts.Add("Response is cached.");
        if (hasCountQuery)
            descriptionParts.Add("Supports pagination with count.");

        // Rate limit, resolved exactly as Step4bRateLimiting resolves it (endpoint → global → default).
        var rateLimit = _rateLimitResolver.Resolve(section);
        if (rateLimit is not null)
            descriptionParts.Add($"Rate limited: {rateLimit.MaxRequests} requests per {rateLimit.WindowSeconds} s per {RateLimitScopeLabel(rateLimit.Per)}.");
        var fullDescription = descriptionParts.Count > 0 ? string.Join(" | ", descriptionParts) : null;

        // Build response schema
        var responseContent = BuildResponseContent(responseStructure, responseSchemaStr, hasCountQuery, rootNode);

        var pathItem = new Dictionary<string, object>();
        foreach (var verb in verbs)
        {
            var operation = BuildOperation(
                verb, summary, fullDescription, tags,
                pathParams, nonPathMandatory, upload,
                successCode, responseContent,
                usesApiKey, usesBearer, rateLimit);
            pathItem[verb] = operation;
        }

        return (openApiPath, pathItem, usesApiKey, usesBearer);
    }

    /// <summary>The field names an upload route's request uses.</summary>
    private sealed record UploadFields(string FilesField, string NameField, string ContentField);

    /// <summary>A file_management setting: the route's, else the global one, else null.</summary>
    private string? FileSetting(IConfigurationSection section, string name)
    {
        var value = section.GetValue<string>("file_management:" + name);
        if (string.IsNullOrWhiteSpace(value))
            value = _configuration.GetValue<string>("file_management:" + name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Dictionary<string, object> BuildOperation(
        string verb,
        string summary,
        string? description,
        List<string> tags,
        HashSet<string> pathParams,
        List<string> nonPathMandatory,
        UploadFields? upload,
        string successCode,
        Dictionary<string, object>? responseContent,
        bool usesApiKey,
        bool usesBearer,
        RateLimitPolicy? rateLimit)
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

        // Request body for body verbs. An upload route takes a multipart form (browsers, large
        // files) or JSON with base64 content (09-file-uploads.md, request formats).
        if (isBodyVerb && (nonPathMandatory.Count > 0 || upload != null))
        {
            var content = new Dictionary<string, object>();
            if (upload != null)
                content["multipart/form-data"] = new Dictionary<string, object>
                {
                    ["schema"] = BodySchema(nonPathMandatory, MultipartUploadProperties(upload, nonPathMandatory))
                };
            content["application/json"] = new Dictionary<string, object>
            {
                ["schema"] = BodySchema(nonPathMandatory, upload == null ? new() : JsonUploadProperties(upload))
            };

            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = nonPathMandatory.Count > 0,
                ["content"] = content
            };
        }

        // Responses
        var successResponse = new Dictionary<string, object>
        {
            ["description"] = "Success"
        };
        if (responseContent != null)
            successResponse["content"] = responseContent;

        var responses = new Dictionary<string, object>
        {
            [successCode] = successResponse,
            ["default"] = new Dictionary<string, object>
            {
                ["description"] = "Error response",
                ["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object>
                    {
                        // The body every error is answered with (errors.md).
                        ["schema"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["required"] = new List<object> { "success", "message" },
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["success"] = new Dictionary<string, object> { ["type"] = "boolean" },
                                ["message"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["error_number"] = new Dictionary<string, object>
                                {
                                    ["type"] = "integer",
                                    ["description"] = "The status, when the query raised the error itself (50000 + status)"
                                }
                            }
                        }
                    }
                }
            }
        };

        if (rateLimit is not null)
            responses["429"] = BuildRateLimitResponse(rateLimit);

        operation["responses"] = responses;

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

    /// <summary>
    /// A request body's schema: the mandatory parameters as strings, then the upload's own fields,
    /// which replace a mandatory parameter of the same name.
    /// </summary>
    private static Dictionary<string, object> BodySchema(List<string> nonPathMandatory, Dictionary<string, object> uploadProperties)
    {
        var properties = new Dictionary<string, object>();
        foreach (var param in nonPathMandatory)
            properties[param] = new Dictionary<string, object> { ["type"] = "string" };
        foreach (var (name, property) in uploadProperties)
            properties[name] = property;

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (nonPathMandatory.Count > 0)
            schema["required"] = nonPathMandatory;
        return schema;
    }

    /// <summary>
    /// A multipart upload: the files field holds the entries as a JSON string, and each new file is
    /// a part of its own, matched to its entry by its file name.
    /// </summary>
    private static Dictionary<string, object> MultipartUploadProperties(UploadFields upload, List<string> nonPathMandatory)
    {
        var example = JsonSerializer.Serialize(new[] { new Dictionary<string, string> { [upload.NameField] = "report.pdf" } });
        // The engine doesn't read a part's field name, so the parts get one no other field uses.
        var partsName = new[] { "files", "file", "file_parts" }.First(name =>
            !name.Equals(upload.FilesField, StringComparison.OrdinalIgnoreCase)
            && !nonPathMandatory.Contains(name, StringComparer.OrdinalIgnoreCase));
        return new Dictionary<string, object>
        {
            [upload.FilesField] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = $"A JSON array of the file entries, such as {example}. A new file's entry has a part whose "
                    + $"file name equals its `{upload.NameField}`; an entry without a part stands for a file already stored."
            },
            [partsName] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string", ["format"] = "binary" },
                ["description"] = "The new files, one part each. A part's field name isn't read: its file name matches it to its entry."
            }
        };
    }

    /// <summary>A JSON upload: each new file's entry carries its content in base64.</summary>
    private static Dictionary<string, object> JsonUploadProperties(UploadFields upload) => new()
    {
        [upload.FilesField] = new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = $"The file entries. A new file's entry carries its content in `{upload.ContentField}`; "
                + "an entry without it stands for a file already stored.",
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    [upload.NameField] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "The file's name, with its extension"
                    },
                    [upload.ContentField] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["format"] = "byte",
                        ["description"] = "The file's content in base64"
                    }
                },
                ["required"] = new List<object> { upload.NameField }
            }
        }
    };

    /// <summary>
    /// The 429 a rate-limited operation returns: the Retry-After header (the full window, "at least
    /// this long") and the same { success, message, retry_after_seconds } body the middleware writes.
    /// </summary>
    private static Dictionary<string, object> BuildRateLimitResponse(RateLimitPolicy rateLimit)
    {
        return new Dictionary<string, object>
        {
            ["description"] = $"Too many requests: more than {rateLimit.MaxRequests} in {rateLimit.WindowSeconds} seconds per {RateLimitScopeLabel(rateLimit.Per)}. Wait the number of seconds given in Retry-After before retrying.",
            ["headers"] = new Dictionary<string, object>
            {
                ["Retry-After"] = new Dictionary<string, object>
                {
                    ["description"] = "Seconds to wait before retrying (the full window).",
                    ["schema"] = new Dictionary<string, object> { ["type"] = "integer" }
                }
            },
            ["content"] = new Dictionary<string, object>
            {
                ["application/json"] = new Dictionary<string, object>
                {
                    ["schema"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["success"] = new Dictionary<string, object> { ["type"] = "boolean", ["example"] = false },
                            ["message"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["retry_after_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["example"] = rateLimit.WindowSeconds }
                        }
                    }
                }
            }
        };
    }

    private static string RateLimitScopeLabel(RateLimitPer per) => per switch
    {
        RateLimitPer.Ip => "client IP",
        RateLimitPer.Endpoint => "endpoint (shared by all callers)",
        _ => "caller"
    };

    private static Dictionary<string, object>? BuildResponseContent(
        string? responseStructure,
        string? responseSchemaStr,
        bool hasCountQuery,
        string? rootNode)
    {
        // File download — binary stream. A count query answers its envelope instead.
        if (responseStructure == "file" && !hasCountQuery)
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

        // The answer under root_node. Without `array` or a count query, a result of no row is
        // the wrapper with null inside.
        if (rootNode != null)
        {
            if (responseStructure != "array" && !hasCountQuery && schema is Dictionary<string, object> answer)
                answer["nullable"] = true;
            schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object> { [rootNode] = schema },
                ["required"] = new List<object> { rootNode }
            };
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

        var arraySchema = new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = itemSchema
        };

        if (responseStructure == "array")
            return arraySchema;

        // The default (no tag, `auto`, or `single`, which is read as `auto`): one row answers the
        // row itself, several rows an array of them. anyOf rather than oneOf, so an open item
        // schema such as {} that matches both stays valid.
        if (responseStructure == "auto")
        {
            return new Dictionary<string, object>
            {
                ["anyOf"] = new object[] { itemSchema, arraySchema }
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
