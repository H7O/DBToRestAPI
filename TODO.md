# TODO

Follow-ups recommended after a change landed, so they are not lost between releases. Each item
names the note that explains the background.

## Open

- **An array column in a `{http{}}` block is written as its .NET type name.** Found on
  2026-10-26. A driver that returns an array for one column (Npgsql, for a PostgreSQL `int[]`)
  gives `System.Int32[]`, where the engine's own JSON answers write `[1,2,3]`. What it should be
  inside a string needs a decision.
- **Idea: an opt-in `<allowed_hosts>` for outbound calls** (`{http{ … }http}` and the download
  `http` source). The configuration author chooses the hosts a route calls, and a caller can't
  change them ([SECURITY_HARDENING_1.6.md](SECURITY_HARDENING_1.6.md), section 1). A list of hosts
  (global, overridable per endpoint) would let a deployment have the engine enforce that choice
  too. A convenience, not a fix.

The items below were found on 2026-10-06 while testing the docs with docs-only agents graded
against the code. Each was read in the code; none has a test yet.

- **A caller waiting on a slow cache fill waits for all of it.** Callers that join a running fill
  wait until it ends, however long that takes (a 3-second query made every joined caller wait about
  3.5 s). A maximum join wait, after which a waiting caller runs its own query, would bound it. Not
  requested; recorded from the downstream's measurement of the 1.7.11 fix.
- **A cookie set on a redirect is dropped.** The engine's clients are pooled and keep no cookies, so
  that callers never share them, and they follow redirects themselves. So a cookie the remote sets on
  a `3xx` never reaches the next hop: an `http` download that needs it fails (until 1.7.8 each
  download had a client of its own, whose cookies lived for that download only), and a gateway cookie
  login that answers `302` with the session cookie can't work. `{http{}}` calls can turn off `follow_redirects` and make the
  second call. Fixes: follow redirects in the engine with a cookie jar per request; for the gateway,
  pass the `3xx` through, which needs its `Location` rewritten to the gateway route, or the caller
  would follow it straight to the target.

The items below were found on 2026-10-06 by checking real-world usage patterns against the code.

- **`{http{}}` matching ignores SQL comments**, so AGENTS rule 15 says never to write the marker in
  one. A complete marker in a comment makes a real call. An unclosed one pairs with the next block's
  `}http}`, and the SQL in between is deleted.

The items below were found on 2026-10-21 by an inventory of every configuration key the code reads
(for `docs/reference/tags.md`), each checked in the code or against a running engine.

- **SFTP stores sign in with a password only**, so key-based SFTP isn't possible. An upload store
  without a `password` fails the upload (or is skipped, when optional); a download from the same
  store connects with an empty password.
- **Docs: finish the agent-first rewrite** ([docs/AUTHORING.md](docs/AUTHORING.md)).
  The pages are rewritten or corrected, and `docs/reference/tags.md` and `placeholders.md` exist.
  `DocsExamplesTests` checks every XML example's tags, CDATA and placeholders in CI, and the
  examples of 09, 10, 14 and 17 run on SQL Server in CI. A published build (every release archive)
  holds the docs next to the executable. Still to do: the other pages' examples (several use
  PostgreSQL, DB2 or SQLite, which CI doesn't run).

## Done

- **A `form` object in `{http{}}` calls.** Added in 1.7.32. In `body_raw`, or a `body` string with a
  form type, `&` and `=` in a value add fields, so a form could hold only values the query checked or
  built. `"form": { "amount": "{{amount}}" }` encodes each field as `query` does and sends
  `application/x-www-form-urlencoded`, unless `content_type` gives another type. With `body` or
  `body_raw` beside it, the call fails rather than one of them quietly winning. Topic 17's Stripe
  example now uses it.
- **A binary column in a `{http{}}` block was written as `System.Byte[]`.** Fixed in 1.7.31. A
  `VARBINARY` or `BLOB` column is a byte array, whose .NET text is its type name. It is now base64,
  as the engine's own JSON answers write it (`AQL/`), and outside a string always a JSON string,
  since its base64 can read as JSON (`1234`, `true`).
- **A number or date inside a JSON string of a `{http{}}` block was written in the server's
  culture.** Fixed in 1.7.30, listed as a breaking change. `EmbeddedHttpTemplate.ConvertValue` used
  `ToString()`, so a server whose culture writes a decimal comma sent `19,99`, a Swedish one `−7`
  with a U+2212 minus sign, a Thai one the year 2569, a Saudi one Arabic separators and a Hijri
  date, and an en-US one `10/26/2026 1:45:00 PM`. A value is now written as the engine's own JSON
  answers write it: `19.99`, `-7`, a date in ISO 8601 (`2026-10-26T13:45:00.123`) and a boolean as
  `true`, inside a string and outside one. Before, a date also lost its fraction of a second, and a
  boolean inside a string was `True`. Decided on 2026-10-26: the invariant culture first, then ISO
  8601 dates and lowercase booleans in the same release, so a deployment adjusts once.
- **SQLite writes from many connections at once stalled for 150 ms when they collided.** Fixed in
  1.7.29. Microsoft.Data.Sqlite leaves SQLite's busy timeout at 0, so a statement that found the
  database locked failed at once inside the driver, which slept 150 ms before each retry until the
  command timeout. A COMMIT collides too, with a waiting writer's brief read lock. Each query now
  sets SQLite's own busy timeout to its command timeout (`db_command_timeout`, else the connection's
  default, 30 s; 0 still waits as long as it takes), so it waits inside SQLite in steps of 1 to
  100 ms and still fails, with `504`, when it did. A write that waited for a lock goes on about
  20 ms after its release, against 150 before (`SqliteLockWaitTests`); the bundled SQLite sleeps in
  milliseconds on Windows and Linux. WAL mode, which changes a deployment's database files, was
  left out. The driver ignores cancellation once a statement runs, before as after.
- **The warm-up setting is documented, not set.** Decided on 2026-10-26. Under full load on one
  CPU, the engine reaches full speed about a minute after a start, and
  `DOTNET_TC_CallCountingDelayMs=0` gets it there in 10 to 20 seconds, for a CPU cost once per
  start. Tutorial 21 (small hosts) gives it as an option; the engine doesn't set it.
- **CORS turned credentials on for every route when any `<authorize>` existed.** Fixed in 1.7.29.
  The `<authorize>` at the root of `auth_providers.xml` holds the providers and their defaults and
  protects no route, yet it made every route answer `Access-Control-Allow-Credentials: true`, whose
  default allowed headers are a fixed list rather than `*`. So a page on another origin couldn't
  send `X-Auth-Provider`, or any header outside the list, to any route. Credentials now come from the
  route's own `<authorize>` (or `<allow_credentials>`), and on such a route the default list holds its
  provider hint header (`X-Auth-Provider` unless `<provider_hint_header>` names another).
  `CorsPipelineTests` checks both.
