# No-Code Database-to-REST API

> **LLM / AI agents**: For a structured, token-efficient overview of this project, see [llms.txt](llms.txt) (plain text) or [llms.md](llms.md) (markdown with full documentation links).

A no-code solution that turns your SQL queries into RESTful APIs — no API coding required.

If you can write basic SQL, you can build safe, secure REST APIs in minutes.

It supports a range of use cases out of the box: public APIs, B2B APIs with API key authentication, or full-stack applications with JWT/OIDC authentication. With built-in support for OAuth 2.0/OIDC providers (Azure B2C, Google, Auth0, and others), you can build complete front-end applications in React, Angular, or Vue that communicate directly with your database through secure, authenticated REST APIs.

Multiple database providers are supported out of the box: SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, Oracle, and IBM DB2 — with automatic provider detection.

> **Note**: For .NET developers looking to extend the solution with custom features, the codebase is fully accessible under MIT license.

## Key Features

- **Pure SQL, zero code** — Define endpoints entirely in XML + SQL. No controllers, no ORM, no compilation step.
- **6 database engines** — SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, Oracle, IBM DB2 with automatic provider detection.
- **Hot-reload** — Edit your XML config files and changes take effect immediately, no restart needed.
- **Built-in security** — API key collections, JWT/OIDC authentication (Azure B2C, Google, Auth0, etc.), and SQL injection protection via parameterised queries.
- **API gateway** — Proxy, cache, and protect external APIs alongside your own endpoints.
- **File management** — Upload to local or SFTP stores, download via streaming, all configured in XML.
- **Multi-query chaining** — Execute sequential queries across different databases in a single request.
- **Embedded HTTP calls** — Call external APIs from within your SQL queries.
- **Settings variables** — Reference encrypted configuration values in queries with `{s{name}}`.
- **Caching** — In-memory response caching with parameter-aware invalidation.
- **Pagination** — Automatic `{count, data}` wrapping with `count_query`.
- **Nested JSON** — Embeds `FOR JSON` results as real JSON, not escaped strings.
- **CORS** — Regex-based origin matching, per-endpoint or global, with automatic preflight handling.
- **Encryption at rest** — Automatically encrypt connection strings and secrets in your config files.

## Quick Start

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later)
- A database — SQL Server is used below, but any of the six supported engines works

### 2. Create a test database

```sql
CREATE DATABASE test;
GO
USE test;
GO
CREATE TABLE [dbo].[contacts] (
    [id]     UNIQUEIDENTIFIER DEFAULT (NEWID()) NOT NULL PRIMARY KEY,
    [name]   NVARCHAR(500)    NULL,
    [phone]  NVARCHAR(100)    NULL,
    [active] BIT              NULL DEFAULT 1
);
```

### 3. Clone & configure

```bash
git clone https://github.com/H7O/DBToRestAPI.git
cd DBToRestAPI
```

Open `DBToRestAPI/config/settings.xml` and set the default connection string:

```xml
<ConnectionStrings>
  <default><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;Integrated Security=True;TrustServerCertificate=True;]]></default>
</ConnectionStrings>
```

### 4. Run

```bash
cd DBToRestAPI
dotnet run
```

### 5. Test

```bash
curl -X POST "https://localhost:7054/hello_world" \
     -H "Content-Type: application/json" \
     -d '{"name": "World"}'
```

Response:

```json
{
  "message_from_db": "hello World! Time now is 2025-01-15 12:34:56.789"
}
```

That response came straight from this SQL in `config/sql.xml`:

```xml
<hello_world>
  <query><![CDATA[
    DECLARE @name NVARCHAR(500) = {{name}};
    IF (@name IS NULL OR LTRIM(RTRIM(@name)) = '')
      SET @name = 'world';
    SELECT 'hello ' + @name + '! Time now is '
           + CONVERT(NVARCHAR(50), GETDATE(), 121) AS message_from_db;
  ]]></query>
</hello_world>
```

The XML node name becomes the route. `{{name}}` is safely injected from the request body. Edit the query, save the file, and the endpoint updates instantly — no restart.

## Supported Databases

| Database | Provider | Auto-Detected |
|----------|----------|:-------------:|
| SQL Server | `Microsoft.Data.SqlClient` | ✅ |
| PostgreSQL | `Npgsql` | ✅ |
| MySQL / MariaDB | `MySqlConnector` | ✅ |
| SQLite | `Microsoft.Data.Sqlite` | ✅ |
| Oracle | `Oracle.ManagedDataAccess.Core` | ✅ |
| IBM DB2 | `Net.IBM.Data.Db2` | ✅ |

All providers work cross-platform (Windows, Linux, macOS).

Define connection strings in `config/settings.xml`. Use the `provider` attribute for explicit selection, or let the engine auto-detect:

```xml
<ConnectionStrings>
  <default><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;...]]></default>
  <postgres provider="Npgsql"><![CDATA[Host=localhost;Port=5432;Database=mydb;...]]></postgres>
  <mysql provider="MySqlConnector"><![CDATA[Server=localhost;Port=3306;Database=mydb;...]]></mysql>
</ConnectionStrings>
```

Different endpoints can target different databases via `<connection_string_name>`:

```xml
<get_analytics>
  <route>analytics</route>
  <verb>GET</verb>
  <connection_string_name>postgres</connection_string_name>
  <query><![CDATA[ SELECT * FROM analytics_data; ]]></query>
</get_analytics>
```

## A Quick Taste

