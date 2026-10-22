---
title: Errors, status codes and rollback
summary: How a query rejects a request with a real HTTP status, the exact response body for the statuses the engine sends, and when uploaded files are rolled back.
keywords: [THROW 50404, THROW 50400, RAISE EXCEPTION, USING ERRCODE, SIGNAL SQLSTATE, RAISE_APPLICATION_ERROR, RAISERROR, raise_error, error_number, generic_error_message, debug_mode_header_value, debug-mode, success_status_code, buffer_rows, mandatory_parameters, rollback, root_node, 400, 401, 403, 404, 409, 413, 429, 500, 503, 504, 204]
---

# Errors, status codes and rollback

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt).

Use this page whenever an endpoint must reject a request, or a client must tell success from failure. It covers database-query endpoints, uploads and downloads included. API gateway routes behave differently ([below](#api-gateway-routes)).

## The rules

1. **Reject with a status, never with `200` and a flag.** Raise an error numbered `50000 + status` from SQL (for example `THROW 50400, 'Category is invalid', 1;`) and the caller gets that HTTP status. Don't return `200` with a `status` or `error` column.
2. **Raise before the first statement that returns rows.** Only the first result set is returned, but the engine reads the batch to its end, so an error raised after the rows is still reported with its status, and uploads are rolled back. That is certain for any result of zero rows or one, a file download's row and a count query's count included, and for a result of up to `buffer_rows` rows on a route that sets it ([response formats](../topics/05-response-formats.md#buffer_rows-read-a-result-before-writing-it)). A file download's query or a count query that returns two or more rows is read only to its second row, so an error raised after them is lost. Any other result of two or more rows is written while it is read. Once part of it has been handed to the server (about 4 KB of short values, less when a row holds long text: one value of about 1,000 characters is enough), the status can't change, so the engine cuts the connection. Reading the body then fails; on SQL Server a long result may already have sent a `200` status line before the cut. A GET or HEAD request to a route with `<cache>` reads its result whole, so it gets the status too, except after the second row of a file download's query or a count query. Check first, then `SELECT`.
3. **Uploaded files clean themselves up.** When the final status is `400` or higher, the engine deletes every file the request stored. Write no cleanup code, and don't catch the error in SQL to return a success instead.
4. **Rows don't clean themselves up.** The engine doesn't wrap your query in a transaction. Validate before any write, and put multi-statement writes in a transaction that an error rolls back, so it leaves no rows behind:
   - **SQL Server:** start with `SET XACT_ABORT ON;` and raise with `THROW`. Then an error rolls the transaction back and ends the batch. Without `XACT_ABORT`, many errors end only their own statement, so the batch goes on and its `COMMIT` keeps the rows written before them, and `RAISERROR` below severity 20 doesn't end the batch. A transaction left open keeps its locks until its pooled connection is next used.
   - **MySQL:** in the procedure that holds the transaction, roll back before the error leaves it: `DECLARE EXIT HANDLER FOR SQLEXCEPTION BEGIN ROLLBACK; RESIGNAL; END;`. A transaction left open keeps its locks until its pooled connection is next used.
   - **SQLite and PostgreSQL:** a transaction an error left open is rolled back when its connection is released.
5. **Use `50400` to `50599`.** A number from `50200` to `50399` gives that success or redirect status, which is not treated as an error and doesn't roll back uploads (unless it was raised while a streamed result was being written: that surfaces as an exception, and uploads are rolled back). Any other number, such as the `50000` that SQL Server's `RAISERROR('text', 16, 1)` reports, is not a status: the caller gets the generic `400`.
6. **`mandatory_parameters` only checks that a name is present.** The name can come from any source: body, form, query string, route, a header, a JWT claim or `<vars>`. The comparison is case-sensitive, and an empty or `null` value passes. Validate values in SQL, with `NULL` in mind (`NULL NOT IN (...)` is not true).
7. **Clients read `message`.** Every error body the engine writes is JSON with `"success": false` and a `message` string. Proxy routes, IIS limits and a cut-off stream can return other bodies, so parse defensively.

## Raising an error from SQL

The engine maps a database error numbered `50000 + status`, for a status from 200 to 599, to that HTTP status. The body is:

```json
{ "success": false, "message": "Category is invalid", "error_number": 400 }
```

`error_number` is the HTTP status, not the database number. `message` is the database driver's error text, so it reaches the caller. Keep it user-facing and keep internals out of it.

The engine logs a raised status as one line with the route, the status and the message, and no stack trace: at `Information` for a 4xx and at `Warning` for any other status ([Log level](../topics/02-configuration.md#log-level)). A status raised once the response has started, which can't reach the caller, is logged at `Error` with its stack trace, like any database error you didn't raise yourself.

| Database | How to raise | Caller gets | Notes |
|---|---|---|---|
| SQL Server | `THROW 50404, 'Not found', 1;` | 404, `"Not found"` | End the previous statement with `;`. `IF <condition> THROW ...;` works. `RAISERROR('text', 16, 1)` reports 50000, which is not a status: the caller gets the generic 400. |
| PostgreSQL | `RAISE EXCEPTION 'Not found' USING ERRCODE = '50404';` in a procedure you `CALL` | 404, `"Not found"` | A `DO` block can't see request parameters. Put the check in a procedure and `CALL` it with them (example below). Not a function: `SELECT fn(...)` returns a row, which would replace your response. `RAISE EXCEPTION '[50404] Not found';` works too. |
| MySQL | `SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';` | 404, `"Not found"` | A bare `SIGNAL` works anywhere, but MySQL accepts `IF ... END IF` only in stored programs, so put a conditional check in a procedure and `CALL` it. MariaDB 10.1.1+ also accepts `IF` in a plain batch. |
| SQLite | `raise_error(50404, 'Not found')` in a statement (examples below), or `RAISE(ABORT, '[50404] Not found')` in a trigger | 404, `"Not found"` | SQLite has no `IF`, and refuses `RAISE()` outside a trigger. `raise_error()` is a function the engine adds to every SQLite connection it opens, so the same SQL run by another program fails with `no such function`. A number that isn't `50000 + status` for a status from 200 to 599 makes it fail with a database error: the generic 400. |
| Oracle | `BEGIN RAISE_APPLICATION_ERROR(-20404, 'Not found'); END;` | 404, `"Not found"` | `-20000` minus the status: `-20404` is 404. The message is the text on the error's own line, without `ORA-20404:` and the error stack's other lines. |
| IBM DB2 | `SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';` in a compound statement, procedure or trigger | 404, `"Not found"` | The message must start with the `[nnnnn]`. Use an SQLSTATE of class `7` to `9` or `I` to `Z`, which DB2 leaves to applications. |
| ODBC / OLE DB | the database's own form | the status, with the driver's message | Read from the error's native number (SQL Server's `THROW`, MySQL's `MYSQL_ERRNO`) or from its SQLSTATE (PostgreSQL's `ERRCODE`), not from a `[50404]` in its text. Through ODBC, up to three leading `[..]` groups are removed from the message, the driver's `[vendor][driver][data source]` tags, and other words a driver adds stay. A driver that writes fewer tags (FreeTDS writes two) also removes a `[label]` your message starts with. OLE DB gives the message as the database wrote it. |

A `[nnnnn]` in the text is read only from an error your SQL raised: PostgreSQL's `RAISE` without `ERRCODE`, SQLite's `RAISE()` and `raise_error()`, DB2's `SIGNAL` (`SQL0438N`). It must start the text, spaces aside: `'[50404] Not found'`, not `'Not found [50404]'`. When it isn't `50000 + status` for a status from 200 to 599, the error is not mapped. An error the database raises itself is never mapped, even when its text repeats a value the caller sent, such as a failed `CAST` of `'[50404]'`.

Never start a raised message with a value the caller sent. With `RAISE(ABORT, NEW.name || ' is taken')`, a caller who sends the name `[50201] x` picks the status 201, and a status below 400 keeps the request's uploads. Put the status first and the caller's value after it (`'[50409] Taken: ' || NEW.name`), or use a form whose number isn't in the text: PostgreSQL's `ERRCODE`, `raise_error(50409, ...)`.

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
    RAISE EXCEPTION 'Category must be billing, technical or other.' USING ERRCODE = '50400';
  END IF;
END $$;

-- in the endpoint's query, before any statement that returns rows
CALL check_category({{category}});
```

On SQLite, put the check inside the write, so that a refused request writes nothing. `CASE` reads a `NULL` category as not matching, so `NULL` is refused too:

```sql
INSERT INTO requests (email, category)
SELECT {{email}}, {{category}}
WHERE CASE WHEN {{category}} IN ('billing', 'technical', 'other') THEN 1
           ELSE raise_error(50400, 'Category must be billing, technical or other.') END;
```

To answer `404` when a lookup finds nothing, put the check after the `SELECT`. The `SELECT` returns one row at most, so the error raised after it keeps its status (rule 2):

```sql
SELECT id, name FROM records WHERE id = {{id}};
SELECT raise_error(50404, 'Not found') WHERE NOT EXISTS (SELECT 1 FROM records WHERE id = {{id}});
```

On SQLite, don't write a check as its own `SELECT` before the statement that returns rows. The check is then the query's first result set: when it passes, its empty result is the response, and the rows after it are never returned.

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
| `204` | Route without `array`, `file` or a count query, no row, success code 200 | Empty | Kept |
| other success code | Route without `array`, `file` or a count query, no row, success code not 200 (for example 201) | Empty (`Content-Length: 0`) | Kept |
| success code | Route without `array`, `file` or a count query, no row, `root_node` set | `{"<root_node>": null}` | Kept |
| success code | Route without `array`, `file` or a count query, no row, a GET to a route with `<cache>` | `null` | Kept |
| `400` | A name in `mandatory_parameters` is missing | `{"success":false,"message":"Missing mandatory parameters: email,category"}` | None were stored |
| `400` | Upload refused: file name, extension, size, count, base64, or files-field shape ([uploads](../topics/09-file-uploads.md#errors)) | ``{"success":false,"message":"File extension `.exe` is not permitted."}`` | None were stored |
| `400` | A body sent as JSON (or without a `Content-Type`) that isn't valid JSON, comments and trailing commas included, or that is nested more than 64 levels deep (`The request body is nested more than 64 levels deep.`), or whose bytes aren't UTF-8 (`The request body is not valid UTF-8.`). A blank body has no parameters instead, and a gateway route forwards such a body as sent. | `{"success":false,"message":"The request body is not valid JSON (line 1, byte 3)."}` | None were stored |
| `400` | Malformed form body, or a form over an ASP.NET Core limit (a field over 4 MiB, more than 1024 fields) | `{"success":false,"message":"The form data could not be read."}` | None were stored |
| `400`-`599` | SQL raised `50400`-`50599` | `{"success":false,"message":"...","error_number":404}` | Rolled back |
| `400` | Any other database error: constraint violation, deadlock, a conversion that failed, syntax error | `{"success":false,"message":"An error occurred while processing your request."}`, from `generic_error_message` | Rolled back |
| `503` | The engine couldn't connect to the database (server down or unreachable, login or database refused, every pooled connection in use, a SQLite file it can't open), or the database closed the connection during the query | The same body as the `400` | Rolled back |
| `504` | The database didn't answer in time: the command timeout (`db_command_timeout`) expired, or the database gave up waiting for a lock (a `NOWAIT` lock included) or stopped the statement for its own time limit | The same body as the `400` | Rolled back |
| `400`, `499` | The client disconnected (it never sees the response) | `Request was cancelled` (`400`) if it left while its request was read; nothing (`499`) if it left while its token was checked, while its files were stored, or while its query or one of its `{http{ ... }http}` calls ran (a streamed result keeps the `200` it had sent) | Rolled back if anything was stored; once a streamed result has started, that depends on the driver |
| `401` | API key missing or wrong | `API key was not provided` / `Unauthorized client` | None were stored |
| `401` | JWT missing or invalid | `Authorization header is required` / `Invalid authorization header format. Use: Bearer <token>` / `Bearer token is required` / `Token has expired` / `Invalid token signature` / `Invalid token` | None were stored |
| `403` | JWT valid but lacks required scopes or roles | `Insufficient permissions` | None were stored |
| `404` | No route matches | ``API Endpoint `x` not found`` | - |
| `404` | Download: no row, file missing, or path outside the store ([downloads](../topics/10-file-downloads.md#errors)) | `{"success":false,"message":"..."}` | - |
| `409` | Upload target already exists in a non-optional store, and `overwrite_existing_files` is false. In an optional store, the engine stops writing to that store at the first existing file (so that file and the request's later files are missing from it), and the request goes on. | `{"success":false,"message":"One or more files already exist in the target file store(s). Please rename the file(s) and try again."}` | Rolled back |
| `413` | Request body larger than `max_payload_size_in_bytes` | `{"success":false,"message":"The request body could not be read."}` | None were stored |
| `429` | Rate limit hit | `{"success":false,"message":"...","retry_after_seconds":30}`, plus a `Retry-After` header | None were stored |
| `500` | Writing to a non-optional store failed, or another failure that isn't a database error while rows were being written | `{"success":false,"message":"An unexpected error occurred processing your request"}` | Rolled back |
| `500` | Reading the request failed unexpectedly | ``An unexpected error occurred processing your request (Contact your service provider support and provide them with error code `...`)`` | None were stored |
| `500` | The route's `response_structure` is a value the engine doesn't know (a route with a `count_query` ignores it) | ``Invalid response structure `x` defined for route `r` (Contact your service provider support and provide them with error code `API Controller Error`)`` | Rolled back |
| `500` | The route's `buffer_rows`, or the global one, isn't a whole number of 0 or more. None of the route's queries runs. | ``Invalid buffer_rows `x` for route `r`: use a whole number, 0 or more (Contact your service provider support and provide them with error code `API Controller Error`)`` | Rolled back |
| `500` | A setting that takes a number or `true`/`false` holds something else: `db_command_timeout`, `success_status_code`, a cache duration, an upload limit, a gateway timeout, a provider's `clock_skew_seconds`, and the like ([tags.md](tags.md#how-to-read-this-page)). The request stops where the engine reads it. | ``Invalid db_command_timeout `soon` for route `r`: use a whole number (Contact your service provider support and provide them with error code `Configuration Error`)`` | Rolled back |
| `500` | A non-optional store in `stores` lacks a setting it needs: a local store's `base_path`, an SFTP store's `host`, `username` or `password`. The log names the store and the setting. | `{"success":false,"message":"An unexpected error occurred processing your request"}` | None were stored |
| `500` | Engine configuration error (route, auth provider) | `Improper service setup. (...)`, `Authorization configuration error. (...)` and similar | None were stored |
| `500` | The request sent the `debug-mode` header matching `debug_mode_header_value`, and a database error was not mapped | `{"success":false,"message":"Query 1 of 1 failed: ...","stack_trace":"...","inner_exception":"..."}`. For an error raised while a streamed result was written, `message` is the driver's text without the `Query 1 of 1 failed:` prefix, and `inner_exception` is usually `null`. | Rolled back |

A few behaviours to design for:

- **A `400` can be the caller's mistake or a server-side database failure.** Show `message`, but treat a `400` whose message equals `generic_error_message` as a server problem. A `503` or `504` with that message means the database was unavailable or slow. Retry a read later. Before you retry a write, check whether it happened: statements that ran before the failure, and earlier queries of a chain, may have committed.
- **Results of two or more rows stream their rows,** unless the route's `buffer_rows` covers them, and except a file download's query and a count query's count, which stop at the second row. A database error while rows are read gets the same status and body as one raised before them, as long as no part of the result has been handed to the server yet: about 4 KB of short values, less when a row holds long text (one value of about 1,000 characters is enough). After that the status can't change, so the engine cuts the connection: the client gets a network error, not a `200` with a cut-off body. Stored files are rolled back either way.
- **Read `message` for display.** Use the status, not the text, for logic.

## When uploads are rolled back

The engine stores uploaded files before the query runs, then decides after the response is produced:

- **Final status `400` or higher:** every file this request stored is deleted from every store, optional stores included. Folders are left in place.
- **An exception after storing began** (a store failing, an error while the response is written): the same.
- **Final status below `400`:** files stay. That includes a mapped error from `50200` to `50399`, except one raised while a streamed result was written, which counts as the exception above.
- **Refused before storing** (auth, rate limit, `mandatory_parameters`, upload validation, `413`): nothing was stored, so there is nothing to delete.

Rollback has limits. Know them before relying on it:

- **It covers files, not rows.** Statements that ran before the error, and earlier queries in a chain, stay committed unless your SQL used a transaction. An error after your `COMMIT` (for example in a final `SELECT`) still deletes the files while the rows stay.
- **Deleting a row never deletes its file.** The engine deletes stored files only during rollback. When an update query removes an attachment's row, the file stays in the store.
- **With `overwrite_existing_files` on,** a failed request puts back the file it overwrote.
- **On Windows, a file a download has open** when the request fails can't be deleted or put back: the request's file stays at the path, the previous one stays beside it as `.<id>.replaced`, and a warning names it.
- **A write that failed part-way** (disk full, connection lost mid-upload) leaves nothing: each file is written under a hidden name beside its destination and renamed once complete.
- **Hidden files can stay** beside a store's files: an unfinished copy (`.<id>.uploading`) or, with `overwrite_existing_files` on, the previous version of a file being replaced (`.<id>.replaced`). That happens when the engine stopped mid-request, or when one couldn't be deleted or put back, which a warning reports.
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
-- Avoid: SELECT first and raise afterwards. This one works, because it raises only when there are
-- no rows, but an error raised after a long streamed result cuts the connection instead of
-- returning its status.
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
| `generic_error_message` | `settings.xml` | `An error occurred while processing your request.` | Message for unmapped database errors, with `400`, `503` or `504`. |
| `buffer_rows` | route, then `settings.xml` | not set | Rows read before a result is written, so that an error after them keeps its status ([response formats](../topics/05-response-formats.md#buffer_rows-read-a-result-before-writing-it)). |
| `debug_mode_header_value` | `settings.xml` | none; commented out in the shipped sample | A request whose `debug-mode` header equals it gets exception messages and stack traces. Debug mode is off while the element is absent or empty. If you set it outside development, use a long random secret. |
| `max_payload_size_in_bytes` | `settings.xml` root | `314572800` (300 MiB); the shipped sample sets `367001600` (350 MiB) | Body size limit (413), read for each request, so a change takes effect without a restart. A value that isn't a positive whole number counts as not set. Behind IIS, IIS's own `maxAllowedContentLength` (about 30 MB) applies first: raise it in `web.config` for larger uploads. |

## API gateway routes

Proxy routes ([API gateway](../topics/08-api-gateway.md)) don't run a query. The upstream service's status and body pass through unchanged. A proxy failure is the generic `400`, and in debug mode a `500` whose body is a JSON string rather than an object.

## Related

- [File uploads](../topics/09-file-uploads.md)
- [File downloads](../topics/10-file-downloads.md)
- [Parameters](../topics/04-parameters.md)
- [Databases](../topics/13-databases.md)
