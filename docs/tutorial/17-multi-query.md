# 17 — Multi-Query Chaining

So far, every endpoint you have built executes a single SQL query and returns the
result.  Multi-query chaining lets you place **multiple `<query>` nodes** inside
one endpoint.  Each query runs in sequence and the output of one becomes the
input of the next — even across different databases.

---

## Why Chain Queries?

Imagine you need to:

1. Look up a contact in **SQL Server**.
2. Check that contact's payment history in **PostgreSQL**.
3. Write a combined audit record back to **SQL Server**.

Without chaining, your client would need three separate API calls and manual
data-plumbing between them.  With chaining, a single request handles everything
server-side.

---

## Anatomy of a Chained Endpoint

Here is the `hello_world_multi_query` example that ships with the project
(`config/sql.xml`):

```xml
<hello_world_multi_query>
  <!-- Fallback connection string for queries without explicit attribute -->
  <connection_string_name>server2</connection_string_name>

  <!-- Query 1 — uses 'server2' (fallback) -->
  <query><![CDATA[
    SELECT 'new role' AS role_from_query1,
           'new user' AS user_from_query1;
  ]]></query>

  <!-- Query 2 — uses 'server3' (explicit) -->
  <query connection_string_name="server3"><![CDATA[
    DECLARE @role NVARCHAR(100) = {{role_from_query1}};

    SELECT * FROM (VALUES
      ('Developer',        'Developer'),
      ('Administrator',    'Administrator'),
      ('Insiders Admin',   'Insiders Admin'),
      ('Insiders Maker',   'Insiders Maker'),
      ('Insiders Checker', 'Insiders Checker'),
      (@role,              @role)
    ) AS t ([value], [label]);
  ]]></query>

  <!-- Query 3 — uses 'server2' (fallback) -->
  <query><![CDATA[
    DECLARE @roles_json NVARCHAR(MAX) = {{json}};

    SELECT
      JSON_VALUE(value, '$.value') AS role_value,
      JSON_VALUE(value, '$.label') AS role_label
    FROM OPENJSON(@roles_json);
  ]]></query>
</hello_world_multi_query>
```

Three queries, two databases, one request — no client-side orchestration.

---

## How Data Flows Between Queries

The passing rule depends on **how many rows** the previous query returns.

### Single Row → Named Parameters

When a query returns **exactly one row**, each column becomes a parameter for
the next query, accessible with the usual `{{column_name}}` syntax:

```
Query 1 result
┌──────────────────┬──────────────────┐
│ role_from_query1  │ user_from_query1  │
├──────────────────┼──────────────────┤
│ new role          │ new user          │
└──────────────────┴──────────────────┘

        ↓

Query 2 can use: {{role_from_query1}}, {{user_from_query1}}
```

This is exactly what happens between Query 1 and Query 2 above.

### Multiple Rows → JSON Array

When a query returns **more than one row**, the entire result set is serialised
as a JSON array and passed through a variable called `{{json}}` by default:

```
Query 2 result (6 rows)
┌───────────────────┬───────────────────┐
│ value              │ label              │
├───────────────────┼───────────────────┤
│ Developer          │ Developer          │
│ Administrator      │ Administrator      │
│ ...                │ ...                │
└───────────────────┴───────────────────┘

        ↓   serialised as JSON array

Query 3 receives {{json}} containing:
[
  {"value":"Developer","label":"Developer"},
  {"value":"Administrator","label":"Administrator"},
  ...
]
```

Query 3 then uses `OPENJSON` (SQL Server) or the equivalent JSON function in
other databases to unpack the array.

### Customising the JSON Variable Name

If you chain many queries and need to distinguish results, add a
`json_var` attribute to the **receiving** query:

```xml
<query json_var="roles_data"><![CDATA[
  DECLARE @roles NVARCHAR(MAX) = {{roles_data}};
  -- ...
]]></query>
```

You can even carry forward multiple JSON blobs by using
`json_variable_name` on intermediate queries:

```xml
<!-- Query 1: returns multiple users -->
<query json_variable_name="users_json"><![CDATA[
  SELECT id, name FROM users;
]]></query>

<!-- Query 2: returns multiple orders -->
<query json_variable_name="orders_json"><![CDATA[
  SELECT * FROM orders WHERE user_id IN (
    SELECT JSON_VALUE(value, '$.id')
    FROM OPENJSON({{users_json}})
  );
]]></query>

<!-- Query 3: both are available -->
<query><![CDATA[
  DECLARE @users  NVARCHAR(MAX) = {{users_json}};
  DECLARE @orders NVARCHAR(MAX) = {{orders_json}};
  -- combine as needed
]]></query>
```

---

## Connection String Resolution

Each `<query>` can target a different database.  Resolution order:

| Priority | Source | Example |
|----------|--------|---------|
| **1** | `connection_string_name` attribute on the `<query>` | `<query connection_string_name="analytics_db">` |
| **2** | `<connection_string_name>` tag on the endpoint | `<connection_string_name>primary_db</connection_string_name>` |
| **3** | The `"default"` connection string from `settings.xml` | Automatic fallback |

