# Multi-Query Chaining

Multi-Query Chaining allows you to define a sequence of SQL queries within a single API endpoint, where each query's output is automatically passed as input to the next query in the chain. This enables powerful cross-database workflows and complex data transformations without requiring client-side orchestration.

## Overview

```
┌─────────────┐      ┌─────────────┐      ┌─────────────┐
│   Query 1   │ ──▶  │   Query 2   │ ──▶  │   Query 3   │ ──▶ Response
│  (server1)  │      │  (server2)  │      │  (server1)  │
└─────────────┘      └─────────────┘      └─────────────┘
       │                    │                    │
       ▼                    ▼                    ▼
   HTTP Params        Query 1 Output       Query 2 Output
                      as Parameters        as JSON Array
```

**Key Benefits:**
- Execute queries across multiple databases in a single API call
- Chain operations where results from one database feed into another
- Reduce client-server round trips
- Encapsulate complex multi-step workflows

---

## Basic Configuration

Define multiple `<query>` nodes within an endpoint to create a chain:

```xml
<my_chained_endpoint>
  <!-- Fallback connection string for queries without explicit attribute -->
  <connection_string_name>default</connection_string_name>
  
  <query>
    <![CDATA[ SELECT id, name FROM users WHERE active = 1; ]]>
  </query>
  
  <query connection_string_name="analytics_db">
    <![CDATA[
      DECLARE @user_id INT = {{id}};
      SELECT * FROM user_analytics WHERE user_id = @user_id;
    ]]>
  </query>
</my_chained_endpoint>
```

---

## Parameter Passing Rules

How data flows between queries depends on the row count of the previous query's result:

### Single Row Result → Individual Parameters

When a query returns **exactly one row**, each column becomes a named parameter for the next query:

```xml
<!-- Query 1: Returns single row -->
<query>
  <![CDATA[ SELECT 'John' AS user_name, 42 AS user_id; ]]>
</query>

<!-- Query 2: Access columns as individual parameters -->
<query>
  <![CDATA[
    DECLARE @name NVARCHAR(100) = {{user_name}};
    DECLARE @id INT = {{user_id}};
    SELECT * FROM orders WHERE customer_id = @id;
  ]]>
</query>
```

### Multiple Rows Result → JSON Array

When a query returns **multiple rows**, the entire result set is serialized as a JSON array and passed via a special variable (default: `{{json}}`):

```xml
<!-- Query 1: Returns multiple rows -->
<query>
  <![CDATA[
    SELECT id, name FROM products WHERE category = 'Electronics';
  ]]>
</query>

<!-- Query 2: Parse the JSON array -->
<query>
  <![CDATA[
    DECLARE @products NVARCHAR(MAX) = {{json}};
    
    SELECT 
      JSON_VALUE(value, '$.id') AS product_id,
      JSON_VALUE(value, '$.name') AS product_name
    FROM OPENJSON(@products);
  ]]>
</query>
```

### Customizing the JSON Variable Name

Use the `json_var` attribute to specify a custom variable name:

```xml
<query json_var="roles_data">
  <![CDATA[
    DECLARE @roles NVARCHAR(MAX) = {{roles_data}};
    -- Process roles...
  ]]>
</query>
```

---

## Connection String Resolution

Each query can target a different database. Connection strings are resolved in this order:

1. **Query Attribute**: `<query connection_string_name="specific_db">`
2. **Endpoint Fallback**: `<connection_string_name>fallback_db</connection_string_name>`
3. **Global Default**: `"default"` connection string from settings

```xml
<cross_database_workflow>
  <!-- Fallback for queries without explicit connection string -->
  <connection_string_name>primary_db</connection_string_name>
  
  <!-- Uses primary_db (fallback) -->
  <query>
    <![CDATA[ SELECT customer_id FROM orders WHERE total > 1000; ]]>
  </query>
  
  <!-- Uses analytics_db (explicit) -->
  <query connection_string_name="analytics_db">
    <![CDATA[ INSERT INTO high_value_customers (id) VALUES ({{customer_id}}); ]]>
  </query>
  
  <!-- Uses primary_db (fallback) -->
  <query>
    <![CDATA[ SELECT 'Workflow completed' AS status; ]]>
  </query>
</cross_database_workflow>
```

