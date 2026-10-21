# AGENTS.md

DbToRestAPI turns SQL queries into REST endpoints. You don't write application code: you write XML configuration and the SQL each endpoint runs. This engine's XML tags and placeholders are probably not in your training data. Look each one up in the pages below before you use it, and never invent a tag. An unknown tag is silently ignored.

## Find the right page

| You need to | Read |
|---|---|
| Add or change an endpoint | [03 CRUD operations](docs/topics/03-crud-operations.md), [04 parameters](docs/topics/04-parameters.md) |
| Reject bad input, choose status codes, handle errors in a client | [errors.md](docs/reference/errors.md) |
| Accept file uploads, with or without form fields | [09 file uploads](docs/topics/09-file-uploads.md) |
| Serve files for download | [10 file downloads](docs/topics/10-file-downloads.md) |
| Protect endpoints | [06 API keys](docs/topics/06-api-keys.md), [12 authentication (JWT/OIDC)](docs/topics/12-authentication.md), [18 rate limiting](docs/topics/18-rate-limiting.md) |
| Shape responses, paginate | [05 response formats](docs/topics/05-response-formats.md) |
| Let a browser page on another origin call an endpoint | [11 CORS](docs/topics/11-cors.md) |
| Use another database, or several | [13 databases](docs/topics/13-databases.md), [14 query chaining](docs/topics/14-query-chaining.md) |
| Call other APIs | [17 HTTP calls from SQL](docs/topics/17-embedded-http-calls.md), [08 API gateway](docs/topics/08-api-gateway.md), [19 webhooks](docs/topics/19-webhooks.md) |
| Check that a tag exists, where it goes, its default | [tags.md](docs/reference/tags.md) |
| Check what a placeholder reads and where it works | [placeholders.md](docs/reference/placeholders.md) |
| Anything else | [llms.txt](llms.txt), the full index |

## Where configuration lives

- In a release: `config/*.xml` next to the executable.
- In a clone: `DBToRestAPI/config/*.xml`. **`dotnet run` serves copies**: the config files in `DBToRestAPI/bin/<Configuration>/net10.0/config/`, and the SQLite demo database at `DBToRestAPI/bin/<Configuration>/net10.0/demo.db`. After editing a file under `DBToRestAPI/config/`, stop `dotnet run` and start it again, which copies the changed files. Only the files listed in `DBToRestAPI.csproj` are copied: a new file under `config/` needs its own `<None Update="config\NAME.xml"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` entry there, or the engine fails to start once `<additional_configurations>` names it. Putting new endpoints in the existing `sql.xml` avoids this.
- `sql.xml`: endpoints, each one an element under `<settings><queries>`.
- `settings.xml`: connection strings and global settings. A connection's `provider` attribute decides the SQL dialect you must write. Set it on every connection: without it, the engine guesses from keywords in the string, and a SQL Server string with `User Id=` and `Data Source=` goes to the Oracle driver ([13](docs/topics/13-databases.md#provider-detection)).
- `file_management.xml`: file stores and upload defaults. `api_keys.xml`: key collections. `auth_providers.xml`: OIDC providers. `api_gateway.xml`: proxy routes.
- Edits to the files the running engine reads are picked up without a restart, with these exceptions, which need one:
  - Kestrel and TLS settings;
  - `data_protection_key_path` under `<settings_encryption>`: the engine chooses how it encrypts once, as it starts;
  - adding or removing a file under `<additional_configurations>`.
  A file saved with invalid XML is ignored and the previous version kept, with only a log line. A route with `<cache>` keeps serving its cached response until the entry expires. If a change doesn't take effect, save the file again or restart.
- **The shipped demo database is SQLite**, but most examples in the docs are SQL Server. Write the dialect of the connection your endpoint uses.

## Rules

Each rule prevents a mistake that fails silently or looks like it works. These rules win over any page that disagrees: a few pages not yet rewritten still show older patterns ([TODO.md](TODO.md)).

