# Overview & Quick Start

## What is DbToRestAPI?

DbToRestAPI is a no-code solution that converts SQL queries into RESTful APIs. Write SQL, get endpoints — no ORM, no code generation, no proprietary query languages.

### Philosophy

- **SQL-First**: Your SQL runs as-is. No abstraction layer, no translation.
- **Configuration-Driven**: Define endpoints in XML files. Hot-reload on changes.
- **Database Agnostic**: SQL Server, PostgreSQL, MySQL, SQLite, Oracle, DB2
- **Production-Ready**: Authentication, caching, file handling, CORS built-in

### Use Cases

- Public APIs for mobile/web apps
- B2B APIs with API key authentication  
- Full-stack apps with JWT/OIDC (Azure B2C, Google, Auth0)
- Internal microservices
- Legacy database modernization

## Quick Start

### Prerequisites

- .NET 10+ runtime
- A database (SQL Server, PostgreSQL, MySQL, SQLite, Oracle, or DB2)

### Step 1: Create Test Database

```sql
CREATE TABLE [dbo].[contacts] (
    [id]     UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
    [name]   NVARCHAR(500),
    [phone]  NVARCHAR(100),
    [active] BIT DEFAULT 1
);
```

### Step 2: Clone Repository

```bash
git clone https://github.com/H7O/DBToRestAPI.git
cd DBToRestAPI
```

### Step 3: Configure Connection

Edit `/config/settings.xml`:

```xml
<settings>
  <ConnectionStrings>
    <default>Server=localhost;Database=test;Trusted_Connection=True;TrustServerCertificate=True;</default>
  </ConnectionStrings>
</settings>
```

### Step 4: Run

```bash
dotnet run --project DBToRestAPI
```

### Step 5: Test

```bash
curl -X POST https://localhost:7054/hello_world \
  -H "Content-Type: application/json" \
  -d '{"name": "John"}'
```

Response:
```json
{"message_from_db": "hello John! Time now is 2025-01-24 10:30:00.123"}
```

## How It Works

### The sql.xml File

All endpoints are defined in `/config/sql.xml`. Each XML node becomes an endpoint:

```xml
<hello_world>
  <query><![CDATA[
    DECLARE @name NVARCHAR(500) = {{name}};
    
    IF @name IS NULL OR LTRIM(RTRIM(@name)) = ''
      SET @name = 'world';
    
    SELECT 'hello ' + @name + '!' AS message_from_db;
  ]]></query>
</hello_world>
```

- **Node name** (`hello_world`) → Default route (`/hello_world`)
- **`{{name}}`** → Parameter from request (body, query string, or route)
- **`<query>`** → SQL executed against your database

### Parameter Safety

Parameters use SQL Server's parameterization — **SQL injection protected by default**. Powered by [Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common).

## Your First CRUD API

### Create (POST)

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  
  <query><![CDATA[
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    
    INSERT INTO [contacts] (name, phone)
    OUTPUT inserted.id, inserted.name, inserted.phone, inserted.active
    VALUES (@name, @phone);
  ]]></query>
</create_contact>
```

### Read (GET)

```xml
<list_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  
  <query><![CDATA[
    SELECT id, name, phone, active FROM [contacts] ORDER BY name;
  ]]></query>
</list_contacts>
```

### Update (PUT)

```xml
<update_contact>
  <route>contacts/{{id}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id,name,phone</mandatory_parameters>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    
    UPDATE [contacts] SET name = @name, phone = @phone
    OUTPUT inserted.*
    WHERE id = @id;
  ]]></query>
</update_contact>
```

### Delete (DELETE)

```xml
<delete_contact>
  <route>contacts/{{id}}</route>
  <verb>DELETE</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <success_status_code>204</success_status_code>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DELETE FROM [contacts] WHERE id = @id;
  ]]></query>
</delete_contact>
```

## Error Handling

Return HTTP errors from SQL using error codes 50000-51000:

```sql
-- 404 Not Found
IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id)
BEGIN
  THROW 50404, 'Contact not found', 1;
  RETURN;
END

-- 409 Conflict
IF EXISTS (SELECT 1 FROM [contacts] WHERE email = @email)
BEGIN
  THROW 50409, 'Email already exists', 1;
  RETURN;
END

-- 400 Bad Request
IF @name IS NULL
BEGIN
  THROW 50400, 'Name is required', 1;
  RETURN;
END
```

Mapping: `50XXX` → HTTP `XXX`

## Debugging

Enable debug mode to see SQL errors:

1. Set in `settings.xml`:
   ```xml
   <debug_mode_header_value>my-secret-debug-key</debug_mode_header_value>
   ```

2. Send header with request:
   ```bash
   curl -H "debug-mode: my-secret-debug-key" ...
   ```

## Project Structure

```
DBToRestAPI/
├── config/
│   ├── settings.xml          # Connection strings, global config
│   ├── sql.xml               # API endpoint definitions
│   ├── api_keys.xml          # API key collections
│   ├── api_gateway.xml       # Proxy routes
│   ├── file_management.xml   # File stores
│   └── auth_providers.xml    # OIDC providers
└── DBToRestAPI/              # Source code
```

## Next Steps

- [Configuration](02-configuration.md) - Deep dive into settings
- [CRUD Operations](03-crud-operations.md) - Complete patterns
- [Parameters](04-parameters.md) - Parameter injection details
- [API Keys](06-api-keys.md) - Protecting endpoints
- [Authentication](12-authentication.md) - JWT/OIDC setup
