# Your First API Endpoint

In this topic, you'll start the application and build your very first API endpoint — a simple "Hello World" that takes a name and responds with a greeting.

## Starting the Application

### If you downloaded a release:

Open a terminal in the folder where you extracted DBToRestAPI and run:

**Windows:**
```powershell
.\DBToRestAPI.exe
```

**Linux / macOS:**
```bash
./DBToRestAPI
```

### If you built from source:

```bash
cd DBToRestAPI/DBToRestAPI
dotnet run
```

You should see output indicating the application has started:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:7054
      Now listening on: http://localhost:5165
```

> **Tip:** The ports are configured in `Properties/launchSettings.json`. You can change them there if they conflict with other services.

Leave this terminal running. Open a **second terminal** for testing.

## Testing the Built-In Hello World Endpoint

The sample `sql.xml` that ships with the project already includes a `hello_world` endpoint. Let's try it:

```bash
curl http://localhost:5165/hello_world
```

You should get back a JSON response like:

```json
{
  "message_from_db": "hello world! Time now is 2025-01-15 14:30:22.123"
}
```

Now try passing a name:

```bash
curl "http://localhost:5165/hello_world?name=Alice"
```

Response:
```json
{
  "message_from_db": "hello Alice! Time now is 2025-01-15 14:30:45.789"
}
```

It works! But how? Let's look at the configuration that makes this happen.

## Understanding the Endpoint Definition

Open `/config/sql.xml`. At the top of the file, you'll find the `hello_world` definition:

```xml
<settings>
  <queries>
    <hello_world>
      <query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};

        if (@name is null or ltrim(rtrim(@name)) = '')
        begin
            set @name = 'world';
        end
        select 
          'hello ' + @name + '! Time now is ' 
          + convert(nvarchar(50), getdate(), 121) as message_from_db;
        ]]>
      </query>
    </hello_world>
  </queries>
</settings>
```

Let's break down every part of this:

### The XML Structure

```
<settings>           ← Root element (always required)
  <queries>          ← Container for all endpoint definitions
    <hello_world>    ← Endpoint name → becomes the route: /hello_world
      <query>        ← The SQL to execute
      </query>
    </hello_world>
  </queries>
</settings>
```

**Key rule**: The XML tag name becomes the URL route. `<hello_world>` is accessible at `/hello_world`.

### The `<query>` Element and CDATA

```xml
<query>
  <![CDATA[
    -- Your SQL goes here
  ]]>
</query>
```

The `<![CDATA[ ... ]]>` wrapper tells the XML parser to treat the content as raw text. This is important because SQL commonly uses characters like `<`, `>`, and `&` that have special meaning in XML. Without CDATA, this query would break the XML:

```sql
-- This would break XML without CDATA:
WHERE age > 18 AND name <> 'Unknown'
```

> **Rule of thumb**: Always wrap your SQL in `<![CDATA[ ... ]]>`.

### Parameter Injection with `{{name}}`

```sql
declare @name nvarchar(500) = {{name}};
```

The `{{name}}` marker tells DBToRestAPI: "Replace this with the value of the `name` parameter from the HTTP request."

Where can the `name` parameter come from? Multiple places, checked in this priority order:

1. **Query string** (highest priority): `?name=Alice`
2. **Route segment**: `/hello_world/{{name}}` (if configured)
3. **Request body** (JSON): `{"name": "Alice"}`
4. **HTTP headers** (lowest priority): `name: Alice`

If `name` isn't provided anywhere, `{{name}}` evaluates to `NULL` — which is why the SQL has a fallback:

```sql
if (@name is null or ltrim(rtrim(@name)) = '')
begin
    set @name = 'world';
end
```

### The SELECT → JSON Response

```sql
select 'hello ' + @name + '!' as message_from_db;
```

Whatever your SQL `SELECT` returns becomes the JSON response. The column name becomes the JSON key:

| SQL Column        | JSON Key            |
|-------------------|---------------------|
| `message_from_db` | `"message_from_db"` |

Multiple columns? Each becomes a key:

```sql
SELECT 'Alice' as name, '555-1234' as phone;
```

Returns:
```json
{
  "name": "Alice",
  "phone": "555-1234"
}
```

## Building Your Own Endpoint

Now let's create a brand-new endpoint. Add this inside the `<queries>` block in `sql.xml`:

```xml
<server_time>
  <query>
    <![CDATA[
    SELECT 
      GETDATE() as current_time,
      @@VERSION as server_version,
      DB_NAME() as database_name;
    ]]>
  </query>
</server_time>
```

Save the file. Remember — **hot-reload is automatic**. No need to restart the application.

Test it immediately:

```bash
curl http://localhost:5165/server_time
```

Response:
```json
{
  "current_time": "2025-01-15T14:35:00",
  "server_version": "Microsoft SQL Server 2022 ...",
  "database_name": "test"
}
```

You just created a new REST endpoint by writing XML and SQL. No compilation, no deployment, no restart.

## What About GET, POST, PUT, DELETE?

You may have noticed: we didn't specify an HTTP verb for `hello_world` or `server_time`. When you omit the `<verb>` tag, the endpoint responds to **any** HTTP verb (GET, POST, PUT, DELETE — all work).

For the phonebook API we'll build in the next topics, we'll assign specific verbs to each endpoint to follow REST conventions. Here's a preview:

| Operation           | Verb   | Route               |
|---------------------|--------|----------------------|
| List contacts       | GET    | `/contacts`          |
| Create a contact    | POST   | `/contacts`          |
| Update a contact    | PUT    | `/contacts/{{id}}`   |
| Delete a contact    | DELETE | `/contacts/{{id}}`   |

We'll cover this in detail starting with the next topic.

## Quick Experiment: Try Breaking Things

Before moving on, try these experiments to build intuition:

**1. What happens with a missing database?**

Change the connection string in `settings.xml` to point to a non-existent database. What error do you get?

**2. What happens with invalid SQL?**

Add an endpoint with broken SQL:

```xml
<broken>
  <query><![CDATA[SELECTTTT * FROM nonexistent;]]></query>
</broken>
```

Call it. What does the response look like?

**3. What about multiple rows?**

```xml
<multi_row_test>
  <query>
    <![CDATA[
    SELECT 1 as id, 'Alice' as name
    UNION ALL
    SELECT 2, 'Bob'
    UNION ALL
    SELECT 3, 'Charlie';
    ]]>
  </query>
</multi_row_test>
```

Does it return an array or a single object?

> **Spoiler**: By default (`auto` response structure), multiple rows return as an array, and a single row returns as a plain object. We'll explore response structures in a later topic.

---

### What You Learned

- How to start the application and test endpoints with cURL
- The XML structure: `<settings>` → `<queries>` → `<endpoint_name>` → `<query>`
- How `<![CDATA[ ... ]]>` protects SQL from XML parsing issues
- How `{{param}}` injects request parameters into SQL safely
- That column names in your SELECT become JSON keys
- That hot-reload means no restarts after config changes

---

**Next:** [Building CRUD Endpoints →](02-basic-crud.md)

**[Back to Tutorial Index](index.md)**
