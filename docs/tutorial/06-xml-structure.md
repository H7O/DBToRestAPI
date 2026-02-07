# XML Configuration Structure

So far we've been adding endpoints and tags to `sql.xml` without stepping back to look at the full picture. In this topic, we'll explore the complete XML configuration system — how files relate to each other, what every tag does, and how configuration features like hot-reload, encryption, and multi-file organization work.

## The Configuration File Map

```
config/
├── settings.xml          ← The hub: connection strings, global settings, file references
├── sql.xml               ← API endpoint definitions
├── api_keys.xml          ← API key collections
├── api_gateway.xml       ← Proxy/gateway route definitions
├── file_management.xml   ← File upload/download store configurations
├── auth_providers.xml    ← OIDC/JWT authentication providers
└── regex.xml             ← Custom parameter regex patterns
```

**`settings.xml` is the hub** — it references all other config files through the `<additional_configurations>` block:

```xml
<settings>
  <ConnectionStrings>
    <default><![CDATA[...]]></default>
  </ConnectionStrings>

  <additional_configurations>
    <path>config/sql.xml</path>
    <path>config/api_gateway.xml</path>
    <path>config/api_keys.xml</path>
    <path>config/file_management.xml</path>
    <path>config/regex.xml</path>
    <path>config/auth_providers.xml</path>
  </additional_configurations>
</settings>
```

The application reads `settings.xml` first, then loads each file listed in `<additional_configurations>`. All loaded configurations merge into a single in-memory settings tree.

## Hot-Reload: Edit Without Restarting

All configuration files are monitored for changes. When you save a file:

1. The application detects the change
2. It reloads the affected configuration
3. New requests use the updated configuration

**No restart required.** This applies to all config files — `sql.xml`, `api_keys.xml`, `api_gateway.xml`, etc.

> **Exception**: Adding or removing `<path>` entries in `<additional_configurations>` may require a restart depending on the `<restart_on_path_changes>` setting. Content changes within existing files always reload automatically.

## The Complete sql.xml Tag Reference

Here's every tag you can use inside an endpoint definition:

```xml
<settings>
  <queries>  <!-- Some config files use <queries>, the endpoing definitions work the same regardless -->
    <endpoint_name>

      <!-- Routing -->
      <route>custom/path/{{id}}</route>
      <verb>GET</verb>

      <!-- Parameters -->
      <mandatory_parameters>id,name</mandatory_parameters>

      <!-- Database -->
      <connection_string_name>analytics</connection_string_name>

      <!-- Query -->
      <query><![CDATA[ SELECT * FROM table WHERE id = {{id}}; ]]></query>
      <count_query><![CDATA[ SELECT COUNT(*) FROM table; ]]></count_query>

      <!-- Response -->
      <response_structure>auto</response_structure>
      <success_status_code>200</success_status_code>

      <!-- Security -->
      <api_keys_collections>mobile_apps,web_apps</api_keys_collections>
      <authorize>
        <provider>my_auth_provider</provider>
      </authorize>

      <!-- Caching -->
      <cache>
        <duration_in_seconds>300</duration_in_seconds>
      </cache>

      <!-- CORS (endpoint-level override) -->
      <cors>
        <pattern><![CDATA[^(localhost|.*\.mysite\.com)$]]></pattern>
        <fallback_origin>https://www.mysite.com</fallback_origin>
      </cors>

      <!-- File Management -->
      <file_management>
        <!-- Covered in the file uploads topic -->
      </file_management>

    </endpoint_name>
  </queries>
</settings>
```

### Tag-by-Tag Reference

| Tag | Required | Default | Description |
|-----|----------|---------|-------------|
| `<route>` | No | XML tag name | URL path. Supports `{{param}}` placeholders. |
| `<verb>` | No | Any verb | HTTP method: `GET`, `POST`, `PUT`, `DELETE`, `PATCH` |
| `<mandatory_parameters>` | No | None | Comma-separated params. Missing → HTTP 400. |
| `<connection_string_name>` | No | `default` | Which connection string from settings.xml. |
| `<query>` | **Yes** | — | SQL wrapped in `<![CDATA[...]]>`. |
| `<count_query>` | No | None | Pagination count query. Wraps response in `{ count, data }`. |
| `<response_structure>` | No | `auto` | `auto`, `single`, `array`, or `file`. |
| `<success_status_code>` | No | `200` | HTTP status for successful responses. |
| `<api_keys_collections>` | No | None | Comma-separated API key collection names from api_keys.xml. |
| `<authorize>` | No | None | JWT/OIDC auth configuration. |
| `<cache>` | No | None | Response caching settings. |
| `<cors>` | No | Global setting | Per-endpoint CORS override. |
| `<file_management>` | No | None | File upload/download configuration. |

## Connection Strings in settings.xml

You can define multiple connection strings for different databases:

```xml
<ConnectionStrings>
  <!-- Default: auto-detected SQL Server -->
  <default><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;Integrated Security=True;TrustServerCertificate=True;]]></default>

  <!-- Named connection: PostgreSQL -->
  <analytics provider="Npgsql"><![CDATA[Host=analytics.example.com;Database=analytics;Username=user;Password=pass;]]></analytics>

  <!-- Named connection: SQLite -->
  <cache_db provider="Microsoft.Data.Sqlite"><![CDATA[Data Source=cache.db;]]></cache_db>
</ConnectionStrings>
```

