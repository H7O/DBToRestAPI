# 19 — Settings Variables

In earlier topics you learned how to call external APIs from SQL using
`{http{...}http}` markers.  You probably noticed that API keys and URLs were
hardcoded directly in the query text.  Settings variables solve this — they let
you store values in configuration and reference them in queries with `{s{name}}`
or `{settings{name}}`.

---

## Why Settings Variables?

Consider this embedded HTTP call from Topic 16:

```sql
DECLARE @result NVARCHAR(MAX) = {http{
  {
    "url": "https://api.weatherapi.com/v1/current.json?key=YOUR_KEY",
    "query": { "q": "{{city}}" },
    "method": "GET"
  }
}http};
```

The API key (`YOUR_KEY`) is in plain text, visible to anyone with access to
`sql.xml`.  If you need to rotate the key, you must find and update every query
that uses it.

Settings variables fix both problems:

1. **Centralise** — define the value once in config, reference it everywhere.
2. **Encrypt** — use the built-in encryption support to protect secrets at rest.

---

## Step 1: Add a `<vars>` Section

Open `config/settings.xml` and add a `<vars>` block anywhere inside `<settings>`:

```xml
<settings>
  <!-- ... existing connection strings, cors, etc. ... -->

  <vars>
    <weather_api_key>YOUR_KEY</weather_api_key>
    <weather_api_url>https://api.weatherapi.com/v1</weather_api_url>
    <notification_email>alerts@example.com</notification_email>
  </vars>
</settings>
```

Each child element becomes a named variable you can reference in queries.

> **Hot-reload works here too.** Change a value, save the file, and the next
> request picks it up automatically.

---

## Step 2: Reference Variables in a Query

Use `{s{name}}` (short) or `{settings{name}}` (long) — both work identically:

```xml
<weather_lookup>
  <route>weather/{{city}}</route>
  <verb>GET</verb>
  <query><![CDATA[
    DECLARE @weather NVARCHAR(MAX) = {http{
      {
        "url": "{s{weather_api_url}}/current.json?key={s{weather_api_key}}",
        "query": { "q": "{{city}}" },
        "method": "GET"
      }
    }http};

    SELECT
      JSON_VALUE(@weather, '$.data.current.temp_c') AS temperature,
      JSON_VALUE(@weather, '$.data.current.condition.text') AS condition;
  ]]></query>
</weather_lookup>
```

Inside a `{http{...}http}` block, a `{s{...}}` value is written into the call's
JSON before the call is made, escaped like any value inside a JSON string. Your
own values, such as the key, can sit in `"url"`. The caller's `{{city}}` goes in
`"query"`, which percent-encodes it: in `"url"`, a city such as `a&key=other`
would add a parameter of its own. Outside a block, in the SQL itself, a
`{s{...}}` value is bound as a SQL parameter, like `{{param}}`, so it never
becomes SQL text.

---

## Step 3: Encrypt Sensitive Values

You almost certainly don't want API keys sitting in plain text on disk.  Add
the `vars` section (or individual keys) to your encryption configuration:

```xml
<settings_encryption>
  <sections_to_encrypt>
    <!-- Encrypt a specific var -->
    <section>vars:weather_api_key</section>

    <!-- Or encrypt all vars at once -->
    <!-- <section>vars</section> -->
  </sections_to_encrypt>
</settings_encryption>
```

On the next startup, the plain-text value is encrypted in-place:

```xml
<vars>
  <weather_api_key>encrypted:CfDJ8NhY2kB...base64...</weather_api_key>
  <weather_api_url>https://api.weatherapi.com/v1</weather_api_url>
</vars>
```

Your queries don't change — `{s{weather_api_key}}` still works because the
engine decrypts the value transparently at runtime.

---

## Practical Example: Secure Microservice Integration

Here's a realistic pattern — calling a payment API with credentials stored
safely in config:

```xml
<!-- settings.xml -->
<vars>
  <payment_api_url>https://api.stripe.com/v1</payment_api_url>
  <stripe_secret_key>sk_live_abc123</stripe_secret_key>
</vars>
```

The call sits in the second query of a [chain](17-multi-query.md), whose first
query looked up the order's amount and customer in the database:

```sql
DECLARE @payment NVARCHAR(MAX) = {http{
  {
    "url": "{s{payment_api_url}}/payment_intents",
    "method": "POST",
    "headers": { "Authorization": "Bearer {s{stripe_secret_key}}" },
    "content_type": "application/x-www-form-urlencoded",
    "body_raw": "amount={pq{amount_cents}}&currency=usd&customer={pq{stripe_customer_id}}"
  }
}http};
```

The form goes in `body_raw`, with its type in `content_type`. The values in
the form come from the database, not the request: `&` or `=` in a caller's
value would add fields. The whole endpoint is in topic 17,
[POST with a form body](../topics/17-embedded-http-calls.md#post-with-a-form-body).

The Stripe secret key never appears in `sql.xml` — it lives in `settings.xml`
and can be encrypted at rest.

---

## Variable Names Are Case-Insensitive

All of these resolve to the same value:

- `{s{weather_api_key}}`
- `{s{Weather_Api_Key}}`
- `{settings{WEATHER_API_KEY}}`

---

## What Happens When a Variable Doesn't Exist?

If you reference a variable that isn't defined in `<vars>`, the parameter
resolves to `NULL` — the same behavior as any unresolved `{{param}}`:

```sql
DECLARE @key NVARCHAR(500) = {s{nonexistent_key}};
-- @key is NULL
```

Handle it defensively:

```sql
IF {s{partner_api_key}} IS NULL
  THROW 50500, 'Missing configuration: partner_api_key in vars', 1;
```

---

## Security: What's NOT Exposed

Only values inside the `<vars>` section are accessible via `{s{}}`.  Connection
strings, auth provider secrets, encryption keys, and all other configuration
sections are **never** exposed through settings variables.  This is intentional
— it prevents accidental data leakage through query results.

---

## Using Alongside Other Parameter Types

Settings variables work alongside all other parameter types in the same query:

```sql
-- From request body / query string
DECLARE @city NVARCHAR(100) = {{city}};

-- From JWT claims
DECLARE @user_email NVARCHAR(500) = {auth{email}};

-- From configuration
DECLARE @api_key NVARCHAR(500) = {s{weather_api_key}};

-- From HTTP headers
DECLARE @request_id NVARCHAR(100) = {h{X-Request-Id}};
```

---

### What You Learned

- The `<vars>` section in `settings.xml` for defining query-accessible configuration values
- The `{s{name}}` (short) and `{settings{name}}` (long) syntax
- Using settings variables to keep API keys out of query text
- Encrypting settings variables with `sections_to_encrypt`
- How unresolved variables default to `NULL`
- That only `<vars>` values are exposed — other config sections stay private

---

**Next:** [OpenAPI & Swagger UI →](20-openapi.md)

**[Back to Tutorial Index](index.md)**
