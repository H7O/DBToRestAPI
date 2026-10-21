---
title: Configuration tags
summary: Every tag the engine reads, where it goes, its default, and which value wins when it is set in more than one place. A tag that isn't on this page is ignored without a log line.
keywords: [tags, settings, route, verb, host, query, count_query, connection_string_name, json_var, db_command_timeout, buffer_rows, response_structure, success_status_code, root_node, mandatory_parameters, cache, invalidators, cors, api_keys_collections, authorize, provider, rate_limit, routes, url, excluded_headers, applied_headers, file_management, stores, store, openapi, static_files, vars, settings_encryption, additional_configurations, max_payload_size_in_bytes, debug_mode_header_value, generic_error_message, default]
---

# Configuration tags

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](errors.md).

This page lists every tag the engine reads. Use it to check that a tag exists, where it goes, and what happens without it. Each feature's own page has examples and the full contract.

## How to read this page

Each tag has one or more of these places:

- **Endpoint:** inside an endpoint's element, under `<settings><queries>` (usually `sql.xml`).
- **Gateway route:** inside a route's element, under `<settings><routes>` (`api_gateway.xml`).
- **Global:** directly under the root `<settings>` of any loaded file, usually `settings.xml`.
- **Provider:** inside a provider's element, under `<settings><authorize><providers>` (`auth_providers.xml`).
- **Store:** inside a store's element in `file_management.xml`.

"Endpoint, then global" means the endpoint's value wins when it is set and not empty, the global value applies otherwise, and the default applies when neither is set. Each tag is looked up on its own: an endpoint that sets one tag of a block keeps the global values of the others.

Three rules hold for every tag:

