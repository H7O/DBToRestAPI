# API Gateway & Proxy Routes

So far, every endpoint you've built executes SQL against your database. The **API Gateway** feature does something completely different — it **proxies requests to external APIs**. This lets you consolidate multiple external services behind your single API, add authentication to unprotected APIs, cache external responses, and manage headers.

## What Is the API Gateway?

```
Client → Your API (DBToRestAPI) → External API (weather, payments, etc.)
                 ↑
         Authentication, caching,
         header management happen here
```

Instead of executing SQL, gateway routes forward the request to another URL and return the external API's response.

## Basic Proxy Route

Define gateway routes in `/config/api_gateway.xml`:

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

Test it:
```bash
curl http://localhost:5165/cat_facts
```

Response (proxied from catfact.ninja):
```json
{
  "fact": "A cat's brain is more similar to a human's brain than that of a dog.",
  "length": 68
}
```

The XML tag name (`cat_facts`) becomes the route, just like `sql.xml` endpoints.

## Custom Routes

Override the default route with `<route>`:

```xml
<cat_facts_custom>
  <route>cat/facts/list</route>
  <url>https://catfact.ninja/fact</url>
  <excluded_headers>host</excluded_headers>
</cat_facts_custom>
```

Now accessible at `GET /cat/facts/list` instead of `/cat_facts_custom`.

## Wildcard Routes

Use `*` to proxy entire API trees:

```xml
<cat_facts_wildcard>
  <route>cat/*</route>
  <url>https://catfact.ninja/</url>
  <excluded_headers>host</excluded_headers>
</cat_facts_wildcard>
```

| Client Request | Proxied To |
|----------------|------------|
| `GET /cat/fact` | `https://catfact.ninja/fact` |
| `GET /cat/facts?limit=3` | `https://catfact.ninja/facts?limit=3` |
| `GET /cat/breeds` | `https://catfact.ninja/breeds` |

Everything after `cat/` is appended to the target URL.

## Header Management

### Excluding Headers

Remove headers before forwarding to the external API:

```xml
<excluded_headers>x-api-key,host,authorization</excluded_headers>
```

**Why exclude headers?**
- **`host`** — Prevents TLS certificate validation errors (the request's Host header is your domain, not the target's)
- **`x-api-key`** — Your local API key shouldn't leak to external APIs
- **`authorization`** — Keep JWT tokens local

### Adding/Overriding Headers

Inject headers into the forwarded request:

```xml
<external_api_with_key>
  <route>api/weather</route>
  <url>https://api.weather.com/v1/current</url>
  <excluded_headers>host</excluded_headers>
  <applied_headers>
    <header>
      <name>X-Weather-API-Key</name>
      <value>weather-service-secret-key</value>
    </header>
    <header>
      <name>Accept</name>
      <value>application/json</value>
    </header>
  </applied_headers>
</external_api_with_key>
```

This lets you hide external API credentials — call `GET /api/weather` without any authentication, and the gateway adds the weather service's API key automatically.

## Adding Authentication to External APIs

Combine API key protection with proxying:

```xml
<protected_cat_facts>
  <api_keys_collections>external_vendors,internal_solutions</api_keys_collections>
  <url>https://catfact.ninja/fact</url>
  <excluded_headers>x-api-key,host</excluded_headers>
</protected_cat_facts>
```

**Without** your API key:
```bash
curl http://localhost:5165/protected_cat_facts
# → 401 Unauthorized
```

**With** your API key:
```bash
curl -H "x-api-key: api key 1" http://localhost:5165/protected_cat_facts
# → Cat fact JSON (200 OK)
```

Notice `excluded_headers` includes `x-api-key` — your API key is consumed locally and not forwarded to catfact.ninja.

## Caching External API Responses

Cache proxied responses to reduce calls to external APIs:

```xml
<cached_cat_facts>
  <route>cached/cat/facts</route>
  <url>https://catfact.ninja/fact</url>
  <excluded_headers>host</excluded_headers>
  <cache>
    <memory>
      <duration_in_milliseconds>20000</duration_in_milliseconds>
    </memory>
  </cache>
</cached_cat_facts>
```

