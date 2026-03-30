using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace DBToRestAPI.Tests;

public class OpenApiDocumentBuilderTests
{
    /// <summary>
    /// Helper: build an OpenApiDocumentBuilder from a dictionary config
    /// that simulates what the XML configuration provider would produce.
    /// </summary>
    private static OpenApiDocumentBuilder CreateBuilder(Dictionary<string, string?> configData)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Wrap IConfigurationRoot so it implements IEncryptedConfiguration
        var mock = new Mock<IEncryptedConfiguration>();

        // Forward all IConfiguration members to the real config
        mock.Setup(m => m.GetSection(It.IsAny<string>()))
            .Returns<string>(key => configuration.GetSection(key));
        mock.Setup(m => m.GetChildren())
            .Returns(() => configuration.GetChildren());
        mock.Setup(m => m[It.IsAny<string>()])
            .Returns<string>(key => configuration[key]);
        mock.Setup(m => m.GetReloadToken())
            .Returns(() => configuration.GetReloadToken());

        var logger = new Mock<ILogger<OpenApiDocumentBuilder>>();
        return new OpenApiDocumentBuilder(mock.Object, logger.Object);
    }

    private static JsonElement ParseDoc(OpenApiDocumentBuilder builder)
    {
        var bytes = builder.GetDocument();
        Assert.NotEmpty(bytes);
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    [Fact]
    public void DisabledByDefault_ReturnsEmptyDocument()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        Assert.False(builder.IsEnabled);
        Assert.Empty(builder.GetDocument());
    }

    [Fact]
    public void EnabledTrue_ReturnsValidOpenApiDoc()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 'world'"
        });

        Assert.True(builder.IsEnabled);
        var doc = ParseDoc(builder);
        Assert.Equal("3.0.3", doc.GetProperty("openapi").GetString());
        Assert.True(doc.GetProperty("paths").TryGetProperty("/hello", out _));
    }

    [Fact]
    public void RouteWithVerb_GeneratesCorrectOperation()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:users:route"] = "users",
            ["queries:users:verb"] = "GET",
            ["queries:users:query"] = "SELECT * FROM users"
        });

        var doc = ParseDoc(builder);
        var path = doc.GetProperty("paths").GetProperty("/users");
        Assert.True(path.TryGetProperty("get", out _));
        // Should not have other verbs since only GET was specified
        Assert.False(path.TryGetProperty("post", out _));
    }

    [Fact]
    public void PathParameters_ExtractedFromRoute()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:get_user:route"] = "users/{{id}}",
            ["queries:get_user:verb"] = "GET",
            ["queries:get_user:query"] = "SELECT * FROM users WHERE id = {{id}}"
        });

        var doc = ParseDoc(builder);
        var path = doc.GetProperty("paths").GetProperty("/users/{id}");
        var getOp = path.GetProperty("get");
        var parameters = getOp.GetProperty("parameters");
        Assert.Equal(1, parameters.GetArrayLength());
        var param = parameters[0];
        Assert.Equal("id", param.GetProperty("name").GetString());
        Assert.Equal("path", param.GetProperty("in").GetString());
        Assert.True(param.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void MandatoryParameters_QueryForGet_BodyForPost()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:create_user:route"] = "users",
            ["queries:create_user:verb"] = "GET,POST",
            ["queries:create_user:mandatory_parameters"] = "name,email",
            ["queries:create_user:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var path = doc.GetProperty("paths").GetProperty("/users");

        // GET: mandatory params in query
        var getOp = path.GetProperty("get");
        var getParams = getOp.GetProperty("parameters");
        Assert.Equal(2, getParams.GetArrayLength());
        Assert.Equal("query", getParams[0].GetProperty("in").GetString());

        // POST: mandatory params in requestBody
        var postOp = path.GetProperty("post");
        Assert.True(postOp.TryGetProperty("requestBody", out var body));
        var schema = body.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("name", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("email", out _));
    }

    [Fact]
    public void SuccessStatusCode_Mapped()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:create:route"] = "items",
            ["queries:create:verb"] = "POST",
            ["queries:create:success_status_code"] = "201",
            ["queries:create:query"] = "INSERT INTO items VALUES(1)"
        });

        var doc = ParseDoc(builder);
        var postOp = doc.GetProperty("paths").GetProperty("/items").GetProperty("post");
        var responses = postOp.GetProperty("responses");
        Assert.True(responses.TryGetProperty("201", out _));
    }

    [Fact]
    public void ResponseStructure_Array()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:list:route"] = "items",
            ["queries:list:verb"] = "GET",
            ["queries:list:response_structure"] = "array",
            ["queries:list:query"] = "SELECT * FROM items"
        });

        var doc = ParseDoc(builder);
        var schema = doc.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("array", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void ResponseStructure_Single()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:detail:route"] = "items/{{id}}",
            ["queries:detail:verb"] = "GET",
            ["queries:detail:response_structure"] = "single",
            ["queries:detail:query"] = "SELECT * FROM items WHERE id={{id}}"
        });

        var doc = ParseDoc(builder);
        var schema = doc.GetProperty("paths")
            .GetProperty("/items/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void ResponseStructure_File()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:download:route"] = "files/{{id}}",
            ["queries:download:verb"] = "GET",
            ["queries:download:response_structure"] = "file",
            ["queries:download:query"] = "SELECT data FROM files WHERE id={{id}}"
        });

        var doc = ParseDoc(builder);
        var content = doc.GetProperty("paths")
            .GetProperty("/files/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content");
        Assert.True(content.TryGetProperty("application/octet-stream", out var octet));
        Assert.Equal("binary", octet.GetProperty("schema").GetProperty("format").GetString());
    }

    [Fact]
    public void ApiKeysSecurity_AddedWhenPresent()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:secure:route"] = "secure",
            ["queries:secure:verb"] = "GET",
            ["queries:secure:api_keys_collections"] = "vendors",
            ["queries:secure:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/secure").GetProperty("get");
        var security = getOp.GetProperty("security");
        Assert.Equal(1, security.GetArrayLength());

        // Components should have ApiKeyAuth scheme
        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("ApiKeyAuth", out _));
    }

    [Fact]
    public void BearerSecurity_AddedWhenAuthorizePresent()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:protected:route"] = "protected",
            ["queries:protected:verb"] = "GET",
            ["queries:protected:authorize:provider"] = "my_provider",
            ["queries:protected:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/protected").GetProperty("get");
        var security = getOp.GetProperty("security");
        Assert.Equal(1, security.GetArrayLength());

        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("BearerAuth", out _));
    }

    [Fact]
    public void EnrichmentTags_SummaryDescriptionTags()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:greet:route"] = "greet",
            ["queries:greet:verb"] = "GET",
            ["queries:greet:openapi:summary"] = "Say hello",
            ["queries:greet:openapi:description"] = "Returns a greeting",
            ["queries:greet:openapi:tags"] = "greetings,public",
            ["queries:greet:query"] = "SELECT 'hi'"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/greet").GetProperty("get");
        Assert.Equal("Say hello", getOp.GetProperty("summary").GetString());
        Assert.Contains("Returns a greeting", getOp.GetProperty("description").GetString());
        var tags = getOp.GetProperty("tags");
        Assert.Equal(2, tags.GetArrayLength());
        Assert.Equal("greetings", tags[0].GetString());
        Assert.Equal("public", tags[1].GetString());
    }

    [Fact]
    public void ResponseSchema_CustomJsonSchema()
    {
        var schema = """{"type":"object","properties":{"message":{"type":"string"}}}""";
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:msg:route"] = "message",
            ["queries:msg:verb"] = "GET",
            ["queries:msg:response_structure"] = "single",
            ["queries:msg:openapi:response_schema"] = schema,
            ["queries:msg:query"] = "SELECT 'hi' AS message"
        });

        var doc = ParseDoc(builder);
        var respSchema = doc.GetProperty("paths")
            .GetProperty("/message")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.True(respSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("message", out _));
    }

    [Fact]
    public void PaginationEnvelope_WhenCountQueryPresent()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:paged:route"] = "paged",
            ["queries:paged:verb"] = "GET",
            ["queries:paged:count_query"] = "SELECT COUNT(*) FROM items",
            ["queries:paged:query"] = "SELECT * FROM items"
        });

        var doc = ParseDoc(builder);
        var schema = doc.GetProperty("paths")
            .GetProperty("/paged")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("count", out _));
        Assert.True(props.TryGetProperty("data", out var data));
        Assert.Equal("array", data.GetProperty("type").GetString());
    }

    [Fact]
    public void GlobalSettings_TitleVersionDescription()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["openapi:title"] = "My Cool API",
            ["openapi:version"] = "2.5.0",
            ["openapi:description"] = "A very cool API",
            ["queries:test:route"] = "test",
            ["queries:test:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var info = doc.GetProperty("info");
        Assert.Equal("My Cool API", info.GetProperty("title").GetString());
        Assert.Equal("2.5.0", info.GetProperty("version").GetString());
        Assert.Equal("A very cool API", info.GetProperty("description").GetString());
    }

    [Fact]
    public void MultipleEndpoints_MultiplePathsGenerated()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:users:route"] = "users",
            ["queries:users:verb"] = "GET",
            ["queries:users:query"] = "SELECT * FROM users",
            ["queries:orders:route"] = "orders",
            ["queries:orders:verb"] = "POST",
            ["queries:orders:query"] = "INSERT INTO orders VALUES(1)"
        });

        var doc = ParseDoc(builder);
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/users", out _));
        Assert.True(paths.TryGetProperty("/orders", out _));
    }

    [Fact]
    public void NoVerb_DefaultsToAllVerbs()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:any:route"] = "anything",
            ["queries:any:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var path = doc.GetProperty("paths").GetProperty("/anything");
        Assert.True(path.TryGetProperty("get", out _));
        Assert.True(path.TryGetProperty("post", out _));
        Assert.True(path.TryGetProperty("put", out _));
        Assert.True(path.TryGetProperty("delete", out _));
        Assert.True(path.TryGetProperty("patch", out _));
    }

    [Fact]
    public void DefaultErrorResponse_AlwaysPresent()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:test:route"] = "test",
            ["queries:test:verb"] = "GET",
            ["queries:test:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var responses = doc.GetProperty("paths")
            .GetProperty("/test")
            .GetProperty("get")
            .GetProperty("responses");
        Assert.True(responses.TryGetProperty("default", out var def));
        var errorSchema = def.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("properties");
        Assert.True(errorSchema.TryGetProperty("error_message", out _));
    }

    [Fact]
    public void HostTag_IncludedInDescription()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:hosted:route"] = "hosted",
            ["queries:hosted:verb"] = "GET",
            ["queries:hosted:host"] = "api.example.com",
            ["queries:hosted:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/hosted").GetProperty("get");
        var description = getOp.GetProperty("description").GetString();
        Assert.Contains("api.example.com", description);
    }

    [Fact]
    public void CacheTag_NotedInDescription()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:cached:route"] = "cached",
            ["queries:cached:verb"] = "GET",
            ["queries:cached:cache:duration"] = "60",
            ["queries:cached:query"] = "SELECT 1"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/cached").GetProperty("get");
        var description = getOp.GetProperty("description").GetString();
        Assert.Contains("cached", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedPathAndMandatoryParams_CorrectPlacement()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:update:route"] = "users/{{id}}",
            ["queries:update:verb"] = "PUT",
            ["queries:update:mandatory_parameters"] = "id,name,email",
            ["queries:update:query"] = "UPDATE users SET name={{name}}, email={{email}} WHERE id={{id}}"
        });

        var doc = ParseDoc(builder);
        var putOp = doc.GetProperty("paths").GetProperty("/users/{id}").GetProperty("put");

        // Path param: id
        var parameters = putOp.GetProperty("parameters");
        Assert.Equal(1, parameters.GetArrayLength());
        Assert.Equal("id", parameters[0].GetProperty("name").GetString());
        Assert.Equal("path", parameters[0].GetProperty("in").GetString());

        // Body params: name, email (not id — it's a path param)
        var bodySchema = putOp.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.True(bodySchema.GetProperty("properties").TryGetProperty("name", out _));
        Assert.True(bodySchema.GetProperty("properties").TryGetProperty("email", out _));
        Assert.False(bodySchema.GetProperty("properties").TryGetProperty("id", out _));
    }

    [Fact]
    public void DefaultSummary_UsesNodeName()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:get_all_users:route"] = "users",
            ["queries:get_all_users:verb"] = "GET",
            ["queries:get_all_users:query"] = "SELECT * FROM users"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/users").GetProperty("get");
        Assert.Equal("get all users", getOp.GetProperty("summary").GetString());
    }

    [Fact]
    public void DefaultTags_UsesFirstRouteSegment()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:get_user:route"] = "api/users/{{id}}",
            ["queries:get_user:verb"] = "GET",
            ["queries:get_user:query"] = "SELECT * FROM users WHERE id={{id}}"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/api/users/{id}").GetProperty("get");
        var tags = getOp.GetProperty("tags");
        Assert.Equal(1, tags.GetArrayLength());
        Assert.Equal("api", tags[0].GetString());
    }

    // ── Visibility rule tests ──

    [Fact]
    public void GlobalOff_LocalEnabled_OnlyThatEndpointIncluded()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            // Global off (default)
            ["queries:public_api:route"] = "public",
            ["queries:public_api:verb"] = "GET",
            ["queries:public_api:openapi:enabled"] = "true",
            ["queries:public_api:query"] = "SELECT 1",
            ["queries:private_api:route"] = "private",
            ["queries:private_api:verb"] = "GET",
            ["queries:private_api:query"] = "SELECT 2"
        });

        Assert.True(builder.IsEnabled);
        var doc = ParseDoc(builder);
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/public", out _));
        Assert.False(paths.TryGetProperty("/private", out _));
    }

    [Fact]
    public void GlobalOn_LocalDisabled_ExcludesThatEndpoint()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:visible:route"] = "visible",
            ["queries:visible:verb"] = "GET",
            ["queries:visible:query"] = "SELECT 1",
            ["queries:hidden:route"] = "hidden",
            ["queries:hidden:verb"] = "GET",
            ["queries:hidden:openapi:enabled"] = "false",
            ["queries:hidden:query"] = "SELECT 2"
        });

        Assert.True(builder.IsEnabled);
        var doc = ParseDoc(builder);
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/visible", out _));
        Assert.False(paths.TryGetProperty("/hidden", out _));
    }

    [Fact]
    public void GlobalOff_NoLocalEnabled_DisabledEntirely()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["queries:a:route"] = "a",
            ["queries:a:query"] = "SELECT 1",
            ["queries:b:route"] = "b",
            ["queries:b:query"] = "SELECT 2"
        });

        Assert.False(builder.IsEnabled);
        Assert.Empty(builder.GetDocument());
    }

    [Fact]
    public void GlobalOff_MultipleLocalEnabled_AllIncluded()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["queries:a:route"] = "alpha",
            ["queries:a:verb"] = "GET",
            ["queries:a:openapi:enabled"] = "true",
            ["queries:a:query"] = "SELECT 1",
            ["queries:b:route"] = "beta",
            ["queries:b:verb"] = "GET",
            ["queries:b:openapi:enabled"] = "true",
            ["queries:b:query"] = "SELECT 2",
            ["queries:c:route"] = "gamma",
            ["queries:c:verb"] = "GET",
            ["queries:c:query"] = "SELECT 3"
        });

        Assert.True(builder.IsEnabled);
        var doc = ParseDoc(builder);
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/alpha", out _));
        Assert.True(paths.TryGetProperty("/beta", out _));
        Assert.False(paths.TryGetProperty("/gamma", out _));
    }

    [Fact]
    public void LocalEnrichment_UnderOpenApiNode()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:api:route"] = "items",
            ["queries:api:verb"] = "GET",
            ["queries:api:openapi:summary"] = "List items",
            ["queries:api:openapi:tags"] = "inventory,public",
            ["queries:api:query"] = "SELECT * FROM items"
        });

        var doc = ParseDoc(builder);
        var getOp = doc.GetProperty("paths").GetProperty("/items").GetProperty("get");
        Assert.Equal("List items", getOp.GetProperty("summary").GetString());
        var tags = getOp.GetProperty("tags");
        Assert.Equal("inventory", tags[0].GetString());
        Assert.Equal("public", tags[1].GetString());
    }

    // --- Swagger UI HTML tests ---

    [Fact]
    public void SwaggerUiHtml_EmptyWhenDisabled()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        Assert.False(builder.IsEnabled);
        Assert.Empty(builder.GetSwaggerUiHtml());
    }

    [Fact]
    public void SwaggerUiHtml_ReturnedWhenEnabled()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        var html = builder.GetSwaggerUiHtml();
        Assert.NotEmpty(html);
        var htmlStr = System.Text.Encoding.UTF8.GetString(html);
        Assert.Contains("swagger-ui", htmlStr);
        Assert.Contains("/openapi.json", htmlStr);
    }

    [Fact]
    public void SwaggerUiHtml_ContainsConfiguredTitle()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["openapi:title"] = "My Cool API",
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        var htmlStr = System.Text.Encoding.UTF8.GetString(builder.GetSwaggerUiHtml());
        Assert.Contains("My Cool API", htmlStr);
    }

    [Fact]
    public void SwaggerUiHtml_DefaultsTitleWhenNotConfigured()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        var htmlStr = System.Text.Encoding.UTF8.GetString(builder.GetSwaggerUiHtml());
        Assert.Contains("DBToRestAPI", htmlStr);
    }

    [Fact]
    public void SwaggerUiHtml_HtmlEncodesTitle()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["openapi:enabled"] = "true",
            ["openapi:title"] = "<script>alert(1)</script>",
            ["queries:hello:route"] = "hello",
            ["queries:hello:query"] = "SELECT 1"
        });

        var htmlStr = System.Text.Encoding.UTF8.GetString(builder.GetSwaggerUiHtml());
        Assert.DoesNotContain("<script>alert(1)</script>", htmlStr);
        Assert.Contains("&lt;script&gt;", htmlStr);
    }
}
