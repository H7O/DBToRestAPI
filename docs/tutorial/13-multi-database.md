# Multi-Database Queries

One of the unique strengths of DBToRestAPI is its ability to connect to **multiple databases** — even different database engines — within the same API. In this topic, you'll learn how to configure multiple connection strings and route queries to different databases.

## Defining Multiple Connection Strings

In `/config/settings.xml`, each connection string gets a unique name:

```xml
<ConnectionStrings>
  <!-- Default: SQL Server -->
  <default><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;Integrated Security=True;TrustServerCertificate=True;]]></default>

  <!-- PostgreSQL analytics database -->
  <analytics provider="Npgsql"><![CDATA[Host=analytics.example.com;Port=5432;Database=analytics;Username=reader;Password=pass;]]></analytics>

  <!-- MySQL legacy system -->
  <legacy provider="MySqlConnector"><![CDATA[Server=legacy-server;Port=3306;Database=legacy;User=root;Password=pass;SslMode=None;]]></legacy>

  <!-- SQLite for local config -->
  <config_db provider="Microsoft.Data.Sqlite"><![CDATA[Data Source=config.db;]]></config_db>
</ConnectionStrings>
```

### Supported Databases

| Database | `provider` Attribute | Auto-Detected? |
|----------|---------------------|----------------|
| SQL Server | `Microsoft.Data.SqlClient` | Yes |
| PostgreSQL | `Npgsql` | Yes |
| MySQL/MariaDB | `MySqlConnector` | Yes |
| SQLite | `Microsoft.Data.Sqlite` | Yes |
| Oracle | `Oracle.ManagedDataAccess.Core` | Yes |
| IBM DB2 | `Net.IBM.Data.Db2` | Yes |
| ODBC | `System.Data.Odbc` | Yes |
| OleDb | `System.Data.OleDb` | Yes |

> **Auto-detection**: SQL Server, PostgreSQL, MySQL, and SQLite connection strings are recognized automatically. For Oracle, DB2, ODBC, and OleDb, it's best to always specify the `provider` attribute explicitly despite auto-detection to avoid any ambiguity in some edge cases.

> **ODBC & OleDb named parameters**: These providers natively use positional `?` parameters. DBToRestAPI transparently converts your `{{named}}` parameters into correctly ordered positional parameters — you write the same friendly parameter syntax for all databases.

## Routing Queries to a Specific Database

Use `<connection_string_name>` in your endpoint:

```xml
<!-- Queries the default SQL Server -->
<get_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <query><![CDATA[SELECT * FROM contacts ORDER BY name;]]></query>
</get_contacts>

<!-- Queries the PostgreSQL analytics database -->
<get_analytics>
  <route>analytics/overview</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <connection_string_name>analytics</connection_string_name>
  <query><![CDATA[
    SELECT date, page_views, unique_users 
    FROM daily_stats 
    ORDER BY date DESC 
    LIMIT 30;
  ]]></query>
</get_analytics>

<!-- Queries the MySQL legacy system -->
<get_orders>
  <route>legacy/orders</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <connection_string_name>legacy</connection_string_name>
  <query><![CDATA[
    SELECT order_id, customer_name, total 
    FROM orders 
    ORDER BY order_date DESC 
    LIMIT 100;
  ]]></query>
</get_orders>
```

If `<connection_string_name>` is omitted, the `default` connection string is used.

## Cross-Database Error Handling

Each database engine has its own syntax for raising errors. The application turns most of them into HTTP status codes. [Errors, status codes and rollback](../reference/errors.md) lists which ones work:

### SQL Server
```sql
THROW 50404, 'Not found', 1;
THROW 50409, 'Already exists', 1;
```

