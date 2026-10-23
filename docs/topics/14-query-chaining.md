---
title: Query chaining
summary: Run several queries in one endpoint, in order, each on its own connection and possibly its own database; later queries read earlier results with {pq{name}}, and the last one answers.
keywords: ["<query>", chain, connection_string_name, json_var, db_command_timeout, "{pq{name}}", "{pq{json}}", OPENJSON, cross-database, "Query 2 of 3 failed"]
---

# Query chaining

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](../reference/errors.md).

An endpoint with several `<query>` elements runs them in order. Each query can use another connection, so one request can read from two databases, and each later query reads the earlier ones' results with `{pq{name}}`. The last query's result is the answer. Use a chain when the data lives in more than one database, or when a check must run before a [`{http{ ... }http}` call](17-embedded-http-calls.md) is even read. On one database, one query with several statements is simpler, and it can use a transaction.

## Rules

1. **Read an earlier query's result with `{pq{name}}`, never `{{name}}`.** `{{name}}` falls back to a request value with the same name whenever the earlier queries give no value for it: a `NULL` column, zero rows, or several rows. A caller could then replace a value your SQL looked up, such as an owner id ([AGENTS.md](../../AGENTS.md) rule 17).
2. **Only a query that returns exactly one row passes its columns.** Zero rows or several pass only their JSON array, `{pq{json}}` (`[]` for zero rows). Read several rows with `OPENJSON({pq{json}})`.
3. **Put `json_var` on the query that reads the rows, not on the one that returns them.** `<query json_var="lines">` names the rows of the query just before it `{pq{lines}}`. Without it they are `{pq{json}}`.
4. **The queries don't share a transaction.** Each runs on its own connection and commits on its own, so an error in a later query doesn't undo an earlier one's writes. Put writes that must happen together in one query, and the checks that can refuse a request first.
5. **Alias every column a later query reads.** A row whose only column has no name, such as an unaliased `SELECT COUNT(*)` on SQL Server, passes no column: only `{pq{json}}`, which holds `[2]`.
6. **Write each query in the dialect of its own connection**: SQL Server SQL for a SQL Server connection, PostgreSQL SQL for a PostgreSQL one ([13](13-databases.md)).

## How it works

1. Before any query runs, the engine checks the request as it does for one query: API keys, token, rate limit, `mandatory_parameters`, uploads ([errors.md](../reference/errors.md)).
2. Each query, in order:
   - takes its connection from its `connection_string_name` attribute, else the endpoint's `<connection_string_name>`, else `default`;
   - takes its timeout from its `db_command_timeout` attribute, else the endpoint's `<db_command_timeout>`, else the global one;
   - makes its `{http{ ... }http}` calls, with the earlier queries' results already filled in, so an earlier query can decide whether a call is made ([23](23-http-call-patterns.md#let-the-database-decide-whether-to-call));
   - runs, on a connection of its own, closed before the next query runs.
3. After each query but the last, the engine keeps what the next queries can read: the JSON array of its rows, under the next query's `json_var` (`json` without one), and, when it returned exactly one row, each of that row's columns. Earlier queries' values stay, unless a later query returns a column of the same name.
4. The last query answers, as a single query does: `<response_structure>`, `<count_query>`, `<success_status_code>`, `<root_node>` and `<cache>` all apply to its result.
5. An error raised in any query stops the chain and gives its status. Uploaded files are deleted when the final status is `400` or higher, but rows the earlier queries wrote stay (rule 4).

## Complete example: an order with live stock from another database

An order page shows an order's lines with the stock a warehouse database holds for each, on SQL Server. Query 1 checks that the order is the caller's, and passes its id and status on. Query 2 reads the order's lines. Query 3 runs on the warehouse connection, and joins the lines with the stock.

### Tables

In the shop's database:

```sql
CREATE TABLE customers (
  id       UNIQUEIDENTIFIER PRIMARY KEY,
  owner_id NVARCHAR(200)    NOT NULL,          -- the user's {auth{user_id}}
  name     NVARCHAR(200)    NOT NULL
);

CREATE TABLE orders (
  id          UNIQUEIDENTIFIER PRIMARY KEY,
  customer_id UNIQUEIDENTIFIER NOT NULL REFERENCES customers(id),
  status      NVARCHAR(20)     NOT NULL,       -- 'placed', 'shipped'
  placed_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE order_lines (
  order_id   UNIQUEIDENTIFIER NOT NULL REFERENCES orders(id),
  product_id NVARCHAR(20)     NOT NULL,
  quantity   INT              NOT NULL,
  PRIMARY KEY (order_id, product_id)
);
```

In the warehouse's database:

```sql
CREATE TABLE stock (
  product_id NVARCHAR(20) PRIMARY KEY,
  available  INT          NOT NULL
);
```

### Connections

`config/settings.xml`, in the existing `<ConnectionStrings>`, with the `provider` of each:

```xml
<settings>
  <ConnectionStrings>
    <default provider="Microsoft.Data.SqlClient"><![CDATA[Server=shop-db;Database=shop;Integrated Security=true;TrustServerCertificate=true]]></default>
    <warehouse provider="Microsoft.Data.SqlClient"><![CDATA[Server=warehouse-db;Database=warehouse;Integrated Security=true;TrustServerCertificate=true]]></warehouse>
  </ConnectionStrings>
</settings>
```

### Endpoint

`config/sql.xml`, inside `<settings><queries>`:

```xml
<get_order_with_stock>
  <route>orders/{{order_id}}/stock</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <authorize><provider>my_provider</provider></authorize>

  <!-- Query 1 (shop): the order must be the caller's. One row, so its columns pass on. -->
  <query><![CDATA[
    DECLARE @order_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{order_id}});

    IF NOT EXISTS (SELECT 1 FROM orders o JOIN customers c ON c.id = o.customer_id
                   WHERE o.id = @order_id AND c.owner_id = {auth{user_id}})
      THROW 50404, 'Order not found', 1;

    SELECT id AS order_id, status AS order_status FROM orders WHERE id = @order_id;
  ]]></query>

  <!-- Query 2 (shop): the order's lines, as many rows as there are lines -->
  <query><![CDATA[
    SELECT product_id, quantity FROM order_lines WHERE order_id = {pq{order_id}};
  ]]></query>

  <!-- Query 3 (warehouse): json_var names query 2's rows {pq{lines}} here -->
  <query connection_string_name="warehouse" json_var="lines"><![CDATA[
    SELECT l.product_id,
           l.quantity,
           COALESCE(s.available, 0) AS available,
           CAST(CASE WHEN COALESCE(s.available, 0) >= l.quantity THEN 1 ELSE 0 END AS BIT) AS in_stock,
           {pq{order_status}} AS order_status
    FROM OPENJSON({pq{lines}}) WITH (product_id NVARCHAR(20), quantity INT) l
    LEFT JOIN stock s ON s.product_id = l.product_id
    ORDER BY l.product_id;
  ]]></query>
</get_order_with_stock>
```

`{pq{order_id}}` and `{pq{order_status}}` come from query 1, and `{pq{lines}}` from query 2. A request that sends its own `order_status` doesn't change them (rule 1). Query 3 reads `{pq{lines}}` whether the order has no line, one or many: the JSON array holds the rows however many there are.

### Request

```bash
curl https://api.example.com/orders/6f1c2a4e-0d1b-4a52-9a49-3c3a8f0e2b11/stock \
  -H "Authorization: Bearer $TOKEN"
```

### What the client gets back

| Case | Status | Body |
|---|---|---|
| The owner | `200` | `[{"product_id":"A1","quantity":2,"available":10,"in_stock":true,"order_status":"placed"},{"product_id":"B2","quantity":5,"available":3,"in_stock":false,"order_status":"placed"}]` |
| An order without lines | `200` | `[]` |
| Another user's order, an unknown id, or one that isn't a GUID | `404` | `{"success":false,"message":"Order not found","error_number":404}` |
| No token | `401` | `{"success":false,"message":"Authorization header is required"}` |
| The warehouse database can't be reached | `503` | `{"success":false,"message":"An error occurred while processing your request."}` |

