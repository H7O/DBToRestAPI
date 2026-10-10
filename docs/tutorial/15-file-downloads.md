# File Downloads

In the previous topic, you uploaded files and stored their metadata. Now let's serve those files back to clients. Files are streamed from local storage, SFTP or a URL, or decoded from base64 stored in the database.

## The Key: `response_structure` = `file`

A download endpoint is like any other endpoint, but with two differences:
1. `<response_structure>file</response_structure>` — tells the application to stream a file instead of returning JSON
2. Your SQL returns **file metadata** (path, name, content type) instead of regular data

## Basic Download Endpoint

```xml
<download_contact_document>
  <route>contacts/{{contact_id}}/documents/{{file_id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>contact_id,file_id</mandatory_parameters>
  <response_structure>file</response_structure>

  <file_management>
    <store>primary</store>
  </file_management>

  <query>
    <![CDATA[
    declare @contact_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{contact_id}});
    declare @file_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{file_id}});
    declare @error_msg nvarchar(500);

    if not exists (
      select 1 from contact_files 
      where id = @file_id and contact_id = @contact_id
    )
    begin
      throw 50404, 'File not found', 1;
      return;
    end

    select file_name, relative_path
    from contact_files 
    where id = @file_id and contact_id = @contact_id;
    ]]>
  </query>
</download_contact_document>
```

`TRY_CONVERT` turns an id that isn't a GUID into NULL, so it gets the same 404 as an unknown id. With a plain `declare @file_id UNIQUEIDENTIFIER = {{file_id}}`, the conversion fails and the caller gets the generic 400.

### How It Works

1. SQL returns `file_name` and `relative_path`
2. The application uses `<store>primary</store>` to resolve the full path, and refuses (with a 404) any `relative_path` that would land outside that store
3. The file is **streamed** from the store to the client
4. The browser receives proper headers (`Content-Disposition: attachment` with the file name, and `Content-Type`)

### Test It

```bash
curl -OJ http://localhost:5000/contacts/a1b2c3d4-e5f6-7890-abcd-ef1234567890/documents/f0e1d2c3-b4a5-6789-0abc-def123456789
```

`-O` saves the file, and `-J` names it from the server's `Content-Disposition` header. With `-O` alone, curl would name it `f0e1d2c3-b4a5-6789-0abc-def123456789`.

## Three File Sources

Your SQL tells the application where to find the file by returning specific columns:

| Column Returned | Source | Priority |
|----------------|--------|----------|
| `base64_content` | Inline from database | Highest |
| `relative_path` | File store (local/SFTP) | Middle |
| `http` | Remote URL (proxied) | Lowest |

`base64_content` is used only when it holds non-empty text. The store is used whenever the row has a `relative_path` column, even when its value is NULL. A NULL `relative_path` makes the engine use `file_name` as the path. `http` is used only when there is no `relative_path` column at all.

### Source 1: File Store (Local/SFTP)

The most common approach — files saved to disk:

```sql
SELECT 
  'invoice.pdf' AS file_name,
  '2025/Jan/24/a1b2c3d4/invoice.pdf' AS relative_path
FROM contact_files WHERE id = @file_id;
```

The `<store>` setting must match a store name from `file_management.xml`. Without it, or with an unknown name, the download is a `404`. Return the MIME type as a `mime_type` column if you stored one; otherwise it is guessed from `file_name`.

### Source 2: Database (Base64)

For small files stored directly in the database:

```xml
<download_from_db>
  <response_structure>file</response_structure>
  <!-- No file_management needed for base64 -->
  <query>
    <![CDATA[
    SELECT 
      file_name,
      base64_content,
      'application/pdf' AS mime_type
    FROM files_table WHERE id = {{id}};
    ]]>
  </query>
</download_from_db>
```

No `<file_management>` block needed — the content comes from the database directly. The column must hold base64 text: a binary (`varbinary`, `BLOB`) column is ignored. The whole file is decoded in memory, so keep this for small files.

### Source 3: Remote URL (HTTP Proxy)

Stream a file from an external URL:

```xml
<download_from_url>
  <response_structure>file</response_structure>
  <query>
    <![CDATA[
    SELECT 
      'report.pdf' AS file_name,
      'https://cdn.example.com/reports/2025/report.pdf' AS http
    FROM files WHERE id = {{id}};
    ]]>
  </query>
</download_from_url>
```

The application fetches the file from the URL and streams it to the client. Useful for proxying files from CDNs or partner APIs. Build the URL from your own data, never from caller input: the server will fetch whatever URL the query returns.

## Protected Downloads

### With API Key

```xml
<download_protected>
  <route>secure/files/{{id}}</route>
  <verb>GET</verb>
  <api_keys_collections>internal_solutions</api_keys_collections>
  <response_structure>file</response_structure>
  <file_management><store>primary</store></file_management>
  <query><![CDATA[
    SELECT file_name, relative_path FROM contact_files WHERE id = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
  ]]></query>
</download_protected>
```

### With JWT + Ownership Check

```xml
<download_my_file>
  <route>my/files/{{id}}</route>
  <verb>GET</verb>
  <authorize><provider>azure_b2c</provider></authorize>
  <response_structure>file</response_structure>
  <file_management><store>primary</store></file_management>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    declare @user_email nvarchar(500) = {auth{email}};

    -- The ownership check is part of the WHERE clause. Another user's file
    -- and a file that doesn't exist both return no row, which is a 404,
    -- so the caller can't learn which files exist.
    select cf.file_name, cf.relative_path
    from contact_files cf
    join contacts c on cf.contact_id = c.id
    where cf.id = @id and c.owner_email = @user_email;
    ]]>
  </query>
</download_my_file>
```

## Dynamic Content Type

Serve different formats based on the client's `Accept` header:

```sql
declare @accept nvarchar(500) = {{Accept}};

select 
  case 
    when @accept like '%image/webp%' then 'photo.webp'
    when @accept like '%image/png%' then 'photo.png'
    else 'photo.jpg'
  end as file_name,
  case 
    when @accept like '%image/webp%' then webp_path
    when @accept like '%image/png%' then png_path
    else jpg_path
  end as relative_path
from file_variants where id = {{id}};
```

## Error Handling

| Scenario | HTTP Status |
|----------|-------------|
| SQL returns no rows | 404 |
| `THROW 50404` in SQL | 404 |
| `THROW 50403` in SQL | 403 |
| File not found in store | 404 |
| `relative_path` points outside the store | 404 |
| No `<store>`, or an unknown store name | 404 |
| SFTP connection or login failed | 400 (generic message) |
| The `http` URL answered with an error | the remote server's status |
| The `http` URL is unreachable, `base64_content` isn't valid base64, or the `mime_type` value isn't a valid media type | 400 (generic message) |

Error bodies are JSON with `"success": false` and a `message`. Errors raised from SQL also include `error_number`. See [Errors, status codes and rollback](../reference/errors.md) for every case.

## Performance Notes

- **Streaming**: Files from a store or a URL are streamed in chunks. Base64 content from the database is decoded in memory.
- **SFTP**: Each download opens its own connection.
- **No caching**: Don't add `<cache>` to a download endpoint; it breaks the download (a known issue).

---

### What You Learned

- How `<response_structure>file</response_structure>` switches to file streaming mode
- Three file sources: file store (`relative_path`), database (`base64_content`), URL (`http`)
- How to build protected download endpoints with API keys or JWT
- Dynamic content negotiation based on client headers
- Error handling for missing files and access control

---

**Next:** [Embedded HTTP Calls from SQL →](16-http-from-sql.md)

**[Back to Tutorial Index](index.md)**
