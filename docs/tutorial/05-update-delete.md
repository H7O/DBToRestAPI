# Update & Delete Operations

Your phonebook API can create and read contacts. Now let's complete the CRUD cycle with **Update** (PUT) and **Delete** (DELETE) endpoints, plus a bonus **activate/deactivate** action endpoint.

## Updating Contacts (PUT)

Add this endpoint to your `sql.xml`:

```xml
<update_contact>
  <route>contacts/{{id}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id,name,phone</mandatory_parameters>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @error_msg nvarchar(500);

    -- Check if contact exists
    if ((select count(*) from [contacts] where id = @id) < 1)
    begin 
        set @error_msg = 'Contact with id ' 
                       + cast(@id as nvarchar(50)) + ' does not exist';
        throw 50404, @error_msg, 1;
        return;
    end

    -- Update and return the updated contact
    update [contacts] 
    set [name] = @name, phone = @phone 
    output inserted.id, inserted.name, inserted.phone
    where id = @id;
    ]]>
  </query>
</update_contact>
```

### Key Patterns

**Route parameter for the resource ID:**
```xml
<route>contacts/{{id}}</route>
```

The `id` comes from the URL path: `PUT /contacts/a1b2c3d4-...`

**Body parameters for the updated fields:**

The `name` and `phone` come from the request body. Since `id` is in both the route and `mandatory_parameters`, the application ensures it's always present.

**Existence check with 404:**

```sql
if ((select count(*) from [contacts] where id = @id) < 1)
begin 
    throw 50404, @error_msg, 1;
    return;
end
```

Always check that the resource exists before updating. Return 404 if it doesn't.

**OUTPUT clause returns the updated record:**

```sql
update [contacts] 
set [name] = @name, phone = @phone 
output inserted.id, inserted.name, inserted.phone
where id = @id;
```

The `OUTPUT inserted.*` clause is SQL Server's way of returning the modified row — perfect for returning the updated resource to the client.

### Test It

First, get a contact ID:
```bash
curl http://localhost:5165/contacts
```

Then update it (replace with your actual ID):
```bash
curl -X PUT http://localhost:5165/contacts/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Alice Johnson\", \"phone\": \"555-9999\"}"
```

Response (HTTP 200):
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Alice Johnson",
  "phone": "555-9999"
}
```

**Test the 404 case:**
```bash
curl -X PUT http://localhost:5165/contacts/00000000-0000-0000-0000-000000000000 \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Nobody\", \"phone\": \"000-0000\"}"
```

Response (HTTP 404):
```json
{
  "error": "Contact with id 00000000-0000-0000-0000-000000000000 does not exist"
}
```

## Deleting Contacts (DELETE)

```xml
<delete_contact>
  <route>contacts/{{id}}</route>
  <verb>DELETE</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @error_msg nvarchar(500);

    if ((select count(*) from [contacts] where id = @id) < 1)
    begin 
        set @error_msg = 'Contact with id ' 
                       + cast(@id as nvarchar(50)) + ' does not exist';
        throw 50404, @error_msg, 1;
        return;
    end

    delete from [contacts] 
    OUTPUT DELETED.id, DELETED.name, DELETED.phone, DELETED.active
    where id = @id;
    ]]>
  </query>
</delete_contact>
```

### Points of Interest

**`OUTPUT DELETED.*` instead of `OUTPUT inserted.*`:**

For DELETE operations, use `DELETED` (not `inserted`) to return the row that was just removed. This lets the client see what was deleted.

**Default status code is 200:**

Some REST APIs return HTTP 204 (No Content) for deletes. Here, since we're returning the deleted data, 200 is appropriate. If you wanted an empty response with 204, you could add `<success_status_code>204</success_status_code>` and remove the OUTPUT clause.

### Test It

```bash
curl -X DELETE http://localhost:5165/contacts/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