- **The OpenAPI document described three kinds of route wrong.** Fixed in 1.7.28. An upload route
  had no request schema: a POST, PUT or PATCH to a route with a files field (its own or the global
  one) now has a `multipart/form-data` body (the entries as a JSON string, each new file a part)
  and an `application/json` one (the entries with base64 content), with the field names the route
  uses. A `root_node` wrapper wasn't shown: the answer is now inside it, from the route or the
  global setting, and `null` inside where a route without `array` or a count query finds no row.
  And a `file` route with a `count_query` was documented as a download, though it answers the
  count envelope; a blank `count_query`, which the controller ignores, counted as one.
  `OpenApiDocumentBuilderTests` checks each case.
- **A gateway route read the target's whole answer into memory before writing any of it.** Fixed
  in 1.7.27. `HttpClient.SendAsync` without `ResponseHeadersRead` reads the answer to its end, so
  a large download through a gateway route held its full size in memory per request. The answer
  is now passed on as it arrives, block by block. `target_route_timeout_seconds` covers the wait
  for the headers, then each wait for more of the answer, and stops while a block is written, so
  a slow caller doesn't count against the target (it covered the whole answer, which streaming
  would have made cut off a long download to a slow caller). The target's status and headers are
  set with the first block, so a target that fails before then gets its caller `502` or `504`,
  with the engine's own headers kept; after, the connection is cut and logged, as for a streamed
  query result. A cached route still reads the answer whole, within the timeout, to store it.
  Since 1.7.26, a caller who leaves a gateway route is logged like one who leaves a query route.
  `GatewayStreamingTests` checks each case.
- **A gateway call that failed was the generic `400`, and wasn't logged.** Fixed in 1.7.26. A
  target the engine couldn't reach (no such host, a refused or dropped connection, a TLS failure)
  or that didn't answer within `target_route_timeout_seconds` answered `400` with nothing in the
  log, so the cause showed only in debug mode. It is now `502` or `504` with the generic message,
  as a database the engine can't reach is `503` or `504`, and an error log line names the route
  and the target without its query string. A caller's body too large to forward, which the client
  reports inside its own exception, keeps its `413`. `GatewayFailureTests` checks each case.
- **A body of another type was read as no parameters, and `json/` was a hidden route prefix.**
  Fixed in 1.7.25. A body whose `Content-Type` was neither `application/json` nor a form was
  ignored: `text/plain` (which `fetch` sends for a string body without a type) or
  `application/vnd.api+json` ran the query with every body parameter `NULL`, and the request
  succeeded. `text/json` and `application/*+json` are now read as JSON, as ASP.NET Core's own JSON
  input reads them, and a body of any other type is a `415`, answered after the CORS, API key, JWT
  and rate limit steps. A request without a body and a gateway route's body are never refused. The
  undocumented `json/` prefix, which read any body as JSON, is removed: a request without a
  `Content-Type` already counts as JSON, and the prefix made a route named `json/orders`
  unreachable, because `/json/orders` was looked up as `orders`. Breaking: `/json/<route>` is now
  the path `json/<route>`. A gateway entry routing `json/*` to the engine itself as JSON keeps old
  callers working (the release notes give it; `LegacyJsonPrefixGatewayTests` checks it), and
  `RequestBodyTypeTests` checks the rest.
- **Cache invalidators read from every source: as documented, not a bug.** Checked on
  2026-10-23. Each value is labelled with its source, so a name that a `<vars>` key or a token
  claim shares adds that value beside the request's, and only splits entries further. Naming a
  claim (`user_id`, `email`) gives each caller its own entry, which topic 07 recommends for an
  answer that depends on the caller. `CacheInvalidatorSourcesTests` checks all three cases.
- **A new file under `config/` wasn't copied to the build output.** Fixed in 1.7.24. Each config
  file had its own entry in `DBToRestAPI.csproj`, so a file added under `config/` and named in
  `<additional_configurations>` made `dotnet run` fail at start-up until it got one. One wildcard
  entry now copies every `.xml` file under `config/`, subfolders included.
- **Azure AD B2C's `Not supported` subject made every user one user.** Fixed in 1.7.24. A B2C user
  flow with its subject claim turned off sends `Not supported currently. Use oid claim.` as every
  user's `sub`, and `{auth{user_id}}` took it, so a query scoped by user showed each user everyone's
  rows. A subject that starts with `Not supported` now counts as none, and `user_id` is the token's
  `oid`, or missing without one. `{auth{sub}}` still holds what the token says.
- **Binding JSON numbers as `decimal`: dropped.** A JSON number that isn't a large integer is bound
  as a `double`, so a value with more than about 15 significant digits is rounded, and AGENTS rule
  18 says to send such a value as a string. Binding every JSON number as `decimal` was tried for
  1.7.16 and dropped: it changed what existing SQL computes (on SQL Server, `POWER({{x}}, 2)` takes
  the type of `x`, so 2.5 gave 6.3; a product's scale is cut to 6 places; a number compared with
  `''` failed; on PostgreSQL, 2.5 stored in an int column became 3), and Oracle's driver and the
  Access ODBC driver read some decimal results back wrong or not at all. Binding only the numbers a
  double would change was left as an idea, and decided against on 2026-10-23: not worth its risk
  for the edge case it covers.
- **The Docker image of 1.7.22 wasn't built.** Fixed in 1.7.23. A published build has carried the
  docs since the docs were bundled, but `.dockerignore` kept them out of the image's build context,
  so `dotnet publish` failed in the Docker workflow. The Docker workflow runs only for a release
  tag, so nothing failed until the first release after the change. `.dockerignore` now lets the
  docs in, and CI builds the image on every push, runs it, calls an endpoint and checks that the
  docs are in it.
- **A result of many rows cost about twice the CPU of hand-written code.** Fixed in 1.7.22 with
  Com.H.Data.Common 10.1.0.15. Each row was an `ExpandoObject`, built one column at a time, and
  System.Text.Json wrote it as a dictionary of `object`, looking up a converter for every value. The
  library's rows are now `DbQueryRow`s: the rows of a result share their column names, and the
  library writes them with a converter of its own, each name encoded once per result. The engine
  writes the objects of a JSON column (`ExpandoObject`s still) with `ExpandoJsonConverter`. The JSON
  is byte for byte the same, which `RowJsonPipelineTests` and `SqlServerColumnTypesTests` check
  against topic 05's table. On one CPU, at steady speed, a page of 1,000 rows went from 2,250 to
  1,330 µs of CPU (the hand-written API: about 1,130), 100 rows from 342 to 266 µs, and a row by id
  stayed at 133 µs.
