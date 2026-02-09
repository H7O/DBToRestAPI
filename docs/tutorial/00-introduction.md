# Introduction

Welcome to the DBToRestAPI tutorial! In this series, you'll build a fully functional **phonebook contacts API** — step by step — using nothing but SQL queries and XML configuration files.

## What You'll Build

By the end of this tutorial, your phonebook API will support:

- **Creating** contacts (POST)
- **Reading** contacts with pagination and search (GET)
- **Updating** contacts (PUT)
- **Deleting** contacts (DELETE)
- **Custom actions** like activate/deactivate
- **File attachments** (upload ID documents, photos)
- **File downloads** (stream documents back to clients)
- **API key protection** for B2B access
- **JWT/OIDC authentication** for end-user access
- **Caching** for performance
- **Cross-database queries** for advanced workflows

All without writing a single line of application code.

## How It Works

The core idea is simple:

```
HTTP Request → Route Matching → Parameter Injection → SQL Execution → JSON Response
```

1. You define your API endpoints in an XML file (`/config/sql.xml`)
2. Each endpoint maps to a SQL query
3. When a request comes in, the solution matches the route, injects parameters safely into the SQL, executes it, and returns the result as JSON

Here's a quick visual of what a single endpoint definition looks like:

```xml
<!-- This becomes: GET /users/{{id}} -->
<get_user>
  <route>users/{{id}}</route>
  <verb>GET</verb>
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    SELECT id, name, email FROM users WHERE id = @id;
  ]]></query>
</get_user>
```

That's it. No controllers, no models, no ORM. Your SQL runs exactly as written.

## Prerequisites

To follow this tutorial, you need:

### 1. A Database Server

Any one of these:
- **SQL Server** — [Download SQL Server Express](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (free) or Developer Edition
- **PostgreSQL** — [Download PostgreSQL](https://www.postgresql.org/download/)
- **MySQL / MariaDB** — [Download MySQL](https://dev.mysql.com/downloads/) or [MariaDB](https://mariadb.org/download/)
- **SQLite** — No installation needed (file-based)
- **Oracle** — [Download Oracle XE](https://www.oracle.com/database/technologies/xe-downloads.html) (free)
- **IBM DB2** — [Download DB2 Community](https://www.ibm.com/products/db2/developers)

> **Recommendation for this tutorial**: SQL Server Express (or Developer Edition) is the easiest to get started with on Windows. The SQL examples in this tutorial use SQL Server syntax, but equivalent queries for other databases are noted where they differ.

### 2. DBToRestAPI

**Option A — Download a release** (recommended for most users):

Download the latest release for your platform from the [GitHub Releases page](https://github.com/H7O/DBToRestAPI/releases):
- Windows (x64)
- Linux (x64 / arm64)
- macOS (x64 / arm64)

Extract the archive to a folder of your choice.

**Option B — Build from source** (for .NET developers):

```bash
git clone https://github.com/H7O/DBToRestAPI.git
cd DBToRestAPI
dotnet build
```

### 3. An API Testing Tool

Any of these:
- **cURL** — comes pre-installed on Windows 10+, macOS, and Linux
- **[Postman](https://www.postman.com/downloads/)** — visual API testing tool
- **[REST Client for VS Code](https://marketplace.visualstudio.com/items?itemName=humao.rest-client)** — test APIs directly in your editor
- Any HTTP client you prefer

> This tutorial shows commands using **cURL** (copy-paste friendly) with Postman instructions where helpful.

## Project Structure

Once you have the project, here are the key files you'll work with:

```
DBToRestAPI/
├── config/
│   ├── settings.xml          ← Connection strings, global settings
│   ├── sql.xml               ← Your API endpoints (this is where most of your work happens)
│   ├── api_keys.xml          ← API key collections
│   ├── api_gateway.xml       ← Proxy route definitions
│   ├── file_management.xml   ← File store configurations
│   └── auth_providers.xml    ← OIDC provider configurations
├── appsettings.json           ← ASP.NET Core settings (port, logging)
└── Program.cs                 ← Application entry point
```

The `/config/` folder is where everything happens. You'll spend most of your time in two files:
- **`settings.xml`** — your database connection string
- **`sql.xml`** — your API endpoint definitions

All config files support **hot-reload** — change a file while the app is running and the changes apply automatically. No restart needed.

## Setting Up the Test Database

Let's create the database and table we'll use throughout this tutorial.

### Step 1: Create the database

Connect to your database server and create a new database called `test`:

```sql
CREATE DATABASE test;
```

### Step 2: Create the contacts table

Switch to the `test` database and run:

```sql
CREATE TABLE [dbo].[contacts] (
    [id]     UNIQUEIDENTIFIER CONSTRAINT [DEFAULT_contacts_id] DEFAULT (newid()) NOT NULL,
    [name]   NVARCHAR(500)    NULL,
    [phone]  NVARCHAR(100)    NULL,
    [active] BIT              NULL DEFAULT 1,
    CONSTRAINT [PK_contacts] PRIMARY KEY CLUSTERED ([id] ASC)
);
```

This gives us a simple contacts table with:
- `id` — auto-generated GUID primary key
- `name` — contact name
- `phone` — phone number
- `active` — soft-delete flag (1 = active, 0 = inactive)

> **Using PostgreSQL?** The equivalent table definition:
> ```sql
> CREATE TABLE contacts (
>     id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
>     name VARCHAR(500),
>     phone VARCHAR(100),
>     active BOOLEAN DEFAULT true
> );
> ```

> **Using MySQL?** The equivalent:
> ```sql
> CREATE TABLE contacts (
>     id CHAR(36) PRIMARY KEY DEFAULT (UUID()),
>     name VARCHAR(500),
>     phone VARCHAR(100),
>     active TINYINT(1) DEFAULT 1
> );
> ```

## Configuring the Connection String

Open `/config/settings.xml` and update the `default` connection string to point to your `test` database:

**SQL Server:**
```xml
<ConnectionStrings>
  <default><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;Integrated Security=True;TrustServerCertificate=True;]]></default>
</ConnectionStrings>
```

> Replace `.\SQLEXPRESS` with your SQL Server instance name. If you're using SQL Server Developer Edition, it might be `localhost` or `.\MSSQLSERVER`.

**PostgreSQL:**
```xml
<ConnectionStrings>
  <default provider="Npgsql"><![CDATA[Host=localhost;Port=5432;Database=test;Username=postgres;Password=yourpass;]]></default>
</ConnectionStrings>
```

**MySQL:**
```xml
<ConnectionStrings>
  <default provider="MySqlConnector"><![CDATA[Server=localhost;Port=3306;Database=test;User=root;Password=yourpass;SslMode=None;]]></default>
</ConnectionStrings>
```

**SQLite:**
```xml
<ConnectionStrings>
  <default provider="Microsoft.Data.Sqlite"><![CDATA[Data Source=test.db;]]></default>
</ConnectionStrings>
```

> **Tip**: For SQL Server and PostgreSQL, the solution auto-detects the provider from the connection string — you don't need the `provider` attribute. For other databases, explicitly specifying `provider` is recommended. See [Multi-Database Reference](../topics/13-databases.md) for details.

## You're Ready!

Your environment is set up:
- ✅ Database server running
- ✅ `test` database created with `contacts` table
- ✅ Connection string configured

---

### What You Learned

- How DBToRestAPI turns SQL queries into REST endpoints
- The project structure and key configuration files
- How to set up the test database and configure a connection string

---

**Next:** [Your First API Endpoint →](01-hello-world.md)

**[Back to Tutorial Index](index.md)**
