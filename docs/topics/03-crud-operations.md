# CRUD Operations

Complete patterns for Create, Read, Update, and Delete operations.

## Create (POST)

### Basic Insert

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  
  <query><![CDATA[
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    
    INSERT INTO [contacts] (id, name, phone)
    OUTPUT inserted.id, inserted.name, inserted.phone, inserted.active
    VALUES (NEWID(), @name, @phone);
  ]]></query>
</create_contact>
```

### With Duplicate Check

```xml
<query><![CDATA[
  DECLARE @name NVARCHAR(500) = {{name}};
  DECLARE @phone NVARCHAR(100) = {{phone}};
  
  IF EXISTS (SELECT 1 FROM [contacts] WHERE name = @name AND phone = @phone)
  BEGIN
    THROW 50409, 'Contact already exists', 1;
    RETURN;
  END
  
  INSERT INTO [contacts] (id, name, phone)
  OUTPUT inserted.*
  VALUES (NEWID(), @name, @phone);
]]></query>
```

## Read (GET)

### Single Record

```xml
<get_contact>
  <route>contacts/{{id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>id</mandatory_parameters>
  
  <query><![CDATA[
    -- A malformed id becomes NULL, which matches no row: a 404, not a conversion error.
    DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    
    IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id)
    BEGIN
      THROW 50404, 'Contact not found', 1;
      RETURN;
    END
    
    SELECT id, name, phone, active FROM [contacts] WHERE id = @id;
  ]]></query>
</get_contact>
```

### List with Pagination

```xml
<list_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  
  <query><![CDATA[
    DECLARE @take INT = ISNULL({{take}}, 100);
    DECLARE @skip INT = ISNULL({{skip}}, 0);
    
    IF @take > 1000 SET @take = 1000;
    IF @take < 1 SET @take = 100;
    IF @skip < 0 SET @skip = 0;
    
    SELECT id, name, phone, active
    FROM [contacts]
    ORDER BY name
    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
  ]]></query>
  
  <count_query><![CDATA[
    SELECT COUNT(*) FROM [contacts];
  ]]></count_query>
</list_contacts>
```

**Response:**
```json
{
  "count": 150,
  "data": [
    {"id": "...", "name": "Alice", "phone": "..."},
    {"id": "...", "name": "Bob", "phone": "..."}
  ]
}
```

### Search with Filters

```xml
<search_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  
  <query><![CDATA[
    -- An empty query-string value arrives as '', not NULL: NULLIF turns it back into "no filter".
    DECLARE @name NVARCHAR(500) = NULLIF({{name}}, '');
    DECLARE @phone NVARCHAR(100) = NULLIF({{phone}}, '');
    DECLARE @take INT = ISNULL(TRY_CONVERT(INT, NULLIF({{take}}, '')), 100);
    DECLARE @skip INT = ISNULL(TRY_CONVERT(INT, NULLIF({{skip}}, '')), 0);
    DECLARE @sort_by NVARCHAR(50) = ISNULL(NULLIF({{sort_by}}, ''), 'name');
    DECLARE @sort_order NVARCHAR(10) = ISNULL(NULLIF({{sort_order}}, ''), 'asc');
    IF @take < 1 OR @take > 1000 SET @take = 100;
    IF @skip < 0 SET @skip = 0;
    
    IF @sort_by NOT IN ('name', 'phone') SET @sort_by = 'name';
    IF @sort_order NOT IN ('asc', 'desc') SET @sort_order = 'asc';
    
    SELECT id, name, phone, active
    FROM [contacts]
    WHERE (@name IS NULL OR name LIKE '%' + @name + '%')
      AND (@phone IS NULL OR phone LIKE '%' + @phone + '%')
    ORDER BY
      CASE WHEN @sort_by = 'name' AND @sort_order = 'asc' THEN name END ASC,
      CASE WHEN @sort_by = 'name' AND @sort_order = 'desc' THEN name END DESC,
      CASE WHEN @sort_by = 'phone' AND @sort_order = 'asc' THEN phone END ASC,
      CASE WHEN @sort_by = 'phone' AND @sort_order = 'desc' THEN phone END DESC
    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
  ]]></query>
  
  <count_query><![CDATA[
    DECLARE @name NVARCHAR(500) = NULLIF({{name}}, '');
    DECLARE @phone NVARCHAR(100) = NULLIF({{phone}}, '');
    
    SELECT COUNT(*) FROM [contacts]
    WHERE (@name IS NULL OR name LIKE '%' + @name + '%')
      AND (@phone IS NULL OR phone LIKE '%' + @phone + '%');
  ]]></count_query>
</search_contacts>
```

## Update (PUT)

### Full Update

```xml
<update_contact>
  <route>contacts/{{id}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id,name,phone</mandatory_parameters>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    
    IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id)
    BEGIN
      THROW 50404, 'Contact not found', 1;
      RETURN;
    END
    
    UPDATE [contacts]
    SET name = @name, phone = @phone
    OUTPUT inserted.id, inserted.name, inserted.phone, inserted.active
    WHERE id = @id;
  ]]></query>