- **Five settings were read wrong or not checked.** Fixed in 1.7.21, each found by the inventory of
  configuration keys or the docs pass. A setting that takes a number or `true`/`false` and held
  something else (`success_status_code` of `created`, `db_command_timeout` of `soon`, a cache
  duration, an upload limit, a gateway timeout, `<authorize><enabled>yes`) failed the request with
  the generic `400`, the generic `500` or an empty `500`, and a query's `db_command_timeout`
  attribute was ignored: each is now a `500` naming the setting, its value and the route. A global
  `route_variable_pattern` matched routes but their values were read with the default pattern. A
  global upload limit of `0` rejected every file; below 1 is now no limit, as on a route. An upload
  store missing a setting it needs (an SFTP store's `password`, a local store's `base_path`) was
  skipped without a log line, so the request succeeded with nothing stored; it now fails like a
  store that can't be reached, before any file is written. And the engine sent no
  `Access-Control-Expose-Headers`, so a page on another origin couldn't read a download's
  `Content-Disposition`: `<exposed_headers>` in `<cors>` sets them, `Content-Disposition, Retry-After`
  by default.
- **CORS, embedded HTTP calls and the provider guess got eight things wrong.** Fixed in 1.7.20, each
  found while the docs were checked against a running engine. CORS: `<fallback_origin>*</fallback_origin>`
  was sent as `https://*`; a preflight to a gateway route answered `500` (Step 1 gave it no route
  list); a preflight said credentials were allowed for a route with `allow_credentials` false
  whenever the root had an `<authorize>`; and an answer that depends on the origin had no
  `Vary: Origin`. `{http{}}` calls: a `Content-Type` in `headers` was sent beside the default type,
  as two types; a string `body` was sent as a JSON string in quotes, so a form never arrived (it is
  now sent as its text when the body's type isn't JSON, and unchanged under JSON); and a marker
  outside a JSON string that matched nothing left the block invalid, so the call wasn't made (a
  NULL is now `null` there). And without `provider`, a SQL Server string with a SQL login went to
  the Oracle driver, and one naming a server such as `prod.db.example.com` went to SQLite: ODBC and
  OLE DB are now recognised first, SQL Server's own keywords before Oracle's and SQLite's rules,
  and SQLite only from the data source itself.
- **PATCH and HEAD didn't reach their routes, and four settings or log lines were wrong.** Fixed in
  1.7.19. A PATCH answered `405`: routing, CORS and OpenAPI accepted it, but the controller had no
  `[HttpPatch]`. A HEAD to a GET route answered `404`, and to any route `405`, though the cache
  docs describe HEAD; it now gets the GET route's answer without its body, unless a route takes
  HEAD itself. `regex.xml` set `regex:query_string_variables_pattern`, `regex:route_variables_pattern`
  and `regex:form_variables_pattern`, which nothing read: they are read now, after the keys at the
  root that were read before, so nothing that worked changes, and `regex.xml` names the form one
  `form_data_variables_pattern`. The download step dropped `regex:file_variables_pattern`, and read
  a `response_type` fallback at a path nobody can set (`file_management:file_management:`), which is
  gone. And log lines kept a caller's text as it was: an upload's file name with U+2028 or U+2029
  (escaped now), and a database error that repeats a caller's value (an exception whose message could
  start a new line is logged as escaped text, without the exception object).
- **A request spent CPU on work it didn't need.** Fixed in 1.7.18, found by profiling the engine
  under load. Each middleware step formatted the time for a Debug log line even with Debug off;
  every connection parsed its connection string again; `GetSection` on a configuration section
  built and hashed the full path; route matching read two settings and ran the route variable
  pattern over every parameterized route; CORS sorted the route's verbs; and Com.H.Data.Common
  10.1.0.14 stopped allocating, on each query, a 6.4 KB table and twenty-odd collections for every
  placeholder source that matched nothing. On one CPU, in steady state, a lookup by id went from 158
  to 137 µs of CPU, and `SELECT 1` from 117 to 100. The benchmark changed with it: it now loads each
  server for two minutes before measuring. Under full load on one CPU the runtime recompiles the hot
  code only after about a minute, and the 3-second warmup had measured both servers before that
  (the 1.7.17 figures below among them).
- **Every request compiled a regular expression.** Fixed in 1.7.17 (`QueryRouteResolver.cs`):
  reading a route's parameters fell back to the default pattern's text, not to its precompiled
  regex, so each request compiled one (`RegexOptions.Compiled`), about a millisecond of CPU, most
  of what a request cost. Each pattern is now compiled once. On one CPU, a `SELECT 1` endpoint went
  from about 900 to 3,600 requests a second, and a lookup by id from about 280 to 4,900. Found by
  the benchmarks added in the same release (`benchmarks/`).
- **A JSON integer from 2^53 on lost its last digits.** Fixed in 1.7.16 (Com.H.Data.Common
  10.1.0.13, mirrored in Com.H 10.3.4): `GetDataModelParameters` read every JSON number as a
  `double`, which can't tell such neighbouring integers apart, so 9007199254740993 became
  9007199254740992, and a lookup by either id found both rows. Such an integer, of either sign and
  up to the largest 64-bit one, is now a `long`, so the query, a `{http{}}` block and a cache key
  get every digit. Every other number is still a double.
