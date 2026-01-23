# File Uploads

Upload files to local storage or SFTP servers with automatic metadata handling.

## Overview

Supports:
- `application/json` with base64-encoded files
- `multipart/form-data` for traditional uploads
- Multiple simultaneous stores (local + SFTP)
- Automatic path generation and metadata

## Configuration

### Step 1: Define File Stores

`/config/file_management.xml`:

```xml
<settings>
  <file_management>
    <!-- Path structure for stored files -->
    <relative_file_path_structure>{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}</relative_file_path_structure>
    
    <!-- Global restrictions -->
    <permitted_file_extensions>.pdf,.docx,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>10485760</max_file_size_in_bytes>
    <max_number_of_files>5</max_number_of_files>
    
    <!-- Local stores -->
    <local_file_store>
      <primary>
        <base_path><![CDATA[c:\uploads\]]></base_path>
      </primary>
      <backup>
        <base_path><![CDATA[d:\backup\]]></base_path>
        <optional>true</optional>
      </backup>
    </local_file_store>
    
    <!-- SFTP stores -->
    <sftp_file_store>
      <remote>
        <host>sftp.example.com</host>
        <port>22</port>
        <username>user</username>
        <password>pass</password>
        <base_path>/uploads/</base_path>
      </remote>
    </sftp_file_store>
  </file_management>
</settings>
```

### Step 2: Create Upload Endpoint

`/config/sql.xml`:

```xml
<upload_documents>
  <route>contacts/{{contact_id}}/documents</route>
  <verb>POST</verb>
  <mandatory_parameters>contact_id</mandatory_parameters>
  <success_status_code>201</success_status_code>
  
  <file_management>
    <stores>primary,remote</stores>
    <permitted_file_extensions>.pdf,.png,.jpg</permitted_file_extensions>
    <max_file_size_in_bytes>5242880</max_file_size_in_bytes>
    <max_number_of_files>3</max_number_of_files>
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
  </file_management>
  
  <query><![CDATA[
    DECLARE @contact_id UNIQUEIDENTIFIER = {{contact_id}};
    DECLARE @files_json NVARCHAR(MAX) = {{attachments}};
    
    -- Insert file metadata
    INSERT INTO files (id, contact_id, file_name, relative_path, mime_type, size)
    SELECT 
      JSON_VALUE(value, '$.id'),
      @contact_id,
      JSON_VALUE(value, '$.file_name'),
      JSON_VALUE(value, '$.relative_path'),
      JSON_VALUE(value, '$.mime_type'),
      JSON_VALUE(value, '$.size')
    FROM OPENJSON(@files_json);
    
    -- Return uploaded files
    SELECT id, file_name, relative_path, mime_type, size
    FROM files WHERE contact_id = @contact_id;
  ]]></query>
</upload_documents>
```

## Upload Methods

### JSON with Base64

```bash
POST /contacts/abc-123/documents
Content-Type: application/json

{
  "attachments": [
    {
      "file_name": "document.pdf",
      "base64_content": "JVBERi0xLjQK...",
      "description": "Contract"
    }
  ]
}
```

### Multipart Form Data

```bash
POST /contacts/abc-123/documents
Content-Type: multipart/form-data

attachments: [{"file_name": "doc.pdf", "description": "Contract"}]
file: (binary)
```

## File Metadata

The system generates and passes this JSON to your SQL:

```json
[
  {
    "id": "guid-generated",
    "file_name": "document.pdf",
    "relative_path": "2025/Jan/24/guid/document.pdf",
    "mime_type": "application/pdf",
    "size": 102400,
    "extension": ".pdf",
    "description": "User provided",
    "is_new_upload": true
  }
]
```

## Configuration Options

### Endpoint-Level

| Setting | Description |
|---------|-------------|
| `stores` | Comma-separated store names to use |
| `permitted_file_extensions` | Override allowed extensions |
| `max_file_size_in_bytes` | Override max size |
| `max_number_of_files` | Override max count |
| `files_json_field_or_form_field_name` | JSON field name for metadata |
| `pass_files_content_to_query` | Include base64 in SQL (default: false) |

### Store Options

| Setting | Description |
|---------|-------------|
| `optional` | Don't fail if store unavailable |
| `base_path` | Root path for files |

## Path Structure Variables

| Variable | Example Output |
|----------|----------------|
| `{date{yyyy}}` | 2025 |
| `{date{MMM}}` | Jan |
| `{date{dd}}` | 24 |
| `{{guid}}` | a1b2c3d4-... |
| `{file{name}}` | document.pdf |

Result: `2025/Jan/24/a1b2c3d4-.../document.pdf`

## Update with Files

Handle file additions, updates, and deletions:

```xml
<query><![CDATA[
  DECLARE @contact_id UNIQUEIDENTIFIER = {{id}};
  DECLARE @files_json NVARCHAR(MAX) = {{attachments}};
  
  IF @files_json IS NOT NULL
  BEGIN
    -- Get sent file IDs
    DECLARE @sent_ids TABLE (id UNIQUEIDENTIFIER);
    INSERT INTO @sent_ids
    SELECT TRY_CAST(JSON_VALUE(value, '$.id') AS UNIQUEIDENTIFIER)
    FROM OPENJSON(@files_json)
    WHERE JSON_VALUE(value, '$.id') IS NOT NULL;
    
    -- DELETE: Remove files not in sent list
    DELETE FROM files 
    WHERE contact_id = @contact_id 
      AND id NOT IN (SELECT id FROM @sent_ids);
    
    -- INSERT: New files (is_new_upload = true)
    INSERT INTO files (id, contact_id, file_name, relative_path)
    SELECT 
      JSON_VALUE(value, '$.id'),
      @contact_id,
      JSON_VALUE(value, '$.file_name'),
      JSON_VALUE(value, '$.relative_path')
    FROM OPENJSON(@files_json)
    WHERE JSON_VALUE(value, '$.is_new_upload') = 'true';
  END
]]></query>
```

## Security

- Path traversal protection built-in
- Extension whitelist validation
- Size limit enforcement
- Validate file ownership in SQL before operations

## Related Topics

- [File Downloads](10-file-downloads.md) - Downloading uploaded files
- [Configuration](02-configuration.md) - file_management.xml details
