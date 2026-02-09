# JWT & OIDC Authentication

API keys work well for machine-to-machine access. But for **user-facing applications** — web apps, mobile apps, single-page apps — you need proper user authentication. In this topic, you'll learn how to integrate with any OIDC identity provider (Azure AD, Google, Auth0, Okta, etc.).

## How JWT Authentication Works

```
1. User signs in → Identity Provider (Google, Azure, etc.)
2. Provider issues JWT token → Client app
3. Client sends request with: Authorization: Bearer <token>
4. DBToRestAPI validates the token (signature, issuer, audience, expiry)
5. If valid → SQL executes with user claims available
6. If invalid → HTTP 401 Unauthorized
```

The entire flow is handled by the middleware. Your SQL only needs to access the authenticated user's claims.

## Step 1: Configure a Provider

Define your OIDC provider in `/config/auth_providers.xml`:

```xml
<settings>
  <authorize>
    <providers>

      <!-- Azure AD B2C -->
      <azure_b2c>
        <authority>https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin</authority>
        <audience>your-api-client-id</audience>
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
        <validate_lifetime>true</validate_lifetime>
        <clock_skew_seconds>300</clock_skew_seconds>
        <userinfo_fallback_claims>email,name,given_name,family_name</userinfo_fallback_claims>
        <userinfo_cache_duration_seconds>300</userinfo_cache_duration_seconds>
      </azure_b2c>

    </providers>
  </authorize>
</settings>
```

### Key Settings Explained

| Setting | Description |
|---------|-------------|
| `<authority>` | The OIDC discovery URL. The app appends `/.well-known/openid-configuration` automatically. |
| `<audience>` | Your API's client ID registered with the provider. Tokens must be issued for this audience. |
| `<validate_issuer>` | Check the `iss` claim matches the expected issuer. |
| `<validate_audience>` | Check the `aud` claim matches your audience. |
| `<validate_lifetime>` | Reject expired tokens. |
| `<clock_skew_seconds>` | Grace period (in seconds) for token expiry checks. Handles clock differences between servers. |
| `<userinfo_fallback_claims>` | If these claims are missing from the token, fetch them from the UserInfo endpoint. Essential for providers like Google that issue opaque access tokens. |
| `<userinfo_cache_duration_seconds>` | How long to cache UserInfo responses. |

### Multiple Providers

You can define as many providers as you need:

```xml
<providers>
  <azure_b2c>
    <authority>https://yourb2c.b2clogin.com/...</authority>
    <audience>...</audience>
  </azure_b2c>

  <google>
    <authority>https://accounts.google.com</authority>
    <audience>your-google-client-id.apps.googleusercontent.com</audience>
    <userinfo_fallback_claims>email,name,picture</userinfo_fallback_claims>
  </google>

  <auth0>
    <authority>https://your-domain.auth0.com/</authority>
    <audience>https://your-api-identifier</audience>
  </auth0>
</providers>
```

Each endpoint can specify which provider to use.

## Step 2: Protect an Endpoint

Add the `<authorize>` block to any endpoint in `sql.xml`:

```xml
<my_profile>
  <route>profile</route>
  <verb>GET</verb>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query>
    <![CDATA[
    declare @email nvarchar(500) = {auth{email}};
    declare @name nvarchar(500) = {auth{name}};

    select @email as email, @name as name, 'authenticated' as status;
    ]]>
  </query>
</my_profile>
```

### What Happens at Runtime

**With a valid token:**
```bash
curl -H "Authorization: Bearer eyJhbGciOiJSUzI1NiIs..." \
  http://localhost:5165/profile
```

Response (HTTP 200):
```json
{
  "email": "alice@example.com",
  "name": "Alice Smith",
  "status": "authenticated"
}
```

**Without a token:**
```bash
curl http://localhost:5165/profile
```

Response (HTTP 401):
```json
{
  "error": "Unauthorized"
}
```

**With an expired or invalid token:**

Response (HTTP 401) — the middleware rejects it before SQL runs.

## Accessing Claims with `{auth{claim}}`

