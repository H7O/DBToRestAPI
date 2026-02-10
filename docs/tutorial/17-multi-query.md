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

Suppose you have orders in one database and customer profiles in another.  A
single API call should return the order enriched with customer details:

```xml
<order_with_customer>
  <route>orders/{{order_id}}</route>
  <verb>GET</verb>

  <!-- Query 1: look up the order (default database) -->
  <query><![CDATA[
    DECLARE @id INT = {{order_id}};

    IF NOT EXISTS (SELECT 1 FROM orders WHERE id = @id)
      THROW 50404, 'Order not found', 1;

    SELECT customer_email, total_amount, order_date
    FROM orders
    WHERE id = @id;
  ]]></query>

  <!-- Query 2: enrich with customer info (separate database) -->
  <query connection_string_name="customers_db"><![CDATA[
    DECLARE @email NVARCHAR(255) = {{customer_email}};
    DECLARE @total DECIMAL(10,2) = {{total_amount}};
    DECLARE @date  DATE          = {{order_date}};

    SELECT @email AS email,
           @total AS total_amount,
           @date  AS order_date,
           full_name,
           loyalty_tier
    FROM customers
    WHERE email = @email;
  ]]></query>
</order_with_customer>
```

Two queries, two databases, one request — no client-side orchestration.

Call it:

```
GET /orders/42
```

Response:

```json
{
  "email": "alice@example.com",
  "total_amount": 129.99,
  "order_date": "2025-12-01",
  "full_name": "Alice Johnson",
  "loyalty_tier": "Gold"
}
```

Only the **last query's result** is returned to the caller.  Intermediate
queries are purely for data gathering.

---

## How Data Flows Between Queries

The passing rule depends on **how many rows** the previous query returns.

### Single Row → Named Parameters

When a query returns **exactly one row**, each column becomes a parameter for
the next query, accessible with the usual `{{column_name}}` syntax:

```
Query 1 result
┌──────────────────┬──────────────┬────────────┐
│ customer_email    │ total_amount  │ order_date  │
├──────────────────┼──────────────┼────────────┤
│ alice@example.com │ 129.99        │ 2025-12-01  │
└──────────────────┴──────────────┴────────────┘

        ↓

Query 2 can use: {{customer_email}}, {{total_amount}}, {{order_date}}
```

This is exactly what happens in the order example above.

### Multiple Rows → JSON Array

When a query returns **more than one row**, the entire result set is serialised
as a JSON array and passed through a variable called `{{json}}` by default:

```
Query 1 result (3 rows)
┌──────────────────┬───────┐
│ email             │ name   │
├──────────────────┼───────┤
│ alice@example.com │ Alice  │
│ bob@example.com   │ Bob    │
│ carol@example.com │ Carol  │
└──────────────────┴───────┘

        ↓   serialised as JSON array

Query 2 receives {{json}} containing:
[
  {"email":"alice@example.com","name":"Alice"},
  {"email":"bob@example.com","name":"Bob"},
  {"email":"carol@example.com","name":"Carol"}
]
```

Query 2 then uses `OPENJSON` (SQL Server) or the equivalent JSON function in
other databases to unpack the array.

> **Note**: Single-row results are also available as `{{json}}` (a one-element
> array), so you can always use `{{json}}` regardless of row count.  However,
> named column parameters (`{{column_name}}`) are only available for single-row
> results.

### Customising the JSON Variable Name

If you chain many queries and need to distinguish results, add a
`json_var` attribute to the **receiving** query.  This controls the variable
name that the **previous** query's results are stored under:

```xml
<!-- Query 1: returns multiple roles -->
<query><![CDATA[
  SELECT role_name, role_level FROM roles;
]]></query>

<!-- Query 2: receives Query 1's results as {{roles_data}} instead of {{json}} -->
<query json_var="roles_data"><![CDATA[
  DECLARE @roles NVARCHAR(MAX) = {{roles_data}};
  -- ...
]]></query>
```

To carry forward results from multiple earlier queries, use `json_var` on
each receiving query to give them distinct names:

```xml
<!-- Query 1: returns multiple users -->
<query><![CDATA[
  SELECT id, name FROM users;
]]></query>

<!-- Query 2: json_var="users_json" means Query 1's results arrive as {{users_json}} -->
<query json_var="users_json"><![CDATA[
  SELECT * FROM orders WHERE user_id IN (
    SELECT JSON_VALUE(value, '$.id')
    FROM OPENJSON({{users_json}})
  );
]]></query>

<!-- Query 3: json_var="orders_json" means Query 2's results arrive as {{orders_json}} -->
<!--           Query 1's results are still available as {{users_json}} -->
<query json_var="orders_json"><![CDATA[
  DECLARE @users  NVARCHAR(MAX) = {{users_json}};
  DECLARE @orders NVARCHAR(MAX) = {{orders_json}};
  -- combine as needed
]]></query>
```

Parameters accumulate across the chain — Query 3 can access results from
both Query 1 and Query 2.

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
- Use `json_var` on the receiving query to custom-name the JSON variable.
- Each query can target a different database via `connection_string_name`.
- Errors in any query stop the chain immediately.
- Caching applies to the final result of the entire chain.

---

**Next:** [Production & Deployment Tips →](18-production.md)

[← Back to Index](index.md)
