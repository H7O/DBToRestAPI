# DbToRestAPI

> No-code solution to convert SQL queries into RESTful APIs

## What It Is

DbToRestAPI automatically exposes your SQL queries as REST endpoints. Write SQL, get APIs — no ORM, no code generation, no proprietary query languages.

- **SQL-First Philosophy**: Your SQL expertise translates directly to API development
- **Configuration-Driven**: Define endpoints in XML, queries execute as-is
- **Multi-Database**: SQL Server, PostgreSQL, MySQL, SQLite, Oracle, IBM DB2
- **Production-Ready**: Built-in auth, caching, file handling, CORS, encryption

## Core Architecture

```
HTTP Request → Route Matching → Parameter Injection → SQL Execution → JSON Response
                                      ↓
                              {{param}} safely bound
```

## Quick Example

```xml
<!-- /config/sql.xml -->
<get_user>
  <route>users/{{id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    SELECT id, name, email FROM users WHERE id = @id;
  ]]></query>
</get_user>
```

Request: `GET /users/abc-123` → Returns user JSON

## Documentation Topics

Fetch only what you need:

| Topic | File | When to Use |
|-------|------|-------------|
| Quick Start | [01-overview.md](docs/topics/01-overview.md) | Getting started, philosophy, first endpoint |
| Configuration | [02-configuration.md](docs/topics/02-configuration.md) | settings.xml, sql.xml, connection strings |
| CRUD Operations | [03-crud-operations.md](docs/topics/03-crud-operations.md) | Create, Read, Update, Delete patterns |
| Parameters | [04-parameters.md](docs/topics/04-parameters.md) | {{param}}, route params, mandatory params |
| Response Formats | [05-response-formats.md](docs/topics/05-response-formats.md) | response_structure, count_query, nested JSON |
| API Keys | [06-api-keys.md](docs/topics/06-api-keys.md) | Endpoint protection, key collections |
| Caching | [07-caching.md](docs/topics/07-caching.md) | Memory cache, invalidators, duration |
| API Gateway | [08-api-gateway.md](docs/topics/08-api-gateway.md) | Proxy routes, wildcards, external APIs |
| File Uploads | [09-file-uploads.md](docs/topics/09-file-uploads.md) | Local/SFTP, multipart, base64 |
| File Downloads | [10-file-downloads.md](docs/topics/10-file-downloads.md) | Streaming from DB, local, SFTP, HTTP |
| CORS | [11-cors.md](docs/topics/11-cors.md) | Pattern matching, credentials, preflight |
| Authentication | [12-authentication.md](docs/topics/12-authentication.md) | OIDC/JWT, Azure B2C, Google, Auth0 |
| Multi-Database | [13-databases.md](docs/topics/13-databases.md) | Provider config, per-endpoint connections |
| Query Chaining | [14-query-chaining.md](docs/topics/14-query-chaining.md) | Cross-database workflows, multi-query |
| Encryption | [15-encryption.md](docs/topics/15-encryption.md) | Settings encryption, DPAPI, cross-platform |
| TLS Certificates | [16-tls-certificates.md](docs/topics/16-tls-certificates.md) | HTTPS setup, mkcert, Kestrel TLS config |
| Embedded HTTP Calls | [17-embedded-http-calls.md](docs/topics/17-embedded-http-calls.md) | {http{}} syntax, auth, retries, microservice calls from SQL |

## Essential Concepts

### Parameter Injection
```sql
DECLARE @name NVARCHAR(500) = {{name}};  -- From body, query string, or route
DECLARE @id UNIQUEIDENTIFIER = {{id}};    -- Route: /users/{{id}}
DECLARE @email NVARCHAR(500) = {auth{email}};  -- From JWT claims
```

### Error Handling (SQL → HTTP)
| Database | Syntax | HTTP Result |
|----------|--------|-------------|
| SQL Server | `THROW 50404, 'Not found', 1;` | 404 |
| MySQL | `SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404;` | 404 |
| PostgreSQL | `RAISE EXCEPTION '[50404] Not found';` | 404 |
| Oracle | `RAISE_APPLICATION_ERROR(-20404, 'Not found');` | 404 |

Error codes 50000-51000 map to HTTP 0-1000.

### Key XML Tags
| Tag | Purpose |
|-----|---------|
| `<route>` | URL path with params: `users/{{id}}/orders` |
| `<verb>` | HTTP method: `GET`, `POST`, `PUT`, `DELETE` |
| `<mandatory_parameters>` | Required params (returns 400 if missing) |
| `<success_status_code>` | Success HTTP code (default: 200) |
| `<query>` | SQL wrapped in `<![CDATA[...]]>` |
| `<count_query>` | Optional count for pagination |
| `<connection_string_name>` | Use different database |
| `<api_keys_collections>` | Require API key from collection |
| `<authorize>` | JWT/OIDC authentication |
| `<cache>` | Response caching |
| `<cors>` | Cross-origin settings |
| `<file_management>` | File upload/download config |
| `<response_structure>` | `single`, `array`, `auto`, or `file` |

## Configuration Files

| File | Purpose |
|------|---------|
| `/config/settings.xml` | Connection strings, global settings |
| `/config/sql.xml` | API endpoint definitions |
| `/config/api_keys.xml` | API key collections |
| `/config/api_gateway.xml` | Proxy route configurations |
| `/config/file_management.xml` | File store definitions (local/SFTP) |
| `/config/auth_providers.xml` | OIDC provider configurations |

## Common Patterns

### Pagination with Count
```xml
<query>SELECT * FROM items OFFSET {{skip}} ROWS FETCH NEXT {{take}} ROWS ONLY;</query>
<count_query>SELECT COUNT(*) FROM items;</count_query>
```
Response: `{"count": 150, "data": [...]}`

### Protected Endpoint
```xml
<api_keys_collections>my_keys</api_keys_collections>
```
Client sends: `x-api-key: secret-key-123`

### JWT Protected
```xml
<authorize><provider>azure_b2c</provider></authorize>
```
Access claims: `{auth{email}}`, `{auth{sub}}`, `{auth{roles}}`

### Cross-Database Query Chain
```xml
<query>SELECT id FROM users WHERE email = {{email}};</query>
<query connection_string_name="analytics_db">SELECT * FROM events WHERE user_id = {{id}};</query>
```

### File Download
```xml
<response_structure>file</response_structure>
<query>SELECT file_name, relative_path FROM files WHERE id = {{id}};</query>
```

## Repository

https://github.com/H7O/DBToRestAPI

## Dependencies

- [Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common) - SQL parameterization
- ASP.NET Core 8+
