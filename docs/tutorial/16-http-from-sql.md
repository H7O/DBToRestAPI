# Embedded HTTP Calls from SQL

This is one of the most powerful features in DBToRestAPI — the ability to make HTTP requests to external APIs **directly from within your SQL queries**. This lets you enrich database data with external services, validate inputs against third-party APIs, or orchestrate microservice calls, all without writing application code.

## The `{http{...}http}` Syntax

Embed an HTTP call in your SQL using this marker:

```sql
DECLARE @response NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "method": "GET"
  }
}http};
```

**What happens at runtime:**
1. The engine finds all `{http{...}http}` markers in your query
2. It resolves any `{{param}}` placeholders inside them from the incoming request
3. It executes the HTTP requests **before** running the SQL
4. Each marker is replaced with a **parameterized SQL variable** (e.g., `@http_response_1`)
5. The variable contains a **structured JSON string** with `status_code`, `headers`, and `data`
6. The SQL executes with the full HTTP response available as that variable

### Structured Response Format

Every embedded HTTP call — whether it succeeds or fails — produces a JSON string with this shape:

```json
{
  "status_code": 200,
  "headers": {
    "Content-Type": "application/json",
    "X-Request-Id": "abc-123"
  },
  "data": { ... },
  "error": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `status_code` | int | The HTTP status code. `0` if the request never reached the server (network error, DNS failure, timeout). |
| `headers` | object | All response headers (response + content headers combined). |
| `data` | any | The response body — parsed as a JSON object/array if valid JSON, a plain string if not, or `null` if empty. |
| `error` | object\|null | `null` when a server response was received (even 4xx/5xx). Contains `{"message": "..."}` only when `status_code` is `0` — e.g., `"Request timed out after 30 seconds"` or `"Host not found: api.example.com"`. |

This means you **always** get a result back — you never have to guess whether the call failed or why. Check `$.status_code` to decide what to do, and `$.error.message` to understand infrastructure failures. The only exception is when the [`skip`](#skipping-http-calls) property is truthy — in that case the variable receives `NULL` because the call was never made.

> **For DB Admins — how parameters are bound**: The HTTP response is **never** pasted or concatenated into your SQL string. Instead, it works exactly like `sp_executesql` parameter binding:
>
> ```sql
> -- Conceptually, this is what happens under the hood:
> EXEC sp_executesql
>   N'DECLARE @response NVARCHAR(MAX) = @http_response_1; SELECT ...',
>   N'@http_response_1 NVARCHAR(MAX)',
>   @http_response_1 = '{"status_code":200,"headers":{...},"data":{"name":"John","age":30}}';  -- bound, not concatenated
> ```
>
> This means even if an external API returns something malicious like `'; DROP TABLE users; --`, it is treated as a **value**, not as SQL code. The same protection you trust with `sp_executesql` parameters applies here.

## Basic Example: Enrich Data with External API

```xml
<contact_with_weather>
  <route>contacts/{{id}}/weather</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    -- Fetch weather from external API
    DECLARE @weather NVARCHAR(MAX) = {http{
      {
        "url": "https://api.weatherapi.com/v1/current.json?key=YOUR_KEY&q={{city}}",
        "method": "GET"
      }
    }http};

    -- Combine with database data
    DECLARE @id UNIQUEIDENTIFIER = {{id}};

    -- Check if the weather API responded successfully
    IF JSON_VALUE(@weather, '$.status_code') != '200'
    BEGIN
      -- API failed — return contact data without weather
      SELECT c.name, c.phone, NULL AS temperature, 'Unavailable' AS weather
      FROM contacts c WHERE c.id = @id;
      RETURN;
    END

    -- Combine with database data — response body is under $.data
    SELECT 
      c.name,
      c.phone,
      JSON_VALUE(@weather, '$.data.current.temp_c') AS temperature,
      JSON_VALUE(@weather, '$.data.current.condition.text') AS weather
    FROM contacts c
    WHERE c.id = @id;
    ]]>
  </query>
</contact_with_weather>
```

The `{{city}}` parameter inside the HTTP call comes from the original HTTP request — the same parameter injection you've been using throughout the tutorial.

## HTTP Request Configuration

The JSON inside `{http{...}http}` supports these properties:

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `url` | string | Yes | — | Target URL |
| `method` | string | No | `GET` | HTTP method |
| `headers` | object | No | — | Custom headers |
| `body` | object/string | No | — | Request body |
| `timeout` | number | No | 30 | Timeout in seconds |
| `auth` | object | No | — | Authentication config |
| `retry` | object | No | — | Retry policy |
| `skip` | bool/string/number | No | `false` | When truthy (`true`, `"true"`, `"1"`, `"yes"`, non-zero), the call is not executed and the variable receives `NULL` |

## POST with JSON Body

```sql
DECLARE @result NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/validate",
    "method": "POST",
    "headers": {
      "Content-Type": "application/json",
      "X-API-Key": "your-api-key"
    },
    "body": {
      "email": "{{email}}",
      "name": "{{name}}"
    }
  }
}http};
```

