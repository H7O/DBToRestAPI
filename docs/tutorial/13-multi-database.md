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

> **Auto-detection**: SQL Server, PostgreSQL, MySQL, and SQLite connection strings are recognized automatically. For Oracle and DB2, its best to always specify the `provider` attribute explicitly despite auto-detection to avoid any ambiguity in some edge cases.

## Routing Queries to a Specific Database

Use `<connection_string_name>` in your endpoint:

```xml
<!-- Queries the default SQL Server -->
<get_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  <query><![CDATA[SELECT * FROM contacts ORDER BY name;]]></query>
</get_contacts>

<!-- Queries the PostgreSQL analytics database -->
<get_analytics>
  <route>analytics/overview</route>
  <verb>GET</verb>
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

Each database engine has its own syntax for raising errors. The application normalizes them all into HTTP status codes:

### SQL Server
```sql
THROW 50404, 'Not found', 1;
THROW 50409, 'Already exists', 1;
```

### PostgreSQL
```sql
RAISE EXCEPTION '[50404] Not found';
RAISE EXCEPTION '[50409] Already exists';
```

### MySQL / MariaDB
```sql
SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';
```

### Oracle
```sql
-- Oracle uses -20000 to -20999 range
-- -20404 maps to HTTP 404
RAISE_APPLICATION_ERROR(-20404, 'Not found');
```

### SQLite
```sql
SELECT RAISE(ABORT, '[50404] Not found');
```

### IBM DB2
```sql
SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '[50404] Not found';
```

The key pattern: embed the HTTP status code (404, 409, etc.) in the error code or message, and the application extracts it.

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
