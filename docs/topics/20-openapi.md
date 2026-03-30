# OpenAPI / Swagger Specification

DbToRestAPI can auto-generate an [OpenAPI 3.0.3](https://spec.openapis.org/oas/v3.0.3) specification from your configured endpoints. The spec is built entirely from your existing XML configuration — no additional tooling, no code, no external dependencies.

The spec rebuilds automatically on every config change (hot-reload) and is served at well-known URLs.

## URLs

| URL | Content |
|-----|---------|
| `/openapi.json` | OpenAPI 3.0.3 JSON spec |
| `/swagger.json` | Same content (alias for legacy tool compatibility) |
| `/swagger` | Built-in Swagger UI (interactive browser) |
| `/swagger/index.html` | Same Swagger UI (alternate URL) |
| `/api-docs` | Same Swagger UI (alternate URL) |

All URLs return 404 when OpenAPI is not enabled. The Swagger UI loads assets from [unpkg.com CDN](https://unpkg.com/swagger-ui-dist@5/) — no files are bundled in the binary.

## Enabling OpenAPI

**Secure by default** — the spec is not served until you explicitly opt in.

### Option 1: Enable globally (all endpoints)

Add to `settings.xml`:

```xml
<openapi>
  <enabled>true</enabled>
</openapi>
```

All endpoints appear in the spec. To exclude specific endpoints, add `<openapi><enabled>false</enabled></openapi>` inside those endpoints.

### Option 2: Enable selectively (specific endpoints only)

Leave the global setting off (default). Add `<openapi><enabled>true</enabled></openapi>` to each endpoint you want to expose:

```xml
<get_users>
  <route>users</route>
  <verb>GET</verb>
  <query><![CDATA[ SELECT * FROM users; ]]></query>
  <openapi>
    <enabled>true</enabled>
    <summary>List all users</summary>
  </openapi>
</get_users>
```

Only endpoints with local `<openapi><enabled>true</enabled>` appear in the spec.

### Visibility Rules

| Global `enabled` | Endpoint `enabled` | Included? |
|---|---|---|
| `true` | not set | Yes |
| `true` | `true` | Yes |
| `true` | `false` | No (excluded) |
| `false` (default) | not set | No |
| `false` (default) | `true` | Yes (selective) |
| `false` (default) | `false` | No |

## What's Auto-Discovered

The following are inferred automatically from your existing endpoint tags — no extra configuration needed:

| XML Tag | OpenAPI Mapping |
|---------|----------------|
| `<route>users/{{id}}</route>` | Path `/users/{id}` with `id` as a path parameter |
| `<verb>GET,POST</verb>` | Separate operations per verb |
| `<mandatory_parameters>name,email</mandatory_parameters>` | Required parameters — path params auto-detected, rest as query (GET) or request body (POST/PUT/PATCH) |
| `<success_status_code>201</success_status_code>` | Response status code (default: `200`) |
| `<response_structure>array</response_structure>` | Response schema: array of objects |
| `<response_structure>single</response_structure>` | Response schema: single object |
| `<response_structure>file</response_structure>` | Binary download (`application/octet-stream`) |
| `<api_keys_collections>vendors</api_keys_collections>` | API key security scheme (`x-api-key` header) |
| `<authorize>` | Bearer JWT security scheme |
| `<host>` | Noted in operation description |
| `<cache>` | "Response is cached" in description |
| `<count_query>` | Response wrapped in `{ count, data }` pagination envelope |

## Per-Endpoint Enrichment

For richer specs, add an `<openapi>` child node to any endpoint:

```xml
<create_order>
  <route>orders</route>
  <verb>POST</verb>
  <mandatory_parameters>product_id,quantity</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <query><![CDATA[
    INSERT INTO orders (product_id, quantity, created_by)
    VALUES ({{product_id}}, {{quantity}}, {auth{sub}})
    RETURNING id, product_id, quantity, status, created_at;
  ]]></query>
  <openapi>
    <enabled>true</enabled>
    <summary>Create an order</summary>
    <description>Places a new order for the authenticated user</description>
    <tags>orders,commerce</tags>
    <response_schema><![CDATA[
      {
        "type": "object",
        "properties": {
          "id": { "type": "string", "format": "uuid" },
          "product_id": { "type": "string" },
          "quantity": { "type": "integer" },
          "status": { "type": "string", "enum": ["pending", "confirmed", "shipped"] },
          "created_at": { "type": "string", "format": "date-time" }
        }
      }
    ]]></response_schema>
  </openapi>
</create_order>
```

| Tag | Purpose | Default if omitted |
|-----|---------|-------------------|
| `<enabled>` | Per-endpoint visibility override | Inherits from global setting |
| `<summary>` | Short operation label | XML node name (e.g., `create_order` → `create order`) |
| `<description>` | Longer explanation | Auto-generated from route hints |
| `<tags>` | Comma-separated grouping tags | First route segment (e.g., `orders`) |
| `<response_schema>` | JSON Schema for success response | Generic object/array based on `<response_structure>` |

## Global Settings

Customize the spec metadata in `settings.xml`:

```xml
<openapi>
  <enabled>true</enabled>
  <title>My Awesome API</title>
  <version>2.0.0</version>
  <description>Public API for my application</description>
</openapi>
```

| Setting | Default |
|---------|---------|
| `title` | `DBToRestAPI` |
| `version` | `1.0.0` |
| `description` | `Auto-generated API specification` |

## Parameter Placement

Parameters are placed according to standard REST conventions:

| Condition | Placement |
|-----------|-----------|
| Appears in route as `{{param}}` | `in: path` |
| GET/DELETE, non-path parameter | `in: query` |
| POST/PUT/PATCH, non-path parameters | `requestBody` (JSON) |

## Error Responses

All endpoints include a default error response schema matching DbToRestAPI's SQL `THROW` error format:

```json
{
  "error_message": "Not found"
}
```

## Security Schemes

Security schemes are added to `components/securitySchemes` only when actually used:

| Trigger | Scheme |
|---------|--------|
| Any endpoint has `<api_keys_collections>` | `ApiKeyAuth` — API key in `x-api-key` header |
| Any endpoint has `<authorize>` | `BearerAuth` — HTTP bearer with JWT format |

## Architecture Notes

- **Hot-reload** — the spec rebuilds automatically when any config file changes
- **Short-circuit** — `/openapi.json`, `/swagger.json`, `/swagger`, `/swagger/index.html`, and `/api-docs` are all handled by early middleware before the main pipeline, so there's zero overhead on normal API requests
- **No external dependencies** — built with `System.Text.Json`, no NuGet packages added
- **Thread-safe** — uses the same `AtomicGate` pattern as the route resolver