Use the `<connection_string_name>` tag in your endpoint to select a specific connection:

```xml
<analytics_report>
  <connection_string_name>analytics</connection_string_name>
  <query><![CDATA[ SELECT * FROM daily_report; ]]></query>
</analytics_report>
```

If omitted, the `default` connection string is used. The supported database providers are:

| Database | `provider` Attribute | Auto-Detected? |
|----------|---------------------|----------------|
| SQL Server | `Microsoft.Data.SqlClient` | Yes |
| PostgreSQL | `Npgsql` | Yes |
| MySQL/MariaDB | `MySqlConnector` | Yes |
| SQLite | `Microsoft.Data.Sqlite` | Yes |
| Oracle | `Oracle.ManagedDataAccess.Core` | Yes |
| IBM DB2 | `Net.IBM.Data.Db2` | Yes |

## Global Settings in settings.xml

### Debug Mode

In production, SQL error details are hidden. Debug mode lets you see them:

```xml
<debug_mode_header_value>my-secret-key</debug_mode_header_value>
```

Send this header to see SQL errors in the response:
```bash
curl -H "debug-mode: my-secret-key" http://localhost:5165/broken_endpoint
```

Without this header, errors return a generic message instead:
```xml
<generic_error_message><![CDATA[An error occurred while processing your request.]]></generic_error_message>
```

### Database Timeout

Set a global timeout (in seconds) for all database queries:

```xml
<db_command_timeout>30</db_command_timeout>
```

### Max Payload Size

```xml
<max_payload_size_in_bytes>367001600</max_payload_size_in_bytes>
```

### Global CORS

Define a default CORS policy for all endpoints:

```xml
<cors>
  <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
  <fallback_origin>https://www.example.com</fallback_origin>
  <max_age>86400</max_age>
</cors>
```

- `<pattern>` — Regex checked against the request's `Origin` header. If it matches, the origin is echoed back in `Access-Control-Allow-Origin`.
- `<fallback_origin>` — Used when the origin doesn't match the pattern.
- Endpoint-level `<cors>` overrides this global setting.

## Configuration Encryption

Sensitive values (connection strings, API secrets) can be encrypted at rest:

```xml
<settings_encryption>
  <encryption_prefix>encrypted:</encryption_prefix>
  <data_protection_key_path>./keys/</data_protection_key_path>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
    <section>api_keys_collections:external_vendors</section>
  </sections_to_encrypt>
</settings_encryption>
```

On first run, the application:
1. Reads the unencrypted values
2. Encrypts them using .NET Data Protection API
3. Writes the encrypted values back to the file (prefixed with `encrypted:`)
4. Keeps decrypted values in memory for runtime use

After encryption, your connection string in the file looks like:
```xml
<default>encrypted:CfDJ8NhY2kBx7vF...</default>
```

> **Note**: Encrypted values are machine-specific. When deploying to a new server, use the plain-text values and let the application encrypt them on first run.

## Splitting Endpoints Across Multiple Files

You're not limited to a single `sql.xml`. You can organize endpoints into multiple files:

```xml
<!-- settings.xml -->
<additional_configurations>
  <path>config/contacts_api.xml</path>
  <path>config/products_api.xml</path>
  <path>config/reports_api.xml</path>
  <path>config/api_keys.xml</path>
</additional_configurations>
```

Each file follows the same structure. This is useful for large projects with many endpoints — organize by domain, team, or feature.

## The regex.xml File

This file controls how parameters are detected in SQL queries. The defaults work for most use cases:

| Marker | Default Pattern | Example |
|--------|----------------|---------|
| `{{param}}` | Generic — resolved from all sources | `{{name}}` |
| `{j{param}}` | JSON payload only | `{j{name}}` |
| `{qs{param}}` | Query string only | `{qs{search}}` |
| `{r{param}}` | Route only | `{r{id}}` |
| `{h{param}}` | HTTP headers only | `{h{Authorization}}` |
| `{f{param}}` | Form data only | `{f{username}}` |
| `{auth{claim}}` | JWT claims | `{auth{email}}` |
| `{http{...}http}` | Embedded HTTP calls | See advanced topics |

The **specific decorators** (`{j{}`, `{qs{}`, etc.) are useful when you need to force a parameter to come from a specific source, avoiding conflicts when the same name exists in multiple sources.

You can customize these patterns globally in `regex.xml` or per-endpoint by adding the pattern as an attribute on the `<query>` node.

---

### What You Learned

- The configuration file hierarchy and how `settings.xml` references other files
- Hot-reload behavior for all configuration files
- The complete list of endpoint tags in `sql.xml`
- How connection strings work with multiple databases and providers
- Global settings: debug mode, CORS, timeouts
- Configuration encryption for sensitive values
- How to split endpoints across multiple XML files
- Parameter source-specific decorators (`{j{}`, `{qs{}`, `{r{}`, etc.)

---

**Next:** [Input Validation with Regex →](07-regex-validation.md)

**[Back to Tutorial Index](index.md)**
