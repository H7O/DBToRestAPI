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

> **Exceptions**: Kestrel and TLS settings, `data_protection_key_path`, and adding or removing `<path>` entries in `<additional_configurations>` need a restart. With `<restart_on_path_changes>true</restart_on_path_changes>`, the application stops by itself when the list changes, so a host that restarts it loads the new list. A file saved with invalid XML is ignored, and the previous version kept.

> **Running from a clone?** `dotnet run` serves copies of the files from `DBToRestAPI/bin/`, made when it starts. After editing a file under `DBToRestAPI/config/`, stop it and start it again. See [Configuration](../topics/02-configuration.md#where-the-files-are).

## The Complete sql.xml Tag Reference

Here's every tag you can use inside an endpoint definition:

```xml
<settings>
  <queries>  <!-- Every endpoint goes inside this one <queries> element -->
    <endpoint_name>

      <!-- Routing -->
      <route>custom/path/{{id}}</route>
      <verb>GET</verb>
      <host>www.example.com</host>

      <!-- Parameters -->
      <mandatory_parameters>id,name</mandatory_parameters>

      <!-- Database -->
      <connection_string_name>analytics</connection_string_name>

      <!-- Query -->
      <query><![CDATA[ SELECT * FROM table WHERE id = {{id}}; ]]></query>
      <count_query><![CDATA[ SELECT COUNT(*) FROM table; ]]></count_query>

      <!-- Response -->
      <success_status_code>200</success_status_code>

      <!-- Security -->
      <api_keys_collections>mobile_apps,web_apps</api_keys_collections>
      <authorize>
        <provider>my_auth_provider</provider>
      </authorize>

      <!-- Caching (milliseconds, under <memory>) -->
      <cache>
        <memory>
          <duration_in_milliseconds>300000</duration_in_milliseconds>
        </memory>
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
| `<verb>` | No | Any verb | HTTP method: `GET`, `POST`, `PUT`, `PATCH` or `DELETE`, or several separated by commas. A `HEAD` request gets the `GET` route's answer without its body. |
| `<host>` | No | Any host | Restrict endpoint to a hostname. Exact (`www.example.com`) or wildcard (`*.example.com`). Port is ignored. |
| `<mandatory_parameters>` | No | None | Comma-separated params. Missing → HTTP 400. |
| `<connection_string_name>` | No | `default` | Which connection string from settings.xml. |
| `<query>` | **Yes** | — | SQL wrapped in `<![CDATA[...]]>`. |
| `<count_query>` | No | None | Pagination count query. Wraps response in `{ success, count, data }`. |
| `<response_structure>` | No | Follows the row count | `array` (always a list) or `file` (a download). Leave it out for one record. |
| `<success_status_code>` | No | `200` | HTTP status for successful responses. |
| `<api_keys_collections>` | No | None | Comma-separated API key collection names from api_keys.xml. |
| `<authorize>` | No | None | JWT/OIDC auth configuration. |
| `<cache>` | No | None | Response caching settings. |
| `<cors>` | No | Global setting | Per-endpoint CORS override. |
| `<file_management>` | No | None | File upload/download configuration. |
| `<openapi>` | No | None | Per-endpoint OpenAPI enrichment: `<enabled>`, `<summary>`, `<description>`, `<tags>`, `<response_schema>`. See [OpenAPI topic](../topics/20-openapi.md). |
| `<rate_limit>` | No | Global | Per-endpoint request limit: `<max_requests>`, `<window_seconds>`, `<per>`, `<enabled>`, `<message>`. Falls back tag by tag to the global block in settings.xml. See [Rate Limiting topic](../topics/18-rate-limiting.md). |

> **Host priority:** When multiple endpoints share the same route and verb,
> the engine picks the most specific host: exact (`www.example.com`) beats
> wildcard (`*.example.com`), which beats no `<host>` tag. This lets you
> serve different responses per domain from a single API instance.

## Connection Strings in settings.xml

You can define multiple connection strings for different databases:

```xml
<ConnectionStrings>
  <!-- Default: SQL Server -->
  <default provider="Microsoft.Data.SqlClient"><![CDATA[Data Source=.\SQLEXPRESS;Initial Catalog=test;Integrated Security=True;TrustServerCertificate=True;]]></default>

  <!-- Named connection: PostgreSQL -->
  <analytics provider="Npgsql"><![CDATA[Host=analytics.example.com;Database=analytics;Username=user;Password=pass;]]></analytics>

  <!-- Named connection: SQLite -->
  <cache_db provider="Microsoft.Data.Sqlite"><![CDATA[Data Source=cache.db;]]></cache_db>
</ConnectionStrings>
```

Use the `<connection_string_name>` tag in your endpoint to select a specific connection:

```xml
<analytics_report>
  <response_structure>array</response_structure>
  <connection_string_name>analytics</connection_string_name>
  <query><![CDATA[ SELECT * FROM daily_report; ]]></query>
</analytics_report>
```

If omitted, the `default` connection string is used. The supported database providers are:

| Database | `provider` Attribute |
|----------|---------------------|
| SQL Server | `Microsoft.Data.SqlClient` |
| PostgreSQL | `Npgsql` |
| MySQL/MariaDB | `MySqlConnector` |
| SQLite | `Microsoft.Data.Sqlite` |
| Oracle | `Oracle.ManagedDataAccess.Core` |
| IBM DB2 | `Net.IBM.Data.Db2` |
| ODBC | `System.Data.Odbc` |
| OleDb | `System.Data.OleDb` |

Always set `provider`. Without it, the application guesses from keywords in the connection string, and the guess can't tell every string apart: a SQL Server string with only `Data Source`, `User Id` and `Password` goes to the Oracle driver ([Provider detection](../topics/13-databases.md#provider-detection)).

> **ODBC & OleDb**: These providers natively use positional `?` parameters. DBToRestAPI transparently converts your `{{named}}` parameters into correctly ordered positional parameters — same friendly syntax for all databases.

## Global Settings in settings.xml

### Debug Mode

In production, SQL error details are hidden. Debug mode lets you see them. It is off as shipped: the tag is commented out in `settings.xml`. Uncomment it while you debug:

```xml
<debug_mode_header_value>my-secret-key</debug_mode_header_value>
```

Anyone who sends this value gets your database's error messages and stack traces, so comment the tag out again before deploying, or set it to a long random secret.

Send this header to see SQL errors in the response:
```bash
curl -H "debug-mode: my-secret-key" http://localhost:5000/broken_endpoint
```

Without this header, errors return a generic message instead:
```xml
<generic_error_message><![CDATA[An error occurred while processing your request.]]></generic_error_message>
```

### Database Timeout

Optionally, set a global timeout (in seconds) for all database queries. It overrides any timeout in
the connection strings; without it, each query uses its connection string's timeout, or the
provider's default. The sample `settings.xml` leaves it commented out:

```xml
<db_command_timeout>60</db_command_timeout>
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

- `<pattern>` — Regex checked against the host in the request's `Origin` header, without its scheme or port. Anchor it with `^` and `$`. If it matches, the origin is echoed back in `Access-Control-Allow-Origin`.
- `<fallback_origin>` — Sent when the origin doesn't match the pattern. Without it, the answer is `*`, which lets every page read the response, so set it to your own site. To allow every origin, leave out both tags, or set this one to `*`.
- Endpoint-level `<cors>` overrides this global setting, tag by tag. See [CORS](../topics/11-cors.md), including what changes when an `<authorize>` section exists.

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

The application supports two encryption methods, chosen automatically:

| Priority | Condition | Method |
|----------|-----------|--------|
| 1 | `data_protection_key_path` is configured | ASP.NET Core Data Protection API (cross-platform) |
| 2 | Running on Windows (no key path) | Windows DPAPI |
| 3 | Neither of the above | Encryption disabled (passthrough — values stored as plain text) |

**On Windows**, `<data_protection_key_path>` is optional — if omitted, the application falls back to Windows DPAPI automatically. **On Linux/macOS**, you must configure `<data_protection_key_path>` or encryption will silently be disabled.

The key path can also be set via the `DATA_PROTECTION_KEY_PATH` environment variable.

On first run, the application:
1. Reads the unencrypted values in the configured sections
2. Encrypts them using the resolved encryption method
3. Writes the encrypted values back to the file (prefixed with `encrypted:`)
4. Keeps decrypted values in memory for runtime use

The encryption configuration is **monitored for changes** — if you add a new section to `<sections_to_encrypt>` and save the file, the application picks it up immediately and encrypts the new values without a restart.

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

Each file follows the same structure: its endpoints inside `<settings><queries>`. This is useful for large projects with many endpoints — organize by domain, team, or feature.

One thing to watch: **endpoint names must be unique across all files.** The files merge element by element, so a `<get_contact>` in two files becomes one endpoint mixing both, and nothing is logged.

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

Each source has its own pattern in `regex.xml`, such as `json_variables_pattern` for the JSON body and `query_string_variables_pattern` for the query string. To change one for a single endpoint, add an element with the same name to that endpoint. The endpoint's element wins, then `regex.xml`, then the built-in default. The next topic shows an example.

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
