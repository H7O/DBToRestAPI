# Input Validation with Regex & SQL

Your API needs to protect itself from bad input. In this topic, we'll cover three layers of validation available in DBToRestAPI: mandatory parameters, SQL-level validation with error codes, and custom regex patterns for parameter delimiters.

## Layer 1: Mandatory Parameters

You've already seen this — the simplest validation:

```xml
<mandatory_parameters>name,phone</mandatory_parameters>
```

If `name` or `phone` is missing, the application returns HTTP 400 **before the SQL even runs**:

```json
{
  "error": "Bad request: missing mandatory parameter(s): name"
}
```

This is a quick, zero-cost check — no database round-trip needed.

### When to Use Mandatory Parameters

Use for fields that must **always** be present:
- Primary keys for GET/PUT/DELETE: `id`
- Required creation fields for POST: `name,email`
- Authentication tokens: (better handled by the auth system, covered later)

Don't use for fields that are **optionally filterable** — handle those with SQL defaults instead.

## Layer 2: SQL-Level Validation

For anything beyond "is it present?", validate in SQL. This gives you full control over the logic.

### Pattern: Validate and Reject Early

```sql
declare @email nvarchar(500) = {{email}};
declare @age int = {{age}};
declare @status nvarchar(50) = {{status}};
declare @error_msg nvarchar(500);

-- Format validation
if @email is not null and @email not like '%@%.%'
begin
  throw 50400, 'Invalid email format', 1;
  return;
end

-- Range validation
if @age is not null and (@age < 0 or @age > 150)
begin
  throw 50400, 'Age must be between 0 and 150', 1;
  return;
end

-- Enum validation
if @status is not null and @status not in ('active', 'inactive', 'pending')
begin
  set @error_msg = 'Invalid status. Must be one of: active, inactive, pending';
  throw 50400, @error_msg, 1;
  return;
end
```

### HTTP Error Code Reference

Remember the formula: **50000 + desired HTTP status code**:

| SQL Error Code | HTTP Status | Meaning |
|----------------|-------------|---------|
| 50400          | 400         | Bad Request — invalid input |
| 50401          | 401         | Unauthorized — not authenticated |
| 50403          | 403         | Forbidden — not permitted |
| 50404          | 404         | Not Found — resource doesn't exist |
| 50409          | 409         | Conflict — duplicate resource |
| 50422          | 422         | Unprocessable Entity — semantic error |
| 50429          | 429         | Too Many Requests — rate limiting |
| 50500          | 500         | Internal Server Error |

### Pattern: Multi-Field Validation

Validate multiple fields and return a helpful error:

```sql
declare @name nvarchar(500) = {{name}};
declare @phone nvarchar(100) = {{phone}};
declare @email nvarchar(500) = {{email}};
declare @errors nvarchar(max) = '';

if @name is null or ltrim(rtrim(@name)) = ''
  set @errors = @errors + 'name is required. ';

if @phone is null or ltrim(rtrim(@phone)) = ''
  set @errors = @errors + 'phone is required. ';

if @email is not null and @email not like '%@%.%'
  set @errors = @errors + 'email format is invalid. ';

if len(@errors) > 0
begin
  throw 50400, @errors, 1;
  return;
end
```

Response:
```json
{
  "error": "name is required. phone is required. email format is invalid."
}
```

### Pattern: Uniqueness Validation

```sql
-- Check for duplicates before insert
if exists (select 1 from contacts where phone = @phone)
begin
  throw 50409, 'A contact with this phone number already exists', 1;
  return;
end
```

### Pattern: Cross-Reference Validation

```sql
-- Check that the referenced record exists
declare @department_id int = {{department_id}};

if not exists (select 1 from departments where id = @department_id)
begin
  throw 50400, 'Invalid department_id: department does not exist', 1;
  return;
end
```

## Layer 3: Custom Regex Parameter Patterns

By default, parameters use `{{name}}` syntax. But you can customize the delimiter pattern — globally or per-endpoint.

### Why Customize Delimiters?

