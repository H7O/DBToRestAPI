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
  <response_structure>array</response_structure>
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
| No `<response_structure>` | Response schema: one object or an array of them (`anyOf`) |
| `<response_structure>array</response_structure>` | Response schema: array of objects |
| `<response_structure>file</response_structure>` | Binary download (`application/octet-stream`), unless the route has a `<count_query>`, which answers its envelope instead |
| `<api_keys_collections>vendors</api_keys_collections>` | API key security scheme (`x-api-key` header) |
| `<authorize>` | Bearer JWT security scheme |
| `<host>` | Noted in operation description |
| `<cache>` | "Response is cached" in description, except on a download route, which is never cached |
| `<rate_limit>` | `429` response with the `Retry-After` header and the `{ success, message, retry_after_seconds }` body; the effective limit noted in the description (see [Rate Limiting](18-rate-limiting.md)) |
| `<count_query>` | Response wrapped in `{ success, count, data }` pagination envelope, on a `file` route too |
| `<root_node>items</root_node>`, on the route or in `settings.xml` | Response wrapped in `{ "items": ... }`. Without `array` or a `<count_query>`, the value inside may be `null`: a query that returns no row answers `{"items": null}`. A download isn't wrapped |
| `<files_json_field_or_form_field_name>`, in the route's `<file_management>` or in `file_management.xml` | Request body of POST, PUT and PATCH as `multipart/form-data` (that field holds the file entries as a JSON string, and each new file is a part of its own) or `application/json` (the entries hold each new file's content in base64), with the entry field names `filename_field_in_payload` and `base64_content_field_in_payload` set ([uploads](09-file-uploads.md#request-formats)) |

Set `<response_structure>array</response_structure>` on list routes. Without it, the schema says one object or an array of them, which a generated client sees as a union type.

## Per-Endpoint Enrichment

For richer specs, add an `<openapi>` child node to any endpoint:

```xml
<create_order>
  <route>orders</route>
  <verb>POST</verb>
  <authorize><provider>my_provider</provider></authorize>
  <mandatory_parameters>product_id,quantity</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <query><![CDATA[
    INSERT INTO orders (product_id, quantity, created_by)
    VALUES ({{product_id}}, {{quantity}}, {auth{user_id}})
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
| `<response_schema>` | JSON Schema of one row. It is wrapped as the route answers: an array of it with `array`, one of it or an array of it (`anyOf`) without the tag, and the `data` items with a `count_query`, inside the `root_node` wrapper when there is one | Generic object, wrapped the same way |

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
| POST/PUT/PATCH, non-path parameters | `requestBody` (JSON, and a multipart form on an upload route) |

## Error Responses

Every operation gets a `default` error response whose schema is the body the engine answers every error with: `success` and `message` always, and `error_number` when the query raised the error itself. A query that raises `50404` (for example `THROW 50404, 'Not found', 1;` on SQL Server) returns:

```json
{
  "success": false,
  "message": "Not found",
  "error_number": 404
}
```

See [Errors](../reference/errors.md) for every status and body.

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
- **Thread-safe** — a rebuild runs under a lock, as the route resolver's does, so two never overlap