## Authentication Options

### Bearer Token

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/secure",
    "auth": {
      "type": "bearer",
      "token": "your-access-token"
    }
  }
}http};
```

### API Key in Header

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "auth": {
      "type": "api_key",
      "key": "X-API-Key",
      "value": "your-api-key",
      "in": "header"
    }
  }
}http};
```

## Practical Example: Email Validation

Validate an email address against an external service before creating a user:

```xml
<create_validated_user>
  <route>users</route>
  <verb>POST</verb>
  <mandatory_parameters>email,name</mandatory_parameters>
  <query>
    <![CDATA[
    -- Step 1: Validate email via external API
    DECLARE @validation NVARCHAR(MAX) = {http{
      {
        "url": "https://api.emailvalidation.com/v1/validate",
        "method": "POST",
        "headers": {
          "Content-Type": "application/json",
          "X-API-Key": "your-validation-key"
        },
        "body": {
          "email": "{{email}}"
        }
      }
    }http};

    -- Step 2: Check the API responded successfully
    DECLARE @status INT = CAST(JSON_VALUE(@validation, '$.status_code') AS INT);
    IF @status = 0 OR @status >= 500
    BEGIN
      THROW 50502, 'Email validation service unavailable', 1;
      RETURN;
    END

    -- Step 3: Check validation result (response body is under $.data)
    IF JSON_VALUE(@validation, '$.data.is_valid') = 'false'
    BEGIN
      THROW 50400, 'Invalid email address', 1;
      RETURN;
    END

    -- Step 4: Create the user
    INSERT INTO users (email, name, email_verified)
    VALUES ({{email}}, {{name}}, 1);

    SELECT 'User created successfully' AS message;
    ]]>
  </query>
</create_validated_user>
```

## Multiple HTTP Calls in One Query

You can embed multiple HTTP calls:

```sql
DECLARE @weather NVARCHAR(MAX) = {http{
  {
    "url": "https://api.weather.com/current?city={{city}}",
    "method": "GET"
  }
}http};

DECLARE @news NVARCHAR(MAX) = {http{
  {
    "url": "https://api.news.com/headlines?country={{country}}",
    "method": "GET"
  }
}http};

SELECT 
  JSON_VALUE(@weather, '$.data.temperature') AS temp,
  JSON_VALUE(@news, '$.data.articles[0].title') AS top_headline,
  JSON_VALUE(@weather, '$.status_code') AS weather_api_status,
  JSON_VALUE(@news, '$.status_code') AS news_api_status;
```

## Parsing the Response

The response body lives under `$.data`. Since external APIs typically return JSON, use SQL Server's JSON functions to dig into it:

```sql
-- First, always check the status
DECLARE @status INT = CAST(JSON_VALUE(@response, '$.status_code') AS INT);

-- Single value from the response body
SELECT JSON_VALUE(@response, '$.data.name') AS name;

-- Nested value
SELECT JSON_VALUE(@response, '$.data.address.city') AS city;

-- Array element
SELECT JSON_VALUE(@response, '$.data.items[0].id') AS first_item_id;

-- Parse array into rows
SELECT * FROM OPENJSON(@response, '$.data.items')
WITH (
  id INT '$.id',
  name NVARCHAR(100) '$.name',
  price DECIMAL(10,2) '$.price'
);

-- Access response headers (e.g., pagination, rate limits)
SELECT JSON_VALUE(@response, '$.headers.X-Total-Count') AS total_items;
SELECT JSON_VALUE(@response, '$.headers.X-RateLimit-Remaining') AS rate_limit_left;
```

## Error Handling

