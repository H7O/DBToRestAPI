# API Gateway

Route requests to external APIs while adding authentication, caching, and header management.

## Overview

The API gateway allows you to:
- Consolidate multiple APIs under one base URL
- Add authentication to unprotected APIs
- Cache external API responses
- Manage headers before forwarding

## Basic Configuration

`/config/api_gateway.xml`:

```xml
<settings>
  <routes>
    
    <cat_facts>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>host</excluded_headers>
    </cat_facts>
    
  </routes>
</settings>
```

**Request:** `GET /cat_facts`  
**Proxied to:** `https://catfact.ninja/fact`

## Route Configuration

### Custom Route Path

```xml
<weather>
  <route>api/weather/current</route>
  <url>https://api.weather.com/v1/current</url>
</weather>
```

**Request:** `GET /api/weather/current`

### Wildcard Routes

```xml
<github_api>
  <route>github/*</route>
  <url>https://api.github.com/</url>
</github_api>
```

- `GET /github/users/octocat` → `https://api.github.com/users/octocat`
- `GET /github/repos/H7O/DBToRestAPI` → `https://api.github.com/repos/H7O/DBToRestAPI`

## Header Management

### Exclude Headers

Remove headers before forwarding:

```xml
<external_api>
  <url>https://api.example.com/data</url>
  <excluded_headers>x-api-key,host,authorization</excluded_headers>
</external_api>
```

- `x-api-key` — Don't expose your API key
- `host` — Prevents TLS errors
- `authorization` — Keep JWT tokens local

A route without `<excluded_headers>` uses the global `<excluded_headers>` in `settings.xml` (the shipped file sets `Host`). A route's own list replaces the global one, so list `host` in it too. An empty `<excluded_headers></excluded_headers>` counts as not set, so the global list applies: a route can't forward every header while the global list drops some. Separate the names with commas, spaces or line breaks. They are matched ignoring case.

The gateway keeps no cookies. A caller's `Cookie` header is forwarded like any other header (exclude
`cookie` to drop it), and the target's `Set-Cookie` goes back to that caller. A response that
carries a `Set-Cookie` is never cached, so no other caller receives it. The gateway follows
redirects itself: a `Set-Cookie` sent with a redirect is dropped, and only the final response's
cookies reach the caller.

### Apply Headers

Add or override headers:

```xml
<external_api>
  <url>https://api.example.com/data</url>
  <applied_headers>
    <header>
      <name>X-API-Key</name>
      <value>external-api-secret-key</value>
    </header>
    <header>
      <name>Accept</name>
      <value>application/json</value>
    </header>
  </applied_headers>
</external_api>
```

Each child of `<applied_headers>` is one header, with its `<name>` and `<value>`. An applied header replaces a caller's header with the same name.

## API Key Protection

Add authentication to external APIs:

```xml
<protected_weather>
  <api_keys_collections>mobile,internal</api_keys_collections>
  <url>https://api.weather.com/current</url>
  <excluded_headers>x-api-key,host</excluded_headers>
</protected_weather>
```

- Client must send valid `x-api-key`
- Key is not forwarded to external API
- Only `<api_keys_collections>` protects a route. A route with an `<api_keys>` block and no collections is open.

## Caching

Cache external API responses:

```xml
<cached_api>
  <url>https://api.example.com/data</url>
  
  <cache>
    <memory>
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      <invalidators>category,region</invalidators>
      <exclude_status_codes_from_cache>401,403,429</exclude_status_codes_from_cache>
    </memory>
  </cache>
</cached_api>
```

### Cache Options

| Setting | Description |
|---------|-------------|
| `duration_in_milliseconds` | How long to cache |
| `invalidators` | Query params that create separate cache entries |
| `exclude_status_codes_from_cache` | Don't cache these HTTP codes |

> Invalidator values are hashed (64-bit xxHash3) before they enter the cache key, so a value of any length — a bearer token, say — is safe to nominate, and two requests that differ only in a long value never share an entry. A `max_per_value_cache_size` setting is ignored.

### Default Caching Behavior

Only GET and HEAD requests are cached. Other verbs are always forwarded, because the request body is not part of the key.

By default, **all responses are cached** including errors. A response with an excluded status, or with a `Set-Cookie` header, is passed to its caller and not stored, so the next request is forwarded again. Caching errors protects external APIs during:
- High traffic
- Temporary outages
- Rate limiting by the external API

A cached route reads each answer whole, into memory, before it is stored and sent, so cache only answers of a size the server can hold. A route without `<cache>`, or a request the cache doesn't handle (a POST, say), passes the answer to the caller as it arrives and never holds it whole.

