# Settings Variables (vars)

Access configuration values from your settings files directly in SQL queries using the `{s{}}` or `{settings{}}` syntax.

## Overview

Settings variables let you reference values from a dedicated `<vars>` section in your configuration files. This is especially useful for:

- Storing API keys for embedded HTTP calls (`{http{...}http}`) without hardcoding them in queries
- Centralizing environment-specific values (URLs, thresholds, feature flags)
- Leveraging the built-in [encryption](15-encryption.md) to keep secrets out of plain text

## Configuration

Add a `<vars>` section to any of your settings files (e.g., `settings.xml`):

```xml
<settings>
  <!-- ... connection strings, other settings ... -->

  <vars>
    <partner_api_key>sk-abc123secret</partner_api_key>
    <partner_api_url>https://api.partner.example.com/v2</partner_api_url>
    <notification_email>alerts@example.com</notification_email>
    <max_retry_count>3</max_retry_count>
  </vars>
</settings>
```

All values are loaded once at startup and refreshed automatically via hot-reload when the file changes.

## Syntax

Reference settings variables in your queries with either syntax:

| Syntax | Example | Description |
|--------|---------|-------------|
| `{s{name}}` | `{s{partner_api_key}}` | Short form (recommended for experienced users) |
| `{settings{name}}` | `{settings{partner_api_key}}` | Long form (self-documenting) |

Both forms are interchangeable — use whichever you prefer. They can be mixed within the same query.

## Basic Usage

```xml
<get_enriched_data>
  <route>data/enriched</route>
  <verb>GET</verb>
  <query><![CDATA[
    DECLARE @api_url NVARCHAR(500) = {s{partner_api_url}};
    DECLARE @max_retries INT = {s{max_retry_count}};

    SELECT @api_url AS configured_url, @max_retries AS max_retries;
  ]]></query>
</get_enriched_data>
```

## Primary Use Case: Secure Embedded HTTP Calls

The main motivation for settings variables is passing secrets to `{http{...}http}` calls without hardcoding them:

**Before** (API key exposed in query):
```sql
DECLARE @result NVARCHAR(MAX) = {http{
  {
    "url": "https://api.partner.com/data",
    "headers": {
      "X-API-Key": "sk-abc123secret"
    }
  }
}http};
```

**After** (API key in encrypted config):
```sql
DECLARE @result NVARCHAR(MAX) = {http{
  {
    "url": "{s{partner_api_url}}/data",
    "headers": {
      "X-API-Key": "{s{partner_api_key}}"
    }
  }
}http};
```

The `{s{...}}` references are resolved before the HTTP call executes — the engine replaces them with the actual values from config during parameter processing.

## Encrypting Settings Variables

Combine with [settings encryption](15-encryption.md) to protect sensitive values at rest:

```xml
<settings>
  <vars>
    <partner_api_key>sk-abc123secret</partner_api_key>
  </vars>

  <settings_encryption>
    <sections_to_encrypt>
      <section>vars:partner_api_key</section>
      <!-- Or encrypt all vars at once: -->
      <!-- <section>vars</section> -->
    </sections_to_encrypt>
  </settings_encryption>
</settings>
```

On first startup, the value is encrypted in-place:
```xml
<vars>
  <partner_api_key>encrypted:CfDJ8NhY2kB...long-base64-string...</partner_api_key>
</vars>
```

Your queries continue using `{s{partner_api_key}}` unchanged — decryption is transparent at runtime.

## Custom Regex Pattern

The default pattern matches both `{s{param}}` and `{settings{param}}`. To customize, set `settings_variables_pattern`:

**Per-endpoint:**
```xml
<my_endpoint>
  <settings_variables_pattern>(?&lt;open_marker&gt;\{s\{|\{settings\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})</settings_variables_pattern>
  <query>...</query>
</my_endpoint>
```

**Globally** (in `regex.xml` or `settings.xml`):
```xml
<regex>
  <settings_variables_pattern>...</settings_variables_pattern>
</regex>
```

## Parameter Names

Variable names are **case-insensitive** and correspond to the XML element names inside `<vars>`:

```xml
<vars>
  <My_Api_Key>secret</My_Api_Key>
</vars>
```

All of these resolve to the same value:
- `{s{my_api_key}}`
- `{s{My_Api_Key}}`
- `{s{MY_API_KEY}}`
- `{settings{my_api_key}}`

## Behavior When Variable Not Found

If a query references a settings variable that doesn't exist in `<vars>`, the parameter is set to `NULL` (same behavior as any other unresolved parameter). Handle it with standard SQL:

```sql
DECLARE @api_key NVARCHAR(500) = {s{partner_api_key}};

IF @api_key IS NULL
  THROW 50500, 'Missing configuration: partner_api_key', 1;
```

## Security Note

Only values explicitly placed in the `<vars>` section are accessible via `{s{}}` / `{settings{}}`. Connection strings, auth provider secrets, encryption keys, and other configuration sections are **not** exposed — this is by design to prevent accidental credential leakage through query results.

## Related Topics

- [Embedded HTTP Calls](17-embedded-http-calls.md) — Using `{s{}}` to pass secrets to HTTP calls
- [Settings Encryption](15-encryption.md) — Encrypting sensitive vars values
- [Parameters](04-parameters.md) — All parameter types (`{{}}`, `{auth{}}`, `{h{}}`, etc.)
- [Configuration](02-configuration.md) — Configuration file structure