- **A status raised from SQL didn't map on every database.** Fixed in 1.7.15
  (`Services/DatabaseErrors.cs`). PostgreSQL's message kept its `P0001:` prefix, and SQLite's its
  `SQLite Error 19: '...'` wrapper. Oracle's number was read as negative, though ODP.NET reports it
  positive, and DB2's as the first `[nnnnn]` in the message, which is the SQLSTATE. ODBC and OLE DB
  weren't read at all. Each now gives the status with the database's own text. PostgreSQL also
  takes the number as the `ERRCODE` (`RAISE EXCEPTION 'Not found' USING ERRCODE = '50404'`). A `[nnnnn]` in the text is read only from an error the SQL
  raised, and only at the start of its text, so a database error that repeats a caller's value (a
  failed cast of `'[50404]'`), or a raised message that carries one after its start, no longer
  picks the status. Only a status from 200 to 599 maps: SQL Server's `RAISERROR('text', 16,
  1)`, which reports 50000, sent an invalid status line (`0`) and kept the uploads, and is now the
  generic `400`. On SQL Server, a raised error that followed another error in the batch is found
  too.
- **SQLite could raise a status only from a trigger.** Changed in 1.7.15: the engine adds
  `raise_error(number, message)` to every SQLite connection it opens, so any statement can raise
  one. A number that isn't a status makes the function fail with a database error.
- **A timeout or an unreachable database was a `400`, like a client error.** Changed in 1.7.15: a
  timeout is a `504`, and a database the engine can't connect to, or that closes the connection
  during the query, is a `503`, both with the generic message. The engine now opens each
  connection itself, so that a failure to connect is told apart from a query that fails.
- **On SQLite, a query that failed inside `BEGIN ... COMMIT` left its transaction open.** Fixed in
  1.7.15. Microsoft.Data.Sqlite returns a connection to its pool as it is, so the open transaction
  kept the database's write lock: every other connection's write waited and failed (`database is
  locked`), and the next request given that connection ran inside the transaction. The engine now
  rolls back a transaction a query left open before it lets a SQLite connection go. PostgreSQL's
  driver rolls back as the connection closes. SQL Server's and MySQL's reset a pooled connection
  only when it is next used, so a transaction left open there keeps its locks until then: errors.md
  now says how to roll back on SQL Server (`SET XACT_ABORT ON` with `THROW`) and MySQL (an exit handler).
  Found by the 1.7.15 review.
- **An error after part of a streamed result had been written always cut the connection**, the
  cost of streaming. Changed in 1.7.15 by an opt-in `<buffer_rows>` (route, else global): a result of up to
  that many rows is read before it is written, so such an error keeps its status. The rows read are
  held in memory, so the default still streams.
- **In an SFTP store, a failed request deleted the file its upload had overwritten,** and a write
  that failed part-way could leave a partial file. Fixed in 1.7.14, as 1.7.13 did for local stores,
  with the rename `Com.H.Net.Ssh` 10.2.0 adds. An SFTP upload is written under a hidden name beside
  its destination (`.<id>.uploading`) and renamed into place once complete. With
  `overwrite_existing_files` on, the file at the path is first renamed aside (`.<id>.replaced`): a
  failed request renames it back, and a successful one deletes it. A path taken during the upload is
  a `409`, as for a file there from the start, and an upload now stops when the caller leaves. The
  SFTP account needs rename and delete rights under `base_path`: without the rename right, every
  upload to the store fails.
  In every store, a failed request now undoes its writes newest first, each file deleted and the
  one it replaced put back, so stores that reach one folder by different paths (a link, another SFTP
  account) end as they were. In 1.7.13, such a request could leave its own file at the path.
- **Two uploads that created the same new SFTP folder at once could make one fail**, as on the
  first uploads of a day with the default path structure. Fixed in `Com.H.Net.Ssh` 10.2.1, which
  1.7.14 references: an upload accepts a folder another client created meanwhile.
- **`<cache>` on a file download route broke the download.** Fixed in 1.7.14: a download is never
  cached, and `<cache>` on it is ignored. The cache keeps JSON results only, so a cached download
  lost its file.
- **A request whose `Origin` the server couldn't send back got a `500`.** Fixed in 1.7.14. A
  matching `Origin` was echoed as sent, so one such as `http://localhost/café` put a value in
  `Access-Control-Allow-Origin` that Kestrel refused. An `Origin` that isn't in the form a browser
  sends (`scheme://host[:port]` in ASCII) now matches no pattern and gets the fallback. A browser's
  origin is still sent back exactly as sent.
- **The OpenAPI document's default error schema didn't match the body.** Fixed in 1.7.14: it
  described an `error_message` property, and now lists `success`, `message` and `error_number`.
- **A failed request deleted the file its upload had overwritten.** Fixed in 1.7.13 for local
  stores. With `overwrite_existing_files` on, the file already at the path is moved aside in its
  folder (`.<id>.replaced`): a failed request renames it back, and a successful one deletes it. A
  local store also writes each upload under a hidden name (`.<id>.uploading`) and renames it into
  place once complete, so a write that fails part-way leaves nothing at the path, in an optional
  store too, and a file being replaced stays readable until its replacement is complete. SFTP stores
  followed in 1.7.14.
- **A JSON body that didn't parse ran the query with every body parameter `NULL`.** Fixed in 1.7.13.
  It is now a `400` that says where the JSON broke (`The request body is not valid JSON (line 1,
  byte 3).`), or that it is nested more than 64 levels deep. JSON is read strictly, so a comment or
  a trailing comma is refused too: the parser used to skip them, and depending on where they sat,
  the query then got the body's parameters or none. So are bytes that aren't UTF-8, which could be a
  `500`. A blank body (a UTF-8 byte order mark included) still has no parameters, and a gateway
  route forwards such a body as sent.
- **A connection's provider and `max_payload_size_in_bytes` needed a restart.** Fixed in 1.7.13.
  The `provider` attribute is read for each connection, and a provider inferred from a connection
  string is cached by the string itself, so editing either takes effect on the next connection. The
  body size limit is applied to each request from the current configuration. A value that isn't a
  positive whole number counts as not set: one that didn't parse, or a negative one, used to stop the
  engine at start-up, and 0 refused every body. AGENTS.md lists the few settings that still need a
  restart.
- **The shipped `settings.xml` turned debug mode on with a sample value.** Changed in 1.7.13:
  `debug_mode_header_value` is commented out, with a note on uncommenting it to see database errors
  while developing. Anyone who knew the sample value got stack traces from an engine that kept it.
- **Some pages and TODO entries described choices as risks.** Reworded in 1.7.13. Outbound calls go
  to the hosts the configuration builds, which is the author's choice: an opt-in `<allowed_hosts>`
  stays in Open as an idea. The Open item asking for configuration validation at load was dropped,
  as 1.7.12 decided against such warnings, and entries for documented trade-offs now name the rule
  that covers them. Tutorial 23's topic table had five broken links and missed six topics; it now
  lists every topic.
- **Warnings about configuration the engine doesn't read are gone.** Removed in 1.7.12. The engine
  warned about a few such mistakes (an `<api_keys>` block in a route, an `<api_keys_collections>`
  that names no collection, a `response_structure` of
  `single`, `auto` or an unknown value, a global `response_structure`, `<rate_limit>` values that
  don't parse or a block with no `max_requests`, an empty API key collection, an empty static files
  `root_path`, more than one `<rate_limit>` block in a node) and not about the many others, such
  as a misspelled `<cache>` duration. Keeping a
  list of what is legitimate is a chase that never ends, and each check cost code that has to be
  read and kept in step. The engine now ignores every tag and value it doesn't read, as it always
  did for most of them, and the docs say how to check that a route does what it should. The Open
  item that asked for warnings about silent upload and download misconfigurations was dropped for
  the same reason. Warnings about events at run time stay.