## Ignore Certificate Errors

For development/internal APIs with self-signed certs:

```xml
<internal_api>
  <url>https://internal.local/api</url>
  <ignore_target_route_certificate_errors>true</ignore_target_route_certificate_errors>
</internal_api>
```

A route without it uses the global `<ignore_target_route_certificate_errors>` in `settings.xml` (`false` when not set).

⚠️ **Never use in production with external APIs**

## Outbound Timeout

`target_route_timeout_seconds` limits how long the engine waits for the target. When the answer is passed on as it arrives (a route without `<cache>`), the limit covers the wait for the target's headers, then each wait for more of its answer: a long download goes on while data keeps coming, and a target that sends nothing for that long is cut off. A cached answer is read whole, so all of it must arrive within the limit. A target that times out before any of its answer has been sent gets its caller `504` ([when the target fails](#when-the-target-fails)).

Set per-route timeout in seconds:

```xml
<weather>
  <url>https://api.weather.com/v1/current</url>
  <target_route_timeout_seconds>30</target_route_timeout_seconds>
</weather>
```

Resolution order:
1. Route `target_route_timeout_seconds`
2. Global `target_route_timeout_seconds`
3. Default: `30` seconds

An outbound call stops when its caller disconnects. On a route with `<cache>`, a call that other callers are waiting on keeps running until all of them have gone ([caching](07-caching.md#requests-that-arrive-while-the-query-runs)).

## When the Target Fails

The target's own answer passes through unchanged, an error status included. When the target gives no answer, the caller gets one of these statuses, with the body `{"success":false,"message":"An error occurred while processing your request."}` (the `generic_error_message` of `settings.xml`):

| Status | When |
|---|---|
| `502` | The target couldn't be reached or its answer couldn't be read: no such host, a refused or dropped connection, a TLS failure |
| `504` | The target didn't answer within `target_route_timeout_seconds` |

Each is logged as an error naming the route and the target's URL, without its query string. A caller's body over `max_payload_size_in_bytes` is `413`, and in debug mode every failure is a `500` with the exception's details ([errors.md](../reference/errors.md#api-gateway-routes)).

An answer passed on as it arrives takes the target's status and headers with its first block. If the target fails after that (it breaks off, or sends nothing more within `target_route_timeout_seconds`), the status can't change any more. The engine then cuts the connection, so the caller gets a network error rather than a cut-off answer that looks whole, and logs an error.

## Complete Example

```xml
<settings>
  <routes>
    
    <!-- Public proxy -->
    <public_facts>
      <route>api/facts</route>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>host</excluded_headers>
    </public_facts>
    
    <!-- Protected with caching -->
    <weather>
      <route>api/weather/*</route>
      <api_keys_collections>mobile</api_keys_collections>
      <url>https://api.weather.com/</url>
      <excluded_headers>x-api-key,host</excluded_headers>
      <applied_headers>
        <header>
          <name>X-Weather-API-Key</name>
          <value>weather-service-key</value>
        </header>
      </applied_headers>
      <cache>
        <memory>
          <duration_in_milliseconds>600000</duration_in_milliseconds>
          <invalidators>city,units</invalidators>
        </memory>
      </cache>
    </weather>
    
  </routes>
</settings>
```

## Use Cases

### Consolidate Multiple APIs

```xml
<weather_api>
  <route>external/weather/*</route>
  <url>https://api.weather.com/</url>
</weather_api>

<maps_api>
  <route>external/maps/*</route>
  <url>https://api.maps.com/</url>
</maps_api>

<news_api>
  <route>external/news/*</route>
  <url>https://api.news.com/</url>
</news_api>
```

All accessible via your single API endpoint.

### Add Auth to Open APIs

```xml
<open_api_protected>
  <api_keys_collections>internal</api_keys_collections>
  <url>https://completely-open-api.com/</url>
</open_api_protected>
```

### Rate Limit Protection

This is about a limit the *external* API imposes on you: caching keeps your call count under
it. To limit how fast *your own callers* may hit a gateway route, add a `<rate_limit>` block to
the route instead; see [Rate Limiting](18-rate-limiting.md).

Cache with short duration to reduce hits:

```xml
<rate_limited_api>
  <url>https://api-with-limits.com/data</url>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
    </memory>
  </cache>
</rate_limited_api>
```

## Related Topics

- [Caching](07-caching.md) - Cache configuration details
- [API Keys](06-api-keys.md) - Protecting routes
- [Rate Limiting](18-rate-limiting.md) - Limiting your own callers on gateway routes
- [Configuration](02-configuration.md) - api_gateway.xml structure
