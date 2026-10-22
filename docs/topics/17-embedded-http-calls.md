---
title: HTTP calls from SQL
summary: Call another API from inside an endpoint's query with a {http{ ... }http} block; the engine makes the call before the query runs and hands the answer to the SQL as a JSON parameter.
keywords: ["{http{", "}http}", http_variable_pattern, url, method, headers, query, body, body_raw, content_type, application/x-www-form-urlencoded, timeout_seconds, ignore_certificate_errors, follow_redirects, auth, bearer, api_key, key_header, key_query_param, basic, retry, max_attempts, delay_ms, exponential_backoff, retry_status_codes, skip, no_wait, status_code, data, error, "@http_response_1", JSON_VALUE, OPENJSON, 502, 503]
---

# HTTP calls from SQL

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](../reference/errors.md). Worked patterns (skipping a call, several calls, a KYC check): [23 HTTP call patterns](23-http-call-patterns.md).

A `{http{ ... }http}` block in a query describes an HTTP call in JSON. The engine makes the call before the query runs and puts the answer, as a JSON string, where the block was, bound as a SQL parameter. Use it to check input against another service, to enrich rows, or to call a payment or messaging API as part of a request. To forward a whole request to another API instead, use the [API gateway](08-api-gateway.md); to receive calls from another service, see [webhooks](19-webhooks.md).

## Rules