### PostgreSQL
`RAISE` works only in PL/pgSQL, and a `DO` block can't see request parameters. So put the check in a procedure and `CALL` it from the endpoint's query with the request values. (A function would be called with `SELECT`, whose row would replace your response.)
```sql
-- once, in the database
-- It takes the id as text and checks its format before casting: a failed cast's error message
-- repeats the input, so a caller could otherwise put a [5xxxx] token in it and pick the status.
CREATE OR REPLACE PROCEDURE require_contact(contact_id text) LANGUAGE plpgsql AS $$
BEGIN
  IF contact_id IS NULL OR contact_id !~ '^[0-9]{1,9}$' THEN
    RAISE EXCEPTION '[50404] Not found';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM contacts WHERE id = contact_id::integer) THEN
    RAISE EXCEPTION '[50404] Not found';
  END IF;
END $$;

-- in the endpoint's query, before any statement that returns rows
CALL require_contact(CAST({{id}} AS text));
```
A `NULL` id matches no row, so it gets the 404 too. [Errors, status codes and rollback](../reference/errors.md#raising-an-error-from-sql) has a validation example.

### MySQL / MariaDB
```sql
SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';
```

### Oracle
```sql
-- Oracle uses the -20000 to -20999 range, from PL/SQL only (a BEGIN ... END; block or a procedure).
-- Not mapped (a known issue): the engine expects a negative error number, but the
-- Oracle driver reports a positive one, so this arrives as the generic 400.
BEGIN
  RAISE_APPLICATION_ERROR(-20404, 'Not found');
END;
```

### SQLite
```sql
-- Once, in the database. Only inside a trigger: SQLite refuses RAISE() anywhere else,
-- and the caller gets the generic 400.
CREATE TRIGGER orders_require_customer
BEFORE INSERT ON orders
WHEN NOT EXISTS (SELECT 1 FROM customers WHERE id = NEW.customer_id)
BEGIN
  SELECT RAISE(ABORT, '[50404] Customer not found');
END;
```

### IBM DB2
```sql
-- Only inside a compound statement (BEGIN ... END) or a procedure.
-- Not mapped (a known issue): arrives as the generic 400.
BEGIN
  SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';
END
```

The key pattern: embed the HTTP status code (404, 409, etc.) in the error code or message, and the application extracts it. The Oracle and DB2 forms are not mapped yet and arrive as a generic 400. See [Errors, status codes and rollback](../reference/errors.md) for the details and for what the client receives.

## Practical Example: A Cross-Database Endpoint

Using [Query Chaining](17-multi-query.md) (covered in a later topic), you can query multiple databases in a single request:

```xml
<cross_db_report>
  <route>report/combined</route>
  <verb>GET</verb>
  
  <!-- Query 1: Get contacts from SQL Server (default) -->
  <query><![CDATA[
    SELECT TOP 10 name, phone FROM contacts ORDER BY name;
  ]]></query>
  
  <!-- Query 2: Get analytics from PostgreSQL -->
  <query connection_string_name="analytics"><![CDATA[
    SELECT page_views, unique_users 
    FROM daily_stats 
    WHERE date = CURRENT_DATE;
  ]]></query>
</cross_db_report>
```

Each `<query>` in a chain can target a different database!

Only the last query's result reaches the caller, so in the example above the response holds only Query 2's columns: to include the contacts, Query 2 must read Query 1's rows. Each earlier result is passed to the next query: read a single row's columns as `{pq{name}}`, and the rows as a JSON array with `{pq{json}}`. Use `{pq{...}}` rather than `{{...}}` for these: with `{{name}}`, a `NULL` column, zero rows or several rows let a request value with the same name fill the placeholder instead.

## Database-Specific SQL Tips

### SQL Server
```sql
-- Pagination
SELECT * FROM contacts ORDER BY name OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY;
-- UUID
SELECT NEWID();
-- Date
SELECT GETDATE();
```

### PostgreSQL
```sql
-- Pagination
SELECT * FROM contacts ORDER BY name LIMIT 10 OFFSET 0;
-- UUID
SELECT gen_random_uuid();
-- Date
SELECT NOW();
```

### MySQL
```sql
-- Pagination
SELECT * FROM contacts ORDER BY name LIMIT 10 OFFSET 0;
-- UUID
SELECT UUID();
-- Date
SELECT NOW();
```

### SQLite
```sql
-- Pagination
SELECT * FROM contacts ORDER BY name LIMIT 10 OFFSET 0;
-- UUID (not built-in, use hex + randomblob)
SELECT lower(hex(randomblob(16)));
-- Date
SELECT datetime('now');
```

---

### What You Learned

- How to define multiple connection strings for different databases
- How to route endpoints to specific databases with `<connection_string_name>`
- Cross-database error handling syntax for each database engine
- How query chaining can span multiple databases
- Database-specific SQL patterns for common operations

---

**Next:** [File Uploads →](14-file-uploads.md)

**[Back to Tutorial Index](index.md)**
