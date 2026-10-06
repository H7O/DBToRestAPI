# Multi-Query Chaining

Execute a sequence of queries where each output feeds into the next — even across different databases.

## Overview

```
┌─────────────┐      ┌─────────────┐      ┌─────────────┐
│   Query 1   │ ──▶  │   Query 2   │ ──▶  │   Query 3   │ ──▶ Response
│ (SQL Server)│      │    (DB2)    │      │ (SQL Server)│
└─────────────┘      └─────────────┘      └─────────────┘
       │                    │                    │
       ▼                    ▼                    ▼
   HTTP Params        Query 1 Output       Query 2 Output
```

## Benefits

- Cross-database workflows in single API call
- No client-side orchestration needed
- All queries use parameterized execution (SQL injection safe)
- Works where linked servers don't (Azure SQL, cross-vendor)

## Basic Configuration

Define multiple `<query>` nodes:

```xml
<chained_workflow>
  <route>workflow</route>
  
  <!-- Query 1 -->
  <query><![CDATA[
    DECLARE @user_id INT = {{user_id}};
    SELECT id, name, email FROM users WHERE id = @user_id;
  ]]></query>
  
  <!-- Query 2 uses Query 1 output -->
  <query><![CDATA[
    DECLARE @email NVARCHAR(500) = {pq{email}};  -- From Query 1
    SELECT * FROM orders WHERE user_email = @email;
  ]]></query>
</chained_workflow>
```

## Parameter Passing

### Single Row → Named Parameters

Query 1 returns one row:
```
| id | name | email |
|----|------|-------|
| 1  | John | j@x.com |
```

Query 2 receives:
- `{pq{id}}` = 1
- `{pq{name}}` = "John"
- `{pq{email}}` = "j@x.com"

A row whose only column has no name (an unaliased `SELECT COUNT(*)` on SQL Server) gives no column values. Read it as `{pq{json}}`, which holds `[2]`, or alias the column. (Before 1.7.7 such a row came back twice, so `{pq{json}}` held `[2,{"":2}]`, and `[null,{},{"":null}]` for `NULL`.)

### Multiple Rows → JSON Array

Query 1 returns multiple rows:
```
| product_id | quantity |
|------------|----------|
| A1         | 5        |
| B2         | 3        |
```

Query 2 receives `{pq{json}}`:
```json
[{"product_id":"A1","quantity":5},{"product_id":"B2","quantity":3}]
```

Use OPENJSON to parse:
```sql
SELECT * FROM inventory 
WHERE product_id IN (
  SELECT JSON_VALUE(value, '$.product_id') 
  FROM OPENJSON({pq{json}})
);
```

### Reading Earlier Results: `{pq{name}}`

Read a value from an earlier query with `{pq{name}}`. It looks only at the results of earlier queries in the chain. Columns from every earlier query stay available, not only those of the query just before, unless a later query returns a column with the same name.

`{{name}}` also reads earlier results, but it falls through to the request. When the earlier queries give no value for `name`, a request value with the same name fills the placeholder instead. That happens when:

- the column is `NULL`
- the earlier query returned zero rows
- the earlier query returned several rows, so its columns are only in the JSON variable

So a caller can replace a value your SQL looked up, such as an owner id or a URL, by sending a field with the same name. `{pq{name}}` takes the value from the latest earlier query that returned a column `name`, even a `NULL` one. When none did, it is `NULL`: test it with `IS NULL` before you use it. `{pq{name}}` works inside `{http{ ... }http}` blocks too, where a missing value is inserted as an empty string.

## Cross-Database Example

```xml
<validate_customer>
  <route>customers/{{id}}/validate</route>
  <authorize><provider>my_provider</provider></authorize>
  
  <!-- Query 1: Check permissions (SQL Server) -->
  <query><![CDATA[
    DECLARE @user NVARCHAR(255) = {auth{email}};
    DECLARE @customer_id NVARCHAR(50) = {{id}};
    
    IF NOT EXISTS (SELECT 1 FROM permissions WHERE email = @user AND perm = 'validate')
      THROW 50403, 'Not authorized', 1;
    
    SELECT @customer_id AS customer_id, @user AS validated_by;
  ]]></query>
  
  <!-- Query 2: Validate against registry (DB2) -->
  <query connection_string_name="db2"><![CDATA[
    SELECT ID, FULL_NAME, STATUS
    FROM CUSTOMER_REGISTRY
    WHERE ID = {pq{customer_id}}
  ]]></query>
  
  <!-- Query 3: Store result (SQL Server) -->
  <query><![CDATA[
    DECLARE @customer_id NVARCHAR(50) = {pq{customer_id}};   -- from Query 1
    DECLARE @full_name NVARCHAR(255) = {pq{full_name}};      -- from Query 2, NULL if not in the registry
    DECLARE @validated_by NVARCHAR(255) = {pq{validated_by}};
    
    IF @full_name IS NULL
      THROW 50404, 'Customer not found in the registry', 1;
    
    UPDATE customers
    SET validated = 1, validated_by = @validated_by
    WHERE id = @customer_id;
    
    SELECT 'Validated' AS status, @customer_id AS id;
  ]]></query>
</validate_customer>
```

