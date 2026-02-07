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
1. The application finds the `{http{...}http}` marker
2. It executes the HTTP request **before** running the SQL
3. The marker is replaced with the HTTP response body
4. The SQL executes with the response available as a variable

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

    SELECT 
      c.name,
      c.phone,
      JSON_VALUE(@weather, '$.current.temp_c') AS temperature,
      JSON_VALUE(@weather, '$.current.condition.text') AS weather
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

    -- Step 2: Check validation result
    IF JSON_VALUE(@validation, '$.is_valid') = 'false'
    BEGIN
      THROW 50400, 'Invalid email address', 1;
      RETURN;
    END

    -- Step 3: Create the user
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
  JSON_VALUE(@weather, '$.temperature') AS temp,
  JSON_VALUE(@news, '$.articles[0].title') AS top_headline;
```

## Parsing the Response

Since HTTP responses are typically JSON, use SQL Server's JSON functions:

```sql
-- Single value
SELECT JSON_VALUE(@response, '$.name') AS name;

-- Nested value
SELECT JSON_VALUE(@response, '$.address.city') AS city;

-- Array element
SELECT JSON_VALUE(@response, '$.items[0].id') AS first_item_id;

-- Parse array into rows
SELECT * FROM OPENJSON(@response, '$.items')
WITH (
  id INT '$.id',
  name NVARCHAR(100) '$.name',
  price DECIMAL(10,2) '$.price'
);
```

## Error Handling

If the HTTP call fails (timeout, connection error, non-2xx response), the variable receives the error response or `NULL`. Handle this gracefully:

```sql
DECLARE @response NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "timeout": 10
  }
}http};

IF @response IS NULL
BEGIN
  -- External API unavailable — return cached/default data
  SELECT * FROM cached_data;
  RETURN;
END

-- Normal processing with @response...
```

## Security Considerations

- **Never expose secrets in client-visible responses** — API keys in `{http{...}http}` are server-side only
- **Validate external data** — don't trust external API responses blindly
- **Set timeouts** — prevent your API from hanging if an external service is slow
- **Consider caching** — if the external data doesn't change often

---

### What You Learned

- The `{http{...}http}` syntax for embedded HTTP calls
- How HTTP calls execute before the SQL runs
- Sending GET and POST requests with headers and body
- Authentication options (bearer token, API key)
- Parsing JSON responses with `JSON_VALUE` and `OPENJSON`
- Error handling for failed HTTP calls
- Practical patterns: data enrichment, validation, multi-service calls

---

**Next:** [Multi-Query Chaining →](17-multi-query.md)

**[Back to Tutorial Index](index.md)**
