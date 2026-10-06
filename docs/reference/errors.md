---
title: Errors, status codes and rollback
summary: How a query rejects a request with a real HTTP status, the exact response body for the statuses the engine sends, and when uploaded files are rolled back.
keywords: [THROW 50404, THROW 50400, RAISE EXCEPTION, SIGNAL SQLSTATE, RAISE_APPLICATION_ERROR, RAISERROR, error_number, generic_error_message, debug_mode_header_value, debug-mode, success_status_code, mandatory_parameters, rollback, root_node, 400, 401, 403, 404, 409, 413, 429, 500, 204]
applies_to: 1.7.6
---

# Errors, status codes and rollback

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt).

Use this page whenever an endpoint must reject a request, or a client must tell success from failure. It covers database-query endpoints, uploads and downloads included. API gateway routes behave differently ([below](#api-gateway-routes)).

## The rules

1. **Reject with a status, never with `200` and a flag.** Raise an error numbered `50000 + status` from SQL (for example `THROW 50400, 'Category is invalid', 1;`) and the caller gets that HTTP status. Don't return `200` with a `status` or `error` column.
2. **Raise before the first statement that returns rows.** The engine reads only the first result set. On SQL Server and SQLite, an error raised after it (for example `SELECT ...; IF @@ROWCOUNT = 0 THROW ...`) is lost: the caller gets a success, and uploads are kept. Check first, then `SELECT`.
3. **Uploaded files clean themselves up.** When the final status is `400` or higher, the engine deletes every file the request stored. Write no cleanup code, and don't catch the error in SQL to return a success instead.
4. **Rows don't clean themselves up.** The engine doesn't wrap your query in a transaction. Validate before any write, and put multi-statement writes in a transaction, so an error leaves no rows behind.
5. **Use `50400` to `50599`.** A number from `50000` to `50399` gives a status below 400. That is not treated as an error and doesn't roll back uploads, and `50000`-`50099` produce an invalid status line.
6. **`mandatory_parameters` only checks that a name is present.** The name can come from any source: body, form, query string, route, a header, a JWT claim or `<vars>`. The comparison is case-sensitive, and an empty or `null` value passes. Validate values in SQL, with `NULL` in mind (`NULL NOT IN (...)` is not true).
7. **Clients read `message`.** Every error body the engine writes is JSON with `"success": false` and a `message` string. Proxy routes, IIS limits and a cut-off stream can return other bodies, so parse defensively.

## Raising an error from SQL

The engine maps a database error numbered `n`, with `50000 <= n < 51000`, to HTTP status `n - 50000`. The body is:

```json
{ "success": false, "message": "Category is invalid", "error_number": 400 }
```

`error_number` is the HTTP status, not the database number. `message` is the database driver's error text, so it reaches the caller. Keep it user-facing and keep internals out of it.

| Database | How to raise | Caller gets | Notes |
|---|---|---|---|
| SQL Server | `THROW 50404, 'Not found', 1;` | 404, `"Not found"` | End the previous statement with `;`. `IF <condition> THROW ...;` works. Don't use `RAISERROR('text', 16, 1)`: it reports 50000, which becomes an invalid status line (`0`), so clients see a network error and uploads are kept. |
| PostgreSQL | `RAISE EXCEPTION '[50404] Not found';` in a procedure you `CALL` | 404, `"P0001:  Not found"` | A `DO` block can't see request parameters. Put the check in a procedure and `CALL` it with them (example below). Not a function: `SELECT fn(...)` returns a row, which would replace your response. The `P0001:` prefix is a known issue. |
| MySQL | `SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';` | 404, `"Not found"` | A bare `SIGNAL` works anywhere, but MySQL accepts `IF ... END IF` only in stored programs, so put a conditional check in a procedure and `CALL` it. MariaDB 10.1.1+ also accepts `IF` in a plain batch. |
| SQLite | `SELECT RAISE(ABORT, '[50404] Not found')`, **inside a trigger only** | 404, `"SQLite Error 19: ' Not found'."` | Outside a trigger SQLite refuses `RAISE()`, and the caller gets the generic 400. |
| Oracle | `BEGIN RAISE_APPLICATION_ERROR(-20404, 'Not found'); END;` | the generic 400 | Known issue: the mapping expects a negative error number, but the Oracle driver reports a positive one. |
| IBM DB2 | `SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';` in a compound statement or procedure | the generic 400 | Known issue: the engine reads the first `[nnnnn]` in the message, which is the SQLSTATE. |
| ODBC / OleDb | any | the generic 400 | Not mapped. |

PostgreSQL, SQLite and DB2 are matched on the first `[nnnnn]` (any five digits) in the error text. If that number is outside 50000-50999, the error is not mapped, even when a `[5xxxx]` follows. Any error whose first token is in range maps to a status, including a database error that echoes a value the caller sent. Validate the format of caller input before it can reach an error message. On PostgreSQL, a failed `CAST` repeats the input in its message, so cast only input you have already checked.

A validation block on SQL Server. `IS NULL` comes first, because `NULL NOT IN (...)` is not true:

```sql
DECLARE @category NVARCHAR(MAX) = {{category}};

IF @category IS NULL OR @category NOT IN ('billing', 'technical', 'other')
  THROW 50400, 'Category must be billing, technical or other.', 1;
```

The same check on PostgreSQL, as a procedure the endpoint calls:

```sql
-- once, in the database
CREATE OR REPLACE PROCEDURE check_category(c text) LANGUAGE plpgsql AS $$
BEGIN
  IF c IS NULL OR c NOT IN ('billing', 'technical', 'other') THEN
    RAISE EXCEPTION '[50400] Category must be billing, technical or other.';
  END IF;
END $$;

-- in the endpoint's query, before any statement that returns rows
CALL check_category({{category}});
```

In a [query chain](../topics/14-query-chaining.md), an error in any query stops the chain and maps the same way. Earlier queries in the chain have already run and committed.

## Choosing a status

Pick the status by what the client should do next:

| Situation | Raise | The client |
|---|---|---|
| Missing or malformed input, or a value outside the allowed list | `50400`, naming the field and the rule | shows the message next to the form |
| Session ended (signed out, revoked) | `50401` | signs in again |
| Signed in but not allowed: inactive account, missing role, an action this role can't take | `50403` | shows the message and doesn't retry |
| Not found, including a record the caller may not see and a malformed id (`TRY_CONVERT` gives `NULL`) | `50404` | shows "not found" |
| A duplicate, a transition the current status doesn't allow, an edit that lost a race | `50409` | reloads, then retries |
| Another system answered with an error | `50502` | retries later |
| Another system was unreachable or timed out | `50503` | retries later |
| A check that worked and said no ("reference not valid") | no error: return `200` with the answer | shows the answer |

Prefer `502` or `503` to `500` for failures of other systems: a `500` hides where the problem is. Keep messages user-facing, and never copy another system's response or SQL text into them.

## Statuses you will meet

All bodies are JSON. "Rolled back" means files this request stored are deleted.

| Status | When | Body | Uploaded files |
|---|---|---|---|
| success code | Query succeeded. The code is `success_status_code` (route, then global), default `200`. | Rows, shaped by `response_structure` ([response formats](../topics/05-response-formats.md)) | Kept |
| `204` | `single` or `auto` route, no row, success code 200 | Empty | Kept |
| other success code | `single` or `auto` route, no row, success code not 200 (for example 201) | Empty (`Content-Length: 0`) | Kept |
| success code | `single` or `auto` route, no row, `root_node` set | `{"<root_node>": null}` | Kept |
| success code | `single` or `auto` route, no row, `<cache>` on the route | `null` | Kept |
| `400` | A name in `mandatory_parameters` is missing | `{"success":false,"message":"Missing mandatory parameters: email,category"}` | None were stored |
| `400` | Upload refused: file name, extension, size, count, base64, or files-field shape ([uploads](../topics/09-file-uploads.md#errors)) | ``{"success":false,"message":"File extension `.exe` is not permitted."}`` | None were stored |
| `400` | Malformed form body, or a form over an ASP.NET Core limit (a field over 4 MiB, more than 1024 fields) | `{"success":false,"message":"The form data could not be read."}` | None were stored |
| `400`-`599` | SQL raised `50400`-`50599` | `{"success":false,"message":"...","error_number":404}` | Rolled back |
| `400` | Any other database error: constraint violation, timeout, deadlock, syntax error, unreachable database | `{"success":false,"message":"An error occurred while processing your request."}`, from `generic_error_message` | Rolled back |
| `400` | The client disconnected (it never sees the response) | `Request was cancelled` before storing; the generic message if the query was running | Rolled back if anything was stored |
| `401` | API key missing or wrong | `API key was not provided` / `Unauthorized client` | None were stored |
| `401` | JWT missing or invalid | `Authorization header is required` / `Invalid authorization header format. Use: Bearer <token>` / `Bearer token is required` / `Token has expired` / `Invalid token signature` / `Invalid token` | None were stored |
| `403` | JWT valid but lacks required scopes or roles | `Insufficient permissions` | None were stored |
| `404` | No route matches | ``API Endpoint `x` not found`` | - |
| `404` | Download: no row, file missing, or path outside the store ([downloads](../topics/10-file-downloads.md#errors)) | `{"success":false,"message":"..."}` | - |
| `409` | Upload target already exists in a non-optional store, and `overwrite_existing_files` is false. In an optional store, the engine stops writing to that store at the first existing file (so that file and the request's later files are missing from it), and the request goes on. | `{"success":false,"message":"One or more files already exist in the target file store(s). Please rename the file(s) and try again."}` | Rolled back |
| `413` | Request body larger than `max_payload_size_in_bytes` | `{"success":false,"message":"The request body could not be read."}` | None were stored |
| `429` | Rate limit hit | `{"success":false,"message":"...","retry_after_seconds":30}`, plus a `Retry-After` header | None were stored |
| `500` | Writing to a non-optional store failed, or a database error while an `array` or `auto` route's rows were being sent, before any byte went out | `{"success":false,"message":"An unexpected error occurred processing your request"}` | Rolled back |
| `500` | Reading the request failed unexpectedly | ``An unexpected error occurred processing your request (Contact your service provider support and provide them with error code `...`)`` | None were stored |
| `500` | Engine configuration error (route, auth provider) | `Improper service setup. (...)`, `Authorization configuration error. (...)` and similar | None were stored |
| `500` | The request sent the `debug-mode` header matching `debug_mode_header_value`, and a database error was not mapped | `{"success":false,"message":"Query 1 of 1 failed: ...","stack_trace":"...","inner_exception":"..."}` | Rolled back |

A few behaviours to design for:

- **A `400` can be the caller's mistake or a server-side database failure.** Show `message`, but treat a `400` whose message equals `generic_error_message` as a server problem.
- **An `array` route streams its rows.** A database error while rows are read gives the `500` above if nothing was sent yet. Once bytes have gone out the status can't change, so the client gets a `200` with a cut-off body. Stored files are rolled back either way.
- **Read `message` for display.** Use the status, not the text, for logic.

## When uploads are rolled back

The engine stores uploaded files before the query runs, then decides after the response is produced:

- **Final status `400` or higher:** every file this request stored is deleted from every store, optional stores included. Folders are left in place.
- **An exception after storing began** (a store failing, an error while the response is written): the same.
- **Final status below `400`:** files stay. That includes a mapped error from `50000` to `50399`.
- **Refused before storing** (auth, rate limit, `mandatory_parameters`, upload validation, `413`): nothing was stored, so there is nothing to delete.

Rollback has limits. Know them before relying on it:

- **It covers files, not rows.** Statements that ran before the error, and earlier queries in a chain, stay committed unless your SQL used a transaction. An error after your `COMMIT` (for example in a final `SELECT`) still deletes the files while the rows stay.
- **Deleting a row never deletes its file.** The engine deletes stored files only during rollback. When an update query removes an attachment's row, the file stays in the store.
- **With `overwrite_existing_files` on,** a failed request deletes the file it overwrote. The previous content is lost, not restored.
- **A write that failed part-way** (disk full, connection lost mid-upload) may leave a partial file behind.
- **A client that disconnects after the SQL committed** still gets the files deleted, so committed rows can point at missing files.

## Client pattern

```js
let res, body;
try {
  res = await fetch('/support-requests', { method: 'POST', body: formData });
  const text = await res.text();               // empty for 204, or a non-200 success with no row
  body = text ? JSON.parse(text) : null;
} catch {
  // network failure, a cut-off stream, or a body that isn't JSON (an IIS or proxy error page)
  return showError(res && !res.ok ? `Request failed (${res.status})` : 'The request could not be completed. Please try again.');
}
if (res.ok) {
  // success: body may be null
} else {
  showError(body?.message ?? `Request failed (${res.status})`);
}
```

## Do and don't

```sql
-- Don't: a 200 that means failure. Files stay stored, and clients must parse a flag.
IF @category IS NULL OR @category NOT IN ('billing', 'technical', 'other')
BEGIN
  SELECT 'error' AS status, 'Invalid category' AS message;
  RETURN;
END

-- Do: a real 400, raised before anything returns rows. The engine rolls back stored files.
IF @category IS NULL OR @category NOT IN ('billing', 'technical', 'other')
  THROW 50400, 'Category must be billing, technical or other.', 1;
```

```sql
-- Don't: SELECT first and raise afterwards. The engine has already taken the first result set,
-- so the THROW is lost and the caller gets a success.
SELECT id, name FROM records WHERE id = @id;
IF @@ROWCOUNT = 0 THROW 50404, 'Not found', 1;

-- Do: check first, then SELECT.
IF NOT EXISTS (SELECT 1 FROM records WHERE id = @id) THROW 50404, 'Not found', 1;
SELECT id, name FROM records WHERE id = @id;
```

```sql
-- Don't: catch and swallow the error. The request succeeds and nothing is rolled back.
BEGIN TRY
  INSERT INTO requests (id, email) VALUES (@id, @email);
END TRY
BEGIN CATCH
  SELECT 'failed' AS status;
END CATCH

-- Do: let it fail (generic 400), or rethrow it as a mapped error.
BEGIN TRY
  INSERT INTO requests (id, email) VALUES (@id, @email);
END TRY
BEGIN CATCH
  IF ERROR_NUMBER() IN (2601, 2627) THROW 50409, 'This request was already submitted.', 1;
  THROW;
END CATCH
```

## Settings

| Setting | Where | Default | Effect |
|---|---|---|---|
| `success_status_code` | route, then `settings.xml` | `200` | Status of a successful response. Doesn't apply to file downloads. Don't set it to 400 or higher: every request would roll back its uploads. |
| `generic_error_message` | `settings.xml` | `An error occurred while processing your request.` | Message for unmapped database errors. |
| `debug_mode_header_value` | `settings.xml` | none; the shipped sample sets `54321` | A request whose `debug-mode` header equals it gets exception messages and stack traces. **To turn debug mode off, delete the element.** An empty value also turns it off (from 1.7.6; before that, an empty value was matched by an empty `debug-mode` header). If you keep it, use a long random secret. |
| `max_payload_size_in_bytes` | `settings.xml` root | `314572800` (300 MiB); the shipped sample sets `367001600` (350 MiB) | Body size limit (413). Read at start-up: restart after changing it. It applies only when the engine runs on Kestrel (on its own, in a container, or behind a reverse proxy). Under IIS's default in-process hosting it has no effect: IIS's `maxAllowedContentLength` and ASP.NET Core's `IISServerOptions.MaxRequestBodySize` (both about 30 MB) apply, and the engine can't raise the second. For larger uploads behind IIS, host out-of-process (`hostingModel="outofprocess"` in `web.config`) and raise `maxAllowedContentLength`. |

## API gateway routes

Proxy routes ([API gateway](../topics/08-api-gateway.md)) don't run a query. The upstream service's status and body pass through unchanged. A proxy failure is the generic `400`, and in debug mode a `500` whose body is a JSON string rather than an object.

## Known issues in 1.7.6

These are tracked in [TODO.md](../../TODO.md):

- Unmapped database errors are `400`, so a database outage looks like a client error.
- PostgreSQL messages keep the `P0001:` prefix.
- Oracle and DB2 custom errors are not mapped. ODBC and OleDb have no mapping.
- SQLite can raise a custom status only from a trigger.
- An error raised after the first result set is lost on SQL Server and SQLite.
- An `application/json` body that isn't valid JSON is read as having no parameters, so they are all `NULL`. With `mandatory_parameters`, that becomes a `400` "Missing mandatory parameters".
- The OpenAPI error schema (`error_message`) doesn't match the runtime body (`message`).
- Under IIS in-process hosting, `max_payload_size_in_bytes` has no effect (see [settings](#settings)).

## Related

- [File uploads](../topics/09-file-uploads.md)
- [File downloads](../topics/10-file-downloads.md)
- [Parameters](../topics/04-parameters.md)
- [Databases](../topics/13-databases.md)
