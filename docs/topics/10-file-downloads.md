# File Downloads

Stream files from local storage, SFTP, database, or HTTP URLs.

## Overview

Set `response_structure` to `file` and return file metadata from your SQL query.

## Basic Download Endpoint

```xml
<download_file>
  <route>files/{{id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <response_structure>file</response_structure>
  
  <file_management>
    <store>primary</store>
  </file_management>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    
    IF NOT EXISTS (SELECT 1 FROM files WHERE id = @id)
    BEGIN
      THROW 50404, 'File not found', 1;
      RETURN;
    END
    
    SELECT file_name, relative_path
    FROM files WHERE id = @id;
  ]]></query>
</download_file>
```

## File Source Priority

Return one of these from your query:

| Field | Priority | Description |
|-------|----------|-------------|
| `base64_content` | 1st | File content from database |
| `relative_path` | 2nd | Path in configured file store |
| `http` | 3rd | URL to proxy from |

## Response Fields

| Field | Required | Description |
|-------|----------|-------------|
| `file_name` | Recommended | Download filename |
| `base64_content` | Conditional | Base64-encoded content |
| `relative_path` | Conditional | Path in file store |
| `http` | Conditional | URL to proxy |
| `content_type` | Optional | MIME type (auto-detected if omitted) |

## Download from Local/SFTP Store

```xml
<file_management>
  <store>primary</store>  <!-- Must match store name in file_management.xml -->
</file_management>

<query><![CDATA[
  SELECT 
    'invoice.pdf' AS file_name,
    '2025/Jan/24/guid/invoice.pdf' AS relative_path
  FROM files WHERE id = {{id}};
]]></query>
```

## Download from Database

No `store` config needed:

```xml
<response_structure>file</response_structure>

<query><![CDATA[
  SELECT 
    file_name,
    base64_content,
    'application/pdf' AS content_type
  FROM files WHERE id = {{id}};
]]></query>
```

## Proxy from HTTP URL

```xml
<response_structure>file</response_structure>

<query><![CDATA[
  SELECT 
    'document.pdf' AS file_name,
    'https://cdn.example.com/files/doc123.pdf' AS http
  FROM files WHERE id = {{id}};
]]></query>
```

## Protected Downloads

### With API Key

```xml
<download_protected>
  <api_keys_collections>internal</api_keys_collections>
  <response_structure>file</response_structure>
  <file_management><store>primary</store></file_management>
  
  <query><![CDATA[
    SELECT file_name, relative_path FROM files WHERE id = {{id}};
  ]]></query>
</download_protected>
```

### With JWT + Access Control

```xml
<download_secure>
  <authorize><provider>azure_b2c</provider></authorize>
  <response_structure>file</response_structure>
  <file_management><store>primary</store></file_management>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    
    -- Verify user owns this file
    IF NOT EXISTS (
      SELECT 1 FROM files f
      JOIN users u ON f.user_id = u.id
      WHERE f.id = @id AND u.email = @user_email
    )
    BEGIN
      THROW 50403, 'Access denied', 1;
      RETURN;
    END
    
    SELECT file_name, relative_path FROM files WHERE id = @id;
  ]]></query>
</download_secure>
```

## Audit Logging

Track downloads:

```sql
DECLARE @id UNIQUEIDENTIFIER = {{id}};
DECLARE @user NVARCHAR(500) = {auth{email}};

-- Log download
INSERT INTO download_log (file_id, downloaded_by, downloaded_at)
VALUES (@id, @user, GETUTCDATE());

-- Return file
SELECT file_name, relative_path FROM files WHERE id = @id;
```

## Dynamic Content Type

Serve different formats:

```sql
DECLARE @accept NVARCHAR(500) = {{Accept}};

SELECT 
  CASE 
    WHEN @accept LIKE '%image/webp%' THEN 'image.webp'
    WHEN @accept LIKE '%image/png%' THEN 'image.png'
    ELSE 'image.jpg'
  END AS file_name,
  CASE 
    WHEN @accept LIKE '%image/webp%' THEN webp_path
    WHEN @accept LIKE '%image/png%' THEN png_path
    ELSE jpg_path
  END AS relative_path
FROM file_variants WHERE id = {{id}};
```

## Error Handling

| Error | HTTP Status |
|-------|-------------|
| SQL returns no rows | 404 |
| `THROW 50404` | 404 |
| `THROW 50403` | 403 |
| File not in store | 404 |
| Store not configured | 500 |
| SFTP connection failed | 500 |
| HTTP proxy error | 502 |

## Performance

- **Streaming**: Files never fully loaded into memory
- **81KB buffer**: Optimal chunk size
- **SFTP pooling**: Connection reuse
- **Any file size**: Handles large files efficiently

## Client Usage

### cURL

```bash
# Save with server filename
curl -OJ "https://api.example.com/files/abc-123"

# Save with custom name
curl -o mydoc.pdf "https://api.example.com/files/abc-123"
```

### Browser

Direct link opens download dialog:
```html
<a href="https://api.example.com/files/abc-123">Download</a>
```

## Related Topics

- [File Uploads](09-file-uploads.md) - Uploading files
- [Configuration](02-configuration.md) - Store configuration
- [Response Formats](05-response-formats.md) - response_structure details
