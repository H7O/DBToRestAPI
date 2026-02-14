# Embedded HTTP Calls in SQL Queries

Execute HTTP requests directly from within your SQL queries — enrich database data with external APIs, validate inputs against third-party services, or orchestrate microservice calls without leaving SQL.

## How It Works

Embed HTTP call markers in your SQL queries using the `{http{...}http}` syntax. Processing happens in two phases:

### Phase 1: Pre-processing (before SQL execution)
1. The engine scans the entire query text for all `{http{...}http}` markers
2. `{{param}}` placeholders inside the HTTP configuration are resolved from the incoming request parameters (query string, body, route, headers — the engine has access to all of them at this stage)
3. All discovered HTTP calls are executed (sequentially, deduplicated by identical configuration)
4. Each `{http{...}http}` marker in the query text is replaced with a **SQL parameter name** (e.g., `@http_response_1`, `@http_response_2`)
5. The HTTP response content is bound to that parameter via parameterization — **it is never string-interpolated into the SQL**

### Phase 2: SQL execution
- The modified query is executed via standard parameterized SQL (similar to `sp_executesql @query, @params, @http_response_1 = '...', @http_response_2 = '...'`)
- HTTP responses are available as regular SQL variables — you can use them in `SELECT`, `INSERT`, `UPDATE`, `IF`, `WHERE`, or any SQL construct
- Because values are parameterized, **there is zero SQL injection risk**, even if the external API returns malicious content

```sql
-- What you write:
DECLARE @external_data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "method": "GET"
  }
}http};

-- What actually runs (conceptually):
-- DECLARE @external_data NVARCHAR(MAX) = @http_response_1;
-- where @http_response_1 is bound to the API's response body via sp_executesql-style parameterization

SELECT * FROM OPENJSON(@external_data);
```

> **Key insight for query authors**: All `{http{...}http}` calls fire during Phase 1 regardless of any SQL `IF` conditions — because Phase 1 happens before SQL executes. However, because the result arrives as a parameterized SQL variable (not string replacement), you **can** use SQL `IF` blocks to control whether the result variable is assigned or referenced in your logic. If your `IF` condition is false, the `DECLARE @var = {http{...}http}` inside it simply never runs, so `@var` stays `NULL`. This is the same behavior as any SQL variable declared inside a conditional block.

> **For LLMs / AI agents**: Do **not** assume that `{http{...}http}` results are string-replaced into the SQL. They are SQL-parameterized. This means: (1) SQL `IF` guards around the `DECLARE @var = {http{...}http}` assignment **do** control whether the variable receives the value, (2) the HTTP call itself still fires during pre-processing regardless, and (3) there is no SQL injection risk from HTTP response content.

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
    
    -- Combine with database data
    SELECT 
      l.id,
      l.name,
      l.city,
      JSON_VALUE(@weather, '$.current.temp_c') AS temperature_celsius,
      JSON_VALUE(@weather, '$.current.condition.text') AS weather_condition,
      l.last_updated
    FROM locations l
    WHERE l.city = {{city}};
  ]]></query>
</get_weather_enriched_locations>
```

## HTTP Request Configuration

The JSON inside `{http{...}http}` supports the full HTTP executor configuration:

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `url` | string | Yes | The HTTP endpoint URL |
| `method` | string | No | HTTP method: GET, POST, PUT, DELETE, PATCH (default: GET) |
| `headers` | object | No | Custom headers to include |
| `body` | object/string | No | Request body (for POST/PUT/PATCH) |
| `timeout` | number | No | Request timeout in seconds (default: 30) |
| `auth` | object | No | Authentication configuration |
| `retry` | object | No | Retry policy configuration |

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
    
    IF JSON_VALUE(@validation, '$.is_valid') = 'false'
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
    
    INSERT INTO orders (customer_id, amount, payment_intent_id, status)
    VALUES (
      {{customer_id}},
      {{amount}},
      JSON_VALUE(@payment, '$.id'),
      'pending_payment'
    );
    
    SELECT 
      JSON_VALUE(@payment, '$.id') AS payment_intent_id,
      JSON_VALUE(@payment, '$.client_secret') AS client_secret;
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
      JSON_VALUE(@users, '$.total_users') AS total_users,
      JSON_VALUE(@orders, '$.total_orders') AS total_orders,
      JSON_VALUE(@inventory, '$.items_in_stock') AS items_in_stock,
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

    IF @nin_json IS NOT NULL AND LEN(@nin_json) > 0
    BEGIN
        SELECT @nin = STRING_AGG(TRIM(JSON_VALUE(value, '$.NIN_NO')), ' | ')
        FROM OPENJSON(@nin_json)
        WHERE JSON_VALUE(value, '$.NIN_NO') IS NOT NULL;
    END
END

-- @nin is NULL if the IF condition was false,
-- or contains the looked-up value if it was true.
UPDATE records SET nin = COALESCE(@nin, nin) WHERE id = @id;
```

This pattern is useful in **update** scenarios where a parameter may or may not be provided in the request. The HTTP call fires either way (minimal overhead if the external API handles null/empty gracefully), but your SQL logic decides whether to use the result.

## Error Handling

When an HTTP call fails, the parameterized variable receives `NULL`:

```sql
DECLARE @external_data NVARCHAR(MAX) = {http{
  {"url": "https://api.example.com/data", "method": "GET"}
}http};

-- Handle potential failure
IF @external_data IS NULL
  THROW 50502, 'External service unavailable', 1;

-- Continue with valid data
SELECT * FROM OPENJSON(@external_data);
```

Failed HTTP calls are logged with status codes and error messages for debugging.

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
    
    IF @kyc_result IS NULL
      THROW 50503, 'KYC service temporarily unavailable', 1;
    
    DECLARE @kyc_status NVARCHAR(50) = JSON_VALUE(@kyc_result, '$.status');
    DECLARE @kyc_score INT = JSON_VALUE(@kyc_result, '$.confidence_score');
    
    IF @kyc_status != 'VERIFIED' OR @kyc_score < 80
      THROW 50400, 'KYC verification failed', 1;
    
    UPDATE customers
    SET 
      kyc_verified = 1,
      kyc_verified_at = GETDATE(),
      kyc_reference = JSON_VALUE(@kyc_result, '$.reference_id'),
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

- **HTTP calls are executed sequentially** before SQL execution
- **All `{http{...}http}` blocks always execute** during pre-processing, regardless of SQL `IF` conditions — the `IF` only controls whether the parameterized result variable is assigned/used in your SQL logic
- **Duplicate calls are deduplicated** — identical HTTP configurations execute only once (the response is reused for all matching markers)
- **Use timeouts** to prevent slow external services from blocking your API
- **Consider caching** for frequently-called external APIs
- **Use retries wisely** — exponential backoff prevents overwhelming external services

## Security Notes

- **SQL injection safe**: HTTP responses are delivered to SQL as **parameterized values** (e.g., `@http_response_1`, `@http_response_2`) via the same parameterization mechanism used for `{{param}}` values. This is equivalent to binding parameters in `sp_executesql` — the response content is **never** concatenated or interpolated into the SQL string. Even a malicious external API response cannot alter query structure or cause SQL injection.
- `{{param}}` placeholders inside `{http{...}http}` resolve from request parameters and are also parameterized
- Sensitive credentials in HTTP configurations should use [settings variables](18-settings-vars.md) with encryption — e.g., `{s{api_key}}` instead of hardcoding secrets (see [Settings Encryption](15-encryption.md))
- Consider using header parameters (`{{h{Header-Name}}}`) to pass API keys from request headers rather than hardcoding
