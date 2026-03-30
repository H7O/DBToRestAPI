# Embedded HTTP Calls in SQL Queries

Execute HTTP requests directly from within your SQL queries — enrich database data with external APIs, validate inputs against third-party services, or orchestrate microservice calls without leaving SQL.

## How It Works

Embed HTTP call markers in your SQL queries using the `{http{...}http}` syntax. Processing happens in two phases:

### Phase 1: Pre-processing (before SQL execution)
1. The engine scans the entire query text for all `{http{...}http}` markers
2. **Prepare (sequential):** `{{param}}` placeholders inside each HTTP configuration are resolved from the incoming request parameters (query string, body, route, headers — the engine has access to all of them at this stage). Calls with `skip` set to a truthy value are excluded.
3. **Execute (parallel):** All prepared HTTP calls are fired concurrently via `Task.WhenAll` — the total wait time is the duration of the **slowest** call, not the sum of all calls. Duplicate calls (identical configuration) are deduplicated and executed only once.
4. **Apply (sequential):** Each `{http{...}http}` marker in the query text is replaced with a **SQL parameter name** (e.g., `@http_response_1`, `@http_response_2`), and the corresponding HTTP response is bound to that parameter as a **structured JSON string** via parameterization — **it is never string-interpolated into the SQL**

### Structured Response Format

