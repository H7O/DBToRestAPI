# Embedded HTTP Calls in SQL Queries

Execute HTTP requests directly from within your SQL queries — enrich database data with external APIs, validate inputs against third-party services, or orchestrate microservice calls without leaving SQL.

## How It Works

Embed HTTP call markers in your SQL queries using the `{http{...}http}` syntax. The API engine executes these HTTP calls **before** running the SQL, replacing the markers with the response content.

```sql
DECLARE @external_data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "method": "GET"
  }
}http};

-- Use the response in your query
SELECT * FROM OPENJSON(@external_data);
```

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

## Error Handling

When an HTTP call fails, the marker is replaced with `NULL`:

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
- **Duplicate calls are deduplicated** — identical HTTP configurations execute only once
- **Use timeouts** to prevent slow external services from blocking your API
- **Consider caching** for frequently-called external APIs
- **Use retries wisely** — exponential backoff prevents overwhelming external services

## Security Notes

- HTTP responses are passed as **parameterized values** to SQL — no SQL injection risk
- Sensitive credentials in HTTP configurations should use encrypted settings (see [Settings Encryption](15-encryption.md))
- Consider using header parameters (`{{h{Header-Name}}}`) to pass API keys from request headers rather than hardcoding
