# File Downloads

In the previous topic, you uploaded files and stored their metadata. Now let's serve those files back to clients — streaming them from local storage, SFTP, database, or even remote HTTP URLs.

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
    declare @contact_id UNIQUEIDENTIFIER = {{contact_id}};
    declare @file_id UNIQUEIDENTIFIER = {{file_id}};
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

### How It Works

1. SQL returns `file_name` and `relative_path`
2. The application uses `<store>primary</store>` to resolve the full path
3. The file is **streamed** to the client (never fully loaded into memory)
4. The browser receives proper headers (`Content-Disposition`, `Content-Type`)

### Test It

```bash
curl -O http://localhost:5165/contacts/abc-123/documents/def-456
```

The `-O` flag saves the file with its original name.

## Three File Sources

Your SQL tells the application where to find the file by returning specific columns:

| Column Returned | Source | Priority |
|----------------|--------|----------|
| `base64_content` | Inline from database | Highest |
| `relative_path` | File store (local/SFTP) | Middle |
| `http` | Remote URL (proxied) | Lowest |

### Source 1: File Store (Local/SFTP)

The most common approach — files saved to disk:

```sql
SELECT 
  'invoice.pdf' AS file_name,
  '2025/Jan/24/a1b2c3d4/invoice.pdf' AS relative_path
FROM contact_files WHERE id = @file_id;
```

The `<store>` setting must match a store name from `file_management.xml`.

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
      'application/pdf' AS content_type
    FROM files_table WHERE id = {{id}};
    ]]>
  </query>
</download_from_db>
```

No `<file_management>` block needed — the content comes from the database directly.

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

The application fetches the file from the URL and streams it to the client. Useful for proxying files from CDNs or partner APIs.

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
    SELECT file_name, relative_path FROM contact_files WHERE id = {{id}};
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
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @user_email nvarchar(500) = {auth{email}};

    -- Verify the authenticated user owns this file
    if not exists (
      select 1 from contact_files cf
      join contacts c on cf.contact_id = c.id
      where cf.id = @id and c.owner_email = @user_email
    )
    begin
      throw 50403, 'Access denied', 1;
      return;
    end

    select file_name, relative_path from contact_files where id = @id;
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
| Store not configured | 500 |
| SFTP connection failed | 500 |
| HTTP proxy error | 502 |

## Performance Notes

- **Streaming**: Files are streamed in chunks — never fully loaded into memory
- **Any file size**: Works with files of any size
- **SFTP pooling**: Connections are reused for efficiency

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