This means you can mix SQL Server, PostgreSQL, MySQL, and others in a single
chain — as long as you have connection strings configured for each.

---

## Per-Query Timeout

Long-running queries in the middle of a chain can have their own timeout:

```xml
<query db_command_timeout="120"><![CDATA[
  -- runs an expensive report; gets 120 seconds
  SELECT * FROM slow_analytics_view;
]]></query>
```

Resolution order for timeout: query attribute → endpoint-level
`<db_command_timeout>` → global config → provider default.

---

## Practical Example: Contact Permissions

Let's extend the phonebook from earlier tutorials.  Suppose contacts are stored
in one database and access-control roles live in another:

```xml
<contact_with_permissions>
  <route>contacts/{{contact_id}}/permissions</route>
  <verb>GET</verb>
  <mandatory_parameters>contact_id</mandatory_parameters>

  <!-- Query 1: fetch the contact (SQL Server, default) -->
  <query><![CDATA[
    DECLARE @id INT = {{contact_id}};

    IF NOT EXISTS (SELECT 1 FROM contacts WHERE id = @id)
      THROW 50404, 'Contact not found', 1;

    SELECT id   AS contact_id,
           name AS contact_name,
           email
    FROM contacts
    WHERE id = @id;
  ]]></query>

  <!-- Query 2: fetch permissions from a separate database -->
  <query connection_string_name="permissions_db"><![CDATA[
    DECLARE @email NVARCHAR(255) = {{email}};

    SELECT p.name        AS permission,
           p.description AS permission_desc
    FROM permissions      p
    JOIN user_permissions  up ON p.id = up.permission_id
    JOIN users             u  ON u.id = up.user_id
    WHERE u.email = @email;
  ]]></query>

  <!-- Query 3: combine into a neat response (back on default DB) -->
  <query json_var="perms"><![CDATA[
    DECLARE @contact_name NVARCHAR(255) = {{contact_name}};
    DECLARE @perms_json   NVARCHAR(MAX) = {{perms}};

    SELECT @contact_name AS name,
           (SELECT JSON_VALUE(value, '$.permission')      AS name,
                   JSON_VALUE(value, '$.permission_desc')  AS description
            FROM OPENJSON(@perms_json)
            FOR JSON PATH) AS permissions;
  ]]></query>
</contact_with_permissions>
```

Call it:

```
GET /contacts/42/permissions
```

Response:

```json
{
  "name": "Alice Johnson",
  "permissions": [
    { "name": "view_reports",  "description": "View analytics" },
    { "name": "edit_profile",  "description": "Edit own profile" }
  ]
}
```

---

## Error Handling in Chains

If **any** query in the chain fails, execution stops immediately and the error
is returned to the client.  The same `THROW 50xxx` pattern works:

```xml
<!-- Query 1: validate first -->
<query><![CDATA[
  IF NOT EXISTS (SELECT 1 FROM admins WHERE user_id = {{user_id}})
    THROW 50403, 'Access denied', 1;

  SELECT {{user_id}} AS validated_user_id;
]]></query>

<!-- Query 2: safe to proceed -->
<query><![CDATA[
  DELETE FROM records WHERE owner_id = {{validated_user_id}};
  SELECT 'Deleted' AS status;
]]></query>
```

**Best practice:** put validation in the earliest possible query so you fail
fast before touching other databases.

---

## Caching a Chain

Caching wraps the **entire chain** — only the final query's result is cached.
When the cache expires, all queries re-execute:

```xml
<cached_chain_example>
  <cache>
    <memory>
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      <invalidators>customer_id</invalidators>
    </memory>
  </cache>

  <query>...</query>
  <query connection_string_name="external_db">...</query>
</cached_chain_example>
```

---

## Tips and Limitations

| Tip | Detail |
|-----|--------|
| **Keep chains short** | 3–4 queries maximum; longer chains are hard to debug |
| **Name columns carefully** | Column names become parameter names — be descriptive (`order_total` not `total`) |
| **Validate early** | Check permissions and existence in Query 1 |
| **Handle empty results** | An intermediate query returning zero rows passes `null` values; your SQL should use `IF @param IS NULL …` |
| **No parallel execution** | Queries run sequentially; there is no way to fan out |
| **No cross-database transactions** | Each query runs in its own transaction context |
| **Large JSON payloads** | Very large result sets serialised as JSON can impact performance; filter aggressively in intermediate queries |

---

## What You Learned

- Place multiple `<query>` nodes inside an endpoint to create a chain.
- Single-row results pass as named parameters; multi-row results pass as a
  `{{json}}` array.
- Use `json_variable_name` / `json_var` to custom-name the JSON variable.
- Each query can target a different database via `connection_string_name`.
- Errors in any query stop the chain immediately.
- Caching applies to the final result of the entire chain.

---

**Next:** [Production & Deployment Tips →](18-production.md)

[← Back to Index](index.md)