</update_contact>
```

### Status Toggle

```xml
<toggle_status>
  <route>contacts/{{id}}/{{action}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id</mandatory_parameters>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    DECLARE @action NVARCHAR(50) = {{action}};
    
    IF @action IS NULL OR @action NOT IN ('activate', 'deactivate')
    BEGIN
      THROW 50400, 'Invalid action', 1;
      RETURN;
    END
    
    IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id)
    BEGIN
      THROW 50404, 'Contact not found', 1;
      RETURN;
    END
    
    UPDATE [contacts]
    SET active = CASE WHEN @action = 'activate' THEN 1 ELSE 0 END
    OUTPUT inserted.*
    WHERE id = @id;
  ]]></query>
</toggle_status>
```

**Usage:** `PUT /contacts/abc-123/deactivate`

## Delete (DELETE)

### Hard Delete

```xml
<delete_contact>
  <route>contacts/{{id}}</route>
  <verb>DELETE</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <success_status_code>204</success_status_code>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
    
    IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id)
    BEGIN
      THROW 50404, 'Contact not found', 1;
      RETURN;
    END
    
    DELETE FROM [contacts]
    OUTPUT deleted.id, deleted.name
    WHERE id = @id;
  ]]></query>
</delete_contact>
```

### Soft Delete

```xml
<query><![CDATA[
  DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
  
  IF NOT EXISTS (SELECT 1 FROM [contacts] WHERE id = @id AND deleted_at IS NULL)
  BEGIN
    THROW 50404, 'Contact not found', 1;
    RETURN;
  END
  
  UPDATE [contacts]
  SET deleted_at = GETUTCDATE()
  OUTPUT inserted.id, inserted.deleted_at
  WHERE id = @id;
]]></query>
```

## Error Handling

### Cross-Database Error Syntax

| Database | Syntax |
|----------|--------|
| SQL Server | `THROW 50404, 'Not found', 1;` |
| MySQL | `SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 50404, MESSAGE_TEXT = 'Not found';` |
| PostgreSQL | `RAISE EXCEPTION '[50404] Not found';` inside a procedure that the query calls with the request values, such as `CALL check_category({{category}});` ([example](../reference/errors.md#raising-an-error-from-sql)). Plain SQL has no `RAISE`, and a `DO` block can't see request parameters. |
| Oracle | `BEGIN RAISE_APPLICATION_ERROR(-20404, 'Not found'); END;` (not mapped in 1.7.5: arrives as the generic 400) |
| SQLite | `SELECT RAISE(ABORT, '[50404] Not found');` inside a trigger only. Outside one, it is the generic 400. |
| IBM DB2 | `SIGNAL SQLSTATE '75000' SET MESSAGE_TEXT = '[50404] Not found';` inside a compound statement (`BEGIN ... END`) or a procedure (not mapped in 1.7.5: arrives as the generic 400) |

The response body, every other status, and when uploaded files are rolled back: [Errors, status codes and rollback](../reference/errors.md).

### Common Patterns

```sql
-- 400 Bad Request
THROW 50400, 'Invalid input', 1;

-- 401 Unauthorized
THROW 50401, 'Authentication required', 1;

-- 403 Forbidden
THROW 50403, 'Access denied', 1;

-- 404 Not Found
THROW 50404, 'Resource not found', 1;

-- 409 Conflict
THROW 50409, 'Resource already exists', 1;
```

## OUTPUT Clause

Use SQL Server's OUTPUT for returning affected rows:

```sql
-- INSERT
INSERT INTO [table] (col1)
OUTPUT inserted.id, inserted.col1, inserted.created_at
VALUES (@val);

-- UPDATE
UPDATE [table] SET col1 = @val
OUTPUT inserted.*
WHERE id = @id;

-- DELETE
DELETE FROM [table]
OUTPUT deleted.id, deleted.col1
WHERE id = @id;
```

## Related Topics

- [Parameters](04-parameters.md) - Parameter injection
- [Response Formats](05-response-formats.md) - Controlling output structure
- [Multi-Database](13-databases.md) - Cross-database operations