## Per-Query Settings

### Connection String

```xml
<query connection_string_name="postgres"><![CDATA[
  SELECT * FROM analytics;
]]></query>
```

### Timeout

```xml
<query db_command_timeout="120"><![CDATA[
  -- Long-running query
]]></query>
```

### Custom JSON Variable Name

The `json_var` attribute goes on the **receiving** query to control the variable
name that the previous query's results are stored under:

```xml
<!-- Query 1: returns multiple users -->
<query><![CDATA[
  SELECT id, name FROM users;
]]></query>

<!-- Query 2: json_var="users_json" means Query 1's results arrive as {pq{users_json}} -->
<query json_var="users_json"><![CDATA[
  SELECT * FROM orders WHERE user_id IN (
    SELECT JSON_VALUE(value, '$.id') FROM OPENJSON({pq{users_json}})
  );
]]></query>

<!-- Query 3: json_var="orders_json" means Query 2's results arrive as {pq{orders_json}} -->
<!--           Query 1's results are still available as {pq{users_json}} -->
<query json_var="orders_json"><![CDATA[
  DECLARE @users  NVARCHAR(MAX) = {pq{users_json}};
  DECLARE @orders NVARCHAR(MAX) = {pq{orders_json}};
]]></query>
```

## Error Handling

Errors stop the chain immediately:

```sql
-- In any query
IF @status = 'INVALID'
BEGIN
  THROW 50400, 'Validation failed', 1;
  RETURN;
END
```

An error number from 50000 to 50999 maps to an HTTP status (number minus 50000). Use 50400 to 50599: a smaller number gives a status below 400, which is not an error and keeps uploads. The body carries the database's message, with no hint of which query failed:
```json
{
  "success": false,
  "message": "Validation failed",
  "error_number": 400
}
```

Any other error returns `400` with the generic error message. The position text (`"Query 2 of 3 failed: ..."`) appears only in the `500` response sent in debug mode. See [Errors](../reference/errors.md).

## Caching

Caching applies to entire chain result:

```xml
<cached_chain>
  <cache>
    <memory>
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      <invalidators>customer_id</invalidators>
    </memory>
  </cache>
  
  <query>...</query>
  <query connection_string_name="external">...</query>
</cached_chain>
```

**Note:** Only final query result is cached. When cache expires, all queries re-execute.

## Real-World Example: Order Processing

```xml
<process_order>
  <route>orders</route>
  <verb>POST</verb>
  <authorize><provider>my_provider</provider></authorize>
  <mandatory_parameters>product_ids,quantities</mandatory_parameters>
  
  <!-- Query 1: Validate inventory (PostgreSQL warehouse) -->
  <query connection_string_name="warehouse"><![CDATA[
    SELECT product_id, available_qty
    FROM inventory
    WHERE product_id IN (
      SELECT value FROM json_array_elements_text({{product_ids}}::json)
    );
  ]]></query>
  
  <!-- Query 2: Create order (SQL Server) -->
  <query><![CDATA[
    DECLARE @user_id NVARCHAR(100) = {auth{user_id}};
    DECLARE @inventory_json NVARCHAR(MAX) = {pq{json}};
    
    -- Validate quantities available
    -- ... validation logic ...
    
    INSERT INTO orders (user_id, status, created_at)
    OUTPUT inserted.id, inserted.status
    VALUES (@user_id, 'pending', GETUTCDATE());
  ]]></query>
  
  <!-- Query 3: Update warehouse (PostgreSQL) -->
  <query connection_string_name="warehouse"><![CDATA[
    UPDATE inventory 
    SET available_qty = available_qty - req.qty
    FROM (
      -- SUM per product: UPDATE ... FROM applies one joined row per target row,
      -- so a product listed twice would otherwise be decremented only once
      SELECT p.pid, SUM(q.qty::int) AS qty
      FROM json_array_elements_text({{product_ids}}::json) WITH ORDINALITY AS p(pid, n)
      JOIN json_array_elements_text({{quantities}}::json) WITH ORDINALITY AS q(qty, n) USING (n)
      GROUP BY p.pid
    ) req
    WHERE product_id = req.pid;
    
    SELECT 'Order processed' AS message;
  ]]></query>
</process_order>
```

## Tips

1. **Single row results** are easier to work with — columns become `{pq{column}}` values
2. **Use custom `json_var`** on the receiving query when chaining many queries
3. **Validate early** — check permissions in first query
4. **Handle empty results** — `{pq{name}}` is `NULL` when the earlier query returned no row, so test `IS NULL` first
5. **Keep chains short** — complex workflows may need restructuring

## Related Topics

- [Multi-Database](13-databases.md) - Database configuration
- [Parameters](04-parameters.md) - Parameter passing
- [Caching](07-caching.md) - Caching chained results
