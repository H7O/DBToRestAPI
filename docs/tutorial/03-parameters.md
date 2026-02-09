# Parameters Deep Dive

In the previous topic, you used `{{name}}` and `{{phone}}` to inject values from the HTTP request into your SQL. In this topic, we'll explore the full parameter system — where parameters come from, how priority works, route parameters, headers, defaults, and validation.

## Where Do Parameters Come From?

When you write `{{name}}` in your SQL, the application looks for a value called `name` from **four** sources, checked in this order:

| Priority | Source         | Example                                         |
|----------|----------------|--------------------------------------------------|
| 1 (highest) | Query string  | `GET /contacts?name=Alice`                       |
| 2        | Route segment  | `GET /contacts/{{name}}`                          |
| 3        | Request body   | `POST /contacts` with `{"name": "Alice"}`         |
| 4 (lowest)  | HTTP headers   | Header `name: Alice`                              |

If the same parameter appears in multiple sources, the higher-priority source wins. For example, if the query string has `?name=Alice` and the body has `{"name": "Bob"}`, the SQL receives `Alice`.

> **All parameter names are case-insensitive.** `{{name}}`, `{{Name}}`, and `{{NAME}}` all resolve to the same value.

## Route Parameters

Route parameters let you embed values directly in the URL path. You've already seen a basic route:

```xml
<route>contacts</route>
```

You can add parameters to routes using the `{{param}}` syntax:

```xml
<get_contact>
  <route>contacts/{{id}}</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    select id, name, phone, active 
    from [contacts] 
    where id = @id;
    ]]>
  </query>
</get_contact>
```

Now `GET /contacts/a1b2c3d4-e5f6-7890-abcd-ef1234567890` automatically sets `{{id}}` to that GUID.

### Multiple Route Parameters

Routes can have multiple parameters:

```xml
<route>users/{{user_id}}/orders/{{order_id}}</route>
```

This matches `GET /users/abc-123/orders/456`, resolving:
- `{{user_id}}` → `abc-123`
- `{{order_id}}` → `456`

### Route Parameters + Query String Together

Route and query string parameters work simultaneously. Given:

```xml
<get_user_posts>
  <route>users/{{user_id}}/posts</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    declare @user_id UNIQUEIDENTIFIER = {{user_id}};
    declare @take int = {{take}};
    declare @skip int = {{skip}};

    if (@take is null) set @take = 10;
    if (@skip is null) set @skip = 0;

    select * from posts 
    where user_id = @user_id
    order by created_at desc
    offset @skip rows fetch next @take rows only;
    ]]>
  </query>
</get_user_posts>
```

A request to `GET /users/abc-123/posts?take=5&skip=10` resolves:
- `{{user_id}}` → `abc-123` (from the route)
- `{{take}}` → `5` (from the query string)
- `{{skip}}` → `10` (from the query string)

## Body Parameters (JSON)

For POST and PUT requests, parameters are typically sent in the JSON request body:

```bash
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d '{"name": "Alice", "phone": "555-0101"}'
```

The JSON keys become available as parameters:
- `{{name}}` → `Alice`
- `{{phone}}` → `555-0101`

### Nested JSON in Request Bodies

What if the request body contains nested JSON?

```json
{
  "name": "Alice",
  "address": {
    "street": "123 Main St",
    "city": "Springfield"
  }
}
```

Top-level keys are available directly as parameters. Nested objects arrive as JSON strings, so you extract their values using standard SQL JSON functions:

```sql
declare @name nvarchar(500) = {{name}};
declare @address nvarchar(max) = {{address}};

-- Extract nested values with JSON_VALUE
declare @street nvarchar(500) = JSON_VALUE(@address, '$.street');
declare @city nvarchar(500) = JSON_VALUE(@address, '$.city');
```

For arrays or more complex structures, use `OPENJSON`:

```sql
declare @items nvarchar(max) = {{items}};

SELECT [value] AS item_name
FROM OPENJSON(@items, '$') WITH (name nvarchar(200) '$.name');
```

> **Note**: Don't confuse input JSON parsing (shown here) with the `{type{json{}}}` **output decorator**, which formats SQL results as nested JSON in the API response. See [Response Formats](../topics/05-response-formats.md) for details.

## Header Parameters

HTTP headers are accessible as parameters too. This is useful for accessing custom headers, content types, or other metadata:

