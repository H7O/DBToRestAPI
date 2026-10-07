# Multi-Database Support

Connect to SQL Server, PostgreSQL, MySQL, SQLite, Oracle, IBM DB2, and any ODBC or OleDb data source — even within the same API.

## Supported Databases

| Database | Provider | Auto-Detected |
|----------|----------|---------------|
| SQL Server | `Microsoft.Data.SqlClient` | ✅ |
| PostgreSQL | `Npgsql` | ✅ |
| MySQL/MariaDB | `MySqlConnector` | ✅ |
| SQLite | `Microsoft.Data.Sqlite` | ✅ |
| Oracle | `Oracle.ManagedDataAccess.Core` | ✅ |
| IBM DB2 | `Net.IBM.Data.Db2` | ✅ |
| ODBC | `System.Data.Odbc` | ✅ |
| OleDb | `System.Data.OleDb` | ✅ |

> **ODBC & OleDb**: These providers natively use positional `?` parameters. DBToRestAPI transparently converts your `{{named}}` parameters into correctly ordered positional parameters — same friendly syntax for all databases.

> **Lazy loading**: Provider assemblies load on demand — a deployment that only uses SQLite never loads the PostgreSQL, MySQL, Oracle, DB2, ODBC, or OleDb drivers into memory. So bundling every provider costs disk, not RAM. (An advanced `-p:DbProviders=lite` source build can strip everything except SQL Server + SQLite if you want a truly minimal binary, but it's rarely needed.)

> **IBM DB2 native driver**: DB2 requires IBM's CLI driver (`clidriver`, ~80 MB, x64/OS-specific). The self-contained **`DBToRestAPI-win-x64`** and **`DBToRestAPI-linux-x64`** release archives bundle it, so DB2 works out of the box there. For other targets (or the portable build), publish on an x64 Windows/Linux host with `-p:IncludeDb2Native=true`, or install the IBM Data Server Driver on the host. (Local `dotnet run` includes it automatically.)

## Connection String Configuration

`/config/settings.xml`:

```xml
<ConnectionStrings>
  <!-- SQL Server (default, auto-detected) -->
  <default><![CDATA[Server=.\SQLEXPRESS;Database=app;Integrated Security=True;TrustServerCertificate=True;]]></default>
  
  <!-- PostgreSQL -->
  <postgres provider="Npgsql"><![CDATA[Host=localhost;Database=analytics;Username=user;Password=pass;]]></postgres>
  
  <!-- MySQL -->
  <mysql provider="MySqlConnector"><![CDATA[Server=localhost;Database=legacy;User=root;Password=pass;]]></mysql>
  
  <!-- SQLite -->
  <sqlite provider="Microsoft.Data.Sqlite"><![CDATA[Data Source=local.db;]]></sqlite>
  
  <!-- Oracle -->
  <oracle provider="Oracle.ManagedDataAccess.Core"><![CDATA[Data Source=localhost:1521/ORCL;User Id=user;Password=pass;]]></oracle>
  
  <!-- IBM DB2 -->
  <db2 provider="Net.IBM.Data.Db2"><![CDATA[Server=localhost:50000;Database=mainframe;UID=admin;PWD=pass;]]></db2>
  
  <!-- ODBC -->
  <odbc_source provider="System.Data.Odbc"><![CDATA[Driver={ODBC Driver 18 for SQL Server};Server=myserver;Database=mydb;Uid=user;Pwd=pass;]]></odbc_source>
  
  <!-- OleDb -->
  <oledb_source provider="System.Data.OleDb"><![CDATA[Provider=MSOLEDBSQL;Server=myserver;Database=mydb;Trusted_Connection=yes;]]></oledb_source>
</ConnectionStrings>
```

## Per-Endpoint Database

Use `connection_string_name` to target different databases:

```xml
<!-- Uses default (SQL Server) -->
<get_users>
  <route>users</route>
  <response_structure>array</response_structure>
  <query><![CDATA[SELECT * FROM users;]]></query>
</get_users>

<!-- Uses PostgreSQL -->
<get_analytics>
  <route>analytics</route>
  <response_structure>array</response_structure>
  <connection_string_name>postgres</connection_string_name>
  <query><![CDATA[SELECT * FROM analytics_data;]]></query>
</get_analytics>

<!-- Uses SQLite -->
<get_config>
  <route>config</route>
  <response_structure>array</response_structure>
  <connection_string_name>sqlite</connection_string_name>
  <query><![CDATA[SELECT * FROM app_settings;]]></query>
</get_config>
```

## Cross-Database Error Handling

Each database has different error syntax:

### SQL Server
```sql
THROW 50404, 'Not found', 1;
THROW 50409, 'Conflict', 1;
```

### MySQL/MariaDB
```sql
SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';
```

### PostgreSQL
`RAISE` works only in PL/pgSQL, and a `DO` block can't see request parameters. Put the check in a procedure and `CALL` it from the endpoint's query with the request values. (A function would have to be called with `SELECT`, which returns a row, and the engine reads only the first result set, so that row would replace your response.)
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
**Note:** A `NULL` id matches no row, so it gets the 404 too. The message keeps a `P0001:` prefix (known issue). A validation example: [errors.md](../reference/errors.md#raising-an-error-from-sql).

### Oracle
```sql
BEGIN
  RAISE_APPLICATION_ERROR(-20404, 'Not found');
END;
```
**Note:** `RAISE_APPLICATION_ERROR` is PL/SQL, so it runs only inside a `BEGIN ... END;` block or a procedure. The engine expects the -20000 to -20999 range (`-20404` → HTTP 404), but the Oracle driver reports the number as positive, so in 1.7.8 this arrives as the generic 400 (known issue).

### SQLite
```sql
-- once, in the database: SQLite accepts RAISE() only inside a trigger
CREATE TRIGGER orders_require_customer
BEFORE INSERT ON orders
WHEN NOT EXISTS (SELECT 1 FROM customers WHERE id = NEW.customer_id)
BEGIN
  SELECT RAISE(ABORT, '[50404] Customer not found');
END;
```
**Note:** An endpoint's `INSERT INTO orders` for an unknown customer then gets the 404. Outside a trigger, SQLite refuses `RAISE()` and the caller gets the generic 400.

### DB2
```sql
-- Only inside a compound statement (BEGIN ... END) or a procedure.
BEGIN
  SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';
END
```
**Note:** In 1.7.8 this arrives as the generic 400: the engine reads the first `[nnnnn]` in the message, which is the SQLSTATE (known issue).

The response body, every other status, and when uploaded files are rolled back: [Errors, status codes and rollback](../reference/errors.md).

## Query Chaining Across Databases

Execute queries across multiple databases in one API call:

```xml
<cross_database_workflow>
  <route>workflow</route>
  
  <!-- Query 1: SQL Server (default) -->
  <query><![CDATA[
    SELECT id, email FROM users WHERE id = {{user_id}};
  ]]></query>
  
  <!-- Query 2: PostgreSQL analytics, using Query 1's id column -->
  <query connection_string_name="postgres"><![CDATA[
    SELECT event_type, COUNT(*) as count
    FROM events WHERE user_id = {pq{id}}
    GROUP BY event_type;
  ]]></query>
  
  <!-- Query 3: DB2 mainframe, using Query 1's id column -->
  <query connection_string_name="db2"><![CDATA[
    SELECT ACCOUNT_STATUS FROM MAINFRAME.ACCOUNTS
    WHERE USER_ID = {pq{id}};
  ]]></query>
</cross_database_workflow>
```

Read an earlier query's columns with `{pq{name}}`. With `{{name}}`, a `NULL` column, zero rows or several rows let a request value with the same name fill the placeholder instead. Only the last query's rows reach the caller. See [Query Chaining](14-query-chaining.md).

## Per-Query Timeout

```xml
<query db_command_timeout="120"><![CDATA[
  -- Long-running analytics query
  SELECT * FROM large_table;
]]></query>
```

## Use Cases

### Hybrid Architecture
- **SQL Server**: Transactional data
- **PostgreSQL**: Analytics warehouse
- **SQLite**: Local configuration
- **Oracle**: Legacy system
- **DB2**: Mainframe data

### Read Replicas
```xml
<ConnectionStrings>
  <primary><![CDATA[Server=primary.db;...]]></primary>
  <replica><![CDATA[Server=replica.db;...]]></replica>
</ConnectionStrings>
```

```xml
<!-- Writes go to primary -->
<create_order>
  <connection_string_name>primary</connection_string_name>
  <query><![CDATA[INSERT INTO orders...]]></query>
</create_order>

<!-- Reads from replica -->
<list_orders>
  <response_structure>array</response_structure>
  <connection_string_name>replica</connection_string_name>
  <query><![CDATA[SELECT * FROM orders...]]></query>
</list_orders>
```

### Multi-Tenant
```xml
<ConnectionStrings>
  <tenant_a><![CDATA[Server=tenant-a.db;...]]></tenant_a>
  <tenant_b><![CDATA[Server=tenant-b.db;...]]></tenant_b>
</ConnectionStrings>
```

## Provider Detection

Auto-detection examines connection string patterns:
- `Data Source=`, `Server=` → SQL Server
- `Host=` → PostgreSQL
- `Server=` with `SslMode=` → MySQL
- `Data Source=*.db` → SQLite
- `:1521` or `SERVICE_NAME` → Oracle
- `:50000` → DB2
- `Driver=` → ODBC
- `Provider=` (OleDb-specific providers) → OleDb

**Recommendation:** Explicitly specify `provider` attribute in production — especially for ODBC and OleDb connections.

## Related Topics

- [Query Chaining](14-query-chaining.md) - Cross-database workflows
- [Configuration](02-configuration.md) - Connection string setup
- [CRUD Operations](03-crud-operations.md) - Database-specific patterns
