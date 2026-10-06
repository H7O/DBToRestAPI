# Webhooks

Build webhook-style endpoints using two XML endpoints and zero code — accept a request immediately, process in the background, and call back the partner when done.

## Overview

```
Partner                     Your API
  │                            │
  │── POST /webhooks/accept ──▶│
  │                            ├─ Validate & record
  │                            ├─ Fire /webhooks/process (no_wait)
  │◀── 202 Accepted ──────────│
  │                            │
  │                            ├─ Heavy processing...
  │                            ├─ Call partner callback URL
  │◀── POST /callback ─────────│
```

## Two-Endpoint Pattern

| Endpoint | Role |
|----------|------|
| **Accept** | Validates, records request, fires Process via `no_wait`, returns `202` |
| **Process & Notify** | Protected by `api_keys_collections`, runs work, calls partner callback |

## Accept Endpoint

```xml
<webhook_accept>
  <route>webhooks/accept</route>
  <verb>POST</verb>
  <mandatory_parameters>partner_key,payload</mandatory_parameters>
  <success_status_code>202</success_status_code>

  <!-- Query 1: Validate and record -->
  <query><![CDATA[
    DECLARE @partner_key NVARCHAR(200) = {{partner_key}};
    DECLARE @payload NVARCHAR(MAX) = {{payload}};
    DECLARE @partner_id INT, @callback_url NVARCHAR(2000);

    -- The callback URL comes from the partner registry, never from the request
    SELECT @partner_id = id, @callback_url = callback_url
    FROM partners WHERE api_key = @partner_key;

    IF @partner_id IS NULL
      THROW 50403, 'Unknown partner', 1;

    IF @payload IS NULL OR ISJSON(@payload) = 0
      THROW 50400, 'payload must be valid JSON', 1;

    INSERT INTO webhook_requests (partner_id, callback_url, payload, status, created_at)
    OUTPUT inserted.id AS request_id
    VALUES (@partner_id, @callback_url, @payload, 'pending', GETUTCDATE());
  ]]></query>

  <!-- Query 2: Fire background processing (only runs if Query 1 succeeded) -->
  <query><![CDATA[
    DECLARE @process NVARCHAR(MAX) = {http{
      {
        "url": "{s{base_url}}/webhooks/process",
        "method": "POST",
        "headers": { "x-api-key": "{s{internal_api_key}}" },
        "body": { "request_id": "{pq{request_id}}" },
        "no_wait": true, "timeout_seconds": 600
      }
    }http};

    SELECT {pq{request_id}} AS request_id, 'pending' AS status;
  ]]></query>
</webhook_accept>
```