---

## Complete Example

Here's a real-world example that retrieves a user role from one database, fetches available permissions from another, and returns the combined result:

```xml
<get_user_permissions>
  <route>users/{{user_id}}/permissions</route>
  <verb>GET</verb>
  <mandatory_parameters>user_id</mandatory_parameters>
  <connection_string_name>users_db</connection_string_name>

  <!-- Query 1: Get user's role from users database -->
  <query>
    <![CDATA[
      DECLARE @user_id UNIQUEIDENTIFIER = {{user_id}};
      
      SELECT TOP 1 
        u.id AS user_id,
        u.email,
        r.name AS role_name
      FROM users u
      INNER JOIN roles r ON u.role_id = r.id
      WHERE u.id = @user_id;
    ]]>
  </query>
  
  <!-- Query 2: Get permissions for that role from permissions database -->
  <query connection_string_name="permissions_db">
    <![CDATA[
      DECLARE @role_name NVARCHAR(100) = {{role_name}};
      DECLARE @email NVARCHAR(255) = {{email}};
      
      SELECT 
        p.name AS permission,
        p.description,
        @email AS user_email
      FROM permissions p
      INNER JOIN role_permissions rp ON p.id = rp.permission_id
      INNER JOIN roles r ON rp.role_id = r.id
      WHERE r.name = @role_name;
    ]]>
  </query>
  
  <!-- Query 3: Format final response (uses fallback: users_db) -->
  <query json_var="permissions">
    <![CDATA[
      DECLARE @permissions NVARCHAR(MAX) = {{permissions}};
      DECLARE @user_email NVARCHAR(255);
      
      -- Extract email from first permission record
      SELECT TOP 1 @user_email = JSON_VALUE(value, '$.user_email')
      FROM OPENJSON(@permissions);
      
      SELECT 
        @user_email AS email,
        (SELECT 
          JSON_VALUE(value, '$.permission') AS name,
          JSON_VALUE(value, '$.description') AS description
        FROM OPENJSON(@permissions)
        FOR JSON PATH) AS permissions;
    ]]>
  </query>
</get_user_permissions>
```

**Response:**
```json
{
  "email": "john@example.com",
  "permissions": [
    { "name": "read_reports", "description": "View analytics reports" },
    { "name": "edit_profile", "description": "Modify user profile" }
  ]
}
```

---

## Execution Flow

1. **First Query**: Receives parameters from the HTTP request (query string, body, headers, route)
2. **Intermediate Queries**: Receive parameters from the previous query's output
3. **Final Query**: Its result is returned to the client

```
HTTP Request
     │
     ▼
┌─────────────────────┐
│  Query 1 (First)    │ ◄── HTTP params: {{name}}, {{id}}, etc.
│  IsFirstInChain: ✓  │
│  IsLastInChain: ✗   │
└──────────┬──────────┘
           │ Single row? → column parameters
           │ Multiple rows? → {{json}} array
           ▼
┌─────────────────────┐
│  Query 2 (Middle)   │ ◄── Previous query output
│  IsFirstInChain: ✗  │
│  IsLastInChain: ✗   │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│  Query 3 (Last)     │ ◄── Previous query output
│  IsFirstInChain: ✗  │
│  IsLastInChain: ✓   │ ──► HTTP Response
└─────────────────────┘
```

---

## Error Handling

If any query in the chain fails, the entire chain stops and returns an error:

```xml
<query>
  <![CDATA[
    -- Validate data exists before proceeding
    IF NOT EXISTS (SELECT 1 FROM users WHERE id = {{user_id}})
    BEGIN
      THROW 50404, 'User not found', 1;  -- Returns HTTP 404
    END
    
    SELECT * FROM users WHERE id = {{user_id}};
  ]]>
</query>
```

