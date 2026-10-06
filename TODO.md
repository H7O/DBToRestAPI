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
- **An error raised after the first result set is lost.** Com.H.Data.Common reads only the first
  result set and never calls `NextResult`, so on SQL Server and SQLite `SELECT ...; THROW 50404 ...`
  returns a success and keeps the uploads. Fixed in Com.H.Data.Common 10.1.0.10, which moves through
  the remaining result sets once the caller has read every row. Bumping the reference is not enough
  on its own (found by review on 2026-10-06):
  - `array` (without cache), `auto` with two or more rows, and a count_query's main query enumerate
    while MVC writes the response, after `ApiController.Index` has returned. The error then reaches
    Step6, which answers 500 "An unexpected error occurred", or the client sees a 200 cut off
    mid-body if the response has started. Map it in Step6 with `TryGetCustomDbError` when the
    response hasn't started, and read the first rows inside the controller, as `auto` does.
  - `single`, file downloads and the count query stop after the first row, so they never reach
    the drain and still lose the error. Read them to the end before closing the reader.
  - 10.1.0.10 also returns a single unnamed column as one bare value per row, instead of the value
    plus an object with an empty key. The engine's chain then takes a one-row intermediate result
    as a column source: its value is parsed as JSON, so an unaliased `SELECT (... FOR JSON PATH,
    WITHOUT_ARRAY_WRAPPER)` would feed its keys into the next query's `{{name}}` ahead of request
    values. Use a single row as a column source only when it is an object, and add an engine test.
    Response shapes change too: an `auto` response of an unaliased `COUNT(*)` becomes the bare
    value instead of `[2,{"":2}]`, and `{pq{json}}` of such a query becomes `[v]`. Say so in the
    release notes.
  Keep AGENTS.md rule 2 ("raise before rows") until all of this is done, and add engine tests.
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
- **Download `http` source:** no host allow-list, and its error messages echo the full URL.
- **CORS:** the engine sends no `Access-Control-Expose-Headers`, so cross-origin pages can't read
  `Content-Disposition` on downloads.
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
  `regex:form_variables_pattern`, which the code never reads.
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
  - 11-cors, whose defaults change when an `<authorize>` section exists;
  - the webhook pages' status-polling endpoints, which are public and use sequential ids;
  - `RateLimitCallerIdentity.cs`'s comment about an `oid` fallback that never matches.
  Then add `docs/reference/tags.md` and `placeholders.md`, examples tested in CI, and docs
  bundled in release archives.

## Done

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
