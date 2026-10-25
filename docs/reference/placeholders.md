---
title: Placeholders
summary: Every marker the engine replaces in a query, a route, an embedded HTTP call or a file path, what each one reads, where it works, and what it gives when nothing matches.
keywords: ["{{name}}", "{j{name}}", "{qs{name}}", "{r{name}}", "{f{name}}", "{h{name}}", "{auth{claim}}", "{auth{user_id}}", "{s{name}}", "{settings{name}}", "{pq{name}}", "{pq{json}}", json_var, "{http{", "}http}", "{type{json{name}}}", "{fs{store}}", "{fs{base_path}}", "{date{format}}", "{{guid}}", "{file{name}}", placeholder, marker, regex.xml, NULL]
---

# Placeholders

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](errors.md).

A placeholder, or marker, is text in the configuration that the engine replaces with a value. This page lists every one. In a query, every marker becomes a bound parameter, so a value never becomes SQL text. Each marker's own page has the details.

## Rules

1. **Never quote a marker in SQL.** `'{{name}}'` is a string that holds a parameter's name, not the value. Write `{{name}}` bare, where a value goes.
2. **A marker that matches nothing is `NULL`.** A misspelled name, a claim the token doesn't have, an unknown setting: all `NULL`, and nothing is logged. Test with `IS NULL` before you use a value.
3. **Use a marker only where it works** ([below](#where-each-marker-works)). Anywhere else it stays as written: `{date{yyyy}}` in a query, or `{fs{store}}` outside a download route, reaches the database as text and fails as a syntax error, which the caller gets as a generic `400`.
4. **Read an earlier chained query's result with `{pq{name}}`, not `{{name}}`.** `{{name}}` falls back to the request when the earlier value is missing ([14](../topics/14-query-chaining.md)).
5. **Write a name with every character the caller sends.** `{{first name}}` reads the key `first name`. Letter case doesn't matter, except in `mandatory_parameters` ([04](../topics/04-parameters.md#names-with-spaces-and-other-characters)).

## Request values

| Marker | Reads | When nothing matches |
|---|---|---|
| `{{name}}` | The first source that has `name`: route, query string, form field, JSON body, header | `NULL` |
| `{r{name}}` | The route only (`<route>users/{{name}}</route>`) | `NULL` |
| `{qs{name}}` | The query string only. An empty value (`?name=`) is `''` | `NULL` |
| `{f{name}}` | A form field only (`multipart/form-data` or `application/x-www-form-urlencoded`) | `NULL` |
| `{j{name}}` | The JSON body only. A nested object or array is its JSON text | `NULL` |
| `{h{name}}` | A request header only. The name ignores case, like HTTP | `NULL` |

A JSON `null` counts as missing, so `{{name}}` moves on to the next source. Types, priorities and names with special characters: [04 parameters](../topics/04-parameters.md).

## Other values in a query

| Marker | Reads | When nothing matches | Page |
|---|---|---|---|
| `{auth{claim}}` | A claim of the validated JWT, on a route with `<authorize>`. `{auth{user_id}}` is the subject, or `oid` without one or when the subject starts with `Not supported` (Azure AD B2C) | `NULL`, and always `NULL` on a route without `<authorize>` | [12](../topics/12-authentication.md) |
| `{s{name}}`, `{settings{name}}` | A child of `<vars>` at the root of the configuration. The name ignores case | `NULL` | [21](../topics/21-settings-vars.md) |
| `{pq{column}}` | A column of an earlier chained query that returned one row. When several did, the latest one with that column | `NULL` | [14](../topics/14-query-chaining.md) |
| `{pq{json}}` | The previous query's rows as a JSON array (`[]` for none). `json_var="name"` on a `<query>` renames it | `NULL` in the first query | [14](../topics/14-query-chaining.md) |
| `{http{ ... }http}` | Makes the HTTP call described by the JSON inside it, before the query runs, and gives `{"status_code":...,"headers":...,"data":...,"error":...}` | `NULL` when the call has `skip` or `no_wait` | [17](../topics/17-embedded-http-calls.md) |
| `{fs{store}}`, `{fs{base_path}}` | On a download route with a `<store>`: the store's name and its base path | Not replaced on any other route (rule 3) | [10](../topics/10-file-downloads.md) |

`{type{json{name}}}` is not a value: written as a column alias (`AS {type{json{phones}}}`), it names the column `name` and sends the column's text as JSON instead of a string ([05](../topics/05-response-formats.md)).

## Where each marker works

| Place | Markers |
|---|---|
| `<query>` and `<count_query>` | All of the above |
| Inside a `{http{ ... }http}` block | Request values, `{auth{...}}`, `{s{...}}`, `{pq{...}}`. They are written into the call's JSON as text, the same on every server (`19.99`, `2026-10-26T13:45:00`, `true`), and escaped when they sit inside a JSON string. A caller's value goes in `"query"`, never inside `"url"` ([17](../topics/17-embedded-http-calls.md#caller-supplied-values-url-encoding-and-json-escaping)) |
| `<route>` | `{{name}}` for each route value: `users/{{id}}/orders` |
| `relative_file_path_structure` (uploads) | `{date{format}}`, `{{guid}}` and `{file{name}}`, below |
| Settings files: `api_keys.xml`, `auth_providers.xml`, connection strings | None: a `{s{...}}` there is kept as written |

`relative_file_path_structure` builds each uploaded file's path in its store ([09](../topics/09-file-uploads.md)). The default is `{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}`:

- `{date{format}}`: the upload's UTC date in a .NET date format. `MMM` is the month's short name in the server's culture, such as `Oct` in English.
- `{{guid}}`: the file's id, new for each file.
- `{file{name}}`: the file name the caller sent, after the file-name checks.

## Changing a marker's pattern

Each source's marker is found with a regular expression. `config/regex.xml` sets one per source, and an element with the same name on an endpoint replaces it there: `json_variables_pattern`, `query_string_variables_pattern`, `route_variables_pattern`, `form_data_variables_pattern`, `headers_variables_pattern`, `auth_variables_pattern`, `settings_variables_pattern`, `http_variable_pattern`. A download route's `{fs{...}}` pattern is `file_variables_pattern`, inside its `<file_management>`. The endpoint's element wins, then `regex.xml`, then the built-in default, which is the pattern `regex.xml` ships with. Changing one source's pattern changes that source only: with `json_variables_pattern` set to `||name||`, `||name||` reads the JSON body and `{{name}}` no longer does. Keep the defaults unless your SQL needs `{{` for something else.

## Related

- [04 Parameters](../topics/04-parameters.md): sources, priority, value types, names.
- [14 Query chaining](../topics/14-query-chaining.md): `{pq{}}`.
- [17 Embedded HTTP calls](../topics/17-embedded-http-calls.md): `{http{}}`.
- [errors.md](errors.md): what a failed request returns.