```sql
declare @content_type nvarchar(500) = {{Content-Type}};
declare @user_agent nvarchar(500) = {{User-Agent}};
declare @custom_header nvarchar(500) = {{X-Request-Id}};
```

Since headers have the **lowest priority**, they won't interfere with route, query string, or body parameters that share the same name.

A practical use case — logging the caller's IP or user agent:

```xml
<log_and_list>
  <route>contacts</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    -- Log the request (header parameters)
    declare @user_agent nvarchar(500) = {{User-Agent}};
    
    -- Query parameters
    declare @name nvarchar(500) = {{name}};
    
    select id, name, phone 
    from contacts
    where (@name is null or name like '%' + @name + '%');
    ]]>
  </query>
</log_and_list>
```

## Handling Optional Parameters with SQL Defaults

Not every parameter needs to be mandatory. For optional parameters, use SQL to set sensible defaults:

### Using IF/IS NULL

```sql
declare @take int = {{take}};
declare @skip int = {{skip}};
declare @sort_order nvarchar(10) = {{sort_order}};

if (@take is null or @take < 1) set @take = 100;
if (@take > 1000) set @take = 1000;  -- Cap at 1000
if (@skip is null or @skip < 0) set @skip = 0;
if (@sort_order is null or @sort_order not in ('asc', 'desc')) set @sort_order = 'asc';
```

### Using ISNULL or COALESCE

For simpler defaults:

```sql
declare @take int = ISNULL({{take}}, 100);
declare @skip int = ISNULL({{skip}}, 0);
declare @sort nvarchar(50) = COALESCE({{sort}}, 'name');
```

## Validating Parameters in SQL

Since your SQL has full control, you can validate parameters before executing the main logic:

```sql
declare @email nvarchar(500) = {{email}};
declare @age int = {{age}};

-- Validate email format
if @email not like '%@%.%'
begin
  throw 50400, 'Invalid email format', 1;
  return;
end

-- Validate range
if @age < 0 or @age > 150
begin
  throw 50400, 'Age must be between 0 and 150', 1;
  return;
end

-- Validate enum values
declare @status nvarchar(50) = {{status}};
if @status not in ('active', 'inactive', 'pending')
begin
  throw 50400, 'Invalid status value. Must be: active, inactive, or pending', 1;
  return;
end
```

Remember from the previous topic: `THROW 50400` returns HTTP 400 Bad Request.

## Array Parameters

If a client sends a JSON array, you can parse it in SQL Server using `OPENJSON`:

**Request:**
```json
{"ids": ["id1", "id2", "id3"]}
```

**SQL:**
```sql
declare @ids nvarchar(max) = {{ids}};

select * from contacts
where id in (
  select value from OPENJSON(@ids)
);
```

## Practical Exercise: Add a Get-by-ID Endpoint

Let's put this together. Add a new endpoint to your `sql.xml` that fetches a single contact by ID:

```xml
<get_contact>
  <route>contacts/{{id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <response_structure>single</response_structure>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @error_msg nvarchar(500);

    if ((select count(*) from [contacts] where id = @id) < 1)
    begin 
        set @error_msg = 'Contact with id ' 
                       + cast(@id as nvarchar(50)) + ' does not exist';
        throw 50404, @error_msg, 1;
        return;
    end

    select id, name, phone, active 
    from [contacts] 
    where id = @id;
    ]]>
  </query>
</get_contact>
```

Test it by first getting a contact ID from the list:

```bash
curl http://localhost:5165/contacts
```

Then fetching that specific contact (replace with your actual ID):

```bash
curl http://localhost:5165/contacts/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

Try a non-existent ID to see the 404 error:

```bash
curl http://localhost:5165/contacts/00000000-0000-0000-0000-000000000000
```

```json
{
  "error": "Contact with id 00000000-0000-0000-0000-000000000000 does not exist"
}
```

---

### What You Learned

- The four parameter sources and their priority order (query string > route > body > headers)
- How to use route parameters (`/contacts/{{id}}`) for clean REST URLs
- How to combine route and query string parameters
- How to access HTTP headers as parameters
- How to set SQL defaults for optional parameters
- How to validate parameters with `THROW 50xxx` error codes
- How to handle array parameters with `OPENJSON`

---

**Next:** [Pagination & Filtering →](04-pagination-filtering.md)

**[Back to Tutorial Index](index.md)**
