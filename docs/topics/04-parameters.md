# Parameters

This document covers parameter injection, route parameters, and mandatory parameter enforcement.

## Parameter Sources

Parameters can come from multiple sources. When the same `{{name}}` arrives from more than one source, the source higher in this list wins:

1. **Route parameters**: `/users/{{id}}` → `{{id}}` from URL
2. **Query string**: `/users?name=John` → `{{name}}` = "John"
3. **Request body** (JSON or form): `{"name": "John"}` → `{{name}}` = "John"
4. **HTTP headers**: `{{Content-Type}}`, `{{Authorization}}`
5. **JWT claims**: `{auth{email}}`, `{auth{user_id}}`
6. **Settings variables**: `{s{api_key}}`, `{settings{api_url}}` (from `<vars>` in config)

A route segment cannot be overridden from the query string. JWT claims and settings variables use their own markers, so they never compete with `{{name}}`.

## Basic Parameter Injection

```xml
<query><![CDATA[
  DECLARE @name NVARCHAR(500) = {{name}};
  DECLARE @age INT = {{age}};
  DECLARE @active BIT = {{active}};
  
  SELECT * FROM users 
  WHERE name = @name 
    AND age = @age 
    AND active = @active;
]]></query>
```

**SQL injection protected** — parameters are bound using ADO.NET parameterization.

## Route Parameters

Define parameters in the route path:

```xml
<get_user_order>
  <route>users/{{user_id}}/orders/{{order_id}}</route>
  <verb>GET</verb>
  
  <query><![CDATA[
    DECLARE @user_id UNIQUEIDENTIFIER = {{user_id}};
    DECLARE @order_id INT = {{order_id}};
    
    SELECT * FROM orders 
    WHERE user_id = @user_id AND id = @order_id;
  ]]></query>
</get_user_order>
```

**Request:** `GET /users/abc-123/orders/456`

## Mandatory Parameters

Enforce required parameters (returns HTTP 400 if missing):

```xml
<create_user>
  <mandatory_parameters>name,email</mandatory_parameters>
  
  <query><![CDATA[
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @email NVARCHAR(500) = {{email}};
    DECLARE @phone NVARCHAR(100) = {{phone}};  -- Optional
    
    INSERT INTO users (name, email, phone) VALUES (@name, @email, @phone);
  ]]></query>
</create_user>
```

Missing `name` or `email` → HTTP 400 Bad Request

