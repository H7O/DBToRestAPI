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

### Apply Headers

Add or override headers:

```xml
<external_api>
  <url>https://api.example.com/data</url>
  <applied_headers>
    <header>
      <n>X-API-Key</n>
      <value>external-api-secret-key</value>
    </header>
    <header>
      <n>Accept</n>
      <value>application/json</value>
    </header>
  </applied_headers>
</external_api>
```

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
| `max_per_value_cache_size` | Max chars per invalidator value |

### Default Caching Behavior

By default, **all responses are cached** including errors. This protects external APIs during:
- High traffic
- Temporary outages
- Rate limiting

## Ignore Certificate Errors

For development/internal APIs with self-signed certs:

```xml
<internal_api>
  <url>https://internal.local/api</url>
  <ignore_certificate_errors>true</ignore_certificate_errors>
</internal_api>
```

⚠️ **Never use in production with external APIs**

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
          <n>X-Weather-API-Key</n>
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
- [Configuration](02-configuration.md) - api_gateway.xml structure
