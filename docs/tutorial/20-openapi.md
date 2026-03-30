# 20 — OpenAPI & Swagger UI

Your API now has endpoints, authentication, file handling, webhooks, and
settings variables.  In this tutorial you'll expose it all through an
auto-generated **OpenAPI 3.0 spec** with a built-in **Swagger UI** — no
packages to install, no code to write.

---

## What You'll Get

| URL | Content |
|-----|---------|
| `/openapi.json` | Machine-readable OpenAPI 3.0.3 JSON |
| `/swagger.json` | Same spec (legacy alias) |
| `/swagger` | Interactive Swagger UI |
| `/swagger/index.html` | Same UI (alternate URL) |
| `/api-docs` | Same UI (alternate URL) |

Everything rebuilds automatically when you change config files, and all URLs
return 404 when OpenAPI is not enabled (secure by default).

---

## Step 1: Enable Globally

Add an `<openapi>` section to `config/settings.xml`:

```xml
<settings>
  <!-- ... existing configuration ... -->

  <openapi>
    <enabled>true</enabled>
  </openapi>
</settings>
```

Save the file and open `/swagger` in your browser — you should see the Swagger
UI with every endpoint listed automatically.

> **That's it.** Every `<route>`, `<verb>`, `<mandatory_parameters>`,
> `<response_structure>`, and security tag you've already configured is
> auto-discovered and mapped into the spec.

---

## Step 2: Customise the Spec Metadata

Give your API a proper title and version:

```xml
<openapi>
  <enabled>true</enabled>
  <title>Contacts API</title>
  <version>2.0.0</version>
  <description>Tutorial API built with DBToRestAPI</description>
</openapi>
```

Reload `/swagger` — the header now shows your title and version.

---

## Step 3: Enrich a Specific Endpoint

Auto-discovery gives you a working spec, but you can make it richer by adding
an `<openapi>` child node to any endpoint.  Let's enrich the "create contact"
endpoint from Tutorial 02:

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,email</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <query><![CDATA[
    INSERT INTO contacts (name, email)
    VALUES ({{name}}, {{email}})
    RETURNING id, name, email, created_at;
  ]]></query>
  <openapi>
    <summary>Create a contact</summary>
    <description>Adds a new contact to the database and returns the created record.</description>
    <tags>contacts</tags>
    <response_schema><![CDATA[
      {
        "type": "object",
        "properties": {
          "id":         { "type": "integer" },
          "name":       { "type": "string" },
          "email":      { "type": "string", "format": "email" },
          "created_at": { "type": "string", "format": "date-time" }
        }
      }
    ]]></response_schema>
  </openapi>
</create_contact>
```

Reload `/swagger` — the endpoint now shows a human-friendly summary, a
description, and a typed response schema instead of generic `object`.

### Available Enrichment Tags

| Tag | Purpose | Default |
|-----|---------|---------|
| `<summary>` | Short label shown in the UI | XML node name (e.g. `create_contact` → `create contact`) |
| `<description>` | Longer explanation | Auto-generated from route |
| `<tags>` | Comma-separated grouping | First route segment |
| `<response_schema>` | JSON Schema for success response | Generic object/array |

---

## Step 4: Selective Exposure

You may not want every endpoint in the spec.  There are two approaches:

### Approach A — Expose all, exclude some

Enable globally, then disable specific endpoints:

```xml
<!-- settings.xml -->
<openapi>
  <enabled>true</enabled>
</openapi>
```

```xml
<!-- In an internal-only endpoint -->
<internal_health_check>
  <route>internal/health</route>
  <verb>GET</verb>
  <query>SELECT 1 AS ok;</query>
  <openapi>
    <enabled>false</enabled>
  </openapi>
</internal_health_check>
```

### Approach B — Expose only specific endpoints

Leave the global `<enabled>` off (the default) and opt in per endpoint:

```xml
<get_products>
  <route>products</route>
  <verb>GET</verb>
  <query>SELECT * FROM products;</query>
  <openapi>
    <enabled>true</enabled>
    <summary>List products</summary>
    <tags>catalog</tags>
  </openapi>
</get_products>
```

Only endpoints with `<openapi><enabled>true</enabled></openapi>` appear.

### Visibility Quick Reference

| Global `enabled` | Endpoint `enabled` | Included? |
|---|---|---|
| `true` | not set | Yes |
| `true` | `true` | Yes |
| `true` | `false` | **No** (excluded) |
| `false` (default) | not set | No |
| `false` (default) | `true` | **Yes** (selective) |
| `false` (default) | `false` | No |

---

## Step 5: Try It Out

1. Open `/swagger` in your browser.
2. Expand an endpoint and click **Try it out**.
3. Fill in the parameters and click **Execute**.
4. The Swagger UI shows the response directly — useful for quick testing.

If your endpoint requires an API key, click the **Authorize** button in the
top-right corner and enter your `x-api-key` value.  For JWT-protected
endpoints, paste a valid Bearer token.

---

## What Gets Auto-Discovered

You don't need to add `<openapi>` enrichment for basic functionality.  The
spec builder reads your existing tags automatically:

| Your Tag | Becomes in the Spec |
|----------|-------------------|
| `<route>users/{{id}}</route>` | Path `/users/{id}`, with `id` as a path parameter |
| `<verb>GET,POST</verb>` | Separate operations per verb |
| `<mandatory_parameters>` | Required params (query for GET, body for POST/PUT/PATCH) |
| `<success_status_code>201</success_status_code>` | Response status code |
| `<response_structure>array</response_structure>` | Array response schema |
| `<response_structure>file</response_structure>` | Binary download |
| `<api_keys_collections>` | API key security scheme |
| `<authorize>` | Bearer JWT security scheme |
| `<count_query>` | Pagination envelope (`{ count, data }`) |
| `<host>` / `<cache>` | Noted in operation description |

---

## Summary

In this tutorial you learned:

- How to enable OpenAPI with a single `<enabled>true</enabled>` tag
- How to customise the spec title, version, and description
- How to enrich endpoints with summary, description, tags, and response schemas
- How to selectively expose or hide endpoints
- How to use the built-in Swagger UI to explore and test your API

All of this is driven by configuration — no code, no packages, no build steps.

---

**Next:** [Production & Deployment →](21-production.md)

**[Back to Tutorial Index](index.md)**