1. `{{name}}` is bound as a parameter. Never quote it or build SQL text from it, and never start the message of an error you raise with it: a value that starts with `[50201]` would pick the status ([errors.md](docs/reference/errors.md#raising-an-error-from-sql)). Wrap every query in `<![CDATA[ ... ]]>`. Write each name exactly as the caller sends it, every character included (`{{first name}}`, `{{e-mail}}`, `{{@type}}`): the engine never renames inputs, and a name that matches nothing is `NULL`. ([04](docs/topics/04-parameters.md))
2. Reject a request with a status, never with `200` and an error flag. Raise `50000 + status` from SQL (`THROW 50400, 'Category is invalid', 1;` on SQL Server, other databases in [errors.md](docs/reference/errors.md#raising-an-error-from-sql)) and the client gets that status with `{"success":false,"message":"Category is invalid","error_number":400}`. Raise **before** the first statement that returns rows. On SQLite, `SELECT raise_error(50400, 'Category is invalid')` is itself such a statement: put it inside the write it guards, or after a `SELECT` that returns one row at most, never first. An error raised after the rows still gets its status, and uploads are rolled back, when the result has at most one row, or at most `<buffer_rows>` rows on a route that sets it ([05](docs/topics/05-response-formats.md#buffer_rows-read-a-result-before-writing-it)). A longer result is written to the client as it is read: once part of it has been handed to the server (about 4 KB of short values, less with long text), the connection is cut instead. A GET or HEAD request to a route with `<cache>` reads its result whole, so it gets the status too, except that a file download's query or a count query that returns several rows is read only to its second row: an error after them is lost, with or without `<cache>`. ([errors.md](docs/reference/errors.md))
3. A failed request's uploaded files are deleted automatically when its status is 400 or higher. Write no cleanup code. Rows are not rolled back: validate before writing, and put multi-statement writes in a transaction. ([errors.md](docs/reference/errors.md#when-uploads-are-rolled-back))
4. A database error you didn't raise yourself (a constraint, a syntax error) reaches the client as `400` with a generic message, not `500`. A timeout is `504`, and a database the engine can't reach is `503`, with the same message.
5. `mandatory_parameters` only checks that each name exists in some source: body, form, query string, route, a header, a JWT claim or `<vars>`. The comparison is case-sensitive, and an empty or `null` value passes. Validate values in SQL, testing `IS NULL` first.
6. An upload route needs `files_json_field_or_form_field_name` and `stores`, on the route or in `file_management.xml`, and every store name must be defined there. Without a known store, nothing is stored, yet the request succeeds and your query still sees the files as `is_new_upload`, so it records files that don't exist. Without the files field, the engine doesn't process the files at all: file parts are dropped, and the query gets the caller's raw entries, including any `is_new_upload` or `relative_path` the caller wrote. With both set, insert only entries with `is_new_upload`. ([09](docs/topics/09-file-uploads.md))
7. Upload entries use the field names set in `file_management.xml`: the shipped config uses `name` and `content_base64`. A wrong content field name in a JSON body makes every file look already stored, so nothing is saved.
8. A download route needs `<response_structure>file</response_structure>` and `<file_management><store>NAME</store></file_management>`. Return `relative_path`, `file_name` and `mime_type` (not `content_type`), with `relative_path` from your table, never from the request. A download is never cached: `<cache>` on it is ignored. ([10](docs/topics/10-file-downloads.md))
9. `<authorize>` on a route protects it with JWT. The `<authorize>` at the root of `auth_providers.xml` only defines the providers, and protects no route. Of the API-key tags, only `<api_keys_collections>` protects a route: an `<api_keys>` block inside a route does nothing. ([06](docs/topics/06-api-keys.md), [12](docs/topics/12-authentication.md))
10. On an `<authorize>` route, the user's id is `{auth{user_id}}`: the token's subject, or its `oid` when it has none. Prefer it to `{auth{sub}}`, which is empty for a token without a subject.
11. In `{http{ ... }http}` calls, put caller values in `"query"`, never inside the `"url"` string. Set the body's type with `"content_type"`, never a `Content-Type` in `"headers"`, which is sent beside it as a second type. Send a form as `"body_raw"`: a `"body"` string is sent in JSON quotes. A form's values are inserted as written, so fill it only with values from your database or checked in an earlier chained query. ([17](docs/topics/17-embedded-http-calls.md#post-with-a-form-body))
12. Within one XML file, add to the existing container element. Never write a second `<local_file_store>`, `<sftp_file_store>` or `<queries>` beside the first: repeated siblings are numbered by the configuration reader, and everything inside them stops matching.
13. Debug mode is off as shipped: `debug_mode_header_value` is commented out in `settings.xml`. If you uncomment it to see database errors, comment it out again before deploying, or set it to a long random secret: anyone who sends that value in a `debug-mode` header gets stack traces.
14. A cached route (`<cache>`) is keyed by the endpoint, the verb, the route values and the values named in `<invalidators>`, not by the query string, the body or the caller. Only GET and HEAD use the cache. A cache hit runs no SQL, so any authorization written in SQL is skipped. Cache only routes whose answer is the same for every caller, and list in `<invalidators>` every query-string or body input that changes the answer. ([07](docs/topics/07-caching.md))
15. Every `{http{ ... }http}` in a query's text is called before the query runs, even inside an `IF` branch or a comment. Never write the marker in a comment. To call conditionally, decide in an earlier chained query and pass `"skip": "{pq{skip_it}}"`. ([17](docs/topics/17-embedded-http-calls.md))
16. Endpoint element names must be unique across all loaded files, and so must each verb + route pair (per `<host>`, if you use it). Files merge element by element: the same name in two files becomes one endpoint mixing both, and nothing is logged.
17. In a query chain, read values from an earlier query with `{pq{name}}`. With `{{name}}`, a `NULL` column, zero rows or several rows let a request value with the same name take its place. ([14](docs/topics/14-query-chaining.md))
18. A JSON number arrives as a double (`float` on SQL Server), except an integer from 2^53 (9007199254740992) on, of either sign, which arrives as a 64-bit integer with every digit. On SQL Server, declare numbers `INT`, `BIGINT` or `DECIMAL`, never `NVARCHAR` (`1234567` would become `'1.23457e+006'`). A double keeps about 15 significant digits, so send a value that needs more (an exact amount, an id beyond 64 bits) as a JSON string. An empty query-string value arrives as `''`, not `NULL`: use `NULLIF({{x}}, '')` for optional filters and `TRY_CONVERT` for ids. ([04](docs/topics/04-parameters.md#value-types))
19. Leave `<response_structure>` out on a route that returns one record: one row answers an object. Set it to `array` on a route that returns a list (search, filter, a record's children): without it, a list that matches exactly one row answers an object instead of a one-element array, and a client that loops over it breaks. A route with a `count_query` always answers `{"success":true,"count":N,"data":[...]}` and ignores the tag. Use `file` only for downloads. Don't write `single` or `auto`: both are read as the tag left out, so a `single` query that returns several rows answers all of them as an array; limit such a query to one row and remove the tag. A `<response_structure>` directly under `<settings>` is not read: set the tag on each route. ([05](docs/topics/05-response-formats.md))
20. CORS doesn't protect an endpoint: the engine runs every request whatever its origin. To limit which browser pages can read the answers, set both `<pattern>`, anchored with `^` and `$` and matched against the origin's host only, and `<fallback_origin>` with your own origin: an origin the pattern doesn't match gets the fallback, or `*` without one. To allow every origin, leave both out: a `*` fallback is sent as `https://*`. When any `<authorize>` exists, as in the shipped config, a page that sends a header outside `Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key` (such as `X-Auth-Provider`) needs it in `<allowed_headers>`. ([11](docs/topics/11-cors.md))

## Check your work

1. Start the engine: `dotnet run --project DBToRestAPI` from a clone, or the executable from a release. It prints its URL (port 5000 unless `settings.xml` sets another).
2. Read the start-up log for hosting problems, such as a skipped HTTPS endpoint. It says nothing about endpoint mistakes: an unknown tag, a tag in the wrong place, a value the engine doesn't accept, a missing store or files field all log nothing at start-up, and an unknown store name is logged only when an upload reaches it.
3. So call the endpoint for success and for each failure you designed. Compare the status and the JSON body with what the docs promise. For a route with `<cache>`, also check that it is cached ([07](docs/topics/07-caching.md#basic-configuration)).

## Documentation layout

- [llms.txt](llms.txt): the index of everything. `llms.md` is an identical copy.
- `docs/reference/`: rules that apply to every endpoint: [errors.md](docs/reference/errors.md), every tag in [tags.md](docs/reference/tags.md), every placeholder in [placeholders.md](docs/reference/placeholders.md).
- `docs/topics/`: one reference page per feature.
- `docs/tutorial/`: a step-by-step course for people. It is slower to search.
- Other `*.md` files at the root are design notes and history, not the reference. Ignore `README_full_backup.md`: it is outdated.

## Working on the engine itself

- Build: `dotnet build DBToRestAPI.sln`. Test: `dotnet test DBToRestAPI.Tests`.
- Keep each file's existing line endings.
- When behaviour changes, update its topic page, `llms.txt` (keep `llms.md` identical) and, if the failure is silent, the rules above. Follow [docs/AUTHORING.md](docs/AUTHORING.md).
- Keep docs, comments and commit messages generic: never name a customer, a deployment or an internal system.
