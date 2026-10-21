---
title: Configuration
summary: Where the configuration files live, how they are loaded, merged and reloaded, the top-level shape of each file, connection strings, environment variables and log levels.
keywords: [settings.xml, sql.xml, api_keys.xml, api_gateway.xml, file_management.xml, auth_providers.xml, regex.xml, queries, ConnectionStrings, provider, additional_configurations, path, restart_on_path_changes, debug_mode_header_value, debug-mode, generic_error_message, success_status_code, db_command_timeout, host, verb, PATCH, HEAD, environment variables, appsettings.json, Logging__LogLevel__Default, hot reload]
---

# Configuration

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](../reference/errors.md).

The engine reads XML files from its `config/` folder: `settings.xml` first, then each file it lists. All of them merge into one tree, which environment variables can override. This page covers where the files are, how they load and reload, and the top-level shape of each one. Each feature's own page has its tags.

## Rules

1. **Put every endpoint inside `<settings><queries>`.** An element directly under `<settings>` is not an endpoint, and a request to its route gets `404`.
2. **Add to the container element a file already has.** Never write a second `<queries>`, `<cors>`, `<local_file_store>` or `<sftp_file_store>` beside the first: repeated siblings are numbered by the configuration reader, and everything inside them stops matching.
3. **Give every endpoint an element name that no other loaded file uses,** and every verb and route pair too. Files merge element by element, so the same name in two files becomes one endpoint mixing both, and nothing is logged.
4. **Set `provider` on every connection string.** It decides the SQL dialect you write. Without it the engine guesses from keywords in the string and gets common strings wrong ([13 databases](13-databases.md#provider-detection)).
5. **In a clone, restart `dotnet run` after editing a file under `DBToRestAPI/config/`.** It serves copies of the files from the build output, and copies them again only when it starts.

## Where the files are

- **In a release:** `config/` next to the executable.
- **In a clone:** `DBToRestAPI/config/`. `dotnet run` serves the copies in `DBToRestAPI/bin/<Configuration>/net10.0/config/`, and the SQLite demo database at `DBToRestAPI/bin/<Configuration>/net10.0/demo.db`. Only the files listed in `DBToRestAPI.csproj` are copied. A new file needs its own entry there, `<None Update="config\NAME.xml"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`, or the engine fails to start once `<additional_configurations>` names it. Putting new endpoints in the existing `sql.xml` avoids this.

| File | What it holds |
|------|---------|
| `config/settings.xml` | Connection strings, global settings and defaults, and the list of the other files |
| `config/sql.xml` | Endpoints, under `<settings><queries>` |
| `config/api_keys.xml` | API key collections ([06](06-api-keys.md)) |
| `config/api_gateway.xml` | Proxy routes, under `<settings><routes>` ([08](08-api-gateway.md)) |
| `config/file_management.xml` | File stores and upload defaults ([09](09-file-uploads.md)) |
| `config/auth_providers.xml` | OIDC providers, under `<settings><authorize><providers>` ([12](12-authentication.md)) |
| `config/regex.xml` | The patterns that find placeholders in a query ([04](04-parameters.md)) |

Every shipped file's root element is `<settings>`. The root's name is dropped in the merge, so a tag directly under the root of any file is a global setting.

## How the files load

1. The engine reads `config/settings.xml` from the executable's folder. Without it, the engine doesn't start.
2. It reads each file listed in `<additional_configurations>`, in order, relative to the executable's folder. A file ending in `.json` is read as JSON. A listed file that doesn't exist stops the engine at start-up, and so does a file with invalid XML.

   ```xml
   <settings>
     <additional_configurations>
       <path>config/sql.xml</path>
       <path>config/api_gateway.xml</path>
       <restart_on_path_changes>false</restart_on_path_changes>
     </additional_configurations>
   </settings>
   ```
3. All the files merge into one tree. Where two files set the same value, the file read later wins. Environment variables and command-line arguments come last ([environment variables](#environment-variables)).
4. Sibling elements with the same name become a numbered list. That is how `<path>`, `<key>` and `<header>` lists work, and why a second container element breaks (rule 2).

## Reloading

Saving a file the engine reads applies the change to the next request, without a restart. These need a restart:

- Kestrel and TLS settings;
- `data_protection_key_path` under `<settings_encryption>`: the engine chooses how it encrypts once, as it starts;
- adding or removing a file under `<additional_configurations>`. The engine logs a warning and keeps the files it started with. With `<restart_on_path_changes>true</restart_on_path_changes>` it stops instead, so that a host which restarts it (IIS, a service manager, Docker with a restart policy) loads the new list.

A file saved with invalid XML is ignored, and the previous version is kept. The engine writes a warning naming the file to the standard error stream, not to the log. A route with `<cache>` keeps serving its cached answer until the entry expires. If a change doesn't take effect, save the file again or restart.

## settings.xml

### Connection strings

```xml
<settings>
  <ConnectionStrings>
    <default provider="Microsoft.Data.SqlClient"><![CDATA[Server=.\SQLEXPRESS;Database=test;Integrated Security=True;TrustServerCertificate=True;]]></default>
    <analytics provider="Npgsql"><![CDATA[Host=analytics.example.com;Database=analytics;Username=user;Password=pass;]]></analytics>
    <legacy provider="Oracle.ManagedDataAccess.Core"><![CDATA[Data Source=legacy:1521/ORCL;User Id=user;Password=pass;]]></legacy>
  </ConnectionStrings>
</settings>
```

An endpoint without `<connection_string_name>` uses `default`. The shipped `default` is the SQLite demo database. The providers, and what each one needs, are in [13 databases](13-databases.md).

### Debug mode and the generic error message

```xml
<settings>
  <!-- <debug_mode_header_value>54321</debug_mode_header_value> -->
  <generic_error_message><![CDATA[An error occurred while processing your request.]]></generic_error_message>
</settings>
```

`debug_mode_header_value` is commented out as shipped, which keeps debug mode off. A request whose `debug-mode` header equals it gets the database's error message and stack trace instead of `generic_error_message`. If you uncomment it to debug, comment it out again before deploying, or set it to a long random secret. `generic_error_message` is the message of a database error you didn't raise yourself ([errors.md](../reference/errors.md)). Without it, the message is `An error occurred while processing your request.`

### Global CORS, rate limits and encryption

`settings.xml` holds one `<cors>` block, the default for every route ([11 CORS](11-cors.md)), and an optional `<rate_limit>` block, the default limit for every endpoint ([18 rate limiting](18-rate-limiting.md)). `<settings_encryption>` names the values the engine encrypts in place on first run ([15 encryption](15-encryption.md)):

```xml
<settings>
  <settings_encryption>
    <data_protection_key_path>./keys/</data_protection_key_path>
    <sections_to_encrypt>
      <section>ConnectionStrings</section>
      <section>authorize:providers:azure_b2c:client_secret</section>
    </sections_to_encrypt>
  </settings_encryption>
</settings>
```

## sql.xml

Each element under `<settings><queries>` is an endpoint. Its element name is its route unless `<route>` sets another. SQL Server:

```xml
<settings>
  <queries>

    <get_contact>
      <route>contacts/{{id}}</route>
      <verb>GET</verb>
      <query><![CDATA[
        DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
        IF NOT EXISTS (SELECT 1 FROM contacts WHERE id = @id)
          THROW 50404, 'Contact not found', 1;
        SELECT id, name, phone FROM contacts WHERE id = @id;
      ]]></query>
    </get_contact>

    <!-- every other endpoint goes here, inside the same <queries> -->

  </queries>
</settings>
```

The tags used most:

| Tag | Default | What it does |
|-----|---------|-------------|
| `<route>` | The element name | URL path, with route values in `{{name}}` |
| `<verb>` | Any method | `GET`, `POST`, `PUT`, `PATCH` or `DELETE`, or several separated by commas. A `HEAD` request gets the answer of the route that takes `GET`, without its body, unless a route takes `HEAD` itself |
| `<host>` | Any host | Only requests to this host name: exact (`www.example.com`) or a wildcard (`*.example.com`). The port is ignored |
| `<query>` | Required | The SQL, in `<![CDATA[ ... ]]>` |
| `<mandatory_parameters>` | None | Comma-separated names that must exist in some source, or the request gets `400` ([04](04-parameters.md)) |
| `<connection_string_name>` | `default` | Which connection string the query uses |
| `<success_status_code>` | `200` | The status of a successful answer. The route's value wins, then one at the root of `settings.xml` |
| `<response_structure>` | Shape follows the row count | `array` (always a list) or `file` (a download). Leave it out for one record ([05](05-response-formats.md)) |
| `<count_query>` | None | A second query that counts the rows, for pagination: the answer becomes `{"success":true,"count":N,"data":[...]}` ([05](05-response-formats.md)) |
| `<api_keys_collections>` | None | Requires an API key from these collections ([06](06-api-keys.md)) |
| `<authorize>` | None | Requires a JWT ([12](12-authentication.md)) |
| `<cache>` | None | Caches the answer of `GET` and `HEAD` requests ([07](07-caching.md)) |
| `<cors>` | The global block | CORS headers ([11](11-cors.md)) |
| `<file_management>` | `file_management.xml` | Uploads and downloads ([09](09-file-uploads.md), [10](10-file-downloads.md)) |
| `<rate_limit>` | The global block | Request limit ([18](18-rate-limiting.md)) |

### Host-based routing

Endpoints may share a route and verb when their `<host>` differs. The engine picks the most specific match: an exact host, then a wildcard, then an endpoint without `<host>`. Each element below goes inside `<settings><queries>`:

```xml
<public_home>
  <host>www.example.com</host>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome to Example' AS message; ]]></query>
</public_home>

<tenant_home>
  <host>*.example.com</host>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome, tenant' AS message; ]]></query>
</tenant_home>

<fallback_home>
  <route>home</route>
  <query><![CDATA[ SELECT 'Welcome' AS message; ]]></query>
</fallback_home>
```

### Query timeout

```xml
<query db_command_timeout="120"><![CDATA[
  SELECT * FROM large_table;
]]></query>
```

An endpoint's `<db_command_timeout>` and a global one in `settings.xml` set it too. See [Databases](13-databases.md#per-query-timeout) for which one wins and when the connection string's timeout applies.

## The other files

Each has its own page. Their top-level shape:

```xml
<!-- api_keys.xml -->
<settings>
  <api_keys_collections>
    <external_vendors>
      <key>api key 1</key>
      <key>api key 2</key>
    </external_vendors>
  </api_keys_collections>
</settings>

<!-- api_gateway.xml -->
<settings>
  <routes>
    <cat_facts>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </cat_facts>
  </routes>
</settings>

<!-- file_management.xml -->
<settings>
  <file_management>
    <relative_file_path_structure>{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}</relative_file_path_structure>
    <local_file_store>
      <primary>
        <base_path><![CDATA[c:\uploads\]]></base_path>
      </primary>
    </local_file_store>
  </file_management>
</settings>

<!-- auth_providers.xml -->
<settings>
  <authorize>
    <providers>
      <google>
        <authority>https://accounts.google.com</authority>
        <audience>your-client-id.apps.googleusercontent.com</audience>
      </google>
    </providers>
  </authorize>
</settings>
```

An `<authorize>` section at the root, as in `auth_providers.xml`, also turns CORS credentials on for every route ([11](11-cors.md#rules)).

## Environment variables

Any XML or JSON setting can be overridden by an environment variable. Environment variables
take precedence over every config file; only command-line arguments
(`--Logging:LogLevel:Default=Warning`) come after them.

The full override chain (last wins):

```
appsettings.json → appsettings.{Environment}.json → settings.xml → additional XML files → environment variables → command-line arguments
```

`settings.xml` and the additional files are found in the executable's folder, whatever the
working directory. `appsettings.json` is read twice: from the executable's folder first, then from
the working directory, whose copy wins where both set a key. `appsettings.{Environment}.json` is
found in the working directory, and so is a relative certificate path in it
(`Kestrel:Endpoints:Https:Certificate:Path`). So start the engine from its own folder: the `runme`
scripts, the Docker image and IIS do; a systemd unit needs `WorkingDirectory=`. The environment is `Production` unless `ASPNETCORE_ENVIRONMENT` says
otherwise; `dotnet run` uses `Development`.

### Log level

The shipped `appsettings.json` and `appsettings.Production.json` log at `Information`: start-up,
configuration reloads, rate-limit summaries, warnings and errors, and nothing for a request that
succeeds. `appsettings.Development.json` logs at `Debug`, about 18 entries per request, which slows
each one. `dotnet run`, with or without `--project DBToRestAPI`, starts the engine in the
`DBToRestAPI` folder as `Development`, so it reads that file and logs at `Debug`. To trace a problem
in production, set `Logging__LogLevel__Default=Debug` for a while.

A status your query raises on purpose (`THROW 50404, ...`, see [errors](../reference/errors.md#raising-an-error-from-sql))
is logged as one line with the route, the status and the message, without a stack trace: at
`Information` for a 4xx, which a caller's input causes, and at `Warning` for any other status. A
database error you didn't raise is logged at `Error` with its stack trace, and so is a raised status
that can no longer reach the caller because the response has started. A caller who disconnects
while the engine checks its token, or, on a query route, while it stores the caller's files, waits
on a cache fill, or runs the query or a `{http{ ... }http}` call, is logged as one `Information`
line with the route and the exception's message, without a stack trace. While the engine is
stopping, that line is a `Warning` saying the caller left or the server cut the request off. A
caller who disconnects at another time, such as while its request is still read, once a result of
several rows streams, or on a gateway route after its token check, is logged at `Debug` or not at
all. A timeout is not a caller who left: a database command timeout is still an `Error`, and a
`{http{ ... }http}` call's timeout a `Warning` (below).

A URL in the log is an http or https URL without its query string, its fragment or a user name and
password; any other value is logged as `(not a network URL)`. So a key in a URL's query string stays
out of the log, but a key in its path doesn't: a webhook URL whose path is the secret is logged in
full when a call to it fails, and at `Debug` on every call. A `{http{ ... }http}` call is logged at
`Debug` when it succeeds, at
`Warning` when it gets a status outside 2xx (a redirect it doesn't follow included) or times out, and at `Error` when it can't connect; at
`Debug` its headers are logged by name, never by value. A download from an `http` source is logged
at `Warning` when its URL is invalid or the remote answers an error status, and at `Error` when the
remote can't be reached or doesn't answer in time. The .NET HTTP
clients behind them log four lines per call at `Information`, under `System.Net.Http.HttpClient`,
which the shipped `appsettings.json` sets to `Warning`.

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

An environment variable replaces only the value it names. `ConnectionStrings__default` replaces the
string and keeps the `provider` attribute the file gives it; `ConnectionStrings__default__provider`
sets that. A connection that exists only in the environment needs both. `vars__name` sets a
[settings variable](21-settings-vars.md) the same way.

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

## Related

- [13 Databases](13-databases.md): providers and per-endpoint connections.
- [04 Parameters](04-parameters.md): what a query can read.
- [06 API keys](06-api-keys.md), [12 Authentication](12-authentication.md): protecting endpoints.
- [15 Encryption](15-encryption.md): encrypting settings.
- [18 Rate limiting](18-rate-limiting.md): the global default and per-endpoint limits.
