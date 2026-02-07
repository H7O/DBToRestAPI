# File Uploads

In this topic, you'll learn how to add file upload support to your contacts API — letting users attach documents, photos, or other files to contacts. Files can be stored locally, on SFTP servers, or in multiple locations simultaneously.

## How File Uploads Work

```
Client sends file(s) → Application saves to configured store(s)
                      → Generates metadata JSON (paths, IDs, sizes)
                      → Passes metadata to your SQL query
                      → Your SQL stores the metadata in the database
```

The application handles the file storage. Your SQL handles the metadata.

## Step 1: Configure File Stores

Define where files are stored in `/config/file_management.xml`:

```xml
<settings>
  <file_management>
    <!-- Path structure for stored files -->
    <relative_file_path_structure>
      {date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}
    </relative_file_path_structure>

    <!-- Global restrictions -->
    <permitted_file_extensions>.pdf,.docx,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>10485760</max_file_size_in_bytes>
    <max_number_of_files>5</max_number_of_files>

    <!-- Local file stores -->
    <local_file_store>
      <primary>
        <base_path><![CDATA[c:\uploads\]]></base_path>
      </primary>
      <backup>
        <base_path><![CDATA[d:\backup\uploads\]]></base_path>
        <optional>true</optional>
      </backup>
    </local_file_store>

    <!-- SFTP file stores (optional) -->
    <sftp_file_store>
      <remote>
        <host>sftp.example.com</host>
        <port>22</port>
        <username>uploader</username>
        <password>secret</password>
        <base_path>/uploads/</base_path>
      </remote>
    </sftp_file_store>
  </file_management>
</settings>
```

### Path Structure Variables

The `<relative_file_path_structure>` controls how uploaded files are organized:

| Variable | Output |
|----------|--------|
| `{date{yyyy}}` | `2025` |
| `{date{MMM}}` | `Jan` |
| `{date{dd}}` | `24` |
| `{{guid}}` | `a1b2c3d4-...` (unique per upload) |
| `{file{name}}` | `document.pdf` (original filename) |

Result: `2025/Jan/24/a1b2c3d4-.../document.pdf`

### Multiple Stores

When you specify multiple stores, the file is saved to **all** of them. This provides:
- **Redundancy** — local + SFTP backup
- **Regional access** — same file in multiple locations
- Stores marked `<optional>true</optional>` don't cause failures if unavailable

## Step 2: Create an Upload Endpoint

Add the upload endpoint to `sql.xml`:

```xml
<upload_contact_documents>
  <route>contacts/{{contact_id}}/documents</route>
  <verb>POST</verb>
  <mandatory_parameters>contact_id</mandatory_parameters>
  <success_status_code>201</success_status_code>

  <file_management>
    <stores>primary</stores>
    <permitted_file_extensions>.pdf,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>5242880</max_file_size_in_bytes>
    <max_number_of_files>3</max_number_of_files>
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
  </file_management>

  <query>
    <![CDATA[
    declare @contact_id UNIQUEIDENTIFIER = {{contact_id}};
    declare @files_json nvarchar(max) = {{attachments}};

    -- Verify contact exists
    if not exists (select 1 from contacts where id = @contact_id)
    begin
      throw 50404, 'Contact not found', 1;
      return;
    end

    -- Insert file metadata from the generated JSON
    insert into contact_files (id, contact_id, file_name, relative_path, mime_type, file_size)
    select 
      TRY_CAST(JSON_VALUE(value, '$.id') as UNIQUEIDENTIFIER),
      @contact_id,
      JSON_VALUE(value, '$.file_name'),
      JSON_VALUE(value, '$.relative_path'),
      JSON_VALUE(value, '$.mime_type'),
      JSON_VALUE(value, '$.size')
    from OPENJSON(@files_json);

    -- Return uploaded files
    select id, file_name, relative_path, mime_type, file_size
    from contact_files 
    where contact_id = @contact_id
    order by file_name;
    ]]>
  </query>
</upload_contact_documents>
```

### Endpoint-Level File Settings

| Setting | Description |
|---------|-------------|
| `<stores>` | Comma-separated store names to save to |
| `<permitted_file_extensions>` | Override global allowed extensions |
| `<max_file_size_in_bytes>` | Override global max file size |
| `<max_number_of_files>` | Override global max file count |
| `<files_json_field_or_form_field_name>` | JSON field name containing file data |

## Step 3: Upload Files

### Method 1: JSON with Base64

```bash
curl -X POST http://localhost:5165/contacts/abc-123/documents \
  -H "Content-Type: application/json" \
  -d '{
    "attachments": [
      {
        "file_name": "id_document.pdf",
        "base64_content": "JVBERi0xLjQK..."
      }
    ]
  }'
```

### Method 2: Multipart Form Data

```bash
curl -X POST http://localhost:5165/contacts/abc-123/documents \
  -F "attachments=[{\"file_name\": \"photo.jpg\"}]" \
  -F "file=@/path/to/photo.jpg"
```

## What the Application Generates

After saving the files, the application creates a JSON array and passes it to your SQL as the `{{attachments}}` parameter:

```json
[
  {
    "id": "guid-generated-by-system",
    "file_name": "id_document.pdf",
    "relative_path": "2025/Jan/24/a1b2c3d4/id_document.pdf",
    "mime_type": "application/pdf",
    "size": 102400,
    "extension": ".pdf",
    "is_new_upload": true
  }
]
```

Your SQL parses this JSON to store the metadata however you see fit.

## Create the Files Table

You'll need a table to store file metadata:

```sql
CREATE TABLE contact_files (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    contact_id UNIQUEIDENTIFIER NOT NULL,
    file_name NVARCHAR(500),
    relative_path NVARCHAR(1000),
    mime_type NVARCHAR(200),
    file_size BIGINT,
    uploaded_at DATETIME2 DEFAULT GETUTCDATE(),
    FOREIGN KEY (contact_id) REFERENCES contacts(id)
);
```

---

### What You Learned

- How to configure file stores (local and SFTP) in `file_management.xml`
- How path structure variables organize uploaded files
- How to create upload endpoints with `<file_management>` settings
- Two upload methods: JSON/base64 and multipart form data
- How the application generates file metadata JSON for your SQL
- How to store file metadata in the database

---

**Next:** [File Downloads →](15-file-downloads.md)

**[Back to Tutorial Index](index.md)**
