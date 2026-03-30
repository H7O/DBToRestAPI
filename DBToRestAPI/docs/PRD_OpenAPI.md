# PRD: Auto-Generated OpenAPI Specification

## Overview

Add automatic OpenAPI 3.0 specification generation to DBToRestAPI. The engine builds the spec from existing XML endpoint configuration — no code, no separate tooling. The spec rebuilds on every config change (hot-reload) and is served at well-known URLs.

## Goals

1. Every configured endpoint appears in the OpenAPI spec automatically
2. Zero mandatory configuration — works out of the box with sensible defaults
3. Optional per-endpoint enrichment tags for richer specs
4. Spec rebuilds on config change (same `ChangeToken.OnChange` pattern as `QueryRouteResolver`)
5. Short-circuit delivery — no middleware pipeline overhead for spec requests

## URLs

| URL | Purpose |
|-----|---------|
| `/openapi.json` | Primary canonical URL |
| `/swagger.json` | Alias for legacy tool compatibility (same content, not redirect) |

## Auto-Discovered (No Extra XML Needed)

The following are inferred from existing endpoint tags:

| XML Tag | OpenAPI Mapping |
|---------|----------------|
| `<route>users/{{id}}</route>` | Path `/users/{id}`, `id` as path parameter |
| `<verb>GET,POST</verb>` | Separate operations per verb on the path |
| `<mandatory_parameters>name,email</mandatory_parameters>` | Required parameters — path params auto-detected from route, remainder as query (GET) or request body (POST/PUT/PATCH) |
| `<success_status_code>201</success_status_code>` | Response code (default: `200`) |
| `<response_structure>array</response_structure>` | Response schema hint: array of objects |
| `<response_structure>file</response_structure>` | Binary file download (`application/octet-stream`) |
| `<response_structure>single</response_structure>` | Single object response |
| `<api_keys_collections>vendors</api_keys_collections>` | API key security scheme (`x-api-key` header) |
| `<authorize>` | Bearer JWT security scheme |
| `<host>www.example.com</host>` | Noted in operation description |
| `<cache>` | Noted in operation description |
| `<count_query>` | Response wraps in `{ count, data }` pagination envelope |

## Per-Endpoint `<openapi>` Node

Enrichment tags and per-endpoint visibility live under a local `<openapi>` child node:

```xml
<hello_world>
  <query><![CDATA[ SELECT 'Hello, ' + {{name}} AS message; ]]></query>
  <openapi>
    <enabled>true</enabled>
    <summary>Say hello</summary>
    <description>Returns a greeting message for the given name</description>
    <tags>greetings,public</tags>
    <response_schema><![CDATA[
      {
        "type": "object",
        "properties": {
          "message": { "type": "string" }
        }
      }
    ]]></response_schema>
  </openapi>
</hello_world>
```

| Tag | OpenAPI Mapping |
|-----|----------------|
| `<enabled>` | Per-endpoint visibility override (see rules below) |
| `<summary>` | Operation summary (short label) |
| `<description>` | Operation description (longer explanation) |
| `<tags>` | Comma-separated tag names for grouping |
| `<response_schema>` | JSON Schema for the success response body |

If omitted:
- `summary` defaults to the XML node name (e.g., `hello_world` → `hello world`)
- `description` is auto-generated from route + verb
- `tags` defaults to the first route segment (e.g., `/users/{{id}}` → `users`)
- Response schema defaults to a generic object/array based on `<response_structure>`

### Visibility Rules

| Global `<openapi><enabled>` | Endpoint `<openapi><enabled>` | Included? |
|-----------------------------|-------------------------------|----------|
| `true` | not set | **Yes** |
| `true` | `true` | **Yes** |
| `true` | `false` | **No** (explicitly excluded) |
| `false` (default) | not set | **No** |
| `false` (default) | `true` | **Yes** (selective exposure) |
| `false` (default) | `false` | **No** |

This allows three usage patterns:
1. **Expose everything** — set global `enabled=true`
2. **Expose selectively** — leave global off, set `enabled=true` on chosen endpoints
3. **Expose everything except some** — set global `enabled=true`, set `enabled=false` on private endpoints

## Global Configuration (Optional)

In `settings.xml`:

```xml
<openapi>
  <title>My API</title>
  <version>1.0.0</version>
  <description>My awesome API</description>
</openapi>
```

If omitted, defaults to:
- `title`: `"DBToRestAPI"`
- `version`: `"1.0.0"`
- `description`: `"Auto-generated API specification"`
- `enabled`: `false` — **secure by default**; the spec is not served until the user explicitly opts in (globally or per-endpoint)

To enable globally (all endpoints):

```xml
<openapi>
  <enabled>true</enabled>
</openapi>
```

To enable selectively (only specific endpoints — global stays off):