- **A caller who disconnected was logged as an error with a stack trace.** Fixed in 1.7.12. A
  caller who leaves while it waits on another caller's cache fill, while its query (before a
  streamed result starts) or a `{http{ ... }http}` call runs, while its uploaded files are stored,
  or while an `<authorize>` route loads its provider's discovery document, is now logged as one
  `Information` line with the route and the exception's message, and the engine sets `499`. One
  who leaves while an `<authorize>` route calls UserInfo logged nothing at the default level, and
  one who leaves during an SFTP upload, which doesn't stop with the caller, was a rollback warning;
  both get the same line now. While the engine is stopping, that line is a `Warning` saying the
  caller left or the server cut the request off. Only the request's own `RequestAborted` counts, so a timeout is logged as before (a
  database timeout at `Error`). A `{http{ ... }http}` call whose caller
  left used to be logged as a warning and handed to the query as `status_code` 0, and the query then
  stopped on the cancelled token with an `Error`; the call now stops the run. A call's own
  `timeout_seconds` still gives the query `status_code` 0. A `no_wait` call cut off because the
  engine stops is one warning naming its URL, without a stack trace.
- **An upload cut off while it was copied into a store stayed there half written.** Fixed in
  1.7.12. The rollback deleted only the files whose copy had finished, so a caller who left, or a
  failure, during a copy left a partial file in the store. A file is now tracked for the rollback
  once its copy has created it (local stores), or before its upload when it can't replace an
  existing file (SFTP stores).