First request hits catfact.ninja. Subsequent requests within 20 seconds return the cached response.

### Excluding Error Responses from Cache

By default, all responses are cached — including errors. To avoid caching error responses:

```xml
<cache>
  <memory>
    <duration_in_milliseconds>300000</duration_in_milliseconds>
    <invalidators>category</invalidators>
    <exclude_status_codes_from_cache>401,403,429,500</exclude_status_codes_from_cache>
  </memory>
</cache>
```

This way, if the external API returns a 429 (rate limited) or 500 (server error), that error response isn't cached, and the next request will try the external API again.

## Ignoring Certificate Errors

For internal APIs with self-signed certificates (development/testing):

```xml
<internal_api>
  <url>https://internal.local:8443/api</url>
  <ignore_target_route_certificate_errors>true</ignore_target_route_certificate_errors>
</internal_api>
```

> **Warning**: Never enable this for production external APIs. It disables TLS verification, making the connection vulnerable to man-in-the-middle attacks.

There's also a global setting in `settings.xml`:
```xml
<ignore_certificate_errors_when_routing>false</ignore_certificate_errors_when_routing>
```

The per-route setting overrides the global one.

## Global Header Exclusion

Headers to always exclude when proxying (defined in `settings.xml`):

```xml
<headers_to_exclude_from_routing>Host</headers_to_exclude_from_routing>
```

This applies to all gateway routes. Per-route `<excluded_headers>` adds to this list.

## Complete Gateway Configuration Reference

```xml
<route_name>
  <!-- URL to proxy to (required) -->
  <url>https://api.example.com/endpoint</url>
  
  <!-- Custom route path (optional, defaults to XML tag name) -->
  <route>custom/path</route>
  
  <!-- API key protection (optional) -->
  <api_keys_collections>vendors,internal</api_keys_collections>
  
  <!-- Headers to remove before forwarding -->
  <excluded_headers>x-api-key,host</excluded_headers>
  
  <!-- Headers to add/override -->
  <applied_headers>
    <header>
      <name>X-External-Key</name>
      <value>secret</value>
    </header>
  </applied_headers>
  
  <!-- Skip TLS verification (development only!) -->
  <ignore_target_route_certificate_errors>false</ignore_target_route_certificate_errors>
  
  <!-- Response caching -->
  <cache>
    <memory>
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      <invalidators>param1,param2</invalidators>
      <exclude_status_codes_from_cache>401,429,500</exclude_status_codes_from_cache>
    </memory>
  </cache>
</route_name>
```

## Use Case: Consolidating APIs

Bring all your external APIs under one domain:

```xml
<settings>
  <routes>
    <weather>
      <route>external/weather/*</route>
      <url>https://api.weather.com/</url>
      <excluded_headers>host</excluded_headers>
    </weather>

    <maps>
      <route>external/maps/*</route>
      <url>https://maps.googleapis.com/</url>
      <excluded_headers>host</excluded_headers>
    </maps>

    <payments>
      <route>external/payments/*</route>
      <api_keys_collections>internal_solutions</api_keys_collections>
      <url>https://api.stripe.com/</url>
      <excluded_headers>host</excluded_headers>
    </payments>
  </routes>
</settings>
```

Clients call your single API (`/external/weather/forecast`, `/external/maps/geocode`, etc.), and you control authentication, caching, and header management centrally.

---

### What You Learned

- How the API Gateway proxies requests to external APIs
- Custom routes with `<route>` and wildcard routes with `*`
- Header management: `<excluded_headers>` and `<applied_headers>`
- Adding API key protection to unprotected external APIs
- Caching proxied responses with status code exclusions
- Certificate error handling for development environments
- Consolidating multiple APIs behind one gateway

---

**Next:** [Multi-Database Queries →](13-multi-database.md)

**[Back to Tutorial Index](index.md)**
