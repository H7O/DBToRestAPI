# Configuration

This document covers the configuration files that control DbToRestAPI behavior.

## Configuration Files Overview

| File | Purpose |
|------|---------|
| `/config/settings.xml` | Connection strings, global settings, debug mode |
| `/config/sql.xml` | API endpoint definitions |
| `/config/api_keys.xml` | API key collections |
| `/config/api_gateway.xml` | Proxy route configurations |
| `/config/file_management.xml` | File store definitions |
| `/config/auth_providers.xml` | OIDC provider configurations |

All configuration files support hot-reload — changes apply without restart.

## settings.xml

### Connection Strings

```xml
<settings>
  <ConnectionStrings>
    <!-- Default connection (auto-detected provider) -->
    <default><![CDATA[Server=.\SQLEXPRESS;Database=test;Integrated Security=True;TrustServerCertificate=True;]]></default>
    
    <!-- Explicit provider specification -->
    <postgres provider="Npgsql"><![CDATA[Host=localhost;Database=mydb;Username=user;Password=pass;]]></postgres>
    
    <!-- Multiple databases -->
    <analytics provider="Npgsql"><![CDATA[Host=analytics.example.com;Database=analytics;...]]></analytics>
    <legacy provider="Oracle.ManagedDataAccess.Core"><![CDATA[Data Source=legacy:1521/ORCL;...]]></legacy>
  </ConnectionStrings>
</settings>
```

### Supported Providers

| Database | Provider Attribute | Auto-Detected |
|----------|-------------------|---------------|
| SQL Server | `Microsoft.Data.SqlClient` | ✅ Yes |
| PostgreSQL | `Npgsql` | ✅ Yes |
| MySQL/MariaDB | `MySqlConnector` | ✅ Yes |
| SQLite | `Microsoft.Data.Sqlite` | ✅ Yes |
| Oracle | `Oracle.ManagedDataAccess.Core` | ✅ Yes |
| IBM DB2 | `Net.IBM.Data.Db2` | ✅ Yes |
| ODBC | `System.Data.Odbc` | ✅ Yes |
| OleDb | `System.Data.OleDb` | ✅ Yes |

> **ODBC & OleDb**: These providers natively use positional `?` parameters. DBToRestAPI transparently converts your `{{named}}` parameters into correctly ordered positional parameters — same friendly syntax for all databases.

### Debug Mode

```xml
<settings>
  <!-- Value required in debug-mode header to see SQL errors -->
  <debug_mode_header_value>my-secret-debug-key</debug_mode_header_value>
  
  <!-- Custom generic error message (default: "An error occurred...") -->
  <generic_error_message>An error occurred while processing your request.</generic_error_message>
</settings>
```

### Global CORS

```xml
<settings>
  <cors>
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
    <max_age>86400</max_age>
  </cors>
</settings>
```

### Settings Encryption

```xml
<settings>
  <settings_encryption>
    <!-- Cross-platform: specify key directory -->
    <data_protection_key_path>./keys/</data_protection_key_path>
    
    <!-- What to encrypt -->
    <sections_to_encrypt>
      <section>ConnectionStrings</section>
      <section>authorize:providers:azure_b2c:client_secret</section>
    </sections_to_encrypt>
  </settings_encryption>
</settings>
```

On first run, unencrypted values become `encrypted:CfDJ8NhY2kB...`

## sql.xml

### Basic Endpoint Structure

```xml
<settings>
  <!-- Each child node is an endpoint -->
  
  <endpoint_name>
    <!-- Optional: Custom route (default: node name) -->
    <route>custom/path/{{id}}</route>
    
    <!-- Optional: HTTP method filter -->
    <verb>GET</verb>
    
    <!-- Optional: Restrict to specific hostname -->
    <host>www.example.com</host>
    
    <!-- Optional: Required parameters -->
    <mandatory_parameters>id,name</mandatory_parameters>
    
    <!-- Optional: Success HTTP code (default: 200) -->
    <success_status_code>201</success_status_code>
    
    <!-- Optional: Use different database -->
    <connection_string_name>analytics</connection_string_name>
    
    <!-- Required: SQL query -->
    <query><![CDATA[
      DECLARE @id UNIQUEIDENTIFIER = {{id}};
      SELECT * FROM table WHERE id = @id;
    ]]></query>
    
    <!-- Optional: Count for pagination -->
    <count_query><![CDATA[
      SELECT COUNT(*) FROM table;
    ]]></count_query>
  </endpoint_name>
  
</settings>
```

### All Endpoint Tags

| Tag | Required | Default | Description |
|-----|----------|---------|-------------|
| `<route>` | No | Node name | URL path with `{{param}}` placeholders |
| `<verb>` | No | Any | `GET`, `POST`, `PUT`, `DELETE`, or comma-separated |
| `<host>` | No | Any host | Restrict to hostname. Exact: `www.example.com`, wildcard: `*.example.com`. Port is ignored. |
| `<mandatory_parameters>` | No | None | Comma-separated required params |
| `<success_status_code>` | No | `200` | HTTP status on success |
| `<connection_string_name>` | No | `default` | Which connection string to use |
| `<query>` | **Yes** | — | SQL in `<![CDATA[...]]>` |
| `<count_query>` | No | None | Count query for pagination |
| `<response_structure>` | No | `auto` | `single`, `array`, `auto`, or `file` |
| `<api_keys_collections>` | No | None | Required API key collections |
| `<authorize>` | No | None | JWT/OIDC configuration |
| `<cors>` | No | Global | Endpoint-specific CORS |
| `<cache>` | No | None | Caching configuration |
| `<file_management>` | No | None | File upload/download config |