Here is a complete CRUD endpoint — a POST that creates a contact and returns the new record:

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <response_structure>single</response_structure>
  <query><![CDATA[
    DECLARE @name  NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};

    INSERT INTO contacts (name, phone)
    OUTPUT inserted.id, inserted.name, inserted.phone, inserted.active
    VALUES (@name, @phone);
  ]]></query>
</create_contact>
```

```bash
curl -X POST "https://localhost:7054/contacts" \
     -H "Content-Type: application/json" \
     -d '{"name":"Alice Johnson","phone":"12345"}'
```

```json
{
  "id": "9a4f2c8d-1b3e-4f5a-8c7d-6e9f0a1b2c3d",
  "name": "Alice Johnson",
  "phone": "12345",
  "active": true
}
```

No controllers, no models, no migrations — just XML and SQL.

## HTTPS / TLS Certificates

For HTTPS (recommended), you'll need a TLS certificate. For local development, [mkcert](https://github.com/FiloSottile/mkcert) makes this trivial — see the [TLS Certificates guide](docs/topics/16-tls-certificates.md) for step-by-step instructions.

## Documentation

### Step-by-Step Tutorial

The **[Tutorial](docs/tutorial/index.md)** walks you through building a complete contacts API from scratch across 21 topics:

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 00 | [Introduction](docs/tutorial/00-introduction.md) | Prerequisites, project setup, how the solution works |
| 01 | [Your First API Endpoint](docs/tutorial/01-hello-world.md) | Run the app, call your first endpoint, understand `sql.xml` |
| 02 | [Building CRUD Endpoints](docs/tutorial/02-basic-crud.md) | POST/GET/PUT/DELETE, mandatory parameters, `success_status_code` |
| 03 | [Parameters Deep Dive](docs/tutorial/03-parameters.md) | Parameter sources, priority, nested JSON, headers |
| 04 | [Pagination & Filtering](docs/tutorial/04-pagination-filtering.md) | `count_query`, search, `take`/`skip` |
| 05 | [Update & Delete](docs/tutorial/05-update-delete.md) | Route parameters like `{{id}}`, returning modified data |
| 06 | [XML Configuration Structure](docs/tutorial/06-xml-structure.md) | Config files, hot-reload, encryption, multi-file organization |
| 07 | [Input Validation](docs/tutorial/07-regex-validation.md) | Mandatory parameters, SQL error codes, regex delimiters |
| 08 | [API Key Protection](docs/tutorial/08-api-keys.md) | API key collections, protecting endpoints |
| 09 | [JWT & OIDC Authentication](docs/tutorial/09-jwt-auth.md) | Providers, `{auth{email}}`, roles, database-driven authorization |
| 10 | [Using Claims in Queries](docs/tutorial/10-claims-in-queries.md) | Accessing JWT claims in SQL, auto-registration patterns |
| 11 | [Caching](docs/tutorial/11-caching.md) | Memory cache for SQL endpoints and gateway routes |
| 12 | [API Gateway](docs/tutorial/12-api-gateway.md) | Proxy routes, wildcards, protecting gateway routes |
| 13 | [Multiple Databases](docs/tutorial/13-multi-database.md) | Connection strings, providers, per-endpoint databases |
| 14 | [File Uploads](docs/tutorial/14-file-uploads.md) | Attach documents to contacts, local/SFTP stores |
| 15 | [File Downloads](docs/tutorial/15-file-downloads.md) | Stream files from stores, database, or HTTP |
| 16 | [Embedded HTTP Calls](docs/tutorial/16-http-from-sql.md) | `{http{...}http}` syntax, calling APIs from SQL |
| 17 | [Multi-Query Chaining](docs/tutorial/17-multi-query.md) | Cross-database workflows, parameter passing between queries |
| 18 | [Settings Variables](docs/tutorial/18-settings-vars.md) | `{s{}}` / `{settings{}}`, `<vars>` config, encrypted secrets |
| 19 | [Production & Deployment](docs/tutorial/19-production.md) | Environment config, TLS, Docker, reverse proxy |
| 20 | [What's Next?](docs/tutorial/20-whats-next.md) | Further resources and community |

### Reference Documentation

| Document | Description |
|----------|-------------|
| [Modular Topics](docs/topics/) | Focused reference files for each feature area |
| [Multi-Query Chaining](MULTI_QUERY_CHAINING.md) | Deep dive into chaining sequential queries |
| [Configuration Management](CONFIGURATION_MANAGEMENT.md) | Config file structure and hot-reload behavior |
| [API Gateway Cache](API_GATEWAY_CACHE_IMPLEMENTATION.md) | Gateway caching internals |

### AI-Friendly Documentation

For AI-assisted development, use **[llms.txt](llms.txt)** — a lightweight index (~6KB) pointing to focused topic files, letting AI agents fetch only what they need. Humans can view the same content formatted nicely in **[llms.md](llms.md)**.

## Configuration Files

All configuration lives in `DBToRestAPI/config/`:

| File | Purpose |
|------|---------|
| `settings.xml` | Connection strings, CORS, encryption, global settings |
| `sql.xml` | SQL endpoint definitions (routes, queries, caching, auth) |
| `api_gateway.xml` | API gateway proxy routes |
| `api_keys.xml` | API key collections |
| `file_management.xml` | File store definitions (local, SFTP) |
| `auth_providers.xml` | OIDC/JWT provider configurations |
| `regex.xml` | Shared regex patterns |

All files support **hot-reload** — edit and save, changes apply immediately.

## License

MIT

## Contributing

Contributions are welcome! Please open an issue or submit a pull request on [GitHub](https://github.com/H7O/DBToRestAPI).