**HTTP Error Code Mapping:**
- `THROW 50400` → HTTP 400 Bad Request
- `THROW 50401` → HTTP 401 Unauthorized
- `THROW 50403` → HTTP 403 Forbidden
- `THROW 50404` → HTTP 404 Not Found
- `THROW 50409` → HTTP 409 Conflict
- `THROW 50500` → HTTP 500 Internal Server Error

---

## Best Practices

### 1. Keep Chains Short
Limit chains to 3-4 queries maximum. Longer chains become harder to debug and maintain.

### 2. Validate Early
Place validation logic in earlier queries to fail fast:

```xml
<!-- Query 1: Validate permissions -->
<query>
  <![CDATA[
    IF NOT EXISTS (SELECT 1 FROM admins WHERE user_id = {{user_id}})
      THROW 50403, 'Access denied', 1;
    SELECT {{user_id}} AS validated_user_id;
  ]]>
</query>

<!-- Query 2: Proceed with actual operation -->
<query>
  <![CDATA[ DELETE FROM records WHERE owner_id = {{validated_user_id}}; ]]>
</query>
```

### 3. Use Meaningful Column Names
Column names become parameter names—make them descriptive:

```xml
<!-- Good: Clear what each parameter represents -->
<query>
  <![CDATA[ SELECT id AS order_id, total AS order_total FROM orders; ]]>
</query>

<!-- Avoid: Generic names that are confusing in the next query -->
<query>
  <![CDATA[ SELECT id, total FROM orders; ]]>
</query>
```

### 4. Document Cross-Database Dependencies
Add comments explaining which databases are involved:

```xml
<complex_workflow>
  <!-- Database flow: users_db → analytics_db → reporting_db -->
  <connection_string_name>users_db</connection_string_name>
  ...
</complex_workflow>
```

---

## Limitations

- **No Parallel Execution**: Queries execute sequentially, not in parallel
- **No Transactions Across Databases**: Each query runs in its own transaction context
- **Result Size**: Large result sets passed as JSON may impact performance
- **Count Queries**: Currently, only one `<count_query>` is supported per endpoint (chaining for count queries is planned for a future release)

---

## QueryDefinition Properties

When processing chained queries programmatically, the following properties are available:

| Property | Type | Description |
|----------|------|-------------|
| `Index` | `int` | Zero-based position in the chain |
| `IsFirstInChain` | `bool` | `true` for the first query (receives HTTP params) |
| `IsLastInChain` | `bool` | `true` for the final query (result returned to client) |
| `QueryText` | `string` | The SQL query content |
| `ConnectionStringName` | `string` | Resolved connection string name |
| `JsonVariableName` | `string` | Variable name for JSON array input (default: `"json"`) |

---

## Implementation Details (Developer Reference)

This section documents the internal implementation for contributors working on the codebase.

### Architecture Overview

```
┌─────────────────────┐
│   ApiController     │
│   Index() method    │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────────────────────┐
│  QueryConfigurationParser.Parse()   │
│  Returns: List<QueryDefinition>     │
└──────────┬──────────────────────────┘
           │
           ▼
┌─────────────────────────────────────────┐
│  GetResultFromDbMultipleQueriesAsync()  │
│  Executes chain, returns final result   │
└─────────────────────────────────────────┘
```

### Method Signature

```csharp
private async Task<IActionResult> GetResultFromDbMultipleQueriesAsync(
    IConfigurationSection serviceQuerySection,
    List<QueryDefinition> queries,
    List<DbQueryParams> qParams,
    bool disableDeferredExecution = false)
```

### Execution Algorithm

```
foreach query in queries:
    1. Create DbConnection using _dbConnectionFactory.Create(query.ConnectionStringName)
    2. Execute query.QueryText with current qParams
    3. If NOT query.IsLastInChain:
       a. Detect row count using ToChamberedEnumerableAsync(2)
       b. Single row → Add result as DbQueryParams { DataModel = dynamicObject }
       c. Multiple rows → Add as DbQueryParams { DataModel = { [JsonVariableName] = jsonArray } }
       d. Close reader, dispose connection
    4. If query.IsLastInChain:
       - Apply response_structure logic (array/single/auto/file)
       - Return result to client
```