If your SQL uses `{{` for other purposes (like SQL Server's JSON path syntax), you might want different delimiters to avoid conflicts.

### Per-Endpoint Override

Use `||name||` instead of `{{name}}`:

```xml
<custom_delimiters>
  <json_variables_pattern><![CDATA[(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)]]></json_variables_pattern>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = ||name||;
    
    if (@name is null or @name = '')
      set @name = 'world';
    
    select 'hello ' + @name + '!' as message_from_db;
    ]]>
  </query>
</custom_delimiters>
```

### Source-Specific Decorators

The default `{{param}}` resolves from all sources (route, query string, body, headers). When you need to force a specific source, use the targeted decorator:

| Decorator | Source | Example |
|-----------|--------|---------|
| `{{param}}` | Any (by priority) | `{{name}}` |
| `{j{param}}` | JSON body only | `{j{name}}` |
| `{qs{param}}` | Query string only | `{qs{search}}` |
| `{r{param}}` | Route only | `{r{id}}` |
| `{h{param}}` | HTTP headers only | `{h{X-Api-Key}}` |
| `{f{param}}` | Form data only | `{f{file_name}}` |

**When is this useful?** When the same parameter name exists in multiple sources and you need to control which one to use:

```sql
-- Force `id` from route, `name` could come from body or query
declare @id UNIQUEIDENTIFIER = {r{id}};
declare @name_from_body nvarchar(500) = {j{name}};
declare @name_from_query nvarchar(500) = {qs{name}};
```

### Global Override in regex.xml

To change delimiters for all endpoints, edit `/config/regex.xml`:

```xml
<settings>
  <regex>
    <json_variables_pattern><![CDATA[(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)]]></json_variables_pattern>
    <query_string_variables_pattern><![CDATA[(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)]]></query_string_variables_pattern>
    <!-- ... similar for other patterns ... -->
  </regex>
</settings>
```

> **Recommendation**: Stick with the default `{{param}}` unless you have a specific conflict. It's well-known, easy to read, and works with all examples in this tutorial.

## Practical Exercise: Validation for Your Contacts API

Update your `create_contact` endpoint with comprehensive validation:

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <response_structure>single</response_structure>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @active bit = {{active}};
    declare @error_msg nvarchar(500);

    -- Trim whitespace
    set @name = ltrim(rtrim(@name));
    set @phone = ltrim(rtrim(@phone));

    -- Validate name length
    if len(@name) < 2
    begin
      throw 50400, 'Name must be at least 2 characters', 1;
      return;
    end

    if len(@name) > 200
    begin
      throw 50400, 'Name must be 200 characters or fewer', 1;
      return;
    end

    -- Validate phone format (basic: digits, spaces, dashes, plus)
    if @phone like '%[^0-9 +-]%'
    begin
      throw 50400, 'Phone may only contain digits, spaces, dashes, and plus signs', 1;
      return;
    end

    -- Check for duplicates
    if exists (select 1 from contacts where name = @name and phone = @phone)
    begin
      set @error_msg = 'Contact ' + @name + ' with phone ' + @phone + ' already exists';
      throw 50409, @error_msg, 1;
      return;
    end

    if (@active is null) set @active = 1;

    insert into [contacts] (id, name, phone, active) 
    output inserted.id, inserted.name, inserted.phone, inserted.active
    values (newid(), @name, @phone, @active);
    ]]>
  </query>
</create_contact>
```

Test the validation:

```bash
# Too short name → 400
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"A\", \"phone\": \"555-0101\"}"

# Invalid phone characters → 400
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Alice\", \"phone\": \"not_a_phone!\"}"

# Duplicate → 409
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Alice Smith\", \"phone\": \"555-0101\"}"
```

---

### What You Learned

- Three layers of validation: mandatory parameters → SQL logic → regex patterns
- How to use `THROW 50xxx` for any HTTP error code
- Patterns for format, range, enum, uniqueness, and cross-reference validation
- Source-specific parameter decorators (`{j{}`, `{qs{}`, `{r{}`, `{h{}`, `{f{}}`)
- How to customize parameter delimiters per-endpoint or globally
- A practical validation recipe for the contacts API

---

**Next:** [Protecting Endpoints with API Keys →](08-api-keys.md)

**[Back to Tutorial Index](index.md)**
