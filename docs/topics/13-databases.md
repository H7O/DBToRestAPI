# Multi-Database Support

Connect to SQL Server, PostgreSQL, MySQL, SQLite, Oracle, and IBM DB2 — even within the same API.

## Supported Databases

| Database | Provider | Auto-Detected |
|----------|----------|---------------|
| SQL Server | `Microsoft.Data.SqlClient` | ✅ |
| PostgreSQL | `Npgsql` | ✅ |
| MySQL/MariaDB | `MySqlConnector` | ✅ |
| SQLite | `Microsoft.Data.Sqlite` | ✅ |
| Oracle | `Oracle.ManagedDataAccess.Core` | ✅ |
| IBM DB2 | `Net.IBM.Data.Db2` | ✅ |

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
</ConnectionStrings>
```

## Per-Endpoint Database

Use `connection_string_name` to target different databases:

```xml
<!-- Uses default (SQL Server) -->
<get_users>
  <route>users</route>
  <query><![CDATA[SELECT * FROM users;]]></query>
</get_users>

<!-- Uses PostgreSQL -->
<get_analytics>
  <route>analytics</route>
  <connection_string_name>postgres</connection_string_name>
  <query><![CDATA[SELECT * FROM analytics_data;]]></query>
</get_analytics>

<!-- Uses SQLite -->
<get_config>
  <route>config</route>
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
```sql
RAISE EXCEPTION '[50404] Not found';
```

### Oracle
```sql
RAISE_APPLICATION_ERROR(-20404, 'Not found');
```
**Note:** Oracle uses -20000 to -20999 range. `-20404` → HTTP 404.

### SQLite
```sql
SELECT RAISE(ABORT, '[50404] Not found');
```

### DB2
```sql
SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';
```

## Query Chaining Across Databases

Execute queries across multiple databases in one API call:

```xml
<cross_database_workflow>
  <route>workflow</route>
  
  <!-- Query 1: SQL Server (default) -->
  <query><![CDATA[
    SELECT id, email FROM users WHERE id = {{user_id}};
  ]]></query>
  
  <!-- Query 2: PostgreSQL analytics -->
  <query connection_string_name="postgres"><![CDATA[
    SELECT event_type, COUNT(*) as count
    FROM events WHERE user_id = {{id}}
    GROUP BY event_type;
  ]]></query>
  
  <!-- Query 3: DB2 mainframe -->
  <query connection_string_name="db2"><![CDATA[
    SELECT ACCOUNT_STATUS FROM MAINFRAME.ACCOUNTS
    WHERE USER_ID = {{id}};
  ]]></query>
</cross_database_workflow>
```

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
  <query>INSERT INTO orders...</query>
</create_order>

<!-- Reads from replica -->
<list_orders>
  <connection_string_name>replica</connection_string_name>
  <query>SELECT * FROM orders...</query>
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

**Recommendation:** Explicitly specify `provider` attribute in production.

## Related Topics

- [Query Chaining](14-query-chaining.md) - Cross-database workflows
- [Configuration](02-configuration.md) - Connection string setup
- [CRUD Operations](03-crud-operations.md) - Database-specific patterns