### Host-Based Routing

The `<host>` tag lets you restrict an endpoint to a specific hostname — useful for multi-tenant APIs or serving different content per domain.

**Exact match:**
```xml
<public_home>
  <host>www.example.com</host>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome to Example' AS message; ]]></query>
</public_home>
```

**Wildcard match** (`*.example.com` matches any subdomain):
```xml
<tenant_home>
  <host>*.example.com</host>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome, tenant' AS message; ]]></query>
</tenant_home>
```

**No host** (matches any hostname — the default):
```xml
<fallback_home>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome' AS message; ]]></query>
</fallback_home>
```

When multiple endpoints share the same route and verb, the engine picks the most specific host match:

| Priority | Pattern | Example |
|----------|---------|---------|
| Highest | Exact host | `www.example.com` |
| Medium | Wildcard | `*.example.com` |
| Lowest | No `<host>` tag | Matches everything |

Port numbers in the request are ignored during matching.

### Query Timeout

```xml
<query db_command_timeout="120"><![CDATA[
  -- Long-running query with 120 second timeout
  SELECT * FROM large_table;
]]></query>
```

## api_keys.xml

```xml
<settings>
  <api_keys_collections>
    
    <vendors>
      <key>vendor-key-abc123</key>
      <key>vendor-key-def456</key>
    </vendors>
    
    <internal>
      <key>internal-service-key</key>
    </internal>
    
  </api_keys_collections>
</settings>
```

Reference in sql.xml:
```xml
<api_keys_collections>vendors,internal</api_keys_collections>
```

## api_gateway.xml

```xml
<settings>
  <routes>
    
    <cat_facts>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </cat_facts>
    
    <external_api>
      <route>external/*</route>
      <url>https://api.example.com/</url>
      <api_keys_collections>vendors</api_keys_collections>
      <cache>
        <memory>
          <duration_in_milliseconds>60000</duration_in_milliseconds>
        </memory>
      </cache>
    </external_api>
    
  </routes>
</settings>
```

## file_management.xml

```xml
<settings>
  <file_management>
    <!-- Path structure for stored files -->
    <relative_file_path_structure>{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}</relative_file_path_structure>
    
    <!-- Global restrictions -->
    <permitted_file_extensions>.pdf,.docx,.png,.jpg</permitted_file_extensions>
    <max_file_size_in_bytes>10485760</max_file_size_in_bytes>
    <max_number_of_files>5</max_number_of_files>
    
    <!-- Local stores -->
    <local_file_store>
      <primary>
        <base_path><![CDATA[c:\uploads\]]></base_path>
      </primary>
    </local_file_store>
    
    <!-- SFTP stores -->
    <sftp_file_store>
      <remote>
        <host>sftp.example.com</host>
        <port>22</port>
        <username>user</username>
        <password>pass</password>
        <base_path>/uploads/</base_path>
      </remote>
    </sftp_file_store>
  </file_management>
</settings>
```

## auth_providers.xml

```xml
<settings>
  <authorize>
    <providers>
      
      <azure_b2c>
        <authority>https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin</authority>
        <audience>your-api-client-id</audience>
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
        <validate_lifetime>true</validate_lifetime>
        <clock_skew_seconds>300</clock_skew_seconds>
        <userinfo_fallback_claims>email,name</userinfo_fallback_claims>
      </azure_b2c>
      
      <google>
        <authority>https://accounts.google.com</authority>
        <audience>your-client-id.apps.googleusercontent.com</audience>
      </google>
      
    </providers>
  </authorize>
</settings>
```

## Environment Variables

Any XML or JSON setting can be overridden by an environment variable.  Environment
variables are loaded **last** in the configuration chain, so they take precedence
over everything in config files.

The full override chain (last wins):

```
appsettings.json → appsettings.{Environment}.json → settings.xml → additional XML files → environment variables
```

### Mapping convention

Use `__` (double underscore) as the hierarchy separator:

| Config path | Environment variable |
|---|---|
| `ConnectionStrings:default` | `ConnectionStrings__default` |
| `debug_mode_header_value` | `debug_mode_header_value` |
| `settings_encryption:data_protection_key_path` | `settings_encryption__data_protection_key_path` |
| `Kestrel:Endpoints:Http:Url` | `Kestrel__Endpoints__Http__Url` |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` |

> On Windows and in the Azure App Service portal, both `:` and `__` work as
> separators (e.g., `ConnectionStrings:default` or `ConnectionStrings__default`).
> On Linux, use `__` because `:` is not valid in environment variable names on
> some shells.

### Examples

```bash
# Override the default connection string
ConnectionStrings__default="Server=prod;Database=app;..."

# Encryption key path (also read directly via Environment.GetEnvironmentVariable)
DATA_PROTECTION_KEY_PATH=./keys/

# Set the ASP.NET Core environment
ASPNETCORE_ENVIRONMENT=Production

# Override logging level
Logging__LogLevel__Default=Warning
```

### Cloud deployment platforms

This is the standard mechanism for per-environment configuration on platforms like
Azure App Service, Docker, AWS ECS, and Kubernetes.  For example, on Azure App
Service you can set `ConnectionStrings__default` in the portal under
*Configuration → Application settings* and remove the connection string from
`settings.xml` entirely.

See the [Production & Deployment](../tutorial/21-production.md) tutorial for more
deployment-specific examples.

## Related Topics

- [Multi-Database](13-databases.md) - Per-endpoint database connections
- [API Keys](06-api-keys.md) - Key collection setup
- [Authentication](12-authentication.md) - OIDC provider configuration
- [Encryption](15-encryption.md) - Settings encryption details
