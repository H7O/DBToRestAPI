# CORS Configuration

Control Cross-Origin Resource Sharing with pattern matching and per-endpoint settings.

## How It Works

1. Browser sends `Origin` header
2. System checks CORS config (endpoint → global → default)
3. Origin matched against regex pattern
4. If matched: origin allowed; if not: fallback origin used
5. CORS headers added to response

## Configuration Hierarchy

1. **Endpoint-specific** — `<cors>` in sql.xml
2. **Global** — `<cors>` in settings.xml
3. **Default** — Allows all origins (`*`)

## Basic Configuration

### Endpoint-Level

```xml
<my_api>
  <cors>
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
    <max_age>3600</max_age>
    <allow_credentials>true</allow_credentials>
    <allowed_headers>Content-Type, Authorization, X-Api-Key</allowed_headers>
  </cors>
  
  <query><![CDATA[SELECT 'data' AS result;]]></query>
</my_api>
```

### Global (settings.xml)

```xml
<settings>
  <cors>
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
    <max_age>86400</max_age>
  </cors>
</settings>
```

## Configuration Options

| Setting | Default | Description |
|---------|---------|-------------|
| `pattern` | None | Regex to match allowed origins |
| `fallback_origin` | `*` | Origin when pattern doesn't match |
| `max_age` | 86400 | Preflight cache (seconds) |
| `allow_credentials` | false | Allow cookies/auth headers |
| `allowed_headers` | `*` | Comma-separated allowed headers |

## Pattern Examples

```xml
<!-- Localhost only -->
<pattern><![CDATA[^localhost$]]></pattern>

<!-- Localhost with any port -->
<pattern><![CDATA[^localhost(:\d+)?$]]></pattern>

<!-- Any subdomain of example.com -->
<pattern><![CDATA[^.*\.example\.com$]]></pattern>

<!-- Multiple specific domains -->
<pattern><![CDATA[^(example\.com|another\.com)$]]></pattern>

<!-- Localhost OR example.com subdomains -->
<pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>

<!-- Multiple domain families -->
<pattern><![CDATA[^.*\.(example\.com|myapp\.io)$]]></pattern>
```

## Behavior Examples

### Origin Matches Pattern

```
Request: Origin: https://app.example.com
Pattern: ^.*\.example\.com$
Result: Access-Control-Allow-Origin: https://app.example.com
```

### Origin Doesn't Match

```
Request: Origin: https://evil.com
Pattern: ^.*\.example\.com$
Fallback: https://www.example.com
Result: Access-Control-Allow-Origin: https://www.example.com
Browser: Blocks response (origin mismatch)
```

### No Origin Header (API Client)

```
Request: (no Origin header)
Result: Access-Control-Allow-Origin: https://www.example.com
```

## Preflight Requests

Automatic handling of `OPTIONS` requests:

```
OPTIONS /api/data
Origin: https://app.example.com
Access-Control-Request-Method: POST
Access-Control-Request-Headers: Content-Type

Response:
Access-Control-Allow-Origin: https://app.example.com
Access-Control-Allow-Methods: POST, OPTIONS
Access-Control-Allow-Headers: Content-Type
Access-Control-Max-Age: 3600
```

## Allowed Methods

Auto-determined from `<verb>`:

```xml
<my_api>
  <verb>GET,POST</verb>
  <!-- Results in: Access-Control-Allow-Methods: GET, POST, OPTIONS -->
</my_api>
```

No `<verb>` = all methods allowed.

## Credentials

When `allow_credentials` is true:
- Cannot use `*` for origin
- System uses matched origin or fallback
- Cookies and auth headers are allowed

```xml
<cors>
  <pattern><![CDATA[^app\.example\.com$]]></pattern>
  <fallback_origin>https://app.example.com</fallback_origin>
  <allow_credentials>true</allow_credentials>
</cors>
```

**Note:** Automatically set to `true` when `<authorize>` section exists.

## Common Scenarios

### Development (Allow All)

```xml
<cors>
  <fallback_origin>*</fallback_origin>
</cors>
```

### Production (Specific Domains)

```xml
<cors>
  <pattern><![CDATA[^(www|app)\.mycompany\.com$]]></pattern>
  <fallback_origin>https://www.mycompany.com</fallback_origin>
  <allow_credentials>true</allow_credentials>
</cors>
```

### Multi-Tenant

```xml
<cors>
  <pattern><![CDATA[^.*\.myapp\.com$]]></pattern>
  <fallback_origin>https://www.myapp.com</fallback_origin>
</cors>
```

### Mixed (Some Strict, Some Open)

```xml
<!-- settings.xml - default -->
<cors>
  <pattern><![CDATA[^localhost$]]></pattern>
  <fallback_origin>*</fallback_origin>
</cors>

<!-- sql.xml - strict endpoint -->
<sensitive_api>
  <cors>
    <pattern><![CDATA[^app\.example\.com$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
    <allow_credentials>true</allow_credentials>
  </cors>
  <query>...</query>
</sensitive_api>

<!-- sql.xml - uses global -->
<public_api>
  <!-- No cors section = inherits global -->
  <query>...</query>
</public_api>
```

## Testing

### cURL

```bash
# With Origin header
curl -i -H "Origin: https://app.example.com" \
  https://localhost:7054/api/data

# Preflight
curl -i -X OPTIONS \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: POST" \
  https://localhost:7054/api/data
```

### JavaScript

```javascript
fetch('https://api.example.com/data', {
  credentials: 'include'  // For allow_credentials: true
})
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| CORS error in browser | Check Origin matches pattern |
| Missing credentials | Set `allow_credentials: true` |
| Headers rejected | Add to `allowed_headers` |
| Works in Postman, not browser | Postman ignores CORS; test with real browser |

## Security Best Practices

1. Use specific patterns, not `.*`
2. Don't use `*` with credentials
3. Limit `allowed_headers`
4. Use different configs for dev/prod
5. Set reasonable `max_age`

## Related Topics

- [Authentication](12-authentication.md) - Auto credentials with JWT
- [Configuration](02-configuration.md) - Global CORS settings