Once authenticated, all JWT claims are available in your SQL using the `{auth{claim}}` syntax:

```sql
declare @email nvarchar(500) = {auth{email}};
declare @user_id nvarchar(100) = {auth{sub}};
declare @name nvarchar(500) = {auth{name}};
declare @first_name nvarchar(500) = {auth{given_name}};
declare @last_name nvarchar(500) = {auth{family_name}};
declare @roles nvarchar(500) = {auth{roles}};
```

### Common Claims

| Claim | Syntax | Description |
|-------|--------|-------------|
| `sub` | `{auth{sub}}` | Subject — unique user identifier |
| `email` | `{auth{email}}` | User's email address |
| `name` | `{auth{name}}` | Full display name |
| `given_name` | `{auth{given_name}}` | First name |
| `family_name` | `{auth{family_name}}` | Last name |
| `roles` | `{auth{roles}}` | User roles (if included in token) |
| `scope` | `{auth{scope}}` | Token scopes |

### Special Characters in Claim Names

Some providers use dots or URLs as claim names. In the `{auth{}}` syntax, these get mapped to underscores:

| Original Claim | Syntax |
|----------------|--------|
| `user.email` | `{auth{user_email}}` |
| `http://schemas.example.com/role` | `{auth{http___schemas_example_com_role}}` |

## Role and Scope Requirements

### Require Specific Roles

If your provider includes roles in the token:

```xml
<admin_only>
  <route>admin/dashboard</route>
  <authorize>
    <provider>azure_b2c</provider>
    <required_roles>admin,superuser</required_roles>
  </authorize>
  <query><![CDATA[
    select * from admin_dashboard_data;
  ]]></query>
</admin_only>
```

The user must have **all** of the listed roles.

### Require Specific Scopes

```xml
<write_endpoint>
  <route>data</route>
  <verb>POST</verb>
  <authorize>
    <provider>auth0</provider>
    <required_scopes>api.write</required_scopes>
  </authorize>
  <query><![CDATA[ ... ]]></query>
</write_endpoint>
```

## Authorization Patterns in SQL

### Database-Driven Authorization (Recommended)

Most OIDC providers only give you identity (email, name). Store application-specific roles and permissions in your own database:

```sql
declare @email nvarchar(500) = {auth{email}};
declare @name nvarchar(500) = {auth{name}};

-- Look up user role in our database
declare @role nvarchar(100);
select @role = role from app_users where email = @email;

-- Auto-register on first login
if @role is null
begin
  insert into app_users (email, name, role, created_at)
  values (@email, @name, 'user', getutcdate());
  set @role = 'user';
end

-- Check authorization for this specific action
if @role not in ('admin', 'manager')
begin
  throw 50403, 'Insufficient permissions. Admin or manager role required.', 1;
  return;
end

-- Authorized — return the data
select * from sensitive_data;
```

This pattern gives you:
- **Auto-registration** — new users are created on first login
- **Flexible roles** — managed in your database, not your identity provider
- **Granular control** — different endpoints can check different permissions

### Row-Level Security

Use the authenticated user's identity to filter data:

```sql
declare @email nvarchar(500) = {auth{email}};

-- Users only see their own data
select * from orders where user_email = @email;
```

### Combining API Keys + JWT Auth

You can use both on the same endpoint:

```xml
<dual_protected>
  <api_keys_collections>internal_solutions</api_keys_collections>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query><![CDATA[ ... ]]></query>
</dual_protected>
```

The request must pass **both** checks — a valid API key AND a valid JWT token.

---

### What You Learned

- How JWT/OIDC authentication works in the request pipeline
- How to configure identity providers in `auth_providers.xml`
- How to protect endpoints with `<authorize>`
- How to access user claims in SQL with `{auth{claim}}`
- Role and scope requirements at the endpoint level
- Database-driven authorization patterns
- Row-level security using authenticated identity

---

**Next:** [Using Claims in Queries →](10-claims-in-queries.md)

**[Back to Tutorial Index](index.md)**
