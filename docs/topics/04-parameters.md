# Parameters

This document covers parameter injection, route parameters, and mandatory parameter enforcement.

## Parameter Sources

Parameters can come from multiple sources (checked in this order):

1. **Route parameters**: `/users/{{id}}` → `{{id}}` from URL
2. **Query string**: `/users?name=John` → `{{name}}` = "John"
3. **Request body**: `{"name": "John"}` → `{{name}}` = "John"
4. **HTTP headers**: `{{Content-Type}}`, `{{Authorization}}`
5. **JWT claims**: `{auth{email}}`, `{auth{sub}}`

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
DECLARE @user_id NVARCHAR(100) = {auth{sub}};
DECLARE @user_name NVARCHAR(500) = {auth{name}};
DECLARE @user_roles NVARCHAR(500) = {auth{roles}};
```

### Common Claims

| Claim | Syntax | Description |
|-------|--------|-------------|
| `sub` | `{auth{sub}}` | Subject (user ID) |
| `email` | `{auth{email}}` | Email address |
| `name` | `{auth{name}}` | Full name |
| `given_name` | `{auth{given_name}}` | First name |
| `family_name` | `{auth{family_name}}` | Last name |
| `roles` | `{auth{roles}}` | User roles |
| `scope` | `{auth{scope}}` | Token scopes |

### Special Characters in Claims

Claims with dots, slashes, or special chars use underscores:

| Original Claim | Syntax |
|----------------|--------|
| `user.email` | `{auth{user_email}}` |
| `http://schemas.example.com/role` | `{auth{http___schemas_example_com_role}}` |

## Parameter Validation

Validate parameters in SQL:

```sql
DECLARE @email NVARCHAR(500) = {{email}};
DECLARE @age INT = {{age}};

-- Validate email format
IF @email NOT LIKE '%@%.%'
BEGIN
  THROW 50400, 'Invalid email format', 1;
  RETURN;
END

-- Validate range
IF @age < 0 OR @age > 150
BEGIN
  THROW 50400, 'Age must be between 0 and 150', 1;
  RETURN;
END

-- Validate enum
DECLARE @status NVARCHAR(50) = {{status}};
IF @status NOT IN ('active', 'inactive', 'pending')
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
  -- {{user_name}} = 'John', {{user_id}} = 123
  SELECT * FROM orders WHERE user_id = {{user_id}};
]]></query>
```

For multiple rows, use `{{json}}`:

```sql
-- Query 2 receives Query 1 results as JSON array
SELECT * FROM products 
WHERE id IN (
  SELECT JSON_VALUE(value, '$.product_id') 
  FROM OPENJSON({{json}})
);
```

## Related Topics

- [CRUD Operations](03-crud-operations.md) - Using parameters in queries
- [Authentication](12-authentication.md) - JWT claim parameters
- [Query Chaining](14-query-chaining.md) - Parameter passing between queries