A `404` from query 1 stops the chain: queries 2 and 3 never run, and the warehouse is never asked.

## What a query receives

| An earlier query returned | `{pq{column}}` | The JSON variable (`{pq{json}}`, or the reading query's `json_var`) |
|---|---|---|
| One row | The row's value, `NULL` included | `[{...}]` |
| Several rows | Not set by this query: an older query's value, or `NULL` | `[{...},{...}]` |
| No row | Not set by this query: an older query's value, or `NULL` | `[]` |
| One row whose only column has no name | Not set | `[2]` |

The JSON variable holds the rows of the query just before only. An older query's rows stay under the name its reader gave them: with `json_var="users"` on query 2 and `json_var="orders"` on query 3, query 3 reads both `{pq{users}}` and `{pq{orders}}`. The first query has no earlier result: a `json_var` on it is ignored.

## Settings

| Setting | Where | Default | Notes |
|---|---|---|---|
| `connection_string_name` | attribute on `<query>` | the endpoint's `<connection_string_name>`, else `default` | The connection this query runs on. |
| `json_var` | attribute on `<query>` | `json` | The name of the previous query's rows, in this query. |
| `db_command_timeout` | attribute on `<query>` | the endpoint's `<db_command_timeout>`, else the global one | Seconds. A value that isn't a whole number is a `500` naming it ([tags.md](../reference/tags.md#how-to-read-this-page)). |

Every other tag of the endpoint applies to the chain as a whole ([tags.md](../reference/tags.md)).

## Errors

An error raised as `50000 + status` in any query stops the chain and gives that status, with the database's message and nothing that says which query failed:

```json
{ "success": false, "message": "Order not found", "error_number": 404 }
```

Any other database error is the generic `400`, `503` when a query's database can't be reached, and `504` when it times out ([errors.md](../reference/errors.md)). In debug mode, the `500` says which query failed: `Query 2 of 3 failed: ...`.

Silent failures, where the request succeeds:

- `{{name}}` reading an earlier result takes the caller's value when the earlier query gave none (rule 1).
- A query that returned several rows passes no column: `{pq{column}}` is `NULL`, or an older query's value (rule 2).
- `json_var` on the query that returns the rows names nothing: the reading query finds its rows only under `{pq{json}}` (rule 3).
- A write in an earlier query stays when a later one fails (rule 4).

## Caching

A route with [`<cache>`](07-caching.md) caches the last query's answer. A cache hit runs none of the queries, and when the entry expires all of them run again. List in `<invalidators>` every input that changes the answer, whichever query reads it.

## Do and don't

```sql
-- Don't: {{owner_id}} takes the caller's owner_id when query 1 found no row
WHERE owner_id = {{owner_id}}
-- Do
WHERE owner_id = {pq{owner_id}}
```

```xml
<!-- Don't: json_var on the query that returns the rows -->
<query json_var="lines"><![CDATA[SELECT product_id, quantity FROM order_lines;]]></query>
<query><![CDATA[SELECT * FROM OPENJSON({pq{lines}});]]></query>

<!-- Do: on the query that reads them -->
<query><![CDATA[SELECT product_id, quantity FROM order_lines;]]></query>
<query json_var="lines"><![CDATA[SELECT * FROM OPENJSON({pq{lines}});]]></query>
```

## Related

- [13 Databases](13-databases.md): connections, providers and their dialects.
- [17 HTTP calls from SQL](17-embedded-http-calls.md): a chain whose second query makes a call, tested as its complete example.
- [23 HTTP call patterns](23-http-call-patterns.md): letting an earlier query decide whether a call is made.
- [04 Parameters](04-parameters.md): where `{{name}}` comes from.
- [07 Caching](07-caching.md): caching a chain's answer.