### Parameter Accumulation

The `Com.H.Data.Common` library's `DataModel` accepts multiple formats: anonymous objects, `IDictionary<string, object>`, normal objects, JSON strings, and `JsonElement`. For query chaining, we use:

| Result Type | DataModel Format | Why |
|-------------|------------------|-----|
| Single row | `dynamic` object directly | Zero transformation—properties like `.name`, `.id` are already accessible |
| Multiple rows | `Dictionary<string, object>` | Allows dynamic key names via `[JsonVariableName]` |

```csharp
// After Query N executes...
var chamberedResult = await queryResult.ToChamberedEnumerableAsync(2, cancellationToken);

if (chamberedResult.WasExhausted(2))
{
    // SINGLE ROW: Pass the dynamic object directly
    // Its properties (e.g., .user_id, .role_name) become {{user_id}}, {{role_name}} in next query
    var singleRow = chamberedResult.AsEnumerable().FirstOrDefault();
    
    qParams.Add(new DbQueryParams 
    { 
        DataModel = singleRow,
        QueryParamsRegex = DefaultVariablesPattern  // {{param}}
    });
}
else
{
    // MULTIPLE ROWS: Serialize to JSON, wrap in Dictionary with JsonVariableName as key
    var allRows = chamberedResult.AsEnumerable().ToList();
    var jsonArray = JsonSerializer.Serialize(allRows);
    
    qParams.Add(new DbQueryParams 
    { 
        DataModel = new Dictionary<string, object> 
        { 
            [nextQuery.JsonVariableName] = jsonArray  // e.g., "json" → "[{...}, {...}]"
        },
        QueryParamsRegex = DefaultVariablesPattern  // {{json}} or {{custom_name}}
    });
}

// Close reader before moving to next query
await queryResult.CloseReaderAsync();
```

**Why these formats?**

1. **Single row as `dynamic`**: The `ExecuteQueryAsync` result is already a dynamic object with column-named properties. Passing it directly means zero transformation—the library's parameter substitution finds `{{column_name}}` and resolves it from the object's properties.

2. **Multiple rows as `Dictionary<string, object>`**: 
   - Respects the `JsonVariableName` from configuration (e.g., `json_var="roles_data"`)
   - Avoids hardcoding the key name in an anonymous object
   - The library explicitly supports `IDictionary<string, object>` as documented in Com.H.Data.Common

### Why This Approach?

**Problem**: Cross-database queries traditionally require:
- Linked servers (vendor lock-in, complex setup)
- `OPENQUERY` (vulnerable to SQL injection via string concatenation)
- Client-side orchestration (multiple round trips)

**Solution**: Server-side query chaining with SQL parameterization:
- Each query executes independently with proper parameterization
- Results flow through `DbQueryParams` (same mechanism as HTTP parameters)
- No string concatenation in SQL—safe from injection
- Works across any supported database provider

### Key Design Decisions

1. **New method vs modifying existing**: `GetResultFromDbMultipleQueriesAsync` is separate from `GetResultFromDbAsync` to maintain backward compatibility and keep single-query path optimized.

2. **Connection per query**: Each query gets its own connection (potentially different databases). Connections are disposed after intermediate queries.

3. **Eager evaluation for intermediates**: Intermediate query results must be fully materialized to detect row count and serialize. Only the final query can use deferred execution.

4. **JsonVariableName on receiving query**: The `json_var` attribute is specified on the query that *receives* the JSON, not the one that produces it. This allows the receiving query to name the variable meaningfully.

---

## See Also

- [README.md](README.md) - Main project documentation
- [CONFIGURATION_MANAGEMENT.md](CONFIGURATION_MANAGEMENT.md) - Configuration file management
- [config/sql.xml](DBToRestAPI/config/sql.xml) - Example configurations including `hello_world_multi_query`