The check is presence-only. A name passes when it arrives from any source, including headers and JWT claims. An empty or null value still passes, so validate values in SQL, testing `IS NULL` first (see [Parameter Validation](#parameter-validation)). Names are matched case-sensitively.

## Default Values

Handle optional parameters with SQL defaults:

```sql
DECLARE @take INT = ISNULL({{take}}, 100);
DECLARE @skip INT = ISNULL({{skip}}, 0);
DECLARE @sort NVARCHAR(50) = ISNULL({{sort}}, 'created_at');

-- Or use COALESCE
DECLARE @status NVARCHAR(50) = COALESCE({{status}}, 'active');
```

## HTTP Header Parameters

Access HTTP headers as parameters:

```sql
DECLARE @content_type NVARCHAR(500) = {{Content-Type}};
DECLARE @user_agent NVARCHAR(500) = {{User-Agent}};
DECLARE @custom_header NVARCHAR(500) = {{X-Custom-Header}};
```

## JWT Claim Parameters

When using OIDC/JWT authentication, access claims with `{auth{}}`:

```sql
DECLARE @user_email NVARCHAR(500) = {auth{email}};
DECLARE @user_id NVARCHAR(100) = {auth{user_id}};
DECLARE @user_name NVARCHAR(500) = {auth{name}};
DECLARE @user_roles NVARCHAR(500) = {auth{roles}};
```

### Common Claims

| Claim | Syntax | Description |
|-------|--------|-------------|
| `user_id` | `{auth{user_id}}` | The user's id, the token's subject (`sub`) |
| `email` | `{auth{email}}` | Email address |
| `name` | `{auth{name}}` | Full name |
| `roles` | `{auth{roles}}` | User roles (pipe-delimited) |
| `auth_time` | `{auth{auth_time}}` | Login instant as Unix time (seconds): the `auth_time` claim when present, otherwise `iat` |
| `auth_provider` | `{auth{auth_provider}}` | Resolved provider name (e.g. `google`, `azure_b2c`) |
| `scope` | `{auth{scope}}` | Token scopes, from a claim named `scope` |

.NET renames many token claims on the way in: `sub`, `oid`, `given_name`, `family_name`, `tid`, `scp` and others. The engine always adds short names for `user_id`, `email`, `name`, `roles`, `auth_time` and `auth_provider`. Any other claim must be written with its full claim type. A claim .NET doesn't rename keeps its name from the token, such as `scope`. A renamed claim is available only under its long type (see below). So in 1.7.5 `{auth{sub}}`, `{auth{given_name}}` and `{auth{family_name}}` are never filled from the token, and `{auth{tid}}` and `{auth{scp}}` are never filled at all: don't use them. While `given_name` or `family_name` is listed in `userinfo_fallback_claims` (the default, and the shipped `auth_providers.xml`), the engine also calls the provider's UserInfo endpoint for each new token and adds the fields it returns (`sub`, `given_name`, `family_name` and so on) under their short names, as JSON values the database driver can't bind, so a query that uses one fails with the generic 400. Read the given and family names as `{auth{http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname}}` and `{auth{http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname}}`.

A provider that sends the same subject for every user gives every user the same `user_id`. For example, an Azure AD B2C user flow with the subject claim turned off sends `Not supported` as the subject. Configure the provider to send a unique subject (in B2C, the object ID).

On a route without `<authorize>`, every `{auth{...}}` placeholder is `NULL`.

### Special Characters in Claims

Write a claim exactly as its claim type, including dots and slashes. Don't replace them with underscores:

| Claim type | Syntax |
|----------------|--------|
| `user.email` | `{auth{user.email}}` |
| `http://schemas.example.com/role` | `{auth{http://schemas.example.com/role}}` |
| `tid` (renamed by .NET) | `{auth{http://schemas.microsoft.com/identity/claims/tenantid}}` |

## Parameter Validation

Validate parameters in SQL. Test `IS NULL` first: a comparison with `NULL` is never true, so `IF @status NOT IN (...)` lets a missing value through.

```sql
DECLARE @email NVARCHAR(500) = {{email}};
DECLARE @age INT = {{age}};

-- Validate email format
IF @email IS NULL OR @email NOT LIKE '%@%.%'
BEGIN
  THROW 50400, 'Invalid email format', 1;
  RETURN;
END

-- Validate range
IF @age IS NULL OR @age < 0 OR @age > 150
BEGIN
  THROW 50400, 'Age must be between 0 and 150', 1;
  RETURN;
END

-- Validate enum
DECLARE @status NVARCHAR(50) = {{status}};
IF @status IS NULL OR @status NOT IN ('active', 'inactive', 'pending')
BEGIN
  THROW 50400, 'Invalid status value', 1;
  RETURN;
END
```

## Array Parameters

Handle JSON arrays in parameters:

```sql
DECLARE @ids NVARCHAR(MAX) = {{ids}};  -- Sent as JSON array

-- Parse with OPENJSON
SELECT * FROM products 
WHERE id IN (
  SELECT value FROM OPENJSON(@ids)
);
```

**Request:**
```json
{"ids": ["id1", "id2", "id3"]}
```

## Query Chaining Parameters

In multi-query endpoints, previous query results become parameters:

```xml
<!-- Query 1 output columns become Query 2 parameters -->
<query><![CDATA[
  SELECT 'John' AS user_name, 123 AS user_id;
]]></query>

<query><![CDATA[
  -- From Query 1: user_name = 'John', user_id = 123
  SELECT * FROM orders WHERE user_id = {pq{user_id}};
]]></query>
```

Read an earlier query's columns with `{pq{name}}`. With `{{name}}`, a `NULL` column, zero rows or several rows let a request value with the same name fill the placeholder instead.

For multiple rows, use `{pq{json}}`:

```sql
-- Query 2 receives Query 1 results as JSON array
SELECT * FROM products 
WHERE id IN (
  SELECT JSON_VALUE(value, '$.product_id') 
  FROM OPENJSON({pq{json}})
);
```

## Related Topics

- [CRUD Operations](03-crud-operations.md) - Using parameters in queries
- [Authentication](12-authentication.md) - JWT claim parameters
- [Query Chaining](14-query-chaining.md) - Parameter passing between queries
- [Settings Variables](21-settings-vars.md) - Configuration values in queries