Response (HTTP 200):
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Alice Johnson",
  "phone": "555-9999",
  "active": true
}
```

Verify it's gone:
```bash
curl http://localhost:5165/contacts
```

The deleted contact should no longer appear.

## Bonus: Activate/Deactivate Action Endpoint

Sometimes you want a "soft delete" — marking a contact as inactive instead of removing it. Let's add an action endpoint:

```xml
<activate_deactivate_contact>
  <route>contacts/{{id}}/{{status_action}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id</mandatory_parameters>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @status_action nvarchar(50) = {{status_action}};
    declare @error_msg nvarchar(500);

    -- Validate the action
    if (@status_action is null or @status_action = ''
        or @status_action not in ('activate', 'deactivate'))
    begin
      set @error_msg = 'Invalid status action';
      throw 50400, @error_msg, 1;
      return;
    end

    -- Check if contact exists
    if ((select count(*) from [contacts] where id = @id) < 1)
    begin 
        set @error_msg = 'Contact with id ' 
                       + cast(@id as nvarchar(50)) + ' does not exist';
        throw 50404, @error_msg, 1;
        return;
    end

    -- Set status based on action
    declare @status_bit bit = case 
      when @status_action = 'activate' then 1 
      else 0 
    end;

    update [contacts] 
    set [active] = @status_bit
    output 
      inserted.id, 
      inserted.name, 
      inserted.phone,
      case when inserted.active = 1 then 'active' else 'inactive' end as status
    where id = @id;
    ]]>
  </query>
</activate_deactivate_contact>
```

### Route Design

```xml
<route>contacts/{{id}}/{{status_action}}</route>
```

This creates action-style URLs:
- `PUT /contacts/{id}/deactivate` — soft-delete a contact
- `PUT /contacts/{id}/activate` — reactivate a contact

The `{{status_action}}` route parameter becomes a SQL variable that controls the behavior.

### Computed Output Column

```sql
case when inserted.active = 1 then 'active' else 'inactive' end as status
```

The OUTPUT clause can contain expressions — not just columns. Here we convert the boolean `active` flag into a human-readable `"status"` field.

### Test It

Deactivate a contact:
```bash
curl -X PUT http://localhost:5165/contacts/b2c3d4e5-.../deactivate
```

Response:
```json
{
  "id": "b2c3d4e5-...",
  "name": "Bob Jones",
  "phone": "555-0102",
  "status": "inactive"
}
```

Reactivate:
```bash
curl -X PUT http://localhost:5165/contacts/b2c3d4e5-.../activate
```

Response:
```json
{
  "id": "b2c3d4e5-...",
  "name": "Bob Jones",
  "phone": "555-0102",
  "status": "active"
}
```

Invalid action:
```bash
curl -X PUT http://localhost:5165/contacts/b2c3d4e5-.../archive
```

Response (HTTP 400):
```json
{
  "error": "Invalid status action"
}
```

## Complete REST API Summary

At this point, your phonebook API offers these endpoints:

| Verb   | Route                              | Action                  |
|--------|------------------------------------|-------------------------|
| POST   | `/contacts`                        | Create a contact         |
| GET    | `/contacts`                        | List/search contacts     |
| GET    | `/contacts/{{id}}`                 | Get a single contact     |
| PUT    | `/contacts/{{id}}`                 | Update a contact         |
| DELETE | `/contacts/{{id}}`                 | Delete a contact         |
| PUT    | `/contacts/{{id}}/activate`        | Activate a contact       |
| PUT    | `/contacts/{{id}}/deactivate`      | Deactivate a contact     |

All built with zero application code — just SQL and XML.

---

### What You Learned

- How to create PUT endpoints for updating resources
- How to create DELETE endpoints with `OUTPUT DELETED.*`
- How existence checks with `THROW 50404` return proper 404 responses
- How to design action-style routes like `/contacts/{id}/activate`
- How route parameters can control SQL logic (not just filter data)
- How to use computed columns in OUTPUT clauses

---

**Next:** [XML Configuration Structure →](06-xml-structure.md)

**[Back to Tutorial Index](index.md)**