```xml
<!-- In the endpoint's config -->
<get_users>
  <route>users</route>
  <query>SELECT * FROM users</query>
  <openapi>
    <enabled>true</enabled>
    <summary>List users</summary>
  </openapi>
</get_users>
```

## Architecture

### OpenApiDocumentBuilder (Singleton Service)

- Monitors config changes via `ChangeToken.OnChange` on the `queries` section (same pattern as `QueryRouteResolver`)
- On change: iterates all endpoint sections, builds OpenAPI 3.0 JSON document, caches as a `byte[]`
- Thread-safe rebuild using `AtomicGate` (same pattern as `QueryRouteResolver`)
- No external dependencies — builds JSON using `System.Text.Json`

### OpenApiMiddleware (Early Middleware)

- Registered **before** `Step1ServiceTypeChecks` in the pipeline
- Matches `/openapi.json` and `/swagger.json` (case-insensitive)
- Returns cached `byte[]` with `Content-Type: application/json`
- Short-circuits — no further middleware processing

### Data Flow

```
XML Config Change
      │
      ▼
ChangeToken fires
      │
      ▼
OpenApiDocumentBuilder.Rebuild()
  ├─ Read all endpoint sections
  ├─ Extract route, verb, params, auth, response info
  ├─ Read optional summary, description, tags, response_schema
  ├─ Build OpenAPI 3.0 JSON
  └─ Cache as byte[]
      │
      ▼
Request to /openapi.json
      │
      ▼
OpenApiMiddleware
  └─ Return cached byte[] (short-circuit)
```

## OpenAPI Output Structure

```json
{
  "openapi": "3.0.3",
  "info": {
    "title": "My API",
    "version": "1.0.0",
    "description": "My awesome API"
  },
  "paths": {
    "/users/{id}": {
      "get": {
        "summary": "get user",
        "tags": ["users"],
        "parameters": [
          { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
        ],
        "responses": {
          "200": {
            "description": "Success",
            "content": {
              "application/json": {
                "schema": { ... }
              }
            }
          }
        },
        "security": [{ "BearerAuth": [] }]
      }
    }
  },
  "components": {
    "securitySchemes": {
      "ApiKeyAuth": {
        "type": "apiKey",
        "in": "header",
        "name": "x-api-key"
      },
      "BearerAuth": {
        "type": "http",
        "scheme": "bearer",
        "bearerFormat": "JWT"
      }
    }
  }
}
```

## Parameter Placement Rules

| Condition | Placement |
|-----------|-----------|
| Parameter appears in route as `{{param}}` | `in: path` |
| GET/DELETE request, non-path parameter | `in: query` |
| POST/PUT/PATCH request, non-path parameters | `requestBody` (JSON object with properties) |

## Error Responses

All endpoints implicitly support error responses via SQL `THROW 50xxx`:

```json
"responses": {
  "200": { "description": "Success", ... },
  "default": {
    "description": "Error response",
    "content": {
      "application/json": {
        "schema": {
          "type": "object",
          "properties": {
            "error_message": { "type": "string" }
          }
        }
      }
    }
  }
}
```

## Phase 2: Swagger UI (Future)

Serve a built-in Swagger UI at `/swagger/index.html`:

- Embed a minimal HTML page that loads Swagger UI from CDN (or bundled)
- Points at `/openapi.json`
- Also rebuilt/cached on config change
- Same short-circuit middleware pattern
- Optional: `/api-docs` as alias

This is a separate follow-up — the OpenAPI JSON spec is fully functional without UI.

## Security Considerations

- The spec exposes route paths and parameter names — this is expected (it's a public API contract)
- Endpoints protected by `api_keys_collections` or `authorize` are marked with security schemes in the spec, so consumers know auth is required
- The `/openapi.json` endpoint itself has no auth — standard practice for API specs
- No SQL content, connection strings, or internal values are exposed

## Non-Goals

- Parsing SQL to infer response schemas (SQL stays opaque)
- Validating `<response_schema>` JSON Schema syntax (pass-through)
- Supporting OpenAPI 3.1 (3.0.3 has widest tool compatibility)
- Request schema inference beyond parameter names

## Testing

Unit tests for `OpenApiDocumentBuilder`:
1. Endpoint with route + verb → correct path + operation
2. Path parameters extracted from `{{param}}` in route
3. Mandatory parameters split correctly (path vs query vs body)
4. Success status code mapped correctly
5. Response structure hints (array, single, file)
6. API key security scheme added when `api_keys_collections` present
7. Bearer security scheme added when `authorize` present
8. Optional enrichment tags (summary, description, tags, response_schema)
9. Pagination envelope when `count_query` present
10. Config change triggers rebuild
11. Multiple endpoints → multiple paths
12. Duplicate XML tag endpoints handled correctly
13. Global openapi settings (title, version, description)
14. Disabled via `<enabled>false</enabled>` → empty/404 response