Every embedded HTTP call produces a JSON string with this shape — regardless of success or failure:

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
| `status_code` | int | HTTP status code (e.g., 200, 404, 500). `0` if the request failed before receiving a response (network error, timeout, DNS failure). |
| `headers` | object | All response headers (including content headers). Multi-value headers are joined with `, `. |
| `data` | any | The response body — parsed as a JSON object/array if valid JSON, a plain string if not, or `null` if the body was empty. |
| `error` | object\|null | `null` when a server response was received (even 4xx/5xx — the server's error details are in `data`). Populated as `{"message": "..."}` only when `status_code` is `0` (infrastructure failures: timeout, DNS, network, SSL, etc.). |

### Phase 2: SQL execution
- The modified query is executed via standard parameterized SQL (similar to `sp_executesql @query, @params, @http_response_1 = '...', @http_response_2 = '...'`)
- HTTP responses are available as regular SQL variables — you can use them in `SELECT`, `INSERT`, `UPDATE`, `IF`, `WHERE`, or any SQL construct
- Use `JSON_VALUE(@var, '$.status_code')` to inspect the HTTP status, `$.headers` for response headers, and `$.data` for the response body
- Because values are parameterized, **there is zero SQL injection risk**, even if the external API returns malicious content

```sql
-- What you write:
DECLARE @response NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "method": "GET"
  }
}http};

-- What actually runs (conceptually):
-- DECLARE @response NVARCHAR(MAX) = @http_response_1;
-- where @http_response_1 = '{"status_code":200,"headers":{...},"data":{...},"error":null}'

-- Check status before using data
IF JSON_VALUE(@response, '$.status_code') != '200'
  THROW 50502, 'External API call failed', 1;

-- Access the response body via $.data
SELECT * FROM OPENJSON(@response, '$.data');
```

> **Key insight for query authors**: All `{http{...}http}` calls fire during Phase 1 regardless of any SQL `IF` conditions — because Phase 1 happens before SQL executes. However, because the result arrives as a parameterized SQL variable (not string replacement), you **can** use SQL `IF` blocks to control whether the result variable is assigned or referenced in your logic. If your `IF` condition is false, the `DECLARE @var = {http{...}http}` inside it simply never runs, so `@var` stays `NULL`. This is the same behavior as any SQL variable declared inside a conditional block. When executed, the response is **always** a structured JSON string — check `JSON_VALUE(@var, '$.status_code')` to determine success or failure. The only case where the variable receives `NULL` is when the call is explicitly skipped via the [`skip`](#skipping-http-calls) property.

> **For LLMs / AI agents**: Do **not** assume that `{http{...}http}` results are string-replaced into the SQL. They are SQL-parameterized. The parameter value is a **structured JSON string** with `status_code`, `headers`, and `data` fields. This means: (1) SQL `IF` guards around the `DECLARE @var = {http{...}http}` assignment **do** control whether the variable receives the value, (2) the HTTP call still fires during pre-processing regardless (unless the `skip` property is truthy — see [Skipping HTTP Calls](#skipping-http-calls)), (3) there is no SQL injection risk from HTTP response content, and (4) the actual API response body is at `$.data`, not at the root level.

## Basic Example

```xml
<get_weather_enriched_locations>
  <route>locations/weather</route>
  <verb>GET</verb>
  <query><![CDATA[
    -- Fetch current weather from external API
    DECLARE @weather NVARCHAR(MAX) = {http{
      {
        "url": "https://api.weatherapi.com/v1/current.json?key=YOUR_API_KEY&q={{city}}",
        "method": "GET"
      }
    }http};
    
    -- Check if the call succeeded
    IF JSON_VALUE(@weather, '$.status_code') != '200'
      THROW 50502, 'Weather API unavailable', 1;
    
    -- Combine with database data (response body is under $.data)
    SELECT 
      l.id,
      l.name,
      l.city,
      JSON_VALUE(@weather, '$.data.current.temp_c') AS temperature_celsius,
      JSON_VALUE(@weather, '$.data.current.condition.text') AS weather_condition,
      l.last_updated
    FROM locations l
    WHERE l.city = {{city}};
  ]]></query>
</get_weather_enriched_locations>
```

## HTTP Request Configuration

The JSON inside `{http{...}http}` supports the full HTTP executor configuration:

> If you override `http_variable_pattern` with a custom regex, include inline `(?s)` (Singleline) when your `{http{...}http}` payload spans multiple lines.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `url` | string | Yes | The HTTP endpoint URL |
| `method` | string | No | HTTP method: GET, POST, PUT, DELETE, PATCH (default: GET) |
| `headers` | object | No | Custom headers to include |
| `body` | object/string | No | Request body (for POST/PUT/PATCH) |
| `timeout` | number | No | Request timeout in seconds (default: 30) |
| `auth` | object | No | Authentication configuration |
| `retry` | object | No | Retry policy configuration |
| `skip` | bool/string/number | No | When truthy (`true`, `"true"`, `"1"`, `"yes"`, non-zero), the call is **not executed** and the SQL variable receives `NULL` instead of structured JSON. See [Skipping HTTP Calls](#skipping-http-calls). |

## Using Request Parameters

Parameters from the incoming HTTP request (query string, body, headers, route) can be used inside the HTTP call configuration:

```xml
<validate_email>
  <route>users/validate-email</route>
  <verb>POST</verb>
  <query><![CDATA[
    DECLARE @validation NVARCHAR(MAX) = {http{
      {
        "url": "https://api.emailvalidation.com/v1/validate",
        "method": "POST",
        "headers": {
          "Content-Type": "application/json",
          "X-API-Key": "your-api-key"
        },
        "body": {
          "email": "{{email}}"
        }
      }
    }http};
    
    -- Check HTTP status first
    IF JSON_VALUE(@validation, '$.status_code') != '200'
      THROW 50502, 'Email validation service unavailable', 1;
    
    IF JSON_VALUE(@validation, '$.data.is_valid') = 'false'
      THROW 50400, 'Invalid email address', 1;
    
    INSERT INTO users (email, name, email_verified)
    VALUES ({{email}}, {{name}}, 1);
    
    SELECT 'User created successfully' AS message;
  ]]></query>
</validate_email>
```

## POST with JSON Body

```xml
<create_order_with_payment>
  <route>orders</route>
  <verb>POST</verb>
  <query><![CDATA[
    DECLARE @payment NVARCHAR(MAX) = {http{
      {
        "url": "https://api.stripe.com/v1/payment_intents",
        "method": "POST",
        "headers": {
          "Authorization": "Bearer sk_test_YOUR_STRIPE_KEY",
          "Content-Type": "application/x-www-form-urlencoded"
        },
        "body": "amount={{amount}}&currency=usd&customer={{customer_id}}"
      }
    }http};
    
    -- Check payment API responded successfully
    IF JSON_VALUE(@payment, '$.status_code') != '200'
      THROW 50502, 'Payment service error', 1;
    
    INSERT INTO orders (customer_id, amount, payment_intent_id, status)
    VALUES (
      {{customer_id}},
      {{amount}},
      JSON_VALUE(@payment, '$.data.id'),
      'pending_payment'
    );
    
    SELECT 
      JSON_VALUE(@payment, '$.data.id') AS payment_intent_id,
      JSON_VALUE(@payment, '$.data.client_secret') AS client_secret;
  ]]></query>
</create_order_with_payment>
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

### API Key

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

### Basic Auth

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "auth": {
      "type": "basic",
      "username": "user",
      "password": "pass"
    }
  }
}http};
```

### OAuth 2.0 Client Credentials

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "auth": {
      "type": "oauth2_client_credentials",
      "token_url": "https://auth.example.com/oauth/token",
      "client_id": "your-client-id",
      "client_secret": "your-client-secret",
      "scope": "read write"
    }
  }
}http};
```

## Retry Configuration

Configure automatic retries for transient failures:

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "retry": {
      "max_attempts": 3,
      "delay_seconds": 2,
      "backoff_multiplier": 2.0,
      "retry_on_status_codes": [500, 502, 503, 504]
    }
  }
}http};
```

## Skipping HTTP Calls

Use the `skip` property to conditionally prevent an HTTP call from executing. When `skip` evaluates to a truthy value, the call is **not made** and the SQL variable receives `NULL` (DbNull) instead of a structured JSON response.

```sql
-- Skip validation when the caller passes skip_validation=true
DECLARE @validation NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/validate",
    "method": "POST",
    "body": { "email": "{{email}}" },
    "skip": "{{skip_validation}}"
  }
}http};

-- Three-state check:
--   NULL          = call was skipped (skip property was truthy)
--   status_code 0 = infrastructure failure (timeout, DNS, network)
--   status_code>0 = server responded (check the specific code)
IF @validation IS NULL
BEGIN
  -- Skipped — proceed without validation
  INSERT INTO users (email, name) VALUES ({{email}}, {{name}});
END
ELSE IF CAST(JSON_VALUE(@validation, '$.status_code') AS INT) = 0
BEGIN
  DECLARE @err NVARCHAR(500) = JSON_VALUE(@validation, '$.error.message');
  THROW 50502, @err, 1;
END
ELSE IF JSON_VALUE(@validation, '$.status_code') != '200'
BEGIN
  THROW 50502, 'Validation service error', 1;
END
ELSE
BEGIN
  IF JSON_VALUE(@validation, '$.data.is_valid') = 'false'
    THROW 50400, 'Invalid email', 1;
  INSERT INTO users (email, name, validated) VALUES ({{email}}, {{name}}, 1);
END
```

### Truthy Values for `skip`

The `skip` property accepts multiple representations because `{{param}}` placeholders are resolved as strings before the JSON is parsed:

| Value | Skipped? |
|-------|----------|
| `true` (boolean) | Yes |
| `"true"` (string, case-insensitive) | Yes |
| `"1"` | Yes |
| `"yes"` (string, case-insensitive) | Yes |
| Non-zero number | Yes |
| `false`, `"false"`, `"0"`, `"no"`, `0`, absent | No |

> **Tip**: Since `{{param}}` values are resolved as strings, `"skip": "{{should_skip}}"` works naturally — pass `true` or `1` in the request to skip the call.

### Conditionally Skipping Based on Database Logic

The `skip` property is resolved during **Phase 1 pre-processing**, before any SQL executes. This means the skip decision must come from something available at that stage — request parameters (`{{param}}`), JWT claims (`{auth{claim}}`), or settings variables (`{s{var}}`). It **cannot** come from a SQL computation in the same query, because that SQL hasn't run yet.

However, by combining `skip` with **[query chaining](14-query-chaining.md)**, you can let the database drive the skip decision. Query 1 runs first, and when it returns a single row, each output column becomes a `{{column_name}}` parameter for Query 2. Query 2 can reference one of those columns as the `skip` value — effectively letting SQL decide whether the HTTP call fires.

| Condition source | Can drive `skip`? | Mechanism |
|---|---|---|
| Request parameter (`{{param}}`) | Yes | Body, query string, route, or header |
| JWT claim (`{auth{claim}}`) | Yes | Any claim from the validated token |
| Settings variable (`{s{var}}`) | Yes | From `<vars>` in settings.xml |
| SQL in the **same** query | No | SQL hasn't run yet during Phase 1 |
| SQL in a **previous** chained query | Yes | Output columns become `{{column}}` parameters — see below |

#### Example: Skip Enrichment If Data Already Exists

Query 1 checks the database and outputs a `skip_http` flag. Query 2 uses it to conditionally fire an enrichment API.

```xml
<enrich_contact>
  <route>contacts/{{id}}/enrich</route>
  <verb>POST</verb>
  <mandatory_parameters>id</mandatory_parameters>

  <!-- Query 1: Check if enrichment is needed -->
  <!-- Output columns become {{column}} parameters in Query 2 -->
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};

    IF NOT EXISTS (SELECT 1 FROM contacts WHERE id = @id)
    BEGIN
      THROW 50404, 'Contact not found', 1;
      RETURN;
    END

    SELECT
      id,
      email,
      -- '1' = already enriched → skip the API call
      -- '0' = not yet enriched → fire the API call
      CASE WHEN enriched_at IS NOT NULL THEN '1' ELSE '0' END AS skip_http
    FROM contacts
    WHERE id = @id;
  ]]></query>

  <!-- Query 2: Conditionally call the enrichment API -->
  <!-- {{skip_http}}, {{id}}, {{email}} all come from Query 1's single-row output -->
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @email NVARCHAR(500) = {{email}};

    DECLARE @enrichment NVARCHAR(MAX) = {http{
      {
        "url": "{s{enrichment_api_url}}/lookup",
        "method": "POST",
        "headers": { "X-API-Key": "{s{enrichment_api_key}}" },
        "body": { "email": "{{email}}" },
        "skip": "{{skip_http}}"
      }
    }http};

    -- Three-state check:
    --   NULL          → skipped (already enriched)
    --   status_code 0 → infrastructure failure
    --   status_code>0 → server responded
    IF @enrichment IS NULL
    BEGIN
      SELECT id, name, email, company, enriched_at,
             'already_enriched' AS status
      FROM contacts WHERE id = @id;
    END
    ELSE IF CAST(JSON_VALUE(@enrichment, '$.status_code') AS INT) = 0
    BEGIN
      DECLARE @err NVARCHAR(500) = JSON_VALUE(@enrichment, '$.error.message');
      THROW 50502, @err, 1;
    END
    ELSE IF CAST(JSON_VALUE(@enrichment, '$.status_code') AS INT) NOT BETWEEN 200 AND 299
    BEGIN
      THROW 50502, 'Enrichment service returned an error', 1;
    END
    ELSE
    BEGIN
      UPDATE contacts
      SET
        company     = JSON_VALUE(@enrichment, '$.data.company'),
        location    = JSON_VALUE(@enrichment, '$.data.location'),
        enriched_at = GETUTCDATE()
      WHERE id = @id;

      SELECT id, name, email, company, enriched_at,
             'enriched' AS status
      FROM contacts WHERE id = @id;
    END
  ]]></query>
</enrich_contact>
```

**Why this works:** Query 1 executes first and outputs `skip_http` alongside `id` and `email`. Because Query 1 returns a single row, all output columns automatically become `{{column_name}}` parameters available to Query 2 (see [Query Chaining — Parameter Passing](14-query-chaining.md#parameter-passing)). When Query 2's Phase 1 pre-processing resolves `"skip": "{{skip_http}}"`, it reads `"1"` or `"0"` from that parameter — before any SQL in Query 2 runs. If `skip_http` is `"1"`, the HTTP call never fires and `@enrichment` receives `NULL`. If it's `"0"`, the call executes normally. The database made the decision; no application code, no extra round-trips.

> **When to use this pattern:** pay-per-call APIs (avoid unnecessary charges), rate-limited APIs (protect your quota), slow external services (skip when data is already fresh), and idempotent enrichment workflows (safely re-run without double-calling).

## Multiple HTTP Calls

You can include multiple HTTP calls in a single query. Each unique call is executed once, and duplicates are automatically deduplicated:

```xml
<aggregate_external_data>
  <route>dashboard/summary</route>
  <verb>GET</verb>
  <query><![CDATA[
    DECLARE @users NVARCHAR(MAX) = {http{
      {"url": "https://users-service/api/stats", "method": "GET"}
    }http};
    
    DECLARE @orders NVARCHAR(MAX) = {http{
      {"url": "https://orders-service/api/stats", "method": "GET"}
    }http};
    
    DECLARE @inventory NVARCHAR(MAX) = {http{
      {"url": "https://inventory-service/api/stats", "method": "GET"}
    }http};
    
    SELECT 
      JSON_VALUE(@users, '$.data.total_users') AS total_users,
      JSON_VALUE(@orders, '$.data.total_orders') AS total_orders,
      JSON_VALUE(@inventory, '$.data.items_in_stock') AS items_in_stock,
      JSON_VALUE(@users, '$.status_code') AS users_api_status,
      JSON_VALUE(@orders, '$.status_code') AS orders_api_status,
      JSON_VALUE(@inventory, '$.status_code') AS inventory_api_status,
      GETDATE() AS generated_at;
  ]]></query>
</aggregate_external_data>
```

## Conditional Usage with IF Blocks

Because HTTP responses are delivered as parameterized SQL variables, you can use standard SQL `IF` blocks to conditionally process the result — even though the HTTP call itself always fires during pre-processing:

```sql
DECLARE @emirates_id NVARCHAR(50) = {{emirates_id}};

-- The HTTP call executes during pre-processing regardless,
-- but its result (a parameterized variable) is only assigned
-- to @nin_json inside the IF block when the condition is met.
DECLARE @nin NVARCHAR(MAX) = NULL;
IF @emirates_id IS NOT NULL AND @emirates_id NOT LIKE '%[^0-9]%'
BEGIN
    DECLARE @nin_json NVARCHAR(MAX) = {http{
      {
        "url": "https://api.example.com/lookup",
        "method": "GET",
        "body": { "eid": "{{emirates_id}}" }
      }
    }http};

    IF CAST(JSON_VALUE(@nin_json, '$.status_code') AS INT) BETWEEN 200 AND 299
    BEGIN
        SELECT @nin = STRING_AGG(TRIM(JSON_VALUE(value, '$.NIN_NO')), ' | ')
        FROM OPENJSON(@nin_json, '$.data')
        WHERE JSON_VALUE(value, '$.NIN_NO') IS NOT NULL;
    END
END

-- @nin is NULL if the IF condition was false,
-- or contains the looked-up value if it was true.
UPDATE records SET nin = COALESCE(@nin, nin) WHERE id = @id;
```

This pattern is useful in **update** scenarios where a parameter may or may not be provided in the request. The HTTP call fires either way (minimal overhead if the external API handles null/empty gracefully), but your SQL logic decides whether to use the result. You can also inspect `$.status_code` to handle API failures gracefully within the conditional block.

## Error Handling

Every embedded HTTP call **always** returns a structured JSON response — even when the call fails. This gives query authors full visibility into what happened:

```sql
DECLARE @response NVARCHAR(MAX) = {http{
  {"url": "https://api.example.com/data", "method": "GET"}
}http};

DECLARE @status INT = CAST(JSON_VALUE(@response, '$.status_code') AS INT);

-- Handle specific failure scenarios
IF @status = 0
BEGIN
  -- Infrastructure failure — check $.error.message for details
  DECLARE @err_msg NVARCHAR(500) = JSON_VALUE(@response, '$.error.message');
  -- e.g., "Request timed out after 30 seconds" or "Host not found: api.example.com"
  THROW 50502, @err_msg, 1;
END

IF @status = 401 OR @status = 403
  THROW 50401, 'External service authentication failed', 1;

IF @status = 429
  THROW 50429, 'External service rate limit exceeded', 1;

IF @status < 200 OR @status >= 300
  THROW 50502, 'External service returned an error', 1;

-- Access response headers (e.g., pagination, rate-limit info)
SELECT JSON_VALUE(@response, '$.headers.X-RateLimit-Remaining') AS rate_limit_remaining;

-- Continue with the actual response body
SELECT * FROM OPENJSON(@response, '$.data');
```

### Status Code Reference

| `$.status_code` | Meaning |
|------------------|---------|
| `0` | Request never reached the server (DNS failure, network error, timeout, exception). Check `$.error.message` for details. |
| `200`–`299` | Success — `$.data` contains the response body |
| `400`–`499` | Client error — `$.data` may contain error details from the server |
| `500`–`599` | Server error — `$.data` may contain error details from the server |

All HTTP calls are also logged with status codes and timing for debugging.

## Real-World Example: KYC Verification

```xml
<verify_customer_kyc>
  <route>customers/{{customer_id}}/verify</route>
  <verb>POST</verb>
  <authorize>true</authorize>
  <query><![CDATA[
    DECLARE @customer_id UNIQUEIDENTIFIER = {{customer_id}};
    DECLARE @id_number NVARCHAR(50) = {{id_number}};
    DECLARE @id_type NVARCHAR(20) = {{id_type}};
    
    DECLARE @kyc_result NVARCHAR(MAX) = {http{
      {
        "url": "https://kyc-provider.com/api/v1/verify",
        "method": "POST",
        "headers": {
          "Content-Type": "application/json",
          "Authorization": "Bearer {{h{X-KYC-API-Key}}}"
        },
        "body": {
          "document_number": "{{id_number}}",
          "document_type": "{{id_type}}",
          "country": "AE"
        },
        "timeout": 60,
        "retry": {
          "max_attempts": 2,
          "delay_seconds": 5
        }
      }
    }http};
    
    DECLARE @kyc_status_code INT = CAST(JSON_VALUE(@kyc_result, '$.status_code') AS INT);
    IF @kyc_status_code = 0
    BEGIN
      DECLARE @kyc_err NVARCHAR(500) = JSON_VALUE(@kyc_result, '$.error.message');
      THROW 50503, @kyc_err, 1;
    END
    IF @kyc_status_code >= 500
      THROW 50503, 'KYC service temporarily unavailable', 1;
    IF @kyc_status_code = 401 OR @kyc_status_code = 403
      THROW 50401, 'KYC service authentication failed', 1;
    
    DECLARE @kyc_status NVARCHAR(50) = JSON_VALUE(@kyc_result, '$.data.status');
    DECLARE @kyc_score INT = JSON_VALUE(@kyc_result, '$.data.confidence_score');
    
    IF @kyc_status != 'VERIFIED' OR @kyc_score < 80
      THROW 50400, 'KYC verification failed', 1;
    
    UPDATE customers
    SET 
      kyc_verified = 1,
      kyc_verified_at = GETDATE(),
      kyc_reference = JSON_VALUE(@kyc_result, '$.data.reference_id'),
      kyc_score = @kyc_score
    WHERE id = @customer_id;
    
    SELECT 
      @customer_id AS customer_id,
      'KYC verification successful' AS message,
      @kyc_score AS confidence_score;
  ]]></query>
</verify_customer_kyc>
```

## Performance Considerations

- **HTTP calls are executed concurrently** — after sequential parameter preparation, all calls fan out in parallel via `Task.WhenAll`, so the total latency is the **slowest** call, not the sum. Results are then applied to the query sequentially before SQL execution
- **All `{http{...}http}` blocks execute during pre-processing**, regardless of SQL `IF` conditions (unless the `skip` property is truthy) — the `IF` only controls whether the parameterized result variable is assigned/used in your SQL logic
- **Duplicate calls are deduplicated** — identical HTTP configurations execute only once (the response is reused for all matching markers)
- **Use timeouts** to prevent slow external services from blocking your API
- **Consider caching** for frequently-called external APIs
- **Use retries wisely** — exponential backoff prevents overwhelming external services

## Security Notes

- **SQL injection safe**: HTTP responses are delivered to SQL as **parameterized values** (e.g., `@http_response_1`, `@http_response_2`) via the same parameterization mechanism used for `{{param}}` values. This is equivalent to binding parameters in `sp_executesql` — the response content is **never** concatenated or interpolated into the SQL string. Even a malicious external API response cannot alter query structure or cause SQL injection.
- **No information leakage to clients**: The structured response (status_code, headers, data, error) is available only inside the SQL query. The query author controls what, if anything, is returned to the API consumer.
- `{{param}}` placeholders inside `{http{...}http}` resolve from request parameters and are also parameterized
- Sensitive credentials in HTTP configurations should use [settings variables](21-settings-vars.md) with encryption — e.g., `{s{api_key}}` instead of hardcoding secrets (see [Settings Encryption](15-encryption.md))
- Consider using header parameters (`{{h{Header-Name}}}`) to pass API keys from request headers rather than hardcoding