- **The tutorial's endpoint anatomy taught a cache that never caches.** Fixed in 1.7.12.
  `docs/tutorial/06-xml-structure.md` showed `<cache><duration_in_seconds>`, but the engine reads
  `<cache><memory><duration_in_milliseconds>`, so a route copied from it ran its query on every
  request. The caching topic now says how to check that a route is cached. Also in 1.7.12: Com.H
  10.3.3 (its Fill and FillDate fixes; the engine doesn't call them).

- **A caller waiting on another caller's cache fill got a 400 when that caller disconnected.** Fixed
  in 1.7.11. HybridCache runs one fill per key, and every caller with the same key waits on it, but
  the fill ran on the first caller's `RequestAborted`, and a query endpoint's connection and readers
  were disposed with the first caller's response. When that caller left, the fill failed, and every
  caller waiting on it got the generic 400. A fill now runs on HybridCache's own token, cancelled only
  once every waiting caller has gone; a query endpoint's fill disposes its own connection and readers
  and no longer reads the first caller's `HttpContext`, which is disposed when that request ends. A
  gateway fill forwards a request built, body included, before the fill starts, and hands a response
  that must not be stored back to the first caller instead of writing to its response. The UserInfo
  lookup, which callers with the same token share, no longer stops on the first caller's token either. The callers don't catch `OperationCanceledException`: a 1.7.6 attempt did, and
  also caught upstream timeouts, so every waiting caller re-sent its request to an upstream that was
  already too slow.
- **A new endpoint could answer 404 until its file was saved a second time.** Fixed in 1.7.10. The
  services built from the configuration (endpoints, gateway routes, API keys, OIDC providers, static
  files, OpenAPI, the configuration path monitor) were woken by the same reload signal as
  `SettingsEncryptionService`, whose merged copy they read. That signal wakes its newest subscriber
  first, and the order flips on every reload, so on every other save they rebuilt from the previous
  configuration: the route table (endpoints added, removed or re-routed) and the other services' lists
  lagged one save behind. The service now gives them its own signal, sent once its copy holds the new
  configuration.
- **Without settings encryption, nothing reloaded.** Fixed in 1.7.10. On Linux and macOS without
  `settings_encryption:data_protection_key_path`, `SettingsEncryptionService` built its copy once and
  never subscribed to changes, so every edit to a configuration file needed a restart there. It
  follows the files in every mode now.
- **The reload gates could close each other.** Changed in 1.7.10. Each service guarded its reload with
  a gate that skipped a second caller, and six of the seven opened it inside the `try` whose `finally`
  closed it, so a skipped call would also have closed the gate of the one still running. It never
  happened: `ChangeToken.OnChange` never runs one subscription's reload twice at once. The services use
  a lock now, which waits instead of skipping.
- **Removing the last gateway route left it matching.** Fixed in 1.7.10. With no `<routes>` left, the
  gateway's resolver kept its previous routes, so a request to the removed route answered `500`
  (``Improper route settings (missing `url`)``) instead of `404`. The endpoint resolver kept its routes
  in the same way, but nothing reached them: with no `<queries>` left, the engine answers before it asks
  the resolver (a static file, or `404`). Both resolvers now clear their routes.
- **A `404` from a file download named the stored `relative_path`.** Fixed in 1.7.10. The message showed
  the caller the path the query returned, which tells how the store is laid out. It names the route
  only; each case that resolves the path already logged it.
- **A status raised from SQL was logged as an error with a stack trace.** Fixed in 1.7.10. A `THROW
  50400` that rejects a caller's input is the query's answer, not a failure of the engine, but it
  wrote an `Error` entry with the full stack on every request, and a second one from the upload step
  when it was raised after a streamed result's rows on an upload route. It is now one line with the
  route, the status and the message, at
  `Information` for a 4xx and at `Warning` for any other status. A raised
  status that can't reach the caller, because the response has started, is still an `Error`.
- **The sample `settings.xml` set a global `db_command_timeout` of 30 seconds.** Changed in 1.7.9.
  Because it was always set, every query got a command timeout of 30 seconds, which overrode any
  timeout in a connection string (`Command Timeout=120` for SQL Server). It is now a commented-out
  example, so a query without its own timeout uses its connection string's, or the provider's default.
- **A name used both in a block's JSON comment and outside a string was not escaped in the comment.**
  Fixed in 1.7.9. Markers in a comment, and markers that start inside another marker, were skipped
  when the engine worked out where each name sits, so a name also used outside strings counted as
  outside-only and got its JSON value unescaped everywhere. A caller's `*/ } //` then closed the
  comment and the block, and the block's keys after the comment (a `skip`, say) were dropped. Such a
  name now counts as inside a string, as topic 17 says: it is escaped everywhere and a warning is logged.
- **A comment or a trailing comma in an `{http{}}` block made the engine ignore its `skip` and
  `no_wait`.** Fixed in 1.7.9. The call itself was read leniently, but those two properties were read
  strictly, so a parse failure counted as false: a call the author meant to skip went out, and a
  `no_wait` call was waited for. Both are read like the call now.
- **Caller text was logged as sent in four more places.** Fixed in 1.7.9. An unvalidated token's
  issuer that matched none of a route's providers (Debug), a provider hint header that names no allowed
  provider (Warning), a header name the gateway can't forward (Warning), and the `Origin` header the CORS
  check reads (an `Origin` that isn't a URL was an Error entry with a stack trace on every request). The
  server accepts a vertical tab, ESC and U+2028 in a header, so a caller could break a log line or send
  terminal codes. All four go through `LogText.Escape` now, and a malformed `Origin` is logged at Debug.
- **A file or count query that returns several rows loses an error raised after them.** Kept by
  design (decided 2026-10-10). The engine takes their first row by reading two, and closing the reader
  discards the rest of the batch, the error included. Such a query should return one row; reading a
  wrong one to its end would spend the memory and time the 512 MB, 1 vCPU footprint can't spare.
  AGENTS.md rule 2 and errors.md state the exception.
- **One `<path>` under `<additional_configurations>` was never loaded.** Fixed in 1.7.9. The XML reader
  gives a single element as a value of its own and numbers only repeated ones, and the engine read the
  list as numbered children only. So a `settings.xml` listing one file started without it (and its
  endpoints), and its encrypted sections weren't encrypted. Both shapes are read now, so a single
  listed file that is missing now stops start-up, as it already did when several were listed.
- **The TLS walkthrough ran the engine as Development.** Fixed in 1.7.9. Step 5's `dotnet run
  --environment Production` doesn't reach the engine with the .NET 10 SDK, so the launch profile's
  `Development` stayed in force and `appsettings.Production.json` was never read. The page now says
  `dotnet run -- --environment Production`.
- **The request's route was logged as decoded.** Fixed in 1.7.9. A `%0A` in the path put a line break
  into every log line that names the route. Step1 now stores it with control characters, the Unicode
  line separators and `%` percent-encoded (`LogText.Escape`), so it stays on one line and two paths
  never look alike.
- **The gateway docs and the shipped `settings.xml` named settings the engine never reads.** Fixed in
  1.7.9. Topic 08 showed `<n>` for `<name>` in `<applied_headers>` and `<ignore_certificate_errors>`
  for `<ignore_target_route_certificate_errors>`; tutorial 12 and `settings.xml` used
  `<headers_to_exclude_from_routing>` and `<ignore_certificate_errors_when_routing>` for the global
  `<excluded_headers>` and `<ignore_target_route_certificate_errors>`, and said a route's list adds to
  the global one (it replaces it). The shipped `settings.xml` now sets the global `excluded_headers` to
  `Host`, as it meant to. Two `<header>` elements in `<applied_headers>` also applied neither header,
  because the configuration reader numbers repeated siblings; each is applied now.
- **With the header and query-string patterns overridden to one string, a header beat the body.** Fixed
  in 1.7.9 by Com.H.Data.Common 10.1.0.12. Sources with the exact same pattern string were read together
  in the place of the last of them, so the headers moved past the body. The body, listed between them,
  now keeps its place.
- **A value from an earlier query could pull a setting into an `{http{}}` call.** Fixed in 1.7.9 by
  Com.H.Data.Common 10.1.0.12. The block was filled one source at a time, and each source read the
  values written in before it. So a `{s{name}}` or `{{name}}` marker inside a value an earlier query
  returned (a caller's field it passed through, say) was filled too, and a caller could send a
  `<vars>` value to the call's target. Values are now written in as they are. One name under two
  markers of one pattern in a block (`{{id}}` and `{j{id}}`) also gets its value at both; only the
  first one found used to be filled, and the other, depending on their order, was left as written,
  emptied, or filled from a lower-ranked source.
- **A skipped or `no_wait` `{http{}}` call could get another query's response.** Fixed in 1.7.9. Each
  query of a chain, and the main query after a `count_query`, numbered its calls from 1, and every
  query's responses were read with one marker pattern. So a skipped or `no_wait` call in a later
  query got the response of the earlier query's call with the same number instead of `NULL`. Calls
  are now numbered across the request.
- **Some input names made the query fail, or bound the wrong value.** Fixed in 1.7.9 by
  Com.H.Data.Common 10.1.0.11. The generated parameter name kept `@` and characters outside ASCII,
  which SQLite (`@`) and SQL Server (`€`, `°`, typographic quotes) reject, and two markers could get
  the same name: `{{first name}}` with `{{first-name}}`, `{{Email}}` with `{{email}}` on SQL Server,
  and `{{email}}` with `{j{email}}`. SQL Server and SQLite refused such a query. PostgreSQL ran it,
  and both placeholders got the first one's value, so rows written that way may hold the wrong one.
  Generated names now hold only ASCII letters, digits and underscores and are unique ignoring case.
- **A download's `http` URL reached the caller.** Fixed in 1.7.9. The `http` source's error
  messages held the full URL, a signed query string included. The caller's message no longer names
  it. No log line in the engine shows a URL's query string, fragment or user info any more: the
  download lines, the `{http{}}` lines (which also wrote a key in the url string at Information on
  every successful call, now at Debug) and the prepared-call Debug line (which held the whole filled
  JSON, headers included, and now holds its length), and the OIDC lines naming the authority and the
  key-set URL. A value that isn't an http or https URL is
  logged as `(not a network URL)`: .NET reads `//host/a?k=`, `/a?k=` (Linux), Windows paths and ftp
  URLs with `?` as an ordinary character. The executor's Debug request line names headers without
  their values; its pattern-based redaction missed names like `Ocp-Apim-Subscription-Key`.
- **The gateway and `{http{}}` calls shared cookies between callers.** Fixed in 1.7.9. Their pooled
  handlers kept a cookie jar, so a `Set-Cookie` from the target while serving one caller went out with
  the next caller's request, merged into a gateway caller's own `Cookie` header. No engine client keeps
  cookies now. A cached gateway route also stored the target's `Set-Cookie` and replayed it to every
  caller; a response with a `Set-Cookie` is no longer cached.
- **The configuration sources were read out of the documented order.** Fixed in 1.7.9.
  `appsettings.json` was added again after `settings.xml`, so it beat `settings.xml`, the environment
  file and command-line arguments, and its `Debug` level beat `appsettings.Production.json`'s
  `Information`. It is now the lowest layer, command-line arguments the highest, and the shipped level
  is `Information`.
- **A key in an encrypted section ignored environment variables and command-line arguments.** Fixed
  in 1.7.9. The encryption service laid the values it decrypted from the XML files over every source,
  so a `ConnectionStrings__default` environment variable lost to an encrypted `<default>`. It now
  decrypts only encrypted text, an override written in encrypted form included.
- **The HTTPS start-up check looked for the certificate beside the executable.** Fixed in 1.7.9.
  Kestrel loads a relative `Kestrel:Endpoints:Https:Certificate:Path` from the working directory, so
  `dotnet run` with the certificate in the project's `config/certs` (the TLS walkthrough) skipped
  HTTPS, and a release started elsewhere with the certificate beside the executable failed to start.
  The check now looks where Kestrel does, and its warning names the full path.
- **A failed OIDC discovery or key-set fetch logged the whole URL.** Fixed in 1.7.9. IdentityModel's
  exception message holds the address with its query string (an Azure AD B2C `?p=` policy); the
  engine now rethrows it with the address as the URL rule logs it.
- **Every GET threw and caught a JsonException.** Fixed in 1.7.9. A request without a Content-Type
  counts as JSON, so its empty body was parsed. The parse is skipped when the server knows there is no
  body.
- **OIDC signing keys were fetched twice per discovery, through a static HttpClient, and http
  downloads opened a new HttpClient each.** Fixed in 1.7.9: both use pooled IHttpClientFactory
  clients, which the tests answer in process. A download's client no longer keeps the cookies a
  redirect sets for its next hop (see Open). The key set is parsed before it is cached, so a
  provider's maintenance page or an empty key set is fetched again on the next request instead of
  breaking sign-in for the cache's 24 hours.
- **A name with a space couldn't be listed in `mandatory_parameters`.** The list was split on
  spaces too, so `first name` required `first` and `name`. Fixed in 1.7.8: commas and line breaks
  separate names, or `|` and line breaks when the list contains a `|` (for names with commas). The
  OpenAPI builder now splits the list the same way, and the missing-parameters message joins names
  with the list's separator. Cache `<invalidators>` use the same split, except that a name with a
  comma, a space or `;` stays whole only when one of the route's queries uses it in a marker;
  otherwise it is split on them as before, so an old list keeps every input in the key. Gateway
  routes run no query, so their names are always split that way.
- **The OpenAPI schema of a route without `response_structure` said it was always an array.** From
  1.7.8 it is one object or an array of them (`anyOf`); a `single` route, which showed the object
  alone, gets the same.
- **`single`, file and count-query results were read to their end.** 1.7.7 read every row of them
  to surface an error raised after the rows, building an object for each, so a 3,000,000-row
  SQLite `single` went from 0.02 s to 2.7 s. Fixed in 1.7.8: `single` is read as the default
  shape (it was mostly set on queries that return one row anyway, and logs a warning), and file
  and count queries take their first row by reading two.
- **A global `<response_structure>` under `<settings>` was half-supported.** The controller
  applied it to every route without its own tag, but it was undocumented and the file download
  middleware ignored it, so a store download that relied on a global `file` answered 404. Removed
  in 1.7.8: it is not read, the start-up log reports one that is still set, and `auto` (the only
  reason to write a tag for the default shape) is read as the tag left out, like `single`.
- **An error raised after the rows was lost.** Fixed in 1.7.7, with Com.H.Data.Common 10.1.0.10.
  The library closed the reader once the first result set's rows ran out, and closing discards an
  error raised after them (SQL Server's THROW after a SELECT, or a later statement that fails), so
  the request succeeded and its uploads were kept. 10.1.0.10 reads the batch to its end once every
  row has been read. The engine now reads `single`, file and count-query results to the end; reads
  the first two rows of `array` and count-query data inside the controller, as `auto` did; maps an
  error that surfaces while MVC streams a longer result in Step6 (the status the SQL asked for, or
  the generic 400) when nothing has been sent; and aborts the connection when something has, instead
  of ending a cut-off 200 (also when MVC has buffered part of the body without sending it, which
  used to give the error status with invalid JSON). With 10.1.0.10 a one-row chain result whose
  only column has no name is a bare value; the engine keeps it out of the `{{column}}` sources, so
  a JSON-object string there can't fill the next query's `{{name}}`. See
  [LateDbErrorTests.cs](DBToRestAPI.Tests/LateDbErrorTests.cs) and
  [PipelineTests.cs](DBToRestAPI.Tests/PipelineTests.cs).

- **A write to a cached endpoint was answered from the cache.** Fixed in 1.7.6. The key of a
  cached database endpoint was only its element name plus the `<invalidators>` values. An endpoint
  with no `<verb>` answers every verb, so a POST, PUT or DELETE got the cached GET response and its
  SQL never ran, while the caller saw a success. A route value missing from `<invalidators>`
  shared one entry across all ids. Now only GET and HEAD read or write the cache, on database
  endpoints and on API gateway routes (whose key never held the request body), and the key is the
  endpoint's configuration path, the verb, the hashed route values and the invalidators (the
  values, not the raw path, so `Items//1` and `items/1` share an entry). The gateway route is
  hashed in its key too, so a path carrying `|` or `=` can't build another request's key, and a
  repeated query value (`?tag=a&tag=b`) no longer shares the entry of `?tag=a,b`. Each invalidator
  value is labelled with its source, so `?category=toys` sent with a `category: books` header can't
  fill the books entry, and gateway query-string names keep their spelling. A gateway
  response whose status is in `exclude_status_codes_from_cache` used to be stored as an empty
  entry, so later requests got an empty 200 without being forwarded; it is no longer stored.
  See [CacheVerbAndRouteTests.cs](DBToRestAPI.Tests/CacheVerbAndRouteTests.cs).

- **A raw marker in an `{http{}}` block could take a request value and redirect the call.** Fixed in
  1.7.6. A marker outside a JSON string (`"body": {{payload}}`) was inserted as raw text. When a
  chained query's column was `NULL` (or it returned no row or several), a request field with the
  same name filled it, so `1, "url": "https://attacker.example"` added a second `url`, and the
  call went there with the block's credential headers. Now such a value must be exactly one JSON
  value (read as leniently as the block: comments and trailing commas allowed) and is inserted as
  compact JSON, or it is inserted as a JSON string. Booleans become `true`/`false` (their .NET text,
  `True`, was invalid JSON) and numbers use the invariant culture. Markers are found with the
  patterns the route actually uses, so an overridden delimiter such as `||id||` inside a string is
  escaped too. A marker used both inside and outside a string, in a comment, or brought in by an
  earlier value is escaped so it can't change the structure anywhere. See
  [EmbeddedHttpTemplateTests.cs](DBToRestAPI.Tests/HttpExecutor/EmbeddedHttpTemplateTests.cs).

- **Renamed token claims were missing, and `required_scopes` rejected `scp` tokens.** Fixed in
  1.7.6. .NET renames `sub`, `scp`, `oid`, `tid`, `given_name`, `family_name` and others on the way
  in. So `{auth{sub}}` and the other short names were empty, `required_scopes` gave every Entra ID
  or Okta token a 403, `user_id` never fell back to `oid`, and the UserInfo fallback ran for every
  new token because it looked for `given_name` under its short name only. The UserInfo values it
  added were `JsonElement`s that no SQL driver binds, so a query using one failed with the generic
  400. Now every claim is exposed under its .NET type and the name the token used, look-ups match
  either name, UserInfo values are text and never replace a claim the token has, and `user_id`,
  email, name, roles and scopes are read from the token before the UserInfo merge (so a B2C
  token's `emails` beats a UserInfo `email`). `{auth{scp}}` and `{auth{scope}}` hold every scope,
  space-separated, also when Okta sends them as an array. See
  [Step4ClaimNamesTests.cs](DBToRestAPI.Tests/Step4ClaimNamesTests.cs).

- **An empty `debug_mode_header_value` turned debug mode on for an empty header.** Fixed in 1.7.6.
  A missing or blank setting now means debug mode is off, and the header must match exactly. See
  [DebugModeTests.cs](DBToRestAPI.Tests/DebugModeTests.cs).

- **A caller could set the fields an upload entry gets from the engine.** Fixed in 1.7.5. A caller
  could hand the query a files array of their own, marked `is_new_upload` and pointing at any file
  in the store, so an update query that inserts new uploads would record a row for someone else's
  file. There were four ways in. The files field in a query string parameter or a header (both
  answer to the same `{{attachments}}`), or a second copy in the body in another case, replaced
  the array the engine built. An entry that brought no file (an existing file in a partial update)
  reached the query exactly as sent. A stored entry copied the caller's own fields after the
  engine's, so it could carry a second `relative_path` or `is_new_upload`. And a multipart part's
  `mime_type` was its own Content-Type header. Now the files field comes only from the body, once;
  an existing entry arrives without `relative_path`, `extension`, `mime_type`, `size`,
  `backend_temp_file_path`, `is_new_upload` or the content field, and the query matches it by `id`;
  a stored entry drops the caller's values for those names; and `mime_type` always comes from the
  file name. Upgrade note: a query copied from the old docs that inserts every entry should add
  `WHERE JSON_VALUE(value, '$.is_new_upload') = 'true'`, as the docs now do. A multipart upload
  with `pass_files_content_to_query` writes the content under the configured content field name,
  as a JSON upload does, instead of always `base64_content`. See
  [UploadEngineFieldsTests.cs](DBToRestAPI.Tests/UploadEngineFieldsTests.cs).

- **A route with `<api_keys>` instead of `<api_keys_collections>` was open without a word.** Fixed
  in 1.7.5. Only `<api_keys_collections>` protects a route and nothing reads a route's
  `<api_keys>`, so such a route answered callers without a key while reading as protected. The
  engine now logs a warning naming each such route, and each `<api_keys_collections>` that names
  no collection (keys pasted in from api_keys.xml, say), at start-up and when the configuration is
  reloaded. The API key and gateway docs say which tag protects a route. (1.7.12 removed the
  warning, with the other configuration warnings.)

- **Uploads could store something other than what was sent.** Fixed in 1.7.4. Base64 content
  without its trailing `=` padding lost its last one or two bytes (the streaming decoder dropped
  an incomplete last group), and two multipart parts with the same name were both stored with the
  first part's bytes. Unpadded base64 is now completed before decoding (a length no base64 can
  have is a 400), each multipart entry claims its own part in order, and a part no entry names is
  a 400 instead of being ignored. The decoder no longer uses FromBase64Transform, which could
  also throw (a 500) on valid content over 8 KB with whitespace near its chunk boundaries. The
  multipart path no longer swallows failures: a form that can't be read is a 400, and an
  unexpected failure is a logged 500 instead of a request that runs on without its files and
  reports success. A form content type with no body is still read as no parameters.
  See [UploadIntegrityTests.cs](DBToRestAPI.Tests/UploadIntegrityTests.cs).

- **An invalid upload returned an empty 500, or was silently dropped.** Fixed in 1.7.3. Upload
  validation errors (file name, extension, size, count, content that is not base64, metadata
  shape) escaped parameter building, so a JSON upload got an empty 500. The multipart path
  swallowed them, along with malformed metadata and a body over the size limit, so the request
  ran on with every form field null and could report success. They are now a
  `RequestValidationException`, which both paths let through and which becomes a 400 that says
  why. A body over `max_payload_size_in_bytes` is a 413 for JSON and forms alike, a form over a
  `FormOptions` limit is a 400, and anything else that fails while reading parameters is a JSON
  500 with the error code. See `ParametersBuilder.GetParamsOrErrorAsync` in
  [ParametersBuilder.cs](DBToRestAPI/Services/ParametersBuilder.cs).

- **Keep a download's `relative_path` inside its store.** Shipped in 1.7.2. Before, the path a
  download query returned was joined to the store's `base_path` unchecked, so `..`, an absolute
  path, or on Windows a UNC or device path (`\\host\share\...`, `\??\UNC\...`) read any file the
  app's account could reach, on that machine or another one. Local and SFTP stores now refuse
  anything outside `base_path` with the same 404 as a missing file, and neither 404 echoes the
  resolved path any more. A local store without a `base_path` no longer falls back to the app's
  own folder. The upload side got the same check, and on Linux a file name containing `\` can
  no longer turn into a path outside the store. See
  [FileStorePath.cs](DBToRestAPI/Services/FileStorePath.cs) and
  [docs/topics/10-file-downloads.md](docs/topics/10-file-downloads.md).

- **SFTP uploads failed for every new file.** Fixed in Com.H.Net.Ssh 10.1.1, which 1.7.2
  references. In 10.1.0, `SFtpClient.Exists` and `ExistsAsync` threw `SftpPathNotFoundException`
  for a missing path instead of returning false, so the existence check that runs before each
  SFTP upload (with the default `overwrite_existing_files=false`) failed on every new file.

- **OpenAPI `429` annotation for rate-limited operations.** Shipped in 1.7.0 together with the
  rate-limiting feature itself. See [docs/topics/18-rate-limiting.md](docs/topics/18-rate-limiting.md)
  and [docs/topics/20-openapi.md](docs/topics/20-openapi.md).
