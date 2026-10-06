---
title: File downloads
summary: Serve a file from a local or SFTP store, from base64 in the database, or proxied from a URL; the query returns one row naming the file and decides who may get it.
keywords: [response_structure, file, file_management, store, relative_path, file_name, mime_type, base64_content, http, Content-Disposition, attachment, filename*, local_file_store, sftp_file_store, base_path, FileStorePath, 404, "{fs{store}}", "{fs{base_path}}"]
applies_to: 1.7.7
---

# File downloads

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](../reference/errors.md).

A download endpoint runs a query that returns one row describing a file, and the engine sends the file as an attachment. The query decides which file and who may have it. Use it to serve files saved through [uploads](09-file-uploads.md), files kept in the database, or files on another HTTP server.

## Rules

1. **Set `<response_structure>file</response_structure>`** on the route. For files in a store, also set `<file_management><store>NAME</store></file_management>`, with one store name. Without the store, every store download is a `404`.
2. **Return exact column names:** `relative_path`, `file_name`, `mime_type`, `base64_content`, `http`. Names are case-sensitive. A `content_type` column is ignored. On Oracle and DB2, quote the aliases (`AS "relative_path"`): unquoted aliases come back in upper case and don't match.
3. **Take `relative_path` from your table, never from the request.** Look the file up by its id, scoped to the caller.
4. **Enforce ownership in the query.** Join to the owner and use `{auth{user_id}}` (or an API key collection). For a file that doesn't exist or isn't the caller's, return no row: that is a `404` on every database. Raising `50404` works too on SQL Server, MySQL and PostgreSQL, but not on Oracle, DB2, ODBC or SQLite outside a trigger ([errors.md](../reference/errors.md#raising-an-error-from-sql)).
5. **Don't add `<cache>` to a download route.** In 1.7.7 a cached file route never delivers the file.
6. **Return one row.** The engine uses the first row and, from 1.7.7, reads the rest of the result to its end, so an error raised after the row still gets its status. Every extra row is read for nothing: use `TOP 1` or `LIMIT 1` when the query could match several.

## Complete example: owner-only download

