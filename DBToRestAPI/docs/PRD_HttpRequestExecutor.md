# Product Requirements Document (PRD)

## HTTP Request Executor Service

**Version:** 1.0  
**Status:** Draft  
**Author:** GitHub Copilot  
**Created:** 2025-01-20  
**Last Updated:** 2025-01-20

---

## Table of Contents

1. [Overview](#1-overview)
2. [JSON Request Schema](#2-json-request-schema)
3. [Service Interface](#3-service-interface)
4. [Service Registration](#4-service-registration)
5. [Implementation Details](#5-implementation-details)
6. [Usage Examples](#6-usage-examples)
7. [Error Handling](#7-error-handling)
8. [Logging](#8-logging)
9. [Future Considerations](#9-future-considerations)
10. [Acceptance Criteria](#10-acceptance-criteria)

---

## 1. Overview

### 1.1 Purpose

A reusable service that executes HTTP requests defined in JSON format, providing a programmatic curl-like interface for .NET applications. The service abstracts away the complexity of `HttpClient` management, header handling, authentication, and retry logic.

### 1.2 Problem Statement

Developers often need to make HTTP calls based on dynamic configurations (stored in databases, config files, or received at runtime). Currently, this requires manually constructing `HttpRequestMessage` objects, handling headers, managing `HttpClient` lifecycle, and implementing retry/timeout logic repeatedly.

### 1.3 Solution

A singleton service (`IHttpRequestExecutor`) that:

- Accepts JSON-defined HTTP request specifications
- Internally manages `HttpClient` instances via `IHttpClientFactory`
- Handles all HTTP complexities (headers, auth, certificates, retries)
- Returns a unified response container

### 1.4 Target Framework

- **.NET Version:** .NET 10
- **C# Version:** 14.0
- **Dependencies:** `System.Text.Json`, `Microsoft.Extensions.Http`

---

## 2. JSON Request Schema

### 2.1 Complete Schema

```json
{
  "url": "https://api.example.com/users",
  "method": "POST",
  "headers": {
    "X-Custom-Header": "value",
    "X-Request-Id": "abc-123"
  },
  "query": {
    "page": "1",
    "limit": "50"
  },
  "body": {
    "name": "John Doe",
    "email": "john@example.com"
  },
  "body_raw": "raw string content if body is not JSON",
  "content_type": "application/json",
  "timeout_seconds": 30,
  "ignore_certificate_errors": false,
  "follow_redirects": true,
  "auth": {
    "type": "bearer",
    "token": "eyJhbGciOiJIUzI1NiIs..."
  },
  "retry": {
    "max_attempts": 3,
    "delay_ms": 1000,
    "exponential_backoff": true
  }
}
```

### 2.2 Field Specifications

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `url` | string | ? Yes | - | Target URL (can include query string) |
| `method` | string | No | `"GET"` | HTTP method: GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS |
| `headers` | object | No | `{}` | Key-value pairs of request headers |
| `query` | object | No | `{}` | Query parameters (appended to URL) |
| `body` | object/array | No | `null` | JSON body (serialized automatically) |
| `body_raw` | string | No | `null` | Raw string body (used if `body` is null) |
| `content_type` | string | No | `"application/json"` | Content-Type header (convenience field) |
| `timeout_seconds` | int | No | `30` | Request timeout in seconds |
| `ignore_certificate_errors` | bool | No | `false` | Skip SSL certificate validation |
| `follow_redirects` | bool | No | `true` | Automatically follow HTTP redirects |
| `auth` | object | No | `null` | Authentication configuration |
| `retry` | object | No | `null` | Retry policy configuration |

### 2.3 Authentication Object

```json
{
  "auth": {
    "type": "basic|bearer|api_key",
    
    "username": "user",
    "password": "pass",
    
    "token": "bearer-token-value",
    
    "key": "api-key-value",
    "key_header": "X-API-Key",
    "key_query_param": "api_key"
  }
}
```

| Auth Type | Required Fields | Description |
|-----------|-----------------|-------------|
| `basic` | `username`, `password` | HTTP Basic Authentication |
| `bearer` | `token` | Bearer token in Authorization header |
| `api_key` | `key` + (`key_header` OR `key_query_param`) | API key in header or query string |

### 2.4 Retry Object

```json
{
  "retry": {
    "max_attempts": 3,
    "delay_ms": 1000,
    "exponential_backoff": true,
    "retry_status_codes": [500, 502, 503, 504]
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `max_attempts` | int | `3` | Maximum retry attempts |
| `delay_ms` | int | `1000` | Base delay between retries (ms) |
| `exponential_backoff` | bool | `true` | Double delay after each retry |
| `retry_status_codes` | int[] | `[500,502,503,504]` | Status codes that trigger retry |

---

## 3. Service Interface

### 3.1 Primary Interface

```csharp
namespace DBToRestAPI.Services.HttpExecutor;

public interface IHttpRequestExecutor
{
    /// <summary>
    /// Executes an HTTP request defined by a JSON string.
    /// </summary>
    Task<HttpExecutorResponse> ExecuteAsync(
        string requestJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an HTTP request defined by a JsonElement.
    /// </summary>
    Task<HttpExecutorResponse> ExecuteAsync(
        JsonElement requestConfig,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an HTTP request defined by a strongly-typed object.
    /// </summary>
    Task<HttpExecutorResponse> ExecuteAsync(
        HttpExecutorRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a JSON request configuration without executing it.
    /// </summary>
    HttpExecutorValidationResult Validate(string requestJson);
}
```

### 3.2 Response Container

```csharp
namespace DBToRestAPI.Services.HttpExecutor;

public class HttpExecutorResponse
{
    /// <summary>
    /// Indicates whether the HTTP request completed with a success status code (2xx).
    /// </summary>
    public bool IsSuccess { get; init; }
    
    /// <summary>
    /// The HTTP status code returned by the server. 0 if request failed before receiving response.
    /// </summary>
    public int StatusCode { get; init; }
    
    /// <summary>
    /// The HTTP reason phrase (e.g., "OK", "Not Found").
    /// </summary>
    public string? ReasonPhrase { get; init; }
    
    /// <summary>
    /// Response headers (excluding content headers).
    /// </summary>
    public Dictionary<string, string[]> Headers { get; init; } = [];
    
    /// <summary>
    /// Content-specific headers (Content-Type, Content-Length, etc.).
    /// </summary>
    public Dictionary<string, string[]> ContentHeaders { get; init; } = [];
    
    /// <summary>
    /// The raw response body as bytes.
    /// </summary>
    public byte[] Content { get; init; } = [];
    
    /// <summary>
    /// The response body decoded as UTF-8 string.
    /// </summary>
    public string ContentAsString => Encoding.UTF8.GetString(Content);
    
    /// <summary>
    /// Deserializes the response body to the specified type.
    /// </summary>
    public T? ContentAs<T>() where T : class;
    
    /// <summary>
    /// Parses the response body as JSON.
    /// </summary>
    public JsonElement? ContentAsJson();
    
    /// <summary>
    /// Total time elapsed for the request (including retries).
    /// </summary>
    public TimeSpan ElapsedTime { get; init; }
    
    /// <summary>
    /// Number of retry attempts made (0 if succeeded on first try).
    /// </summary>
    public int RetryAttempts { get; init; }
    
    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// The exception that caused the failure, if any.
    /// </summary>
    public Exception? Exception { get; init; }
}
```

### 3.3 Request Model (Strongly-Typed Alternative)

```csharp
namespace DBToRestAPI.Services.HttpExecutor;

public class HttpExecutorRequest
{
    public required string Url { get; init; }
    public string Method { get; init; } = "GET";
    
    public Dictionary<string, string>? Headers { get; init; }
    public Dictionary<string, string>? Query { get; init; }
    
    public object? Body { get; init; }
    public string? BodyRaw { get; init; }
    public string ContentType { get; init; } = "application/json";
    
    public int TimeoutSeconds { get; init; } = 30;
    public bool IgnoreCertificateErrors { get; init; } = false;
    public bool FollowRedirects { get; init; } = true;
    
    public HttpExecutorAuth? Auth { get; init; }
    public HttpExecutorRetry? Retry { get; init; }
}

public class HttpExecutorAuth
{
    public required string Type { get; init; }
    
    // Basic auth
    public string? Username { get; init; }
    public string? Password { get; init; }
    
    // Bearer token
    public string? Token { get; init; }
    
    // API Key
    public string? Key { get; init; }
    public string? KeyHeader { get; init; }
    public string? KeyQueryParam { get; init; }
}

public class HttpExecutorRetry
{
    public int MaxAttempts { get; init; } = 3;
    public int DelayMs { get; init; } = 1000;
    public bool ExponentialBackoff { get; init; } = true;
    public int[]? RetryStatusCodes { get; init; }
}
```

### 3.4 Validation Result

```csharp
namespace DBToRestAPI.Services.HttpExecutor;

public class HttpExecutorValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
```

---

## 4. Service Registration

### 4.1 Program.cs Registration

```csharp
// Basic registration
builder.Services.AddHttpRequestExecutor();

// With options
builder.Services.AddHttpRequestExecutor(options =>
{
    options.DefaultTimeoutSeconds = 60;
    options.DefaultRetryAttempts = 3;
    options.EnableRequestLogging = true;
    options.SensitiveHeaderPatterns = ["Authorization", "X-API-Key"];
});
```

### 4.2 Service Options

```csharp
namespace DBToRestAPI.Services.HttpExecutor;

public class HttpExecutorOptions
{
    /// <summary>
    /// Default timeout for requests when not specified in the request config.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Default number of retry attempts when not specified in the request config.
    /// 0 means no retries.
    /// </summary>
    public int DefaultRetryAttempts { get; set; } = 0;
    
    /// <summary>
    /// Default behavior for following redirects.
    /// </summary>
    public bool DefaultFollowRedirects { get; set; } = true;
    
    /// <summary>
    /// Enable detailed request/response logging.
    /// </summary>
    public bool EnableRequestLogging { get; set; } = false;
    
    /// <summary>
    /// Header name patterns to redact in logs.
    /// </summary>
    public string[] SensitiveHeaderPatterns { get; set; } = ["Authorization", "API-Key", "Token"];
    
    /// <summary>
    /// Maximum response body size to buffer (in bytes).
    /// Responses larger than this will be truncated.
    /// </summary>
    public int MaxResponseBufferSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
}
```

---

## 5. Implementation Details

### 5.1 File Structure

```
DBToRestAPI/
??? Services/
    ??? HttpExecutor/
        ??? IHttpRequestExecutor.cs           # Primary interface
        ??? HttpRequestExecutor.cs            # Main implementation
        ??? HttpExecutorOptions.cs            # Configuration options
        ??? Models/
        ?   ??? HttpExecutorRequest.cs        # Strongly-typed request model
        ?   ??? HttpExecutorResponse.cs       # Response container
        ?   ??? HttpExecutorAuth.cs           # Authentication configuration
        ?   ??? HttpExecutorRetry.cs          # Retry configuration
        ?   ??? HttpExecutorValidationResult.cs
        ??? Internal/
        ?   ??? RequestMessageBuilder.cs      # Builds HttpRequestMessage from config
        ?   ??? AuthenticationHandler.cs      # Applies authentication to requests
        ?   ??? QueryStringBuilder.cs         # Builds and merges query strings
        ?   ??? RetryHandler.cs               # Handles retry logic with backoff
        ?   ??? JsonRequestParser.cs          # Parses JSON to request model
        ??? Extensions/
            ??? ServiceCollectionExtensions.cs  # DI registration helpers
```

### 5.2 HttpClient Management

The service internally uses `IHttpClientFactory` with named clients to handle different configurations:

```csharp
namespace DBToRestAPI.Services.HttpExecutor.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHttpRequestExecutor(
        this IServiceCollection services,
        Action<HttpExecutorOptions>? configure = null)
    {
        var options = new HttpExecutorOptions();
        configure?.Invoke(options);
        
        services.AddSingleton(options);
        
        // Standard client
        services.AddHttpClient("HttpExecutor")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(options.DefaultTimeoutSeconds);
            });
        
        // Client that ignores certificate errors
        services.AddHttpClient("HttpExecutor.IgnoreCerts")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        
        // Client that doesn't follow redirects
        services.AddHttpClient("HttpExecutor.NoRedirect")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        
        // Combined: ignore certs + no redirect
        services.AddHttpClient("HttpExecutor.IgnoreCerts.NoRedirect")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AllowAutoRedirect = false
            });
        
        services.AddSingleton<IHttpRequestExecutor, HttpRequestExecutor>();
        
        return services;
    }
}
```

### 5.3 Client Selection Logic

```csharp
private HttpClient GetClient(HttpExecutorRequest request)
{
    var clientName = (request.IgnoreCertificateErrors, request.FollowRedirects) switch
    {
        (true, false)  => "HttpExecutor.IgnoreCerts.NoRedirect",
        (true, true)   => "HttpExecutor.IgnoreCerts",
        (false, false) => "HttpExecutor.NoRedirect",
        (false, true)  => "HttpExecutor"
    };
    
    return _httpClientFactory.CreateClient(clientName);
}
```

### 5.4 Content Header Handling

The service must properly distinguish between request headers and content headers:

```csharp
private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
{
    "Content-Type",
    "Content-Length", 
    "Content-Encoding",
    "Content-Language",
    "Content-Location",
    "Content-MD5",
    "Content-Range",
    "Content-Disposition"
};
```

---

## 6. Usage Examples

### 6.1 Basic GET Request

```csharp
public class MyService(IHttpRequestExecutor httpExecutor)
{
    public async Task<User?> GetUserAsync(int userId)
    {
        var response = await httpExecutor.ExecuteAsync($$"""
        {
            "url": "https://api.example.com/users/{{userId}}"
        }
        """);

        if (response.IsSuccess)
            return response.ContentAs<User>();
        
        return null;
    }
}
```

### 6.2 POST with Authentication

```csharp
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/orders",
    "method": "POST",
    "auth": {
        "type": "bearer",
        "token": "eyJhbGciOiJIUzI1NiIs..."
    },
    "body": {
        "product_id": "SKU-001",
        "quantity": 2
    }
}
""");
```

### 6.3 With Query Parameters

```csharp
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/search",
    "query": {
        "q": "test query",
        "page": "1",
        "limit": "20"
    }
}
""");
// Results in: https://api.example.com/search?q=test%20query&page=1&limit=20
```

### 6.4 With Retry Policy

```csharp
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://unreliable-api.example.com/data",
    "retry": {
        "max_attempts": 5,
        "delay_ms": 500,
        "exponential_backoff": true,
        "retry_status_codes": [500, 502, 503, 504, 429]
    }
}
""");

_logger.LogInformation("Succeeded after {Attempts} retries in {Elapsed}ms", 
    response.RetryAttempts, 
    response.ElapsedTime.TotalMilliseconds);
```

### 6.5 Basic Authentication

```csharp
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/protected",
    "auth": {
        "type": "basic",
        "username": "admin",
        "password": "secret123"
    }
}
""");
```

### 6.6 API Key Authentication

```csharp
// API key in header
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/data",
    "auth": {
        "type": "api_key",
        "key": "sk-abc123xyz",
        "key_header": "X-API-Key"
    }
}
""");

// API key in query string
var response2 = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/data",
    "auth": {
        "type": "api_key",
        "key": "sk-abc123xyz",
        "key_query_param": "api_key"
    }
}
""");
```

### 6.7 Strongly-Typed Usage

```csharp
var response = await _httpExecutor.ExecuteAsync(new HttpExecutorRequest
{
    Url = "https://api.example.com/users",
    Method = "POST",
    Headers = new() 
    { 
        ["X-Correlation-Id"] = Guid.NewGuid().ToString() 
    },
    Body = new 
    { 
        Name = "John Doe", 
        Email = "john@example.com" 
    },
    Auth = new HttpExecutorAuth
    {
        Type = "basic",
        Username = "admin",
        Password = "secret"
    },
    Retry = new HttpExecutorRetry
    {
        MaxAttempts = 3,
        DelayMs = 1000,
        ExponentialBackoff = true
    }
});
```

### 6.8 Validation Before Execution

```csharp
var userProvidedJson = GetJsonFromDatabase();

var validation = _httpExecutor.Validate(userProvidedJson);

if (!validation.IsValid)
{
    foreach (var error in validation.Errors)
        _logger.LogError("Validation error: {Error}", error);
    
    return BadRequest(new { errors = validation.Errors });
}

var response = await _httpExecutor.ExecuteAsync(userProvidedJson);
```

### 6.9 Ignoring Certificate Errors

```csharp
// Useful for development/testing with self-signed certificates
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://localhost:5001/api/test",
    "ignore_certificate_errors": true
}
""");
```

### 6.10 Raw String Body

```csharp
// When you need to send non-JSON content
var response = await _httpExecutor.ExecuteAsync("""
{
    "url": "https://api.example.com/xml-endpoint",
    "method": "POST",
    "content_type": "application/xml",
    "body_raw": "<request><id>123</id><action>process</action></request>"
}
""");
```

---

## 7. Error Handling

### 7.1 Error Scenarios

| Scenario | `IsSuccess` | `StatusCode` | `ErrorMessage` |
|----------|-------------|--------------|----------------|
| Successful 2xx response | `true` | 200-299 | `null` |
| HTTP error (4xx/5xx) | `false` | 400-599 | HTTP reason phrase |
| Connection failure | `false` | `0` | "Connection refused" / details |
| Timeout | `false` | `0` | "Request timed out after X seconds" |
| Invalid JSON config | `false` | `0` | Validation error details |
| SSL certificate error | `false` | `0` | Certificate error details |
| DNS resolution failure | `false` | `0` | "Host not found: {hostname}" |
| Request cancelled | `false` | `0` | "Request was cancelled" |

### 7.2 Exception Handling

The service **never throws exceptions** to callers. All errors are captured in the response:

```csharp
var response = await _httpExecutor.ExecuteAsync(json);

if (!response.IsSuccess)
{
    _logger.LogError(response.Exception, 
        "HTTP request failed with status {Status}: {Error}", 
        response.StatusCode,
        response.ErrorMessage);
    
    // Handle specific scenarios
    if (response.StatusCode == 0)
    {
        // Network/connection error
    }
    else if (response.StatusCode == 401)
    {
        // Authentication failed
    }
    else if (response.StatusCode >= 500)
    {
        // Server error
    }
}
```

### 7.3 Retry Behavior

When retry is configured:

1. Initial request is made
2. If response status code matches `retry_status_codes`, wait for `delay_ms`
3. Retry the request
4. If `exponential_backoff` is true, double the delay for next retry
5. Continue until `max_attempts` is reached or request succeeds
6. Return the final response (success or last failure)

```
Attempt 1: Request fails with 503
Wait: 1000ms
Attempt 2: Request fails with 503
Wait: 2000ms (exponential backoff)
Attempt 3: Request succeeds with 200
Return: success response with RetryAttempts = 2
```

---

## 8. Logging

### 8.1 Log Levels

| Level | Events |
|-------|--------|
| `Trace` | Full request/response bodies (only when explicitly enabled) |
| `Debug` | Request/response headers, timing details |
| `Information` | Request initiated, response received (method, URL, status) |
| `Warning` | Retry attempts, non-2xx responses, slow requests |
| `Error` | Request failures, exceptions, validation errors |

### 8.2 Log Examples

```
[INF] HttpExecutor: POST https://api.example.com/users -> 201 Created (156ms)
[WRN] HttpExecutor: GET https://api.example.com/data -> 503 Service Unavailable, retrying (1/3)...
[DBG] HttpExecutor: Request headers: { "Content-Type": "application/json", "Authorization": "[REDACTED]" }
[ERR] HttpExecutor: Request failed: Connection refused (https://offline-api.example.com)
```

### 8.3 Sensitive Data Redaction

Headers matching `SensitiveHeaderPatterns` are redacted in logs:

```csharp
// Configuration
options.SensitiveHeaderPatterns = ["Authorization", "API-Key", "Token", "Secret"];

// Log output
[DBG] Request headers: { 
    "Content-Type": "application/json", 
    "Authorization": "[REDACTED]",
    "X-API-Key": "[REDACTED]",
    "X-Request-Id": "abc-123"
}
```

---

## 9. Future Considerations

### 9.1 Phase 2 Enhancements

- [ ] **File upload support** - `multipart/form-data` with file streams
- [ ] **Response streaming** - For large payloads without buffering entire response
- [ ] **Request/response interceptors** - Middleware pattern for cross-cutting concerns
- [ ] **Metrics/telemetry integration** - OpenTelemetry support

### 9.2 Phase 3 Enhancements

- [ ] **OAuth 2.0 client credentials flow** - Automatic token acquisition and refresh
- [ ] **Circuit breaker pattern** - Prevent cascading failures
- [ ] **Request caching** - With configurable TTL and cache key strategies
- [ ] **Bulk/batch request execution** - Execute multiple requests in parallel
- [ ] **Rate limiting** - Built-in rate limiting per host

### 9.3 Integration Opportunities

- Can be used by `Step5APIGatewayProcess` middleware to replace direct `HttpClient` usage
- Can be used by database query post-processors to make HTTP callbacks
- Can be exposed as a REST endpoint for testing/debugging HTTP configurations

---

## 10. Acceptance Criteria

### 10.1 Functional Requirements

- [x] Service can parse and execute JSON-defined HTTP requests
- [x] All HTTP methods supported (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS)
- [x] Custom headers can be specified per request
- [x] Query parameters can be specified separately or in URL
- [x] JSON body is automatically serialized
- [x] Raw string body is supported for non-JSON content
- [x] Authentication types supported: Basic, Bearer, API Key
- [x] Configurable retry with exponential backoff
- [x] SSL certificate validation can be disabled per-request
- [x] Redirect following can be disabled per-request
- [x] Request validation available without execution
- [x] Strongly-typed request model as alternative to JSON

### 10.2 Non-Functional Requirements

- [x] No exceptions thrown to callers (error details in response)
- [x] Sensitive headers redacted in logs
- [x] Singleton-safe with proper `HttpClient` management via `IHttpClientFactory`
- [x] Thread-safe for concurrent requests
- [x] Configurable timeouts per-request
- [x] Elapsed time tracked for performance monitoring

### 10.3 Testing Requirements

- [ ] Unit tests for JSON parsing and validation
- [ ] Unit tests for request message building
- [ ] Unit tests for authentication handlers
- [ ] Integration tests with mock HTTP server
- [ ] Performance tests for concurrent request handling

---

## Appendix A: JSON Schema (Draft-07)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "HttpExecutorRequest",
  "type": "object",
  "required": ["url"],
  "properties": {
    "url": {
      "type": "string",
      "format": "uri",
      "description": "Target URL for the HTTP request"
    },
    "method": {
      "type": "string",
      "enum": ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"],
      "default": "GET"
    },
    "headers": {
      "type": "object",
      "additionalProperties": { "type": "string" }
    },
    "query": {
      "type": "object",
      "additionalProperties": { "type": "string" }
    },
    "body": {
      "type": ["object", "array", "null"]
    },
    "body_raw": {
      "type": ["string", "null"]
    },
    "content_type": {
      "type": "string",
      "default": "application/json"
    },
    "timeout_seconds": {
      "type": "integer",
      "minimum": 1,
      "maximum": 300,
      "default": 30
    },
    "ignore_certificate_errors": {
      "type": "boolean",
      "default": false
    },
    "follow_redirects": {
      "type": "boolean",
      "default": true
    },
    "auth": {
      "type": ["object", "null"],
      "properties": {
        "type": {
          "type": "string",
          "enum": ["basic", "bearer", "api_key"]
        },
        "username": { "type": "string" },
        "password": { "type": "string" },
        "token": { "type": "string" },
        "key": { "type": "string" },
        "key_header": { "type": "string" },
        "key_query_param": { "type": "string" }
      },
      "required": ["type"]
    },
    "retry": {
      "type": ["object", "null"],
      "properties": {
        "max_attempts": {
          "type": "integer",
          "minimum": 1,
          "maximum": 10,
          "default": 3
        },
        "delay_ms": {
          "type": "integer",
          "minimum": 100,
          "maximum": 60000,
          "default": 1000
        },
        "exponential_backoff": {
          "type": "boolean",
          "default": true
        },
        "retry_status_codes": {
          "type": "array",
          "items": { "type": "integer" },
          "default": [500, 502, 503, 504]
        }
      }
    }
  }
}
```

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-01-20 | GitHub Copilot | Initial draft |
