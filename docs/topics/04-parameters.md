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

A JSON `null` counts as missing, so the next source down fills `{{name}}`: with `{"name": null}` in the body, a `name` header fills it. If you override the marker patterns and give the body and the headers the identical pattern string, the body's `null` hides the header instead, and `{{name}}` is `NULL`.

To read one source only, use its own marker. A name that source doesn't have is `NULL`, whatever the other sources hold:

| Marker | Reads |
|--------|-------|
| `{r{id}}` | the route |
| `{qs{id}}` | the query string |
| `{j{id}}` | the JSON body |
| `{f{id}}` | a form field |
| `{h{id}}` | a header |

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

## Names With Spaces and Other Characters

Write a name exactly as the caller sends it, whatever characters it holds: spaces, dashes, dots, `@`, accented letters, symbols. The engine never renames an input, so `{{first_name}}` doesn't read a JSON key named `first name`: it matches nothing and is `NULL`. Spaces inside the braces are part of the name too: `{{ name }}` reads a key named ` name `, not `name`.

```sql
-- Body: {"first name": "Ann", "e-mail": "ann@example.com", "unit price ($)": 5, "@type": "person"}
DECLARE @first_name NVARCHAR(200) = {{first name}};
DECLARE @email NVARCHAR(320) = {{e-mail}};
DECLARE @unit_price DECIMAL(10, 2) = {{unit price ($)}};
DECLARE @kind NVARCHAR(50) = {{@type}};
```

The same holds for a query-string key (`?sort-by=name` gives `{{sort-by}}`), a form field, a header (`{{X-Tenant-Id}}`) and a claim ([below](#special-characters-in-claims)). A name is matched without regard to case, so `{{First Name}}` reads `first name` too, except in `mandatory_parameters`. Each marker is sent to the database as a parameter whose generated name holds only ASCII letters, digits and underscores, so the characters of your name never reach the SQL text or the parameter's name. A name can't hold a line break or `}}`, and can't end with `}`.

A dot is part of the name, not a path: `{{address.city}}` reads a key named `address.city`. A nested object or array arrives as its JSON text, so read it in SQL (`JSON_VALUE({{address}}, '$.city')` on SQL Server).

One query can spell names in several ways. Spellings that differ only in letter case (`{{Email}}` and `{{email}}`) read the same value. Names that differ in any other character are different inputs: `{{first name}}` reads the key `first name`, and `{{first-name}}` the key `first-name`. One name can also appear in several marker forms (`{{email}}` and `{j{email}}`), each reading the sources its form reads ([above](#parameter-sources)).

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

The check is presence-only. A name passes when it arrives from any source, including headers and JWT claims. An empty or null value still passes, so validate values in SQL, testing `IS NULL` first (see [Parameter Validation](#parameter-validation)). Names are matched case-sensitively. Separate them with commas or line breaks. A name may contain spaces (`<mandatory_parameters>first name, e-mail</mandatory_parameters>`). When a name contains a comma, separate the names with `|` instead (`<mandatory_parameters>last, first|e-mail</mandatory_parameters>`): a list that contains a `|` is split only on `|` and line breaks.

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
| `user_id` | `{auth{user_id}}` | The user's id: the token's subject (`sub`), or `oid` when it has none |
| `sub` | `{auth{sub}}` | The token's subject. Prefer `user_id`, which falls back to `oid` |
| `email` | `{auth{email}}` | Email address |
| `name` | `{auth{name}}` | Full name |
| `roles` | `{auth{roles}}` | User roles (pipe-delimited) |
| `auth_time` | `{auth{auth_time}}` | Login instant as Unix time (seconds): the `auth_time` claim when present, otherwise `iat` |
| `auth_provider` | `{auth{auth_provider}}` | Resolved provider name (e.g. `google`, `azure_b2c`) |
| `scp`, `scope` | `{auth{scp}}`, `{auth{scope}}` | Scopes, space-separated, from whichever claim the provider sends (`scp` for Entra ID and Okta) |

.NET renames many token claims on the way in: `sub` becomes `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`, and `oid`, `given_name`, `family_name`, `tid`, `scp`, `email` and `roles` get long names too. Every claim is available under the name the token used and under its long type (see below), so `{auth{sub}}`, `{auth{oid}}`, `{auth{tid}}`, `{auth{scp}}`, `{auth{given_name}}` and `{auth{family_name}}` all work. Where several claims share a name (two `role` claims, say), the placeholder holds the first one. `{auth{roles}}` holds every role, and `{auth{scp}}` and `{auth{scope}}` hold every scope, space-separated. The engine also adds `user_id`, `email`, `name`, `roles`, `auth_time` and `auth_provider`, which work the same for every provider. When the token lacks a claim listed in `userinfo_fallback_claims`, the engine calls the provider's UserInfo endpoint and adds, as text, the fields the token doesn't have. A claim the token carries keeps the token's value, and `required_scopes` and `required_roles` read the token only.

A provider that sends the same subject for every user gives every user the same `user_id`. For example, an Azure AD B2C user flow with the subject claim turned off sends `Not supported` as the subject. Configure the provider to send a unique subject (in B2C, the object ID).

On a route without `<authorize>`, every `{auth{...}}` placeholder is `NULL`.

### Special Characters in Claims

Write a claim exactly as its claim type, including dots and slashes. Don't replace them with underscores:

| Claim type | Syntax |
|----------------|--------|
| `user.email` | `{auth{user.email}}` |
| `http://schemas.example.com/role` | `{auth{http://schemas.example.com/role}}` |
| `tid` under the long type .NET gives it (`{auth{tid}}` also works) | `{auth{http://schemas.microsoft.com/identity/claims/tenantid}}` |

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

In multi-query endpoints, the columns of a query that returns one row become parameters of the queries after it:

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