1. **Every block in a query's text is called before the query runs**, even one inside an `IF` branch that won't run, or inside a comment. Never write a block in a comment. To call only sometimes, decide in an earlier [chained query](14-query-chaining.md) and pass the decision as `"skip": "{pq{skip_it}}"` ([23](23-http-call-patterns.md#let-the-database-decide-whether-to-call)).
2. **Check `$.status_code` before you read `$.data`.** A failed call doesn't fail the request: a `404` or `500` from the API, or no answer at all (`status_code` `0`), reaches your SQL like a success. Raise the status you want the caller to get ([errors.md](../reference/errors.md#raising-an-error-from-sql)).
3. **Read the API's answer at `$.data`,** not at the root: `JSON_VALUE(@r, '$.data.id')`.
4. **Put caller values in `"query"`, never in the `"url"` string.** A value in `query` is percent-encoded; one inside `url` can add parameters with `&` ([Caller-supplied values](#caller-supplied-values-url-encoding-and-json-escaping)).
5. **Send a form as `"body_raw"` with `"content_type": "application/x-www-form-urlencoded"`, and fill it only with values you checked or built.** The engine has no form encoding: `&` and `=` in a value add or change fields.
6. **A call is made once unless it has `"retry"`.** Don't retry a call that creates something (a payment, an order) unless the API takes an idempotency key: a retried POST can create it twice.
7. **Keep the engine's own credentials in `<vars>` (`{s{api_key}}`), never in a header the caller sends:** any caller can send any value. Encrypt them ([21](21-settings-vars.md), [15](15-encryption.md)).
8. **In a chain, read earlier results with `{pq{name}}`, not `{{name}}`,** inside the block too: `{{name}}` falls back to a request value of the same name when the earlier query gave none ([14](14-query-chaining.md#reading-earlier-results-pqname)).

## How it works

1. The engine finds every `{http{ ... }http}` block in the query's text with `http_variable_pattern` (a route can override it; a custom pattern needs `(?s)` when a block spans lines).
2. In each block, it fills the placeholders as text: request values (`{{name}}`, from the body, query string, route and headers), `{auth{...}}`, `{s{...}}` and `{pq{...}}` ([Caller-supplied values](#caller-supplied-values-url-encoding-and-json-escaping)). A block whose `skip` is then truthy is left out.
3. It makes the calls at the same time, so the request waits for the slowest one, not their sum. Blocks that come out identical are called once.
4. It replaces each block with a SQL parameter (`@http_response_1`, `@http_response_2`...) and binds the call's answer to it. The answer is never written into the SQL text, so nothing an API returns can change the query.
5. The query runs. A `DECLARE @r NVARCHAR(MAX) = {http{ ... }http};` inside an `IF` that is false never runs, so `@r` stays `NULL`, but the call was still made.

Every call's answer is a JSON string of this shape, whether the call succeeded or not:

```json
{ "status_code": 200, "headers": { "Content-Type": "application/json" }, "data": { "id": "pi_123" }, "error": null }
```

| Field | Meaning |
|---|---|
| `status_code` | The API's status, `404` and `500` included. `0` when no answer came: DNS failure, refused connection, TLS error, timeout. |
| `headers` | The answer's headers, content headers included. A header with several values has them joined with `, `. |
| `data` | The body: the parsed JSON when it is JSON, otherwise the text, or `null` when it is empty. An API's error details are here too. |
| `error` | `null` when an answer came, even a `500`. `{"message": "..."}` when `status_code` is `0`, such as `Request timed out after 30 seconds`. |

The parameter is `NULL` instead in two cases only: the call was skipped (`skip`), or it was started without waiting (`no_wait`).

If the caller disconnects, the calls stop and the query doesn't run. On a route with `<cache>`, a call that other callers wait on keeps running until all of them have gone ([caching](07-caching.md#requests-that-arrive-while-the-query-runs)).

## Complete example: POST with a form body

An order page pays for an order with Stripe, which takes `application/x-www-form-urlencoded`, on SQL Server. The first query checks that the order is the caller's and unpaid, and reads the amount and the Stripe customer from the database. The second makes the call with those values, so nothing in the form comes from the request.

### Tables

```sql
CREATE TABLE customers (
  id                 UNIQUEIDENTIFIER PRIMARY KEY,
  owner_id           NVARCHAR(200)    NOT NULL,   -- the user's {auth{user_id}}
  stripe_customer_id NVARCHAR(100)    NOT NULL
);

CREATE TABLE orders (
  id                UNIQUEIDENTIFIER PRIMARY KEY,
  customer_id       UNIQUEIDENTIFIER NOT NULL REFERENCES customers(id),
  amount_cents      INT              NOT NULL,
  status            NVARCHAR(30)     NOT NULL,    -- 'unpaid', 'pending_payment', 'paid'
  payment_intent_id NVARCHAR(100)    NULL
);
```

### Settings

`stripe_secret_key` goes in `<vars>` in `config/settings.xml`, encrypted ([21](21-settings-vars.md), [15](15-encryption.md)):

```xml
<settings>
  <vars>
    <stripe_secret_key>sk_live_...</stripe_secret_key>
  </vars>
</settings>
```

### Endpoint

`config/sql.xml`, inside `<settings><queries>`:

```xml
<pay_for_order>
  <route>orders/{{order_id}}/payment</route>
  <verb>POST</verb>
  <authorize><provider>my_provider</provider></authorize>

  <!-- Query 1: the order must be the caller's and unpaid. The amount and the customer come from
       the database, never from the request. -->
  <query><![CDATA[
    DECLARE @order_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{order_id}});

    IF NOT EXISTS (SELECT 1 FROM orders o JOIN customers c ON c.id = o.customer_id
                   WHERE o.id = @order_id AND c.owner_id = {auth{user_id}})
      THROW 50404, 'Order not found', 1;
    IF NOT EXISTS (SELECT 1 FROM orders WHERE id = @order_id AND status = 'unpaid')
      THROW 50409, 'Only an unpaid order can be paid', 1;

    SELECT o.id AS order_id, o.amount_cents, c.stripe_customer_id
    FROM orders o JOIN customers c ON c.id = o.customer_id
    WHERE o.id = @order_id;
  ]]></query>

  <!-- Query 2: the call, with the values query 1 returned -->
  <query><![CDATA[
    DECLARE @payment NVARCHAR(MAX) = {http{
      {
        "url": "https://api.stripe.com/v1/payment_intents",
        "method": "POST",
        "headers": { "Authorization": "Bearer {s{stripe_secret_key}}" },
        "content_type": "application/x-www-form-urlencoded",
        "body_raw": "amount={pq{amount_cents}}&currency=usd&customer={pq{stripe_customer_id}}"
      }
    }http};

    IF JSON_VALUE(@payment, '$.status_code') = '0'
      THROW 50503, 'Payment service unreachable', 1;
    IF JSON_VALUE(@payment, '$.status_code') <> '200'
      THROW 50502, 'Payment service error', 1;

    -- Only while the order is still unpaid: of two requests at once, the second gets 409
    UPDATE orders
    SET payment_intent_id = JSON_VALUE(@payment, '$.data.id'), status = 'pending_payment'
    WHERE id = {pq{order_id}} AND status = 'unpaid';
    IF @@ROWCOUNT = 0
      THROW 50409, 'This order is already being paid', 1;

    SELECT
      JSON_VALUE(@payment, '$.data.id') AS payment_intent_id,
      JSON_VALUE(@payment, '$.data.client_secret') AS client_secret;
  ]]></query>
</pay_for_order>
```

`{pq{amount_cents}}` reads query 1's column only: an `amount_cents` the caller sends doesn't reach it. Two requests for one order can both pass query 1 and both create a PaymentIntent; the `UPDATE ... AND status = 'unpaid'` lets only the first one store its own. The second one's PaymentIntent is never confirmed, so it charges nothing. The call has no `retry`, so a timeout never creates a second PaymentIntent.

### Request

```bash
curl -X POST https://api.example.com/orders/6f1c2a4e-0d1b-4a52-9a49-3c3a8f0e2b11/payment \
  -H "Authorization: Bearer $TOKEN"
```

### What the client gets back

| Case | Status | Body |
|---|---|---|
| Paid | `200` | `{"payment_intent_id":"pi_...","client_secret":"pi_..._secret_..."}` |
| Another user's order, an unknown id, or one that isn't a GUID | `404` | `{"success":false,"message":"Order not found","error_number":404}` |
| The order isn't unpaid | `409` | `{"success":false,"message":"Only an unpaid order can be paid","error_number":409}` |
| Stripe answered with another status | `502` | `{"success":false,"message":"Payment service error","error_number":502}` |
| Stripe didn't answer | `503` | `{"success":false,"message":"Payment service unreachable","error_number":503}` |
| Another request for the order stored its PaymentIntent during the call | `409` | `{"success":false,"message":"This order is already being paid","error_number":409}` |
| No token | `401` | `{"success":false,"message":"Authorization header is required"}` |

Nothing is called for the `404` and the first `409`: query 1 stops the chain before query 2 is read.

## Request settings

The JSON inside the block:

| Property | Default | Notes |
|---|---|---|
| `url` | required | The address. Constants and `{s{...}}` values may sit in its query string; a caller's value goes in `query`. |
| `method` | `GET` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS`. |
| `headers` | none | An object of headers. A header whose name or value holds a line break is dropped, never sent. A `Content-Type` here replaces the one `content_type` sets. |
| `query` | none | An object of query-string values, each percent-encoded and appended to `url` (with `&` when `url` already has a `?`). |
| `body` | none | The JSON body, sent as written. A marker outside a string (`"body": {{doc}}`) inserts one whole JSON value. A string `body` is sent as its text when the body's type isn't JSON, such as a form, and as a JSON string when it is. |
| `body_raw` | none | A body sent as written, when there is no `body`. `"body_raw": "{{payload}}"` forwards a caller's document as it came. |
| `content_type` | `application/json` | The body's type, sent with `charset=utf-8`. |
| `timeout_seconds` | `30` | `timeout` is accepted too. A timeout gives `status_code` `0`. |
| `auth` | none | `bearer`, `api_key` or `basic` ([Authentication](#authentication)). |
| `retry` | none: one attempt | `max_attempts` (`3`), `delay_ms` (`1000`), `exponential_backoff` (`true`), `retry_status_codes` (`[500, 502, 503, 504]`). A network error or a timeout is retried too ([Retry configuration](#retry-configuration)). |
| `follow_redirects` | `true` | Calls keep no cookies, so a cookie set on a redirect isn't sent to the next hop. |
| `ignore_certificate_errors` | `false` | Development only. |
| `skip` | `false` | Truthy (`true`, or the text `true`, `1` or `yes` in any case, or a non-zero number): the call isn't made and the parameter is `NULL`. Anything else, or none, makes the call. |
| `no_wait` | `false` | Truthy: the call starts before the query runs and nothing waits for it; the parameter is `NULL`. It can still run after the response is sent, and it goes out even if the query then fails ([tutorial](../tutorial/16-http-from-sql.md#fire-and-forget-with-no_wait)). |

## Caller-supplied values: URL encoding and JSON escaping

A placeholder inside the block is replaced with its value **as text, before the JSON is read**. Three things protect the call from a hostile value.

**URL encoding is your job, through `query`.** Characters such as `&`, `#`, `?` and `=` mean something in a URL, and a placeholder inside the `url` string passes them through as written. Put a caller's value in `query`, where it is percent-encoded. Constants and `{s{...}}` values can stay in `url`.

```sql
-- Not this: {{city}} is the caller's, so "a&key=other" would add a parameter
"url": "https://api.example.com/current?key={s{api_key}}&q={{city}}"

-- This: the value is percent-encoded, whatever it holds
"url": "https://api.example.com/current?key={s{api_key}}",
"query": { "q": "{{city}}" }
```

**JSON escaping is automatic.** The engine works out which placeholders sit inside a JSON string and escapes their values (`"`, `\`, control characters), so `a","url":"https://attacker.example` stays inside its string and can't add a key. A placeholder outside a string, such as `"body": {{doc}}`, is how a whole JSON document is inserted, so it isn't escaped. Its value must then be exactly one JSON value (an object, array, string, number, `true`, `false` or `null`), read as leniently as the block (comments and trailing commas allowed) and inserted as compact JSON; anything else, such as `1, "url": "https://attacker.example"`, is inserted as a JSON string, so it can't add keys. A boolean or number from the request body becomes JSON `true`, `false` or a number. Even so, reserve that form for values you built (a settings variable, a column from an earlier query read with `{pq{name}}`). To forward a caller's document, use `"body_raw": "{{payload}}"`.

A placeholder used both inside and outside a string in one block, or inside a comment, is escaped everywhere, so it can't change the structure: outside a string it then works only for a plain number, `true`, `false` or `null`, and a warning is logged. Split such a block into two placeholders. A placeholder inside a value is part of the value and is never filled: a document an earlier query returned arrives as it is, `{s{...}}` and `{{...}}` text included. Placeholders are found with the patterns the route uses, overridden ones such as `||name||` included.

**Header injection is blocked.** A header whose name or value holds a carriage return or line feed is dropped, so a value can't start a second header or a body.

**A placeholder that matches nothing, or a `NULL` value,** is an empty string inside a JSON string, in `query` and in `headers` (`"q": "{{city}}"` sends `q=` when the request has no `city`), and JSON `null` outside one (`"body": { "doc": {{doc}} }` sends `{ "doc": null }`).

## Authentication

| `type` | Fields | Sends |
|---|---|---|
| `bearer` | `token` | `Authorization: Bearer <token>` |
| `api_key` | `key`, and `key_header` or `key_query_param` | The key in that header, or in that query-string parameter |
| `basic` | `username`, `password` | `Authorization: Basic <base64>` |

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "auth": { "type": "api_key", "key": "{s{example_api_key}}", "key_header": "X-API-Key" }
  }
}http};
```

## Retry configuration

```sql
DECLARE @data NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/data",
    "retry": { "max_attempts": 3, "delay_ms": 2000, "exponential_backoff": true, "retry_status_codes": [500, 502, 503, 504] }
  }
}http};
```

`max_attempts` counts the first attempt. A status in `retry_status_codes`, a network error and a timeout are retried. With `exponential_backoff`, each wait doubles (`delay_ms`, then twice it, then four times), up to 60 seconds. The answer is the last attempt's.

## Errors

The engine raises nothing for a call that failed: the query decides. The usual checks, on SQL Server:

```sql
DECLARE @status INT = TRY_CONVERT(INT, JSON_VALUE(@response, '$.status_code'));

IF @status = 0
  THROW 50503, 'The service is unreachable', 1;    -- $.error.message says why
IF @status IN (401, 403)
  THROW 50502, 'The service refused our credentials', 1;
IF @status = 429
  THROW 50503, 'The service is rate limiting us', 1;
IF @status NOT BETWEEN 200 AND 299
  THROW 50502, 'The service returned an error', 1;
```

| `$.status_code` | Meaning |
|---|---|
| `0` | No answer: DNS failure, network error, TLS error, timeout. `$.error.message` says which. |
| `200`-`299` | Success. `$.data` holds the body. |
| `400`-`599` | The API's error. `$.data` may hold its details. |

Every call is logged with its status and time: at `Debug` when it succeeds, at `Warning` when it gets a status outside 2xx (a redirect it doesn't follow included) or times out, and at `Error` when it can't connect. The shipped log level is `Information`, so a successful call leaves no line ([Log level](02-configuration.md#log-level)).

Silent failures, where the request succeeds:

- A query that doesn't check `$.status_code` goes on with an error page or `null` in `$.data` (rule 2).
- A block in a comment, or in an `IF` branch that doesn't run, is still called (rule 1).
- `JSON_VALUE(@r, '$.id')` instead of `'$.data.id'` is `NULL` (rule 3).
- A `skip` or a value read with `{{name}}` in a chain takes the caller's value when the earlier query gave none (rule 8).

## Do and don't

```sql
-- Don't: the caller's value inside the url
"url": "https://api.example.com/search?q={{term}}"
-- Do: in query, percent-encoded
"url": "https://api.example.com/search", "query": { "q": "{{term}}" }
```

```sql
-- Don't: a block in a comment is still called
-- DECLARE @old NVARCHAR(MAX) = {http{ {"url": "https://api.example.com/v1"} }http};
-- Do: delete it, or skip it from an earlier query
"skip": "{pq{skip_it}}"
```

## Security

- An API's answer reaches SQL as a bound parameter, never as SQL text, so it can't change the query.
- The answer is only inside the query: the caller sees only what your `SELECT` returns.
- Calls keep no cookies: a cookie a remote sets is never sent with a later call, for this caller or another, nor with the next hop of a redirect. Send one in `headers` when an API needs it. When a remote sets the cookie on a redirect, set `"follow_redirects": false` and make the second call yourself, with the cookie from the first answer's headers.
- The log shows a url without its query string, so a key there stays out of it, but the path is logged: a webhook URL whose path is the secret (Slack, Discord) appears in full when a call to it fails, and on every call at `Debug`. Headers are logged by name only, at `Debug`.
- Pass a caller's header on (`{h{Header-Name}}`) only when the remote should act as that caller, such as with the caller's own token.

## Related

- [23 HTTP call patterns](23-http-call-patterns.md): enriching rows, validating input, skipping a call, several calls, a KYC check.
- [14 Query chaining](14-query-chaining.md): `{pq{name}}`, and deciding in an earlier query.
- [21 Settings variables](21-settings-vars.md) and [15 Encryption](15-encryption.md): keys in `<vars>`.
- [08 API gateway](08-api-gateway.md): forwarding a whole request instead.
- [19 Webhooks](19-webhooks.md): calling back another service after a request.
- [Tutorial 16](../tutorial/16-http-from-sql.md): HTTP calls step by step.
