# JWT/OIDC Authentication

Enterprise-grade authentication with Azure B2C, Google, Auth0, Okta, and any OIDC provider.

## Features

- Multi-provider support
- Automatic token validation
- Claims available in SQL as `{auth{claim}}`
- Role and scope enforcement
- UserInfo fallback for missing claims
- Smart caching

## How It Works

1. User signs in via identity provider
2. Client receives JWT token
3. Client sends `Authorization: Bearer {token}`
4. System validates token (signature, issuer, audience, expiration)
5. Claims extracted and available in SQL
6. Your SQL handles authorization logic

## Configuration

### Step 1: Define Providers

`/config/auth_providers.xml`:

```xml
<settings>
  <authorize>
    <providers>
      
      <azure_b2c>
        <authority>https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin</authority>
        <audience>your-api-client-id</audience>
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
        <validate_lifetime>true</validate_lifetime>
        <clock_skew_seconds>300</clock_skew_seconds>
        <userinfo_fallback_claims>email,name</userinfo_fallback_claims>
      </azure_b2c>
      
      <google>
        <authority>https://accounts.google.com</authority>
        <audience>your-client-id.apps.googleusercontent.com</audience>
        <userinfo_fallback_claims>email,name,picture</userinfo_fallback_claims>
      </google>
      
      <auth0>
        <authority>https://your-domain.auth0.com/</authority>
        <audience>https://your-api-identifier</audience>
      </auth0>
      
    </providers>
  </authorize>
</settings>
```

### Step 2: Protect Endpoints

`/config/sql.xml`:

```xml
<protected_endpoint>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  
  <query><![CDATA[
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    DECLARE @user_id NVARCHAR(100) = {auth{sub}};
    
    SELECT * FROM user_data WHERE email = @user_email;
  ]]></query>
</protected_endpoint>
```

## Accessing Claims

Use `{auth{claim_name}}` syntax:

```sql
DECLARE @email NVARCHAR(500) = {auth{email}};
DECLARE @user_id NVARCHAR(100) = {auth{sub}};
DECLARE @name NVARCHAR(500) = {auth{name}};
DECLARE @roles NVARCHAR(500) = {auth{roles}};
```

### Common Claims

| Claim | Syntax | Description |
|-------|--------|-------------|
| `sub` | `{auth{sub}}` | User ID |
| `email` | `{auth{email}}` | Email |
| `name` | `{auth{name}}` | Full name |
| `given_name` | `{auth{given_name}}` | First name |
| `family_name` | `{auth{family_name}}` | Last name |
| `roles` | `{auth{roles}}` | Roles (pipe-delimited) |
| `scope` | `{auth{scope}}` | Scopes |

### Special Characters

Claims with dots/slashes use underscores:
- `user.email` → `{auth{user_email}}`

## Authorization Patterns

### Database-Driven (Recommended)

Most OIDC providers only provide identity (email, name). Store roles in your database:

```sql
DECLARE @email NVARCHAR(500) = {auth{email}};

-- Lookup user role in database
DECLARE @role NVARCHAR(100);
SELECT @role = role FROM app_users WHERE email = @email;

-- Check authorization
IF @role != 'admin'
BEGIN
  THROW 50403, 'Admin access required', 1;
  RETURN;
END

SELECT * FROM admin_data;
```

### First-Time Login (Auto-Registration)

```sql
DECLARE @email NVARCHAR(500) = {auth{email}};
DECLARE @name NVARCHAR(500) = {auth{name}};

-- Create user if not exists
IF NOT EXISTS (SELECT 1 FROM users WHERE email = @email)
BEGIN
  INSERT INTO users (email, name, role, created_at)
  VALUES (@email, @name, 'user', GETUTCDATE());
END

SELECT * FROM users WHERE email = @email;
```

### Role Requirement (Token-Based)

If your provider includes roles in token:

```xml
<authorize>
  <provider>azure_b2c</provider>
  <required_roles>admin,superuser</required_roles>
</authorize>
```

The user must have **all** listed roles (AND logic). In the example above, the user must have both `admin` and `superuser` roles.

### Scope Requirement

```xml
<authorize>
  <provider>auth0</provider>
  <required_scopes>api.read,api.write</required_scopes>
</authorize>
```

The user must have **all** listed scopes (AND logic). In the example above, the token must contain both `api.read` and `api.write` scopes.

## Provider Configuration Options

| Setting | Description |
|---------|-------------|
| `authority` | OIDC discovery URL |
| `audience` | Expected audience (your API client ID) |
| `issuer` | Expected issuer (optional, from discovery) |
| `validate_issuer` | Validate iss claim |
| `validate_audience` | Validate aud claim |
| `validate_lifetime` | Check expiration |
| `clock_skew_seconds` | Allowed time drift (default: 300) |
| `userinfo_fallback_claims` | Fetch missing claims from UserInfo |
| `userinfo_cache_duration_seconds` | Cache UserInfo responses |
| `required_roles` | Required roles (comma-separated) |
| `required_scopes` | Required scopes (comma-separated) |

## Endpoint Overrides

Override provider settings per-endpoint:

```xml
<sensitive_endpoint>
  <authorize>
    <provider>azure_b2c</provider>
    <required_roles>admin</required_roles>
    <clock_skew_seconds>60</clock_skew_seconds>
  </authorize>
  
  <query>...</query>
</sensitive_endpoint>
```

## Disable Authorization

Temporarily disable (for testing):

```xml
<authorize>
  <provider>azure_b2c</provider>
  <enabled>false</enabled>
</authorize>
```

Or simply remove the `<authorize>` section.

## Error Responses

| Scenario | HTTP Status |
|----------|-------------|
| Missing Authorization header | 401 |
| Invalid token format | 401 |
| Token validation failed | 401 |
| Missing required scopes | 403 |
| Missing required roles | 403 |

## Client Implementation

### React with Azure B2C

```jsx
import { PublicClientApplication } from '@azure/msal-browser';

const msalConfig = {
  auth: {
    clientId: 'your-client-id',
    authority: 'https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin'
  }
};

const msalInstance = new PublicClientApplication(msalConfig);

// Get token
const response = await msalInstance.acquireTokenSilent({
  scopes: ['openid'],
  account: msalInstance.getAllAccounts()[0]
});

// Call API
fetch('https://api.example.com/protected', {
  headers: {
    'Authorization': `Bearer ${response.accessToken}`
  }
});
```

### cURL

```bash
curl -X GET "https://api.example.com/protected" \
  -H "Authorization: Bearer eyJhbGciOiJSUzI1NiIs..."
```

## CORS Integration

When `<authorize>` is present, `Access-Control-Allow-Credentials: true` is automatically set.

## Security Best Practices

1. **HTTPS only** in production
2. **Short token lifetimes** (1 hour max)
3. **Validate audience** to prevent token reuse
4. **Don't commit** auth_providers.xml with secrets
5. **Use environment-specific** configs
6. **Implement token refresh** in client apps

## Related Topics

- [API Keys](06-api-keys.md) - Combine with API key protection
- [CORS](11-cors.md) - CORS with credentials
- [Configuration](02-configuration.md) - Provider configuration
