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
    DECLARE @email NVARCHAR(500) = {{email}};  -- From Query 1
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
- `{{id}}` = 1
- `{{name}}` = "John"
- `{{email}}` = "j@x.com"

### Multiple Rows → JSON Array

Query 1 returns multiple rows:
```
| product_id | quantity |
|------------|----------|
| A1         | 5        |
| B2         | 3        |
```

Query 2 receives `{{json}}`:
```json
[{"product_id":"A1","quantity":5},{"product_id":"B2","quantity":3}]
```

Use OPENJSON to parse:
```sql
SELECT * FROM inventory 
WHERE product_id IN (
  SELECT JSON_VALUE(value, '$.product_id') 
  FROM OPENJSON({{json}})
);
```

## Cross-Database Example

```xml
<validate_customer>
  <route>customers/{{id}}/validate</route>
  
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
    WHERE ID = {{customer_id}}
  ]]></query>
  
  <!-- Query 3: Store result (SQL Server) -->
  <query><![CDATA[
    DECLARE @customer_id NVARCHAR(50) = {{customer_id}};
    DECLARE @full_name NVARCHAR(255) = {{full_name}};
    DECLARE @validated_by NVARCHAR(255) = {{validated_by}};
    
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

<!-- Query 2: json_var="users_json" means Query 1's results arrive as {{users_json}} -->
<query json_var="users_json"><![CDATA[
  SELECT * FROM orders WHERE user_id IN (
    SELECT JSON_VALUE(value, '$.id') FROM OPENJSON({{users_json}})
  );
]]></query>

<!-- Query 3: json_var="orders_json" means Query 2's results arrive as {{orders_json}} -->
<!--           Query 1's results are still available as {{users_json}} -->
<query json_var="orders_json"><![CDATA[
  DECLARE @users  NVARCHAR(MAX) = {{users_json}};
  DECLARE @orders NVARCHAR(MAX) = {{orders_json}};
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

Error response includes position:
```json
{
  "success": false,
  "message": "Query 2 of 3 failed: Customer not found"
}
```

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
    DECLARE @user_id NVARCHAR(100) = {auth{sub}};
    DECLARE @inventory_json NVARCHAR(MAX) = {{json}};
    
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
      SELECT JSON_VALUE(value, '$.product_id') as pid,
             JSON_VALUE(value, '$.quantity')::int as qty
      FROM json_array_elements({{product_ids}}::json)
    ) req
    WHERE product_id = req.pid;
    
    SELECT 'Order processed' AS message;
  ]]></query>
</process_order>
```

## Tips

1. **Single row results** are easier to work with — columns become direct parameters
2. **Use custom `json_var`** on the receiving query when chaining many queries
3. **Validate early** — check permissions in first query
4. **Handle empty results** — check if previous query returned data
5. **Keep chains short** — complex workflows may need restructuring

## Related Topics

- [Multi-Database](13-databases.md) - Database configuration
- [Parameters](04-parameters.md) - Parameter passing
- [Caching](07-caching.md) - Caching chained results