Every embedded HTTP call returns a structured JSON string — you can inspect `$.status_code` to know exactly what happened. The only case where the variable is `NULL` is when the call is [skipped](#skipping-http-calls):

```sql
DECLARE @response NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "timeout": 10
  }
}http};

DECLARE @status INT = CAST(JSON_VALUE(@response, '$.status_code') AS INT);

-- status_code = 0 means the request never reached the server
-- (network error, DNS failure, timeout, etc.)
-- Check $.error.message for the specific reason
IF @status = 0
BEGIN
  DECLARE @err NVARCHAR(500) = JSON_VALUE(@response, '$.error.message');
  -- e.g., "Request timed out after 10 seconds"
  -- or  "Host not found: api.example.com"
  -- or  "Connection refused - the server may be down"
  THROW 50502, @err, 1;
END

-- Handle specific error scenarios
IF @status = 401 OR @status = 403
  THROW 50401, 'External API authentication failed', 1;

IF @status = 429
  THROW 50429, 'External API rate limit exceeded — try again later', 1;

IF @status < 200 OR @status >= 300
  THROW 50502, 'External API returned an error', 1;

-- Success — process the response body
SELECT * FROM OPENJSON(@response, '$.data');
```

### Quick Reference: Status Codes

| `$.status_code` | What it means |
|-----------------|---------------|
| `0` | Request never reached the server (DNS error, timeout, network failure). Check `$.error.message` for details. |
| `200`–`299` | Success — `$.data` has the response body |
| `400`–`499` | Client error — `$.data` may have error details from the server |
| `500`–`599` | Server error — `$.data` may have error details from the server |

You can also read response headers to make smarter decisions:

```sql
-- Check if the server told us to retry later
DECLARE @retry_after NVARCHAR(50) = JSON_VALUE(@response, '$.headers.Retry-After');
```

## Skipping HTTP Calls

Use the `skip` property to conditionally prevent an HTTP call from executing. When truthy, the call is not made and the SQL variable receives `NULL`:

```sql
DECLARE @validation NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/validate",
    "method": "POST",
    "body": { "email": "{{email}}" },
    "skip": "{{skip_validation}}"
  }
}http};

-- NULL = skipped, status_code 0 = infra failure, status_code > 0 = server responded
IF @validation IS NULL
  PRINT 'Validation skipped';
ELSE IF CAST(JSON_VALUE(@validation, '$.status_code') AS INT) = 0
  THROW 50502, JSON_VALUE(@validation, '$.error.message'), 1;
ELSE IF JSON_VALUE(@validation, '$.status_code') = '200'
  PRINT 'Validation passed';
```

Truthy values for `skip`: `true`, `"true"`, `"1"`, `"yes"` (case-insensitive), and non-zero numbers. Since `{{param}}` values resolve as strings, `"skip": "{{should_skip}}"` works naturally — pass `true` or `1` in the request to skip the call.

## Conditional Usage with IF Blocks

All `{http{...}http}` calls execute during pre-processing regardless of SQL logic (unless `skip` is truthy) — because the engine processes them before the SQL even starts running. However, because results are delivered as parameterized SQL variables (not string-replaced), you can use `IF` blocks to control whether the result is actually assigned to your variables:

```sql
DECLARE @emirates_id NVARCHAR(50) = {{emirates_id}};
DECLARE @lookup_result NVARCHAR(MAX) = NULL;

-- The HTTP call fires either way during pre-processing,
-- but the DECLARE + assignment below only executes
-- when the IF condition passes (standard SQL behavior).
IF @emirates_id IS NOT NULL AND @emirates_id != ''
BEGIN
    DECLARE @api_response NVARCHAR(MAX) = {http{
      {
        "url": "https://api.example.com/lookup?id={{emirates_id}}",
        "method": "GET"
      }
    }http};

    -- Only use the result if the API returned 2xx
    IF CAST(JSON_VALUE(@api_response, '$.status_code') AS INT) BETWEEN 200 AND 299
      SET @lookup_result = JSON_VALUE(@api_response, '$.data.name');
END

-- @lookup_result is NULL if emirates_id wasn't provided
SELECT COALESCE(@lookup_result, 'No lookup performed') AS result;
```

This works because the `{http{...}http}` marker becomes a parameter like `@http_response_1` during pre-processing. When the `IF` block's condition is false, the `DECLARE @api_response = @http_response_1` line never executes — exactly the same as any other SQL variable declared inside a conditional block. The HTTP response data is still bound as a parameter, but your SQL logic simply never reads it.

## Security Considerations

- **SQL injection safe** — HTTP responses are delivered as parameterized SQL variables (`@http_response_1`, `@http_response_2`, etc.), not string-replaced into the query. This is the same `sp_executesql`-style parameter binding that DB Admins already trust for preventing SQL injection. Even if an external API returns `'; DROP TABLE users; --`, it is treated as a harmless string value, not executable SQL.
- **Never expose secrets in client-visible responses** — API keys in `{http{...}http}` are server-side only
- **Validate external data** — don't trust external API responses blindly
- **Set timeouts** — prevent your API from hanging if an external service is slow
- **Consider caching** — if the external data doesn't change often

---

### What You Learned

- The `{http{...}http}` syntax for embedded HTTP calls
- The **structured response format** — every call returns `{status_code, headers, data, error}` (or `NULL` when the `skip` property is truthy)
- How to check `$.status_code` to handle success, client errors, server errors, and network failures
- How to read `$.error.message` for infrastructure failure details (timeout, DNS, connection refused, etc.)
- How to access the response body via `$.data` and response headers via `$.headers`
- How HTTP calls execute before the SQL runs, with results delivered via SQL parameterization (`sp_executesql`-style binding, not string replacement)
- That `IF` blocks can control whether the parameterized result is assigned, even though the HTTP call always fires during pre-processing
- Sending GET and POST requests with headers and body
- Authentication options (bearer token, API key)
- Parsing JSON responses with `JSON_VALUE` and `OPENJSON` — all under the `$.data` path
- Granular error handling — inspecting status codes instead of checking for `NULL`
- The `skip` property for conditionally disabling HTTP calls
- No SQL injection risk — responses are bound as parameterized variables, same as `sp_executesql` parameters
- Practical patterns: data enrichment, validation, conditional lookups, multi-service calls

---

**Next:** [Multi-Query Chaining →](17-multi-query.md)

**[Back to Tutorial Index](index.md)**
