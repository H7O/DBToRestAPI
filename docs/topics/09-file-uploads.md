---
title: File uploads
summary: Accept files with ordinary form fields in one request, store them in local or SFTP stores, and record them in SQL; failed requests delete their files automatically.
keywords: [file_management, files_json_field_or_form_field_name, stores, local_file_store, sftp_file_store, base_path, filename_field_in_payload, base64_content_field_in_payload, relative_file_path_structure, permitted_file_extensions, max_file_size_in_bytes, max_number_of_files, overwrite_existing_files, accept_caller_defined_file_ids, pass_files_content_to_query, max_payload_size_in_bytes, is_new_upload, relative_path, multipart/form-data, base64, OPENJSON, rollback, 409, 413]
applies_to: 1.7.7
---

# File uploads

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors and rollback: [errors.md](../reference/errors.md).

An upload endpoint receives files together with ordinary fields (a form's name, email and so on) in one request. The engine checks and stores the files, then runs your SQL with a JSON description of them. If the request ends in an error status, the engine deletes the files it stored. Use it for any form or API that attaches files to a record. To serve files back, see [file downloads](10-file-downloads.md).

## Rules

1. **Configure `files_json_field_or_form_field_name` and `stores`**, on the route or globally in `file_management.xml`. Every name in `stores` must be defined under `<local_file_store>` or `<sftp_file_store>`. Without a files field, file parts are ignored. Without a known store, no file is written, yet the request succeeds and your query still sees the files as `is_new_upload`, so it records files that don't exist.
2. **Insert only entries with `is_new_upload`.** Entries without it are files the client says it already has. Match them by `id` against your own rows.
3. **Reject with a status, not a flag.** Use `mandatory_parameters` for required fields and `THROW 50400, '...', 1;` (or your database's equivalent) for everything else, raised before any statement that returns rows. A status of 400 or higher deletes the stored files, so write no cleanup code. See [errors.md](../reference/errors.md).
4. **Your SQL protects your rows.** Validate before any write, and put the inserts in a transaction, so a failure part-way leaves no rows behind. The engine handles only the files.
5. **Use the field names from your `file_management.xml`.** Each file entry names its file with `filename_field_in_payload` and its base64 content with `base64_content_field_in_payload`. The shipped config sets `name` and `content_base64`; without those settings the defaults are `name` and `base64_content`. Entry field names are case-sensitive. A wrong name field is a `400`. A wrong content field in a JSON body is silent: every entry looks like a file the client already has, and nothing is stored.
6. **Never trust file data from the request.** Read `relative_path`, `size` and `mime_type` only from entries the engine stored (`is_new_upload`). The engine strips them from all other entries. (With `pass_files_content_to_query` on, nothing is stored at all: see [settings](#settings).)

## How an upload is processed

1. API keys, JWT and rate limits are checked. A failure here stores nothing.
2. The whole request body is received, up to `max_payload_size_in_bytes` (over it: `413`). Then each new file is validated (name, extension, size, count) and written to a server temp file. A failure is a `400` naming the problem, and nothing is stored. Because the body is received first, an oversized file is uploaded in full before it is refused. Check sizes in the client too.
3. `mandatory_parameters` is checked. A missing parameter is a `400` before anything is stored.
4. Each file is copied to every store in `stores`.
5. Your query runs, with the files described in the files field.
6. If the final status is 400 or higher, or an exception occurred after storing began, the stored files are deleted. Temp files are always deleted.

## Complete example: a form with fields and files

A support form on SQL Server: name, email, category, message and one to three attachments (PDF, PNG or JPG, 5 MB each). The endpoint runs on the `default` connection in `settings.xml`, so point it at SQL Server (2016 or later, with the database at compatibility level 130 or higher, for `OPENJSON`), or add `<connection_string_name>` to the endpoint.

### Store

In `config/file_management.xml`, add the store **inside the existing `<local_file_store>` element**:

```xml
<settings>
  <file_management>
    ...
    <local_file_store>
      ...                       <!-- stores already defined stay as they are -->
      <support>
        <base_path><![CDATA[C:\support_files\]]></base_path>
      </support>
    </local_file_store>
  </file_management>
</settings>
```

Don't add a second `<local_file_store>` (or `<sftp_file_store>`) element. Repeated sibling elements are numbered by the configuration reader, so every store inside them stops matching its name, and uploads succeed without storing anything. The engine creates `base_path` and the folders below it on the first write, as long as its account may create folders there.

### Tables

```sql
CREATE TABLE support_requests (
  id         UNIQUEIDENTIFIER PRIMARY KEY,
  owner_id   NVARCHAR(200)    NULL,      -- the submitter's {auth{user_id}}, when the route requires login
  full_name  NVARCHAR(200)    NOT NULL,
  email      NVARCHAR(320)    NOT NULL,
  category   NVARCHAR(20)     NOT NULL,
  message    NVARCHAR(MAX)    NOT NULL,
  created_at DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE support_request_files (
  id            UNIQUEIDENTIFIER PRIMARY KEY,
  request_id    UNIQUEIDENTIFIER NOT NULL REFERENCES support_requests(id),
  file_name     NVARCHAR(260)    NOT NULL,
  relative_path NVARCHAR(1000)   NOT NULL,
  mime_type     NVARCHAR(200)    NOT NULL,
  size          BIGINT           NOT NULL
);
```

### Endpoint

`config/sql.xml`, inside `<settings><queries>`:

```xml
<create_support_request>
  <route>support-requests</route>
  <verb>POST</verb>
  <mandatory_parameters>full_name,email,category,message,attachments</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <response_structure>single</response_structure>

  <file_management>
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
    <stores>support</stores>
    <permitted_file_extensions>.pdf,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>5242880</max_file_size_in_bytes>
    <max_number_of_files>3</max_number_of_files>
  </file_management>

  <query><![CDATA[
    SET XACT_ABORT ON;

    -- NVARCHAR(MAX), so over-long input is refused below instead of being cut silently
    DECLARE @full_name NVARCHAR(MAX) = {{full_name}};
    DECLARE @email     NVARCHAR(MAX) = {{email}};
    DECLARE @category  NVARCHAR(MAX) = {{category}};
    DECLARE @message   NVARCHAR(MAX) = {{message}};
    DECLARE @files     NVARCHAR(MAX) = {{attachments}};
    DECLARE @owner_id  NVARCHAR(200) = {auth{user_id}};   -- NULL unless the route has <authorize>

    -- mandatory_parameters checks presence only: an empty or null value passes it.
    -- Test for NULL first, because NULL fails every comparison.
    IF NULLIF(LTRIM(RTRIM(@full_name)), N'') IS NULL OR LEN(@full_name) > 200
      THROW 50400, 'Full name is required (200 characters at most).', 1;
    IF @email IS NULL OR LEN(@email) > 320 OR @email NOT LIKE '%_@_%._%'
      THROW 50400, 'Email is not valid.', 1;
    IF @category IS NULL OR @category NOT IN ('billing', 'technical', 'other')
      THROW 50400, 'Category must be billing, technical or other.', 1;
    IF NULLIF(LTRIM(RTRIM(@message)), N'') IS NULL
      THROW 50400, 'Message is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM OPENJSON(@files) WHERE JSON_VALUE(value, '$.is_new_upload') = 'true')
      THROW 50400, 'Attach at least one file.', 1;

    DECLARE @request_id UNIQUEIDENTIFIER = NEWID();

    BEGIN TRANSACTION;

    INSERT INTO support_requests (id, owner_id, full_name, email, category, message)
    VALUES (@request_id, @owner_id, @full_name, @email, @category, @message);

    INSERT INTO support_request_files (id, request_id, file_name, relative_path, mime_type, size)
    SELECT JSON_VALUE(value, '$.id'),
           @request_id,
           JSON_VALUE(value, '$.name'),
           JSON_VALUE(value, '$.relative_path'),
           JSON_VALUE(value, '$.mime_type'),
           JSON_VALUE(value, '$.size')
    FROM OPENJSON(@files)
    WHERE JSON_VALUE(value, '$.is_new_upload') = 'true';

    COMMIT;

    SELECT @request_id AS id,
           (SELECT COUNT(*) FROM support_request_files WHERE request_id = @request_id) AS file_count;
  ]]></query>
</create_support_request>
```

Every `THROW` above returns `400` with its message, and any files already stored are deleted.

### Browser request

Send `multipart/form-data`: the ordinary fields, the files field as a JSON array with one entry per file, and one file part per file. A part is paired with its entry by file name. The part's own form field name doesn't matter.

```js
const files = [...fileInput.files];
const form = new FormData();
form.append('full_name', fullName);
form.append('email', email);
form.append('category', category);
form.append('message', message);
form.append('attachments', JSON.stringify(files.map(f => ({ name: f.name }))));
for (const f of files) form.append('file', f, f.name);

// Don't set Content-Type yourself: the browser adds the multipart boundary.
let res;
try { res = await fetch('/support-requests', { method: 'POST', body: form }); }
catch { return showError('Network error. Please try again.'); }
const body = await res.json().catch(() => null);
if (res.ok) showDone(body.id);   // 201 {"id":"...","file_count":2}
else showError(body?.message ?? `Request failed (${res.status})`);
                                 // 400 {"success":false,"message":"Attach at least one file.","error_number":400}
```

A browser file name can break the engine's [file name rules](#errors), for example `Q3?.pdf` on a Windows server. Check names in the client, or rename the file part and its entry together.

The same request with curl:

```bash
curl -X POST http://localhost:5000/support-requests \
  -F "full_name=Jane Doe" -F "email=jane@example.com" -F "category=billing" -F "message=Invoice question" \
  -F 'attachments=[{"name":"invoice.pdf"}]' \
  -F "file=@invoice.pdf;filename=invoice.pdf"
```

### What the client gets back

| Case | Status | Body |
|---|---|---|
| Success | `201` | `{"id":"...","file_count":2}` |
| A mandatory field missing | `400` | `{"success":false,"message":"Missing mandatory parameters: category"}` |
| A `THROW 50400` in the query | `400` | `{"success":false,"message":"Attach at least one file.","error_number":400}` |
| Wrong extension | `400` | ``{"success":false,"message":"File extension `.exe` is not permitted."}`` |
| File too large | `400` | ``{"success":false,"message":"File `big.pdf` exceeds the maximum allowed size of 5242880 bytes"}`` |
| More than 3 files | `400` | `{"success":false,"message":"Number of files exceeds the maximum allowed limit of 3"}` |
| A file part with no entry | `400` | ``{"success":false,"message":"Uploaded file `x.pdf` has no entry in `attachments`."}`` |
| Body over `max_payload_size_in_bytes` | `413` | `{"success":false,"message":"The request body could not be read."}` |
| Any other database error | `400` | `{"success":false,"message":"An error occurred while processing your request."}` |

In each of these cases the engine deletes every file the request stored. The exceptions are rare, such as a write that failed part-way ([rollback limits](../reference/errors.md#when-uploads-are-rolled-back)).

## Request formats

| Format | Use when | Files field | File content |
|---|---|---|---|
| `multipart/form-data` | Browsers, large files | A form field holding a JSON array string | One file part per new entry, matched by file name |
| `application/json` | Server-to-server, small files | A JSON array property of the body | Base64 in each entry's content field |
| `application/x-www-form-urlencoded` | Avoid | A form field holding a JSON array string | Base64 in each entry, `+` percent-encoded. The whole field must fit ASP.NET Core's 4 MiB form-value limit, about 3 MiB of files per request. |

### JSON with base64

```json
{
  "full_name": "Jane Doe",
  "email": "jane@example.com",
  "category": "billing",
  "message": "Invoice question",
  "attachments": [
    { "name": "invoice.pdf", "content_base64": "JVBERi0xLjQK..." }
  ]
}
```

An entry with non-empty content is a new file. An entry without content is an existing file (see [partial updates](#partial-updates-keep-remove-add)). Base64 may omit its `=` padding and may contain whitespace.

### Multipart pairing

- Each entry needs the name field, existing entries included.
- A part goes to the entry whose name equals the part's file name, exact case first, then ignoring case.
- Entries that carry a `relative_path` (files already stored) only get a part left over after the new entries. Send existing files back with the `relative_path` the engine gave them.
- Same-named parts are taken in upload order.
- A part that no entry claims is a `400`.
- The files field must be a JSON array in one form field. It may appear only once.
- In multipart, only a file part makes an entry a new upload. Base64 content inside a multipart entry is ignored.

## What the query receives

The files field arrives as one JSON text parameter, for example `{{attachments}}`. Every other form field or body property arrives as its own parameter. Form fields arrive as strings, and a field sent more than once arrives as the JSON text of an array of its values.

A new file (one the engine stored):

```json
{
  "id": "6f1c2d1e-...",
  "name": "invoice.pdf",
  "relative_path": "2026/Oct/06/6f1c2d1e-.../invoice.pdf",
  "extension": ".pdf",
  "mime_type": "application/pdf",
  "size": 102400,
  "backend_temp_file_path": "C:\\Windows\\Temp\\tmp1A2B.tmp",
  "is_new_upload": true,
  "note": "any other field the client sent"
}
```

| Field | Value |
|---|---|
| `id` | A new GUID, or the client's own `id` when `accept_caller_defined_file_ids` is on and it is a valid GUID |
| name field | The checked file name |
| `relative_path` | The path inside every store, built from `relative_file_path_structure` |
| `extension` | With the dot, from the file name |
| `mime_type` | From the file name's extension (`application/octet-stream` if unknown). The part's own Content-Type is ignored. |
| `size` | Bytes stored |
| `backend_temp_file_path` | A server temp path, deleted after the request. Don't return it to clients. |
| `is_new_upload` | `true` |
| other fields | Copied from the client's entry. The client can't override any field above. |

An existing file (an entry that brought no file) arrives as the client sent it, minus `relative_path`, `extension`, `mime_type`, `size`, `backend_temp_file_path`, `is_new_upload` and the content field. `id`, the name and custom fields are the client's, unchecked.

### Reading the files in other databases

| Database | New uploads |
|---|---|
| SQL Server | `SELECT ... FROM OPENJSON({{attachments}}) WHERE JSON_VALUE(value, '$.is_new_upload') = 'true'` |
| PostgreSQL | `SELECT ... FROM json_array_elements({{attachments}}::json) AS f WHERE (f->>'is_new_upload')::boolean` |
| MySQL 8 | `SELECT ... FROM JSON_TABLE({{attachments}}, '$[*]' COLUMNS (id CHAR(36) PATH '$.id', is_new_upload VARCHAR(5) PATH '$.is_new_upload', ...)) AS f WHERE f.is_new_upload = 'true'` |
| SQLite | `SELECT ... FROM json_each({{attachments}}) WHERE json_extract(value, '$.is_new_upload') = 1` |

SQLite can't raise a custom status outside a trigger ([errors.md](../reference/errors.md#raising-an-error-from-sql)), so its validation failures come back as the generic `400`.

## Partial updates (keep, remove, add)

To edit a record's attachments in one request, the client sends the full list it wants to keep, plus the new files:

- **Kept file:** an entry with its `id`, its name and the `relative_path` the engine returned, with no content and no file part.
- **New file:** an entry with a name, plus content (JSON) or a file part (multipart).
- **Removed file:** left out of the list.

```js
form.append('attachments', JSON.stringify([
  { id: kept.id, name: kept.file_name, relative_path: kept.relative_path },  // keep
  { name: newFile.name }                                                     // add
]));
form.append('file', newFile, newFile.name);
```

The query removes rows that weren't sent back, scoped to the record, and inserts the new uploads:

```xml
<update_support_request_files>
  <route>support-requests/{{id}}/attachments</route>
  <verb>PUT</verb>
  <authorize><provider>my_provider</provider></authorize>
  <mandatory_parameters>attachments</mandatory_parameters>
  <response_structure>array</response_structure>
  <file_management>
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
    <stores>support</stores>
    <permitted_file_extensions>.pdf,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>5242880</max_file_size_in_bytes>
    <max_number_of_files>3</max_number_of_files>
  </file_management>
  <query><![CDATA[
    SET XACT_ABORT ON;

    DECLARE @request_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    DECLARE @user_id    NVARCHAR(200)    = {auth{user_id}};
    DECLARE @files      NVARCHAR(MAX)    = {{attachments}};

    IF NOT EXISTS (SELECT 1 FROM support_requests WHERE id = @request_id AND owner_id = @user_id)
      THROW 50404, 'Request not found.', 1;

    BEGIN TRANSACTION;

    DELETE f FROM support_request_files f
    WHERE f.request_id = @request_id
      AND NOT EXISTS (SELECT 1 FROM OPENJSON(@files) j
                      WHERE TRY_CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(j.value, '$.id')) = f.id);

    INSERT INTO support_request_files (id, request_id, file_name, relative_path, mime_type, size)
    SELECT JSON_VALUE(value, '$.id'), @request_id, JSON_VALUE(value, '$.name'),
           JSON_VALUE(value, '$.relative_path'), JSON_VALUE(value, '$.mime_type'), JSON_VALUE(value, '$.size')
    FROM OPENJSON(@files)
    WHERE JSON_VALUE(value, '$.is_new_upload') = 'true';

    COMMIT;

    SELECT id, file_name, relative_path, mime_type, size
    FROM support_request_files WHERE request_id = @request_id;
  ]]></query>
</update_support_request_files>
```

The ownership check uses `support_requests.owner_id`, which the create endpoint fills from `{auth{user_id}}` when it requires login (add `<authorize>` to it). `<response_structure>array</response_structure>` keeps the reply a list however many files remain.

- `{auth{user_id}}` is the authenticated user's id, from the token's subject. Use it rather than `{auth{sub}}`: it works the same for every provider, and `{auth{sub}}` is empty before 1.7.6 ([authentication](12-authentication.md)).
- `max_number_of_files` counts kept entries too.
- **Removed files stay in the store.** The engine never deletes a stored file because its row was deleted. Return the removed paths and clean them up separately if you need to.
- **To let the client edit a kept file's own fields**, update its row by `id`, scoped to the record, inside the transaction before `COMMIT`. Never insert it. This assumes a `description NVARCHAR(500) NULL` column on `support_request_files`:

```sql
UPDATE f SET f.description = LEFT(JSON_VALUE(j.value, '$.description'), 500)
FROM support_request_files f
JOIN OPENJSON(@files) j ON TRY_CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(j.value, '$.id')) = f.id
WHERE f.request_id = @request_id
  AND ISNULL(JSON_VALUE(j.value, '$.is_new_upload'), 'false') <> 'true'
  AND JSON_VALUE(j.value, '$.description') IS NOT NULL;   -- an entry sent without it keeps its stored value
```

## Settings

Route settings go inside the route's `<file_management>`. Global defaults go in `config/file_management.xml` under `<settings><file_management>`. A route value wins. A blank route string, or a route number below 1, falls back to the global value. A route boolean that is present always wins.

| Setting | Scope | Default | Notes |
|---|---|---|---|
| `files_json_field_or_form_field_name` | route, global | none | **Required.** The body property or form field holding the files array. Prefer setting it per route: set globally, it applies to every route, so that name is refused in every query string and header, and every JSON body must be an object. |
| `stores` | route, global | none | **Required.** Comma-separated store names, each defined in `file_management.xml`. Each file goes to every store. An unknown name among known ones is skipped with a warning. |
| `filename_field_in_payload` | route, global | `name` | Name of the file-name field in each entry. The shipped config sets `name`. |
| `base64_content_field_in_payload` | route, global | `base64_content` | Name of the base64 field in each entry. The shipped config sets `content_base64`. |
| `relative_file_path_structure` | route, global | `{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}` | Write it on one line: whitespace inside the element becomes part of the path. |
| `permitted_file_extensions` | route, global | all allowed | Comma-separated, with dots: `.pdf,.png`. Compared ignoring case. When set, a file must have an extension. |
| `max_file_size_in_bytes` | route, global | unlimited | Per file. |
| `max_number_of_files` | route, global | unlimited | Per request, existing entries included. |
| `overwrite_existing_files` | route, global | `false` | `false`: a file already at the target path in a non-optional store is a `409`. In an optional store, the engine logs a warning and stops writing to that store at the first existing file, so that file and the request's later files are missing from it. Keep `{{guid}}` in the path structure to avoid clashes (with `accept_caller_defined_file_ids` on, callers choose it). |
| `accept_caller_defined_file_ids` | route, global | `false` | `true`: a new entry's own `id` (a valid GUID) is used, and fills `{{guid}}`. |
| `pass_files_content_to_query` | route, global | `false` | `true`: nothing is stored and there is no `backend_temp_file_path`. The query gets the base64 content under the content field. Entries still carry `is_new_upload` and a `relative_path`, but no file exists there: store the content yourself. In a JSON body, `size` is estimated from the text length, so whitespace inflates it. The content is not checked: invalid base64 or a `data:` URI reaches the query as sent. |
| `max_payload_size_in_bytes` | `settings.xml` root | `314572800` (300 MiB); the shipped sample sets `367001600` (350 MiB) | The only limit on bytes received, for every route. Over it: `413`. Size it to at least `max_number_of_files` × `max_file_size_in_bytes` plus the form fields for multipart, and about 4/3 of that for base64. Read at start-up: restart after changing it. It applies only when the engine runs on Kestrel. Under IIS's default in-process hosting it has no effect, and two IIS limits of about 30 MB apply instead ([details](../reference/errors.md#settings)). |

The shipped `file_management.xml` also sets global `permitted_file_extensions`, `max_number_of_files` (5) and `max_file_size_in_bytes`. Every route inherits them unless it sets its own.

### Store definitions

Stores are defined only globally, in `file_management.xml`. A route lists them by name in `stores`.

| Store type | Element | Settings |
|---|---|---|
| Local disk or share | `<local_file_store><NAME>` | `base_path` (required; a UNC path like `\\server\share\uploads\` works), `optional` (default `false`) |
| SFTP | `<sftp_file_store><NAME>` | `host`, `username`, `password` (all required), `port` (default 22), `base_path` (default: the account's home folder), `optional` |

An `optional` store that fails is skipped, with a warning in the log. A non-optional store that fails makes the request a `500`, and the files are rolled back. Encrypt SFTP passwords with [settings encryption](15-encryption.md).

## Path structure variables

| Variable | Value |
|---|---|
| `{date{yyyy}}`, `{date{MMM}}`, `{date{dd}}` | Current UTC date, in any .NET date format, formatted in the server's culture |
| `{{guid}}` | The file's `id` |
| `{file{name}}` | The checked file name |

Default result, on an English server: `2026/Oct/06/6f1c2d1e-.../invoice.pdf`. `MMM` is a localized month name, so for paths that don't depend on the server's language use numbers: `{date{yyyy}}/{date{MM}}/{date{dd}}`. The structure is plain text substitution, so a flat layout such as `{date{yyyy}}/{{guid}}_{file{name}}` works too. Nothing is ever written outside the store's `base_path`, and rollback leaves the folders it created.

## Errors

Upload refusals are `400` with `{"success":false,"message":"..."}`, sent before anything is stored and before the query runs.

| Message | Cause |
|---|---|
| ``File extension `.x` is not permitted.`` / `File must have an extension.` | `permitted_file_extensions` |
| ``File `x` exceeds the maximum allowed size of N bytes`` | `max_file_size_in_bytes` |
| `Number of files exceeds the maximum allowed limit of N` | `max_number_of_files` |
| ``File `x` content is not valid base64.`` | JSON content |
| ``Uploaded file `x` has no entry in `f`.`` | A file part no entry claims, or parts without a files field |
| ``Invalid JSON format: Each file object must contain a non-empty string property `name` ...`` | An entry without a name (or with the name under another field name) |
| `Invalid JSON format: Each file entry must be a JSON object` | An array element that is not an object |
| `Invalid JSON format: Property must be an array` | The files field is valid JSON but not an array |
| ``The `f` form field must be a JSON array of file entries.`` | The form's files field is not valid JSON |
| `The request body must be a JSON object.` | JSON body is not an object |
| ``` `f` appears more than once in the request body.``` / ``` `f` can only be sent in the request body.``` | The files field twice, or in the query string or a header |
| `The form data could not be read.` | Malformed or cut-off form body, or a form over an ASP.NET Core limit (a field value over 4 MiB, more than 1024 fields) |
| File name rules | Empty; not valid Unicode; longer than 150 characters; `..`, `/`, `\` or `:`; control characters or invisible ones (U+200B-U+200F, U+FEFF); characters invalid on the server's OS (on Windows `<` `>` `"` `|` `?` `*`; on Linux only NUL); Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`); only dots; starting or ending with a dot or space; starting with `-` |

Other upload statuses: `409` when a target file exists in a non-optional store and overwriting is off, `413` for an oversized body, and `500` when a non-optional store fails. All statuses are listed in [errors.md](../reference/errors.md#statuses-you-will-meet).

### Silent failures

Each of these returns success, and the request stores nothing:

- **No `files_json_field_or_form_field_name`.** File parts are ignored, and a JSON body's files array reaches the query unprocessed, client-set `is_new_upload` included.
- **No `stores` (on the route or globally), or only unknown names.** Files are validated but never written, yet the query still gets `is_new_upload` entries with a `relative_path`, so it records files that don't exist. An unknown name logs a warning. A local store without `base_path` and an SFTP store missing credentials are skipped without one.
- **A second `<local_file_store>` or `<sftp_file_store>` element.** Every store in either element becomes unknown ([store](#store)).
- **A content field name that doesn't match the config, in a JSON body.** Every entry looks like an existing file.
- **A body that isn't valid JSON** (with `application/json`). It is read as having no parameters, so they are all `NULL`. With `mandatory_parameters` set, this becomes a `400` "Missing mandatory parameters" instead.
- **A Content-Type other than `application/json`, `multipart/form-data` or `application/x-www-form-urlencoded`.** For example `text/plain`, which `fetch` sends for a string body when no `Content-Type` header is set. The body is ignored, so every body parameter is `NULL` and no file is processed.

## Do and don't

```sql
-- Don't: insert every entry. Existing entries carry no relative_path; client-sent ones are untrusted.
INSERT INTO files (id, file_name, relative_path)
SELECT JSON_VALUE(value, '$.id'), JSON_VALUE(value, '$.name'), JSON_VALUE(value, '$.relative_path')
FROM OPENJSON(@files);

-- Do: insert only what the engine stored.
INSERT INTO files (id, file_name, relative_path)
SELECT JSON_VALUE(value, '$.id'), JSON_VALUE(value, '$.name'), JSON_VALUE(value, '$.relative_path')
FROM OPENJSON(@files)
WHERE JSON_VALUE(value, '$.is_new_upload') = 'true';
```

```sql
-- Don't: check a required document by counting raw entries. An entry without a file passes
-- through with whatever id and fields the client chose, so it satisfies the check with no file.
IF NOT EXISTS (SELECT 1 FROM OPENJSON(@files) WHERE JSON_VALUE(value, '$.document_type') = 'contract')
  THROW 50400, 'Attach the signed contract.', 1;

-- Do: count new uploads, plus this record's own kept rows (their stored type, not the client's).
-- This assumes support_request_files has a document_type column, filled from each new entry
-- ($.document_type) by the INSERT, and an update route where @request_id is the existing record.
IF NOT EXISTS (
     SELECT 1 FROM OPENJSON(@files) j
     WHERE JSON_VALUE(j.value, '$.is_new_upload') = 'true'
       AND JSON_VALUE(j.value, '$.document_type') = 'contract'
     UNION ALL
     SELECT 1 FROM support_request_files f
     JOIN OPENJSON(@files) j ON TRY_CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(j.value, '$.id')) = f.id
     WHERE f.request_id = @request_id AND f.document_type = 'contract')
  THROW 50400, 'Attach the signed contract.', 1;
```

- Don't return `200` with an error flag, and don't delete files from SQL or the client after a failure. Raise `50400` and let the rollback work.
- Don't put the files array in the query string or a header. It is refused.
- Don't spread `relative_file_path_structure` over several lines.
- Don't echo `backend_temp_file_path` to clients: it is a server path.

## Known issues in 1.7.7

Tracked in [TODO.md](../../TODO.md):

- With `overwrite_existing_files` on, a failed request deletes the file it overwrote.
- A write that failed part-way can leave a partial file ([rollback limits](../reference/errors.md#when-uploads-are-rolled-back)).
- The silent failures above are not reported at start-up.
- The OpenAPI document has no request schema for upload endpoints.

## Related

- [Errors, status codes and rollback](../reference/errors.md)
- [File downloads](10-file-downloads.md)
- [Authentication](12-authentication.md) for `{auth{user_id}}`
- [Configuration](02-configuration.md)
- Tutorial: [File uploads](../tutorial/14-file-uploads.md)
