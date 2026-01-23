# Response Formats

This document covers controlling the structure of API responses.

## Response Structure Types

| Value | Behavior |
|-------|----------|
| `auto` (default) | Single row → object; Multiple rows → array |
| `single` | Always return first row as object |
| `array` | Always return array (even for single row) |
| `file` | Stream file download |

## Auto Response (Default)

```xml
<get_items>
  <!-- No response_structure = auto -->
  <query><![CDATA[SELECT * FROM items;]]></query>
</get_items>
```

**Single row result:**
```json
{"id": 1, "name": "Item"}
```

**Multiple row result:**
```json
[
  {"id": 1, "name": "Item 1"},
  {"id": 2, "name": "Item 2"}
]
```

## Array Response

Force array even for single row:

```xml
<list_items>
  <response_structure>array</response_structure>
  <query><![CDATA[SELECT * FROM items;]]></query>
</list_items>
```

**Single row result:**
```json
[{"id": 1, "name": "Item"}]
```

## Single Response

Always return first row only:

```xml
<get_item>
  <response_structure>single</response_structure>
  <query><![CDATA[SELECT TOP 1 * FROM items;]]></query>
</get_item>
```

## Pagination with Count Query

Add `count_query` for paginated responses:

```xml
<list_items>
  <query><![CDATA[
    SELECT * FROM items
    ORDER BY name
    OFFSET {{skip}} ROWS FETCH NEXT {{take}} ROWS ONLY;
  ]]></query>
  
  <count_query><![CDATA[
    SELECT COUNT(*) FROM items;
  ]]></count_query>
</list_items>
```

**Response:**
```json
{
  "count": 150,
  "data": [
    {"id": 1, "name": "Item 1"},
    {"id": 2, "name": "Item 2"}
  ]
}
```

**Note:** When `count_query` is present, `response_structure` is ignored.

## Nested JSON with FOR JSON

SQL Server's `FOR JSON` returns escaped strings by default. Use the type decorator to embed as proper JSON:

### Without Decorator (Escaped)

```sql
SELECT
    name,
    (SELECT phone FROM phones WHERE contact_id = c.id FOR JSON PATH) AS phones
FROM contacts c;
```

```json
{
  "name": "John",
  "phones": "[{\"phone\":\"+1-555-0100\"}]"  // Escaped string!
}
```

### With Type Decorator (Proper JSON)

```sql
SELECT
    name,
    (SELECT phone FROM phones WHERE contact_id = c.id FOR JSON PATH) 
      AS {type{json{phones}}}
FROM contacts c;
```

```json
{
  "name": "John",
  "phones": [{"phone": "+1-555-0100"}]  // Proper array!
}
```

### Multiple Nested Fields

```sql
SELECT
    name,
    (SELECT phone FROM phones WHERE contact_id = c.id FOR JSON PATH) 
      AS {type{json{phones}}},
    (SELECT street, city FROM addresses WHERE contact_id = c.id FOR JSON PATH) 
      AS {type{json{addresses}}}
FROM contacts c;
```

## File Download Response

Set `response_structure` to `file` for downloads:

```xml
<download_file>
  <response_structure>file</response_structure>
  <file_management>
    <store>primary</store>
  </file_management>
  
  <query><![CDATA[
    SELECT 
      file_name,        -- Download filename
      relative_path,    -- Path in file store
      content_type      -- MIME type (optional)
    FROM files WHERE id = {{id}};
  ]]></query>
</download_file>
```

### File Source Options

Return one of these from your query:

| Field | Description |
|-------|-------------|
| `base64_content` | File as base64 string (from DB) |
| `relative_path` | Path in configured file store |
| `http` | URL to proxy file from |

## Custom Success Status

```xml
<create_item>
  <success_status_code>201</success_status_code>
  <query><![CDATA[INSERT INTO items...]]></query>
</create_item>
```

```xml
<delete_item>
  <success_status_code>204</success_status_code>
  <query><![CDATA[DELETE FROM items...]]></query>
</delete_item>
```

## Empty Results

| Scenario | Response |
|----------|----------|
| Query returns no rows | Empty array `[]` or `null` |
| With count_query, no rows | `{"count": 0, "data": []}` |
| Single response, no rows | `null` |

## Controlling Column Names

Use SQL aliases:

```sql
SELECT 
  id AS itemId,
  created_at AS createdAt,
  first_name + ' ' + last_name AS fullName
FROM items;
```

```json
{"itemId": 1, "createdAt": "2025-01-24", "fullName": "John Doe"}
```

## Related Topics

- [CRUD Operations](03-crud-operations.md) - Query patterns
- [File Downloads](10-file-downloads.md) - File response details
- [Query Chaining](14-query-chaining.md) - Multi-query responses