Key properties:
- `<success_status_code>202</success_status_code>` — returns `202 Accepted`
- `"no_wait": true` — fires on background thread, variable receives `NULL`
- `"timeout_seconds": 600`: The `no_wait` call still has a timeout: `timeout_seconds`, 30 by default. When it expires, the request it started is cancelled, even though nobody waits for the answer. So set `timeout_seconds` longer than the processing and its callback retries take, as the examples do with `600`.
- `"x-api-key": "{s{internal_api_key}}"` — authenticates via [settings variable](21-settings-vars.md)
- Validation in Query 1 prevents the `no_wait` call in Query 2 from firing on invalid input (embedded HTTP calls are pre-processed per query)
- The callback URL is read from your `partners` table, so the caller can't choose where your server sends requests
- `{pq{request_id}}` reads the `request_id` column of Query 1 ([reading earlier results](14-query-chaining.md#reading-earlier-results-pqname))

> **Never call a URL the caller sent you.** The engine has no host allow-list for `{http{ ... }http}` calls. If the callback URL comes from the request, any caller can make your server send requests to any host, including internal ones the internet can't reach. Register each partner's callback URL yourself and look it up in SQL, as above. If partners must send a URL, reject it before you store it unless it matches a URL registered for that partner.

## Process & Notify Endpoint

```xml
<webhook_process>
  <route>webhooks/process</route>
  <verb>POST</verb>
  <mandatory_parameters>request_id</mandatory_parameters>
  <api_keys_collections>internal_keys</api_keys_collections>

  <!-- Query 1: Load the request -->
  <query><![CDATA[
    DECLARE @id INT = {{request_id}};
    UPDATE webhook_requests SET status = 'processing' WHERE id = @id;
    SELECT id AS request_id, callback_url, payload
    FROM webhook_requests WHERE id = @id;
  ]]></query>

  <!-- Query 2: Process -->
  <query><![CDATA[
    DECLARE @request_id INT = {pq{request_id}};

    -- Business logic here...

    UPDATE webhook_requests
    SET status = 'completed', processed_at = GETUTCDATE()
    WHERE id = @request_id;

    SELECT @request_id AS request_id;
  ]]></query>

  <!-- Query 3: Notify (its HTTP call runs before its SQL, so it waits for Query 2) -->
  <query><![CDATA[
    DECLARE @request_id INT = {pq{request_id}};

    DECLARE @notification NVARCHAR(MAX) = {http{
      {
        "url": "{pq{callback_url}}",
        "method": "POST",
        "body": { "request_id": "{pq{request_id}}", "status": "completed" },
        "retry": {
          "max_attempts": 3,
          "delay_ms": 2000,
          "exponential_backoff": true,
          "retry_status_codes": [500, 502, 503, 504]
        }
      }
    }http};

    UPDATE webhook_requests
    SET callback_status_code = JSON_VALUE(@notification, '$.status_code')
    WHERE id = @request_id;

    SELECT @request_id AS request_id, 'completed' AS status;
  ]]></query>
</webhook_process>
```

Key properties:
- `<api_keys_collections>internal_keys</api_keys_collections>` — only internal/trusted callers
- `retry` — built-in exponential backoff for the callback (see [retry configuration](17-embedded-http-calls.md#retry-configuration))
- `{pq{callback_url}}` in Query 3 reads the column Query 1 loaded from `webhook_requests`. Columns from every earlier query stay available.
- The callback has its own query because a query's `{http{ ... }http}` calls run before its SQL. In Query 2 it would fire before the work is done, and a failure in the work would come after the partner was told `completed`.

## Settings Configuration

```xml
<!-- settings.xml -->
<vars>
  <base_url>https://api.example.com</base_url>
  <internal_api_key>your-internal-secret</internal_api_key>
</vars>
<settings_encryption>
  <sections_to_encrypt>
    <section>vars:internal_api_key</section>
    <section>api_keys_collections:internal_keys</section>
  </sections_to_encrypt>
</settings_encryption>
```

```xml
<!-- api_keys.xml: the same secret as vars:internal_api_key -->
<settings>
  <api_keys_collections>
    <internal_keys>
      <key>your-internal-secret</key>
    </internal_keys>
  </api_keys_collections>
</settings>
```

The shipped `api_keys.xml` already has an `<api_keys_collections>` element: add `<internal_keys>` inside it, never a second `<api_keys_collections>` beside it. Keys must sit under `<api_keys_collections>`, and `{s{...}}` placeholders are not resolved in `api_keys.xml`, so write the key itself. The `api_keys_collections:internal_keys` section in the `<settings_encryption>` block above encrypts it. The shipped `settings.xml` already has a `<settings_encryption>` block, so add both `<section>` lines to its `<sections_to_encrypt>` instead of adding a second block.

## Architectural Advantages

### Validate Before Accepting

Embedded HTTP calls are pre-processed **per query**. Place validation in Query 1 (no HTTP calls) and `no_wait` in Query 2 — the background call only fires if validation passes:

```xml
<!-- Query 1: validation — errors here return 4xx instantly -->
<query><![CDATA[
  DECLARE @payload NVARCHAR(MAX) = {{payload}};
  IF @payload IS NULL OR ISJSON(@payload) = 0 THROW 50400, 'Invalid JSON', 1;
  IF NOT EXISTS (SELECT 1 FROM partners WHERE api_key = {{partner_key}})
    THROW 50403, 'Unknown partner', 1;
  INSERT INTO webhook_requests (...) OUTPUT inserted.id AS request_id VALUES (...);
]]></query>

<!-- Query 2: only fires if Query 1 succeeded -->
<query><![CDATA[
  DECLARE @p NVARCHAR(MAX) = {http{
    {"url": "{s{base_url}}/webhooks/process", "method": "POST",
     "headers": {"x-api-key": "{s{internal_api_key}}"},
     "body": {"request_id": "{pq{request_id}}"}, "no_wait": true, "timeout_seconds": 600}
  }http};
  SELECT {pq{request_id}} AS request_id, 'pending' AS status;
]]></query>
```

### Cross-Database Validation

Each query in the chain can target a different database. Validate across systems before committing to background work:

```xml
<query connection_string_name="partners_db"><![CDATA[
  IF NOT EXISTS (SELECT 1 FROM partners WHERE api_key = {{partner_key}})
    THROW 50403, 'Invalid partner', 1;
  SELECT id AS partner_id, callback_url FROM partners WHERE api_key = {{partner_key}};
]]></query>

<query><![CDATA[  -- main DB: rate limit check
  IF (SELECT COUNT(*) FROM webhook_requests
      WHERE partner_id = {pq{partner_id}}
        AND created_at > DATEADD(MINUTE, -1, GETUTCDATE())) >= 100
    THROW 50429, 'Rate limit exceeded', 1;
  INSERT INTO webhook_requests (partner_id, callback_url, payload, status, created_at)
  OUTPUT inserted.id AS request_id
  VALUES ({pq{partner_id}}, {pq{callback_url}}, {{payload}}, 'pending', GETUTCDATE());
]]></query>

<query><![CDATA[  -- fire background
  DECLARE @p NVARCHAR(MAX) = {http{
    {"url": "{s{base_url}}/webhooks/process", "method": "POST",
     "headers": {"x-api-key": "{s{internal_api_key}}"},
     "body": {"request_id": "{pq{request_id}}"}, "no_wait": true, "timeout_seconds": 600}
  }http};
  SELECT {pq{request_id}} AS request_id, 'pending' AS status;
]]></query>
```

### Progress Callbacks

Send multiple callbacks at each processing stage using embedded HTTP calls in successive chained queries:

```xml
<!-- Query 2: notify 25% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{pq{callback_url}}", "method": "POST",
     "body": {"request_id": "{pq{request_id}}", "status": "validating", "progress": 25}}
  }http};
  -- ... validation work ...
  SELECT {pq{request_id}} AS request_id, {pq{callback_url}} AS callback_url;
]]></query>

<!-- Query 3: notify 50% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{pq{callback_url}}", "method": "POST",
     "body": {"request_id": "{pq{request_id}}", "status": "enriching", "progress": 50}}
  }http};
  -- ... enrichment work ...
  SELECT {pq{request_id}} AS request_id, {pq{callback_url}} AS callback_url;
]]></query>

<!-- Query 4: notify 100% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{pq{callback_url}}", "method": "POST",
     "body": {"request_id": "{pq{request_id}}", "status": "completed", "progress": 100}}
  }http};
  SELECT {pq{request_id}} AS request_id, 'completed' AS status;
]]></query>
```

Each callback is in its own chained query — a failure at any stage stops the chain.

### Status Polling

Optional endpoint for clients to check status without waiting for a callback:

```xml
<webhook_status>
  <route>webhooks/status/{{request_id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>request_id</mandatory_parameters>
  <query><![CDATA[
    IF NOT EXISTS (SELECT 1 FROM webhook_requests WHERE id = {{request_id}})
      THROW 50404, 'Request not found', 1;
    SELECT id AS request_id, status, created_at, processed_at
    FROM webhook_requests WHERE id = {{request_id}};
  ]]></query>
</webhook_status>
```

## Summary

| Feature | How |
|---------|-----|
| Immediate response | `<success_status_code>202</success_status_code>` |
| Background processing | `"no_wait": true` on embedded HTTP call |
| Internal security | `api_keys_collections` + `x-api-key` header via `{s{}}` |
| Safe callback target | Callback URL looked up in your partner registry, never taken from the request |
| Read earlier results | `{pq{name}}` in each chained query |
| Validate first | Place HTTP calls in later chained queries |
| Cross-DB validation | `connection_string_name` per query in chain |
| Progress callbacks | Embedded HTTP calls in successive chained queries |
| Retry on failure | Built-in `retry` property with exponential backoff |

See the [tutorial walkthrough](../tutorial/18-webhooks.md) for a step-by-step guide with complete examples.
