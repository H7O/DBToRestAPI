# 20 — Settings Variables

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
    "url": "https://api.weatherapi.com/v1/current.json?key=YOUR_KEY&q={{city}}",
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
        "url": "{s{weather_api_url}}/current.json?key={s{weather_api_key}}&q={{city}}",
        "method": "GET"
      }
    }http};

    SELECT
      JSON_VALUE(@weather, '$.current.temp_c') AS temperature,
      JSON_VALUE(@weather, '$.current.condition.text') AS condition;
  ]]></query>
</weather_lookup>
```

The `{s{weather_api_key}}` reference is resolved during parameter processing —
the actual value from `<vars>` is injected as a parameterized SQL variable, just
like `{{param}}` values from the request.

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

Here's a realistic pattern — calling a partner payment API with credentials
stored safely in config:

```xml
<!-- settings.xml -->
<vars>
  <payment_api_url>https://api.stripe.com/v1</payment_api_url>
  <payment_api_key>sk_live_abc123</payment_api_key>
</vars>
```

```xml
<!-- sql.xml -->
<create_payment>
  <route>payments</route>
  <verb>POST</verb>
  <mandatory_parameters>amount,currency,customer_id</mandatory_parameters>
  <query><![CDATA[
    DECLARE @payment NVARCHAR(MAX) = {http{
      {
        "url": "{s{payment_api_url}}/payment_intents",
        "method": "POST",
        "headers": {
          "Authorization": "Bearer {s{payment_api_key}}",
          "Content-Type": "application/x-www-form-urlencoded"
        },
        "body": "amount={{amount}}&currency={{currency}}&customer={{customer_id}}"
      }
    }http};

    IF @payment IS NULL
      THROW 50502, 'Payment service unavailable', 1;

    INSERT INTO payments (intent_id, amount, currency, customer_id, status)
    VALUES (
      JSON_VALUE(@payment, '$.id'),
      {{amount}},
      {{currency}},
      {{customer_id}},
      'pending'
    );

    SELECT
      JSON_VALUE(@payment, '$.id') AS payment_intent_id,
      JSON_VALUE(@payment, '$.client_secret') AS client_secret;
  ]]></query>
</create_payment>
```

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

**Next:** [What's Next? →](19-whats-next.md)

**[Back to Tutorial Index](index.md)**
