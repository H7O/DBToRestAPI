# API Key Protection

Protect endpoints using centralized API key collections.

## Overview

1. Define key collections in `/config/api_keys.xml`
2. Reference collections in endpoint configuration
3. Clients send key in `x-api-key` header

## Configuration

### Step 1: Define Collections

`/config/api_keys.xml`:

```xml
<settings>
  <api_keys_collections>
    
    <vendors>
      <key>vendor-key-abc123</key>
      <key>vendor-key-def456</key>
    </vendors>
    
    <internal>
      <key>internal-svc-key-001</key>
    </internal>
    
    <mobile>
      <key>mobile-ios-key</key>
      <key>mobile-android-key</key>
    </mobile>
    
    <admin>
      <key>admin-super-secret</key>
    </admin>
    
  </api_keys_collections>
</settings>
```

### Step 2: Protect Endpoints

`/config/sql.xml`:

```xml
<protected_endpoint>
  <api_keys_collections>vendors,internal</api_keys_collections>
  <route>api/data</route>
  
  <query><![CDATA[SELECT * FROM data;]]></query>
</protected_endpoint>
```

Keys from **any** listed collection are accepted.

## Usage Examples

### Single Collection

```xml
<internal_only>
  <api_keys_collections>internal</api_keys_collections>
  <route>internal/data</route>
  <query><![CDATA[SELECT * FROM internal_data;]]></query>
</internal_only>
```

### Multiple Collections

```xml
<shared_endpoint>
  <api_keys_collections>vendors,internal,mobile</api_keys_collections>
  <route>api/shared</route>
  <query><![CDATA[SELECT * FROM shared_data;]]></query>
</shared_endpoint>
```

### Tiered Access

```xml
<!-- Public - no protection -->
<public_data>
  <route>api/public</route>
  <query><![CDATA[SELECT * FROM public_data;]]></query>
</public_data>

<!-- Partner level -->
<partner_data>
  <api_keys_collections>vendors</api_keys_collections>
  <route>api/partner</route>
  <query><![CDATA[SELECT * FROM partner_data;]]></query>
</partner_data>

<!-- Admin only -->
<admin_data>
  <api_keys_collections>admin</api_keys_collections>
  <route>api/admin</route>
  <query><![CDATA[SELECT * FROM admin_data;]]></query>
</admin_data>
```

## API Gateway Protection

Protect proxy routes in `/config/api_gateway.xml`:

```xml
<protected_proxy>
  <api_keys_collections>vendors</api_keys_collections>
  <url>https://external-api.com/data</url>
  <excluded_headers>x-api-key,host</excluded_headers>
</protected_proxy>
```

Adds authentication to external APIs that don't require it.

## Client Implementation

### cURL

```bash
curl -X GET "https://api.example.com/api/data" \
  -H "x-api-key: vendor-key-abc123"
```

### JavaScript

```javascript
fetch('https://api.example.com/api/data', {
  headers: { 'x-api-key': 'vendor-key-abc123' }
});
```

### Python

```python
requests.get('https://api.example.com/api/data',
  headers={'x-api-key': 'vendor-key-abc123'})
```

### C#

```csharp
client.DefaultRequestHeaders.Add("x-api-key", "vendor-key-abc123");
```

## Key Logging in SQL

Track API key usage:

```sql
-- Hash the key for logging (don't store plain text)
INSERT INTO api_access_log (key_hash, endpoint, accessed_at)
VALUES (HASHBYTES('SHA2_256', {{x-api-key}}), '/api/data', GETUTCDATE());

SELECT * FROM data;
```

## Error Responses

| Scenario | HTTP Status |
|----------|-------------|
| Missing `x-api-key` header | 401 Unauthorized |
| Invalid key | 401 Unauthorized |
| No protection configured | Request proceeds |

## Best Practices

### 1. Descriptive Prefixes

```xml
<vendors>
  <key>vendor-acme-prod-2024</key>
  <key>vendor-globex-prod-2024</key>
</vendors>
```

### 2. Key Rotation

Add new keys before removing old:

```xml
<mobile>
  <key>mobile-v2-new</key>     <!-- New -->
  <key>mobile-v1-old</key>     <!-- Remove after clients update -->
</mobile>
```

### 3. Environment Separation

```xml
<prod_keys>
  <key>prod-vendor-xyz</key>
</prod_keys>

<dev_keys>
  <key>dev-vendor-abc</key>
</dev_keys>
```

## Hot Reload

Changes to `api_keys.xml` apply immediately without restart.

## Combining with JWT

Use both API keys and JWT authentication:

```xml
<highly_secure>
  <api_keys_collections>admin</api_keys_collections>
  <authorize>
    <provider>azure_b2c</provider>
    <required_roles>admin</required_roles>
  </authorize>
  
  <query><![CDATA[
    DECLARE @email NVARCHAR(500) = {auth{email}};
    SELECT * FROM sensitive_data;
  ]]></query>
</highly_secure>
```

## Related Topics

- [Authentication](12-authentication.md) - JWT/OIDC authentication
- [API Gateway](08-api-gateway.md) - Protecting proxy routes
- [Configuration](02-configuration.md) - api_keys.xml details