`config/sql.xml`, inside `<settings><queries>`, with the `support` store and tables from the [uploads example](09-file-uploads.md#complete-example-a-form-with-fields-and-files). There, `support_requests.owner_id` holds the submitter's `{auth{user_id}}` once the create endpoint has `<authorize>`.

```xml
<download_support_file>
  <route>support-files/{{file_id}}</route>
  <verb>GET</verb>
  <authorize><provider>my_provider</provider></authorize>
  <response_structure>file</response_structure>
  <file_management>
    <store>support</store>
  </file_management>
  <query><![CDATA[
    DECLARE @file_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{file_id}});
    DECLARE @user_id NVARCHAR(200)    = {auth{user_id}};

    SELECT f.file_name, f.relative_path, f.mime_type
    FROM support_request_files f
    JOIN support_requests r ON r.id = f.request_id
    WHERE f.id = @file_id AND r.owner_id = @user_id;
  ]]></query>
</download_support_file>
```

- The owner gets `200`, the file, `Content-Type: application/pdf` and `Content-Disposition: attachment; filename=invoice.pdf; filename*=UTF-8''invoice.pdf` (`filename` is quoted when the name has spaces or other special characters).
- Another user, an unknown id, or an id that isn't a GUID gets `404` `{"success":false,"message":"No file record found to download for route ..."}`. Because it is the same `404` in every case, the response doesn't reveal whether a file exists.

A plain link can't send the token, so on an `<authorize>` route it gets `401`. Fetch with the token and save the blob:

```js
async function downloadFile(id, fileName) {
  const res = await fetch(`/support-files/${id}`, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    return showError(body?.message ?? `Download failed (${res.status})`);
  }
  const url = URL.createObjectURL(await res.blob());
  const a = Object.assign(document.createElement('a'), { href: url, download: fileName });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);   // revoking at once can cancel the download
}
```

When you save a blob, the browser names the file from `a.download`, not from the header, so pass the `file_name` you already have. `Content-Disposition` is readable from JavaScript only when the page and the API share an origin: the engine doesn't send `Access-Control-Expose-Headers`. On an endpoint without login, a plain link works and the browser uses the header's name. With curl, `curl -OJ -H "Authorization: Bearer $TOKEN" URL` saves the file under the server's `filename`, with non-ASCII characters replaced by `_`.

## Columns the query returns

Only the first row is used. No row gives a `404`.

| Column | Required | Meaning |
|---|---|---|
| `relative_path` | One source | Path inside the route's store. If the column is present, the store is used, even when its value is `NULL`: then the download name (`file_name`, else `downloaded_file`) is used as the path, at the root of the store. Filter out rows without a path (`WHERE f.relative_path IS NOT NULL`). |
| `base64_content` | One source | The file itself, as a base64 string. Used first when it is a non-empty string. Binary columns (`varbinary`, `BLOB`) are ignored: convert them to base64 in SQL. |
| `http` | One source | An absolute URL to fetch and stream. Used only when there is no `relative_path` column. |
| `file_name` | No | Download name. Default: the last segment of `relative_path`, else `downloaded_file`. |
| `mime_type` | No | Content-Type, for example `application/pdf`. Default: guessed from the download name's extension (`file_name`, else the last segment of `relative_path`), else `application/octet-stream`. An invalid value such as `pdf` makes the request fail. The `http` source ignores it (see below). |

The source is chosen in this order: `base64_content` if it holds text, otherwise the store if a `relative_path` column exists, otherwise `http`. With none of them the response is `404` "No valid file content source found".

## Sources

### Store (local or SFTP)

The route names one store, defined in `config/file_management.xml` the same way as for [uploads](09-file-uploads.md#store-definitions). Uploads use `<stores>` (plural, several names); downloads use `<store>` (singular, one name).

- `relative_path` must stay inside the store's `base_path`. Write it relative to the store, without a leading `/`, exactly as uploads store it. Anything else is refused with the same `404` as a missing file, and a warning is logged. That includes any `..` segment, even one that stays inside the store, absolute paths outside the store, and UNC or device paths (`\\host\share\...`, `\??\UNC\...`). An absolute path that does point inside the store is accepted, except in an SFTP store with no `base_path`, which accepts only relative paths.
- A local store needs `base_path`. Without one, downloads are a `404` with a logged warning. A `base_path` can be a UNC share.
- An SFTP store with no `base_path` uses the account's home folder.
- The query can read `{fs{store}}` (the store name) and `{fs{base_path}}` (its base path).

### Database (base64)

No store is needed. The whole file is decoded in memory, so keep this for small files.

```sql
SELECT file_name, mime_type, content_base64 AS base64_content
FROM documents WHERE id = {{id}};
```

On SQL Server, convert `varbinary` with `CAST('' AS XML).value('xs:base64Binary(sql:column("content"))', 'VARCHAR(MAX)')`, or store base64 text.

### HTTP URL

No store is needed. The engine fetches the URL and streams it to the caller.

```sql
SELECT 'report.pdf' AS file_name, url AS http FROM reports WHERE id = {{id}};
```

- The remote status is passed through: a remote `404` is a `404`, and its JSON message includes the full URL. So never put credentials or signed tokens in the URL.
- The remote Content-Type is used, or `application/octet-stream` when the remote sends none. `mime_type` is ignored. Return `file_name`, or the download is named `downloaded_file`.
- An empty or malformed URL is a `404`. A non-http scheme, or a host that can't be reached, is the generic `400`.
- Never build the URL from caller input. There is no host allow-list, so a caller-chosen URL makes the server fetch anything it can reach.

## Response

- `Content-Disposition` is always `attachment`, with `filename` (non-ASCII replaced by `_`) and `filename*` (UTF-8). It is never `inline`.
- Status is `200`. `success_status_code` doesn't apply to downloads.
- Store and HTTP sources are streamed. Base64 content is held in memory.
- There is no range support (`Accept-Ranges`, `206`) and no `ETag` or `Last-Modified`.
- Each SFTP download opens its own connection.

## Errors

| Status | When | Body `message` |
|---|---|---|
| `404` | Query returned no row | ``No file record found to download for route `...` `` |
| `404` | `relative_path` outside the store, file missing or not readable by the app's account, local store without `base_path` | ``File not found at relative path `...` for route `...` `` |
| `404` | No `<store>` on the route, an unknown store name, or no source column | ``No valid file content source found to download for route `...` `` |
| `404` | SFTP store without `host` or `username` | `SFTP host not defined ...` / `SFTP username not defined ...` |
| `404` | `http` is empty or not a well-formed absolute URL | ``Invalid HTTP URL `...` `` |
| remote status | The `http` source returned an error | ``Failed to download file from `...` `` |
| `400`-`599` | The query raised `50400`-`50599` (for example `THROW 50404`) | Your message, plus `error_number` (the HTTP status) |
| `400` | Any other failure: a database error, an SFTP connection or login failure, a file locked by another process, an unreachable `http` host, invalid `base64_content`, an invalid `mime_type` | The generic message (a `500` with details when the request sends the matching `debug-mode` header) |

Bodies are JSON with `"success": false` and `message`. Errors raised from SQL add `error_number`. See [errors.md](../reference/errors.md).

## Do and don't

```sql
-- Don't: a path from the request. The store check stops escapes, but any caller can read any file in the store.
SELECT 'file.pdf' AS file_name, {{path}} AS relative_path;

-- Do: look the file up by id, scoped to the caller. TRY_CONVERT turns a malformed id into no row, so it is a 404 too.
SELECT f.file_name, f.relative_path, f.mime_type
FROM files f JOIN records r ON r.id = f.record_id
WHERE f.id = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}}) AND r.owner_id = {auth{user_id}};
```

- Don't name the type column `content_type`. It is `mime_type`.
- Don't add a `count_query` to a download route: the route then returns JSON instead of the file.

## Known issues in 1.7.7

Tracked in [TODO.md](../../TODO.md):

- `<cache>` on a file route breaks the download.
- A missing or unknown `<store>` is a `404` at request time, not a start-up warning.
- The `http` source has no host allow-list, and its error messages echo the URL.

## Related

- [File uploads](09-file-uploads.md)
- [Errors, status codes and rollback](../reference/errors.md)
- [Response formats](05-response-formats.md)
- [Authentication](12-authentication.md) for `{auth{user_id}}`
- Tutorial: [File downloads](../tutorial/15-file-downloads.md)
