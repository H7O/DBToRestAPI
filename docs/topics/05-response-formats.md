---
title: Response formats
summary: The shape of a successful response (one object, an array, a count wrapper or a file), chosen with response_structure, plus nested JSON, status codes, empty results, and buffer_rows, which reads a result before writing it.
keywords: [response_structure, array, file, single, auto, count_query, root_node, success_status_code, buffer_rows, "204", "{type{json{...}}}", FOR JSON]
---

# Response Formats

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Error responses: [errors.md](../reference/errors.md).

This page covers the shape of a successful response: one object or an array, pagination with a count, nested JSON, file downloads and empty results.

## Choose the shape

Leave `<response_structure>` out unless you need `array` or `file`. Without it, the shape follows the row count.

| What the route returns | `<response_structure>` | One row | Several rows | No row |
|---|---|---|---|---|
| One record: get by id, create, update, delete | Leave it out | `{"id": 1}` | `[{"id": 1}, {"id": 2}]` | Empty body (`204` with success code 200) |
| A list: search, filter, a record's children | `array` | `[{"id": 1}]` | `[{"id": 1}, {"id": 2}]` | `[]` |
| A file to download | `file` | The file | The first row's file | `404` |

Use `array` for every list. Without it, a list that matches exactly one row answers an object instead of a one-element array, and a client that loops over the result breaks. Tests with two or more rows don't show it.

A query that returns one record (`SELECT ... WHERE id = {{id}}`, or an `INSERT` that returns the new row) answers an object without any tag. Don't add one to make that explicit.

A route with a `count_query` always answers `{"success": true, "count": N, "data": [...]}` and ignores the tag ([below](#pagination-with-count-query)).

Don't write `single` or `auto`. Both are read as the tag left out. So a `single` route whose query returns several rows answers all of them as an array, streamed like any other result (and with `<cache>`, cached whole). Limit such a query to one row (`TOP 1`, `LIMIT 1`), then remove the tag.

Any other value makes every request to the route answer `500`, unless the route has a `count_query`. An empty `<response_structure></response_structure>` counts as no tag. A `<response_structure>` placed directly under `<settings>` is not read: set the tag on each route that needs `array` or `file`.

### A column with no name

A row whose only column has no name (an unaliased `SELECT COUNT(*)` on SQL Server) is returned as the bare value: `2`, or `[2]` with `array`. Alias the column (`COUNT(*) AS total`) to get an object. When that bare value is `NULL` (an unaliased `MAX(x)` over no rows), it counts as no row (`204`, or `{"<root_node>": null}` with `root_node`), and `array` answers `[null]`.

### An error raised after the rows

Raise errors before the first statement that returns rows ([errors.md](../reference/errors.md)). An error raised after them still gets its status when the result has zero rows or one. A longer result is written while it is read. Once part of it has been handed to the server, an error after the rows can't change the status, so the engine cuts the connection. To keep the status for a longer result, set `buffer_rows` ([below](#buffer_rows-read-a-result-before-writing-it)).

A file download's query and a count query take their first row by reading two. When such a query returns one row, it is read to its end, so an error after the row gets its status. When it returns more, the engine stops at the second row, and an error after the rows is lost. `buffer_rows` doesn't change that.

### `buffer_rows`: read a result before writing it

With `<buffer_rows>N</buffer_rows>`, the engine reads up to N rows before it writes anything. A result of N rows or fewer is read to its end first. So an error raised after its rows, or a database error while they are read (a conversion that fails on row 300), answers with its status and JSON body instead of a cut connection. A longer result is written as it would be without the tag, once N rows have been read.

```xml
<settings>
  <queries>
    <report_lines>
      <route>reports/{{id}}/lines</route>
      <verb>GET</verb>
      <response_structure>array</response_structure>
      <!-- A report has at most 5000 lines: read them all before writing -->
      <buffer_rows>5000</buffer_rows>
      <query><![CDATA[
        SELECT line_no, CAST(amount_text AS DECIMAL(18, 2)) AS amount
        FROM report_lines WHERE report_id = {{id}} ORDER BY line_no;
      ]]></query>
    </report_lines>
  </queries>
</settings>
```

The rows read are held in memory until the response is written. Without the tag, a result costs the same small amount of memory whatever its size. With it, each request in flight holds up to N rows. So set it only on routes that need it, to the largest result they return.

| Setting | Where | Default | Notes |
|---|---|---|---|
| `buffer_rows` | route, then directly under `<settings>` | not set: two rows are read first, which tells one row from several | A whole number, 0 or more. `0` on a route turns a global value off for it, and `1` reads as many rows as not setting it. Any other value makes every request to the route answer `500`, and none of its queries runs. It applies to `array`, to the default shape and to a count query's `data`. A file download's query and a count query's count still read two rows. A GET or HEAD request to a route with `<cache>` reads its result whole anyway. |

## One Record: No Tag

```xml
<get_item>
  <route>items/{{id}}</route>
  <verb>GET</verb>
  <!-- No response_structure: one row answers an object -->
  <query><![CDATA[SELECT id, name FROM items WHERE id = {{id}};]]></query>
</get_item>
```

**One row:**
```json
{"id": 1, "name": "Item"}
```

**No row:** `204 No Content` with an empty body. Several rows would come back as an array.

## A List: `array`

Always an array, even for one row or none:

```xml
<list_items>
  <route>items</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <query><![CDATA[SELECT id, name FROM items ORDER BY name;]]></query>
</list_items>
```

**One row:**
```json
[{"id": 1, "name": "Item"}]
```

**No row:** `[]`.

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
  "success": true,
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
      mime_type         -- MIME type (optional; the column must be named mime_type)
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
| No tag, no row, success code 200 | `204 No Content` with an empty body |
| No tag, no row, any other success code | That status with an empty body |
| No tag, no row, with `root_node` | `{"<root_node>": null}` with the success code |
| No tag, no row, a GET to a route with `<cache>` | Body `null` with the success code |
| `array`, no rows | `[]`, or `{"<root_node>": []}` with `root_node` |
| With `count_query`, no rows | `{"success": true, "count": 0, "data": []}` |

An empty body is not JSON. A client that parses every response should check for `204` or an empty body first. See [Errors, status codes and rollback](../reference/errors.md) for every status the engine sends.

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