1. **An unknown tag, or a tag in the wrong place, is ignored without a log line.** A misspelled tag behaves as if it weren't there.
2. **A value that isn't a number where a number is expected** (`success_status_code`, `db_command_timeout`, a cache duration) makes every request to the route fail with the generic `400`, and the error is logged at `Error`. Call the route after each change.
3. **Repeated sibling elements are numbered.** A second `<queries>`, `<cors>` or `<rate_limit>` beside the first stops everything inside it from matching ([02](../topics/02-configuration.md#rules)).

## Endpoints and routing

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<queries>` | Global container | none: no query endpoints | Every endpoint goes inside one `<queries>` per file. |
| `<route>` | Endpoint | the element's name | Route values in `{{name}}`. Matched ignoring case. |
| `<verb>` | Endpoint | any method | `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, or several separated by commas. A `HEAD` request gets the `GET` route's answer without its body, unless a route takes `HEAD` itself. |
| `<host>` | Endpoint | any host | `www.example.com` or `*.example.com`, port ignored. Exact beats wildcard beats none. |
| `route_variable_pattern` | Endpoint, then global | `{{name}}` or `{r{name}}` | The pattern that finds route values in `<route>`. Leave it alone: a global one changes how routes match but not how their values are read ([TODO.md](../../TODO.md)). |

## Queries and databases

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<query>` | Endpoint | required | The SQL in `<![CDATA[ ... ]]>`. Several `<query>` elements form a chain ([14](../topics/14-query-chaining.md)). |
| `connection_string_name` attribute on `<query>` | Endpoint | the endpoint's `<connection_string_name>`, else `default` | One query of a chain on another database. |
| `json_var` attribute on `<query>` | Endpoint | `json` | Names `{pq{...}}`'s JSON array of the previous query's rows. Ignored on the first query. |
| `db_command_timeout` attribute on `<query>` | Endpoint | the endpoint's `<db_command_timeout>` | Seconds. An invalid attribute value is ignored. |
| `<connection_string_name>` | Endpoint | `default` | No global fallback. |
| `<db_command_timeout>` | Endpoint, then global | the provider's default (30 s on most) | Seconds ([13](../topics/13-databases.md#per-query-timeout)). |
| `<count_query>` | Endpoint | none | Runs after the last query, on its connection and timeout. The answer becomes `{"success":true,"count":N,"data":[...]}`, and `<response_structure>` is ignored. Attributes on it are not read. |
| `<buffer_rows>` | Endpoint, then global | 2 rows read ahead | A whole number, 0 or more. Anything else is a `500` ([05](../topics/05-response-formats.md#buffer_rows-read-a-result-before-writing-it)). |
| `<ConnectionStrings>` children | Global | `default` must exist for endpoints without `<connection_string_name>` | One element per connection, the string in CDATA. |
| `provider` attribute on a connection | Global | guessed from the string | Set it always ([13](../topics/13-databases.md#provider-detection)). `Microsoft.Data.SqlClient`, `Npgsql`, `MySqlConnector`, `Microsoft.Data.Sqlite`, `Oracle.ManagedDataAccess.Core`, `Net.IBM.Data.Db2`, `System.Data.Odbc`, `System.Data.OleDb`. |

## Responses and validation

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<response_structure>` | Endpoint only | the row count decides | `array` or `file`. `single` and `auto` are read as the tag left out; any other value is a `500`. A global one is not read ([05](../topics/05-response-formats.md)). |
| `<success_status_code>` | Endpoint, then global | `200` | A query that returns no row with the default answers `204`. A `204` never has a body. |
| `<root_node>` | Endpoint, then global | none | Wraps the answer in `{"<root_node>": ...}`. |
| `<mandatory_parameters>` | Endpoint, gateway route | none | Names that must exist in some source, else `400`. Comma-separated, or `|`-separated when a name holds a comma. Case-sensitive ([04](../topics/04-parameters.md#mandatory-parameters)). |

## Placeholder patterns

Each pattern is a regular expression, read on the endpoint, then in `config/regex.xml` (`<settings><regex>`), then the default. They are `json_variables_pattern`, `query_string_variables_pattern`, `form_data_variables_pattern`, `route_variables_pattern`, `headers_variables_pattern`, `auth_variables_pattern`, `settings_variables_pattern` and `http_variable_pattern`, and on a download route `file_variables_pattern` inside `<file_management>`. What each placeholder reads: [placeholders.md](placeholders.md).

## Caching

Only `GET` and `HEAD` requests use the cache, and a download is never cached ([07](../topics/07-caching.md)).

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<cache><memory>` | Endpoint, gateway route | not cached | The route must have this block. |
| `<duration_in_milliseconds>` | in the route's `<cache><memory>`, then the global one | not cached | Below 1 means not cached. A global duration alone caches nothing. |
| `<invalidators>` | in the route's `<cache><memory>` | none | The query-string or body inputs that change the answer: each gets its own entry. |
| `<exclude_status_codes_from_cache>` | in a gateway route's `<cache><memory>` | none | Statuses not cached, separated by commas or spaces. |

## CORS

Each tag goes in `<cors>`: the endpoint's or gateway route's, then the global one ([11](../topics/11-cors.md)).

| Tag | Default | Notes |
|---|---|---|
| `<pattern>` | none: no origin matches | Matched against the origin's host only, ignoring case. Anchor it with `^` and `$`. |
| `<fallback_origin>` | `*` | Sent when the origin doesn't match. Don't write `*`: it is sent as `https://*`. |
| `<allowed_headers>` | the default list with credentials on, `*` with them off | Replaces the default list. |
| `<allow_credentials>` | `true` when an `<authorize>` exists on the route or at the root, else `false` | `true` or `false`. |
| `<max_age>` | `86400` | Seconds. |

## API keys

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<api_keys_collections>` | Endpoint, gateway route | none: no key needed | Comma-separated collection names. The caller sends the key in `x-api-key`. The only tag that protects a route with keys ([06](../topics/06-api-keys.md)). |
| `<api_keys_collections>` children | Global (`api_keys.xml`) | none | One element per collection, holding `<key>` elements. Keys are compared case-sensitively. `{s{...}}` isn't resolved here. |

## JWT authentication

On an endpoint or gateway route, `<authorize>` protects the route. The `<authorize>` at the root of `auth_providers.xml` only holds the providers: it protects no route ([12](../topics/12-authentication.md)).

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<authorize>` | Endpoint, gateway route | none: no token needed | The block's presence turns the check on. |
| `<enabled>` | in the route's `<authorize>` | `true` | `false` turns the check off for the route. |
| `<provider>` | in the route's `<authorize>` | none | One provider, a comma-separated list, or `*` for every provider. |
| `<authority>` | Provider, or the route's `<authorize>` | required: a `500` without it | The OIDC authority URL. |
| `<audience>` | Provider, or the route's `<authorize>` | none | The token's `aud`. |
| `<issuer>` | Provider, or the route's `<authorize>` | the authority | The token's `iss`, exactly. |
| `<validate_issuer>`, `<validate_audience>`, `<validate_lifetime>` | Provider, or the route's `<authorize>` | `true` | `true` or `false`. |
| `<clock_skew_seconds>` | Provider, or the route's `<authorize>` | `300` | |
| `<required_scopes>`, `<required_roles>` | Provider, or the route's `<authorize>` | none | Comma-separated, all required, else `403`. |
| `<userinfo_fallback_claims>` | Provider, or the route's `<authorize>` | `email,name,given_name,family_name` | Missing claims fetched from UserInfo. Only those four names can start the call. |
| `<userinfo_cache_duration_seconds>` | Provider, or the route's `<authorize>` | the token's lifetime | At most the token's lifetime. |
| `<userinfo_timeout_seconds>` | the route's `<authorize>`, then the provider, then global `<authorize>`, then global | `30` | |
| `<provider_hint_header>` | the route's `<authorize>`, then global `<authorize>` | `X-Auth-Provider` | The header that picks the provider on a route with several. |

With several providers on a route, the route's own `<authority>` through `<userinfo_timeout_seconds>` are ignored: each provider uses its own block.

## Rate limiting

Each tag goes in `<rate_limit>`: the endpoint's or gateway route's, then the global one ([18](../topics/18-rate-limiting.md)).

| Tag | Default | Notes |
|---|---|---|
| `<max_requests>` | none: no limit | Whole number 1 or more. |
| `<window_seconds>` | `60` | Whole number 1 – 86400. |
| `<per>` | `caller` | `caller`, `ip` or `endpoint`. |
| `<enabled>` | `true` | `true/false`, `1/0`, `yes/no`, `on/off`. A global `false` is overridden by a route's `true`. |
| `<message>` | a built-in message | `{{retry_after_seconds}}` in it is replaced. |
| `<client_ip_header>` | the connection's address | Global only. The header a reverse proxy sets. |
| `<client_ip_header_trusted_hops>` | `1` | Global only. Whole number 1 – 64. |

## API gateway

Each route is an element under `<settings><routes>` ([08](../topics/08-api-gateway.md)). `<cors>`, `<cache>`, `<api_keys_collections>`, `<authorize>`, `<rate_limit>` and `<mandatory_parameters>` apply to gateway routes as above. `<verb>` and `<host>` don't: a gateway route takes every method on every host.

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<route>` | Gateway route | the element's name | A trailing `/*` forwards everything under the prefix; the longest prefix wins. |
| `<url>` | Gateway route | required: a `500` without it | The target. The rest of a wildcard path and the caller's query string are appended. |
| `<excluded_headers>` | Gateway route, then global | none | Request headers not forwarded. The route's list replaces the global one; an empty one counts as not set. |
| `<applied_headers>` | Gateway route | none | Each child holds a `<name>` and a `<value>`. |
| `<ignore_target_route_certificate_errors>` | Gateway route, then global | `false` | Development only. |
| `<target_route_timeout_seconds>` | Gateway route, then global | `30` | Below 1 means 30. |

## File uploads

Each tag goes in `<file_management>`: the endpoint's, then the global one in `file_management.xml` ([09](../topics/09-file-uploads.md)).

| Tag | Default | Notes |
|---|---|---|
| `<files_json_field_or_form_field_name>` | none: files are not processed | The JSON field or form field that holds the files. |
| `<stores>` | none: nothing is stored | Comma-separated store names, each defined globally. An unknown name is logged only when an upload reaches it. |
| `<filename_field_in_payload>` | `name` | The file-name field of a JSON entry. |
| `<base64_content_field_in_payload>` | `base64_content` | The content field of a JSON entry. The shipped file sets `content_base64`. |
| `<relative_file_path_structure>` | `{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}` | The stored file's path ([placeholders.md](placeholders.md#where-each-marker-works)). |
| `<max_number_of_files>` | unlimited | A route value below 1 counts as not set. A global `0` rejects every file. |
| `<max_file_size_in_bytes>` | unlimited | The same rule as above. |
| `<permitted_file_extensions>` | every extension | Comma-separated, with the dot: `.pdf,.png`. Ignoring case. |
| `<overwrite_existing_files>` | `false` | |
| `<pass_files_content_to_query>` | `false` | |
| `<accept_caller_defined_file_ids>` | `false` | |

Stores are defined globally, inside `<file_management>`:

| Tag | Default | Notes |
|---|---|---|
| `<local_file_store>` children: `<base_path>` | none: a store without one is skipped | One element per store. |
| `<sftp_file_store>` children: `<host>`, `<port>`, `<username>`, `<password>`, `<base_path>` | port `22`; base path the user's home folder | An upload store needs host, username and password: one without them is dropped, without a log line. |
| `<optional>` in a store | `false` | A failure to store in an optional store doesn't fail the upload. Not read for downloads. |

## File downloads

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<response_structure>file</response_structure>` | Endpoint | not a download | ([10](../topics/10-file-downloads.md)) |
| `<file_management><store>` | Endpoint only | none: every store download is a `404` | One store name. |

## OpenAPI

([20](../topics/20-openapi.md))

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<openapi><enabled>` | Global and endpoint | off | Globally on, a route with `false` is left out; globally off, only routes with `true` are listed. |
| `<openapi><title>`, `<version>`, `<description>` | Global | `DBToRestAPI`, `1.0.0`, `Auto-generated API specification` | |
| `<openapi><summary>`, `<description>`, `<tags>`, `<response_schema>` | Endpoint | the element's name, and the first route segment as the tag | `<tags>` comma-separated; `<response_schema>` is JSON. |

## Static files

All global, inside `<static_files>` ([22](../topics/22-static-files.md)).

| Tag | Default | Notes |
|---|---|---|
| `<enabled>` | `true` when the block exists | |
| `<root_path>` | none: off | Relative to the executable's folder. Refused when it contains the engine's folder, or lies in `config/` or the key folder. |
| `<default>` | `index.html,index.htm` | |
| `<serve_unknown_file_types>` | `false` | |
| `<cache_control_max_age_seconds>` | no header | |
| `<spa_fallback>` | `false` | |

## Settings variables and encryption

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<vars>` children | Global | none | Each child is `{s{name}}` in a query. Nested elements are skipped ([21](../topics/21-settings-vars.md)). |
| `<settings_encryption>` | Global | no encryption | ([15](../topics/15-encryption.md)) |
| `<encryption_prefix>` | in `<settings_encryption>` | `encrypted:` | |
| `<sections_to_encrypt>` with `<section>` children | in `<settings_encryption>` | none | Configuration paths with `:`, such as `ConnectionStrings` or `vars:api_key`. |
| `<data_protection_key_path>` | in `<settings_encryption>` | the `DATA_PROTECTION_KEY_PATH` environment variable, else DPAPI on Windows, else no encryption | Read once at start-up. |

## Loading, hosting and errors

| Tag | Where | Default | Notes |
|---|---|---|---|
| `<additional_configurations>` with `<path>` children | Global (`settings.xml`) | none | Files to load, relative to the executable's folder. `.xml` or `.json`; other extensions are skipped. A missing file stops start-up. A change needs a restart ([02](../topics/02-configuration.md#how-the-files-load)). |
| `<restart_on_path_changes>` | in `<additional_configurations>` | `false` | `true` stops the engine when the list changes, so a host can restart it. |
| `<max_payload_size_in_bytes>` | Global | `314572800` (300 MiB) | The largest request body. |
| `<debug_mode_header_value>` | Global | none: debug mode off | Commented out as shipped. A request whose `debug-mode` header equals it gets database errors and stack traces. |
| `<generic_error_message>` | Global | `An error occurred while processing your request.` | The message of a database error you didn't raise. |

Kestrel, TLS and logging settings are ASP.NET Core's: see [16 TLS](../topics/16-tls-certificates.md) and [02 configuration](../topics/02-configuration.md#log-level).

## Related

- [placeholders.md](placeholders.md): every marker.
- [errors.md](errors.md): every status and body.
- [02 Configuration](../topics/02-configuration.md): where the files are and how they load.
