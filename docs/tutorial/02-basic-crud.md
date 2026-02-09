# Building CRUD Endpoints

In the previous topic, you built a simple hello-world endpoint. Now let's build something real — a **Create** endpoint for our phonebook contacts API, followed by a **Read** endpoint to retrieve contacts.

By the end of this topic, you'll be able to add contacts to your database and fetch them back through your API.

## Creating Contacts (POST)

Add this endpoint definition to your `sql.xml`, inside the `<queries>` block:

```xml
<create_contact>
  <route>contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <response_structure>single</response_structure>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @active bit = {{active}};

    if (@active is null)
    begin
        set @active = 1;
    end

    insert into [contacts] (id, name, phone, active) 
    output inserted.id, inserted.name, inserted.phone, inserted.active
    values (newid(), @name, @phone, @active);
    ]]>
  </query>
</create_contact>
```

There are several new concepts here. Let's examine each one.

### Custom Routes with `<route>`

```xml
<route>contacts</route>
```

Remember how `<hello_world>` automatically became `/hello_world`? The `<route>` tag overrides that behavior. Even though the XML tag is `<create_contact>`, the endpoint is accessible at `/contacts` — not `/create_contact`.

This is essential for RESTful design, where multiple operations share the same path and are distinguished by HTTP verb:

| Endpoint Definition        | Route       | Verb   |
|----------------------------|-------------|--------|
| `<create_contact>`         | `/contacts` | POST   |
| `<search_contacts>`        | `/contacts` | GET    |

Both share the same `/contacts` route, but respond to different HTTP verbs.

### HTTP Verbs with `<verb>`

```xml
<verb>POST</verb>
```

This restricts the endpoint to only respond to POST requests. A GET request to `/contacts` won't hit this endpoint — it'll hit whichever endpoint is configured for GET on the same route.

Available verbs: `GET`, `POST`, `PUT`, `DELETE`, `PATCH`

### Mandatory Parameters with `<mandatory_parameters>`

```xml
<mandatory_parameters>name,phone</mandatory_parameters>
```

This tells the application: "Before even running the SQL, check that `name` and `phone` are present in the request. If either is missing, return HTTP 400 Bad Request immediately."

The error response looks like:

```json
{
  "error": "Bad request: missing mandatory parameter(s): name"
}
```

This saves a round-trip to the database for obviously invalid requests.

> **Note**: Parameters are comma-separated with no spaces. `name,phone` — not `name, phone`.

### Custom Status Codes with `<success_status_code>`

```xml
<success_status_code>201</success_status_code>
```

By default, successful responses return HTTP 200 OK. For resource creation, REST convention says to return **201 Created**. This tag overrides the default.

### Response Structure with `<response_structure>`

```xml
<response_structure>single</response_structure>
```

This controls the shape of the JSON response:

| Value      | Behavior |
|------------|----------|
| `auto`     | Default. Single row → object. Multiple rows → array. |
| `single`   | Always return only the first row as a plain object. |
| `array`    | Always return an array, even for a single row. |

Although in this example we expect only one row to be returned (the newly created contact), hence ommitting this optional tag would still work fine (and is recommended for ease of use). But we explicitly set it to `single` just to demonstrate how it works in cases where you want to enforce a specific response shape regardless of the number of rows returned.

### The SQL Query Itself

```sql
declare @name nvarchar(500) = {{name}};
declare @phone nvarchar(100) = {{phone}};
declare @active bit = {{active}};

if (@active is null)
begin
    set @active = 1;
end

insert into [contacts] (id, name, phone, active) 
output inserted.id, inserted.name, inserted.phone, inserted.active
values (newid(), @name, @phone, @active);
```

Key points:
- **`{{active}}` is optional** — not listed in `mandatory_parameters`, so it can be NULL. The SQL defaults it to `1` (active).
- **`newid()`** generates a new GUID for the primary key.
- **`OUTPUT inserted.*`** returns the newly created row — this is what becomes the JSON response.

### Test It

```bash
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Alice Smith\", \"phone\": \"555-0101\"}"
```

> **Windows CMD note**: Use double quotes for the outer string and escape inner quotes with `\"`. Or use PowerShell:
> ```powershell
> curl -Method POST http://localhost:5165/contacts `
>   -ContentType "application/json" `
>   -Body '{"name": "Alice Smith", "phone": "555-0101"}'
> ```

Response (HTTP 201 Created):
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Alice Smith",
  "phone": "555-0101",
  "active": true
}
```

Add a few more contacts so we have data to work with:

```bash
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Bob Jones\", \"phone\": \"555-0102\"}"

curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Charlie Brown\", \"phone\": \"555-0103\"}"
```

## Reading Contacts (GET)

Now let's add an endpoint to retrieve contacts. Add this to `sql.xml`:

```xml
<search_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};

    select id, name, phone, active 
    from [contacts]
    where
      (@name is null or [name] like '%' + @name + '%')
      and (@phone is null or [phone] like '%' + @phone + '%')
    order by name asc;
    ]]>
  </query>
</search_contacts>
```

### How the Filtering Works

The `WHERE` clause uses a common pattern for optional filters:

```sql
(@name is null or [name] like '%' + @name + '%')
```

This means:
- If `name` is not provided → `@name` is NULL → the condition is always true → no filtering
- If `name` is provided → filter contacts where the name contains the search term

This way a single endpoint handles both "get all contacts" and "search contacts" scenarios.

### Test It

**Get all contacts:**
```bash
curl http://localhost:5165/contacts
```

Response:
```json
[
  {
    "id": "a1b2c3d4-...",
    "name": "Alice Smith",
    "phone": "555-0101",
    "active": true
  },
  {
    "id": "b2c3d4e5-...",
    "name": "Bob Jones",
    "phone": "555-0102",
    "active": true
  },
  {
    "id": "c3d4e5f6-...",
    "name": "Charlie Brown",
    "phone": "555-0103",
    "active": true
  }
]
```

**Search by name:**
```bash
curl "http://localhost:5165/contacts?name=alice"
```

Response:
```json
{
  "id": "a1b2c3d4-...",
  "name": "Alice Smith",
  "phone": "555-0101",
  "active": true
}
```

> Notice: when only one row is returned, the default `auto` response structure returns a plain object instead of an array. We'll explore how to control this in a later topic.

**Search by phone:**
```bash
curl "http://localhost:5165/contacts?phone=0102"
```

```json
{
  "id": "b2c3d4e5-...",
  "name": "Bob Jones",
  "phone": "555-0102",
  "active": true
}
```

## Same Route, Different Verbs

Notice that both `create_contact` and `search_contacts` use the route `/contacts`. The application resolves which endpoint to call based on the HTTP verb:

```
POST /contacts  →  <create_contact>   (creates a new contact)
GET  /contacts  →  <search_contacts>  (lists/searches contacts)
```

This is a core concept you'll use throughout the tutorial. The combination of **route + verb** uniquely identifies an endpoint.

## Handling Duplicates with SQL Error Codes

The sample `sql.xml` that ships with the project includes a duplicate check in `create_contact`. Let's understand how that works:

```sql
if ((select count(*) from [contacts] where name = @name and phone = @phone) > 0)
begin 
    set @error_msg = 'Contact with name ' + @name 
                   + ' and phone ' + @phone + ' already exists';
    throw 50409, @error_msg, 1;
    return;
end
```

The magic number is **50409**. Here's the pattern:

| SQL Error Code | HTTP Status Returned |
|----------------|----------------------|
| 50400          | 400 Bad Request      |
| 50404          | 404 Not Found        |
| 50409          | 409 Conflict         |
| 50500          | 500 Internal Error   |

The formula: **50000 + HTTP status code**. The application strips the `50` prefix and uses the remainder as the HTTP response code.

Try creating a duplicate:

```bash
curl -X POST http://localhost:5165/contacts \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Alice Smith\", \"phone\": \"555-0101\"}"
```

Response (HTTP 409 Conflict):
```json
{
  "error": "Contact with name Alice Smith and phone 555-0101 already exists"
}
```

This is powerful — your SQL controls not just the data but also the HTTP error semantics. No application code needed.

## Summary: Tags Introduced So Far

| Tag                        | Required? | Default            | Description                                |
|----------------------------|-----------|--------------------|--------------------------------------------|
| `<query>`                  | Yes       | —                  | The SQL query to execute                   |
| `<route>`                  | No        | XML tag name       | Custom URL path for the endpoint           |
| `<verb>`                   | No        | Any verb           | Restricts endpoint to a specific HTTP verb |
| `<mandatory_parameters>`   | No        | None               | Comma-separated list of required params    |
| `<success_status_code>`    | No        | 200                | HTTP status code for successful responses  |
| `<response_structure>`     | No        | `auto`             | Controls JSON shape: `auto`, `single`, `array` |

---

### What You Learned

- How to create a POST endpoint that inserts data and returns the new record
- How to create a GET endpoint with optional search filters
- How `<route>` overrides the default URL path
- How `<verb>` separates endpoints that share the same route
- How `<mandatory_parameters>` validates required fields before hitting the database
- How SQL `THROW 50xxx` controls HTTP error responses
- How `<success_status_code>` and `<response_structure>` customize responses

---

**Next:** [Parameters Deep Dive →](03-parameters.md)

**[Back to Tutorial Index](index.md)**
