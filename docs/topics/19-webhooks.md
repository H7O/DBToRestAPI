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
  <mandatory_parameters>callback_url,payload</mandatory_parameters>
  <success_status_code>202</success_status_code>

  <!-- Query 1: Validate and record -->
  <query><![CDATA[
    DECLARE @callback_url NVARCHAR(2000) = {{callback_url}};
    DECLARE @payload NVARCHAR(MAX) = {{payload}};

    IF ISJSON(@payload) = 0
      THROW 50400, 'payload must be valid JSON', 1;

    INSERT INTO webhook_requests (callback_url, payload, status, created_at)
    OUTPUT inserted.id AS request_id
    VALUES (@callback_url, @payload, 'pending', GETUTCDATE());
  ]]></query>

  <!-- Query 2: Fire background processing (only runs if Query 1 succeeded) -->
  <query><![CDATA[
    DECLARE @process NVARCHAR(MAX) = {http{
      {
        "url": "{s{base_url}}/webhooks/process",
        "method": "POST",
        "headers": { "x-api-key": "{s{internal_api_key}}" },
        "body": { "request_id": "{{request_id}}" },
        "no_wait": true
      }
    }http};

    SELECT '{{request_id}}' AS request_id, 'pending' AS status;
  ]]></query>
</webhook_accept>
```

Key properties:
- `<success_status_code>202</success_status_code>` — returns `202 Accepted`
- `"no_wait": true` — fires on background thread, variable receives `NULL`
- `"x-api-key": "{s{internal_api_key}}"` — authenticates via [settings variable](21-settings-vars.md)
- Validation in Query 1 prevents the `no_wait` call in Query 2 from firing on invalid input (embedded HTTP calls are pre-processed per query)

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

  <!-- Query 2: Process and notify -->
  <query><![CDATA[
    DECLARE @request_id INT = {{request_id}};
    DECLARE @callback_url NVARCHAR(2000) = {{callback_url}};

    -- Business logic here...

    UPDATE webhook_requests
    SET status = 'completed', processed_at = GETUTCDATE()
    WHERE id = @request_id;

    DECLARE @notification NVARCHAR(MAX) = {http{
      {
        "url": "{{callback_url}}",
        "method": "POST",
        "body": { "request_id": "{{request_id}}", "status": "completed" },
        "retry": {
          "max_attempts": 3,
          "delay_seconds": 2,
          "backoff_multiplier": 2.0,
          "retry_on_status_codes": [500, 502, 503, 504]
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
- Query chaining passes `{{callback_url}}` from Query 1 to Query 2

## Settings Configuration

```xml
<!-- settings.xml -->
<vars>
  <base_url>https://api.example.com</base_url>
  <internal_api_key>your-internal-secret</internal_api_key>
</vars>
<sections_to_encrypt>
  <section>vars:internal_api_key</section>
</sections_to_encrypt>
```

```xml
<!-- api_keys.xml -->
<api_keys>
  <internal_keys>
    <api_key>{s{internal_api_key}}</api_key>
  </internal_keys>
</api_keys>
```

## Architectural Advantages

### Validate Before Accepting

Embedded HTTP calls are pre-processed **per query**. Place validation in Query 1 (no HTTP calls) and `no_wait` in Query 2 — the background call only fires if validation passes:

```xml
<!-- Query 1: validation — errors here return 4xx instantly -->
<query><![CDATA[
  IF ISJSON({{payload}}) = 0 THROW 50400, 'Invalid JSON', 1;
  IF NOT EXISTS (SELECT 1 FROM partners WHERE id = {{partner_id}})
    THROW 50403, 'Unknown partner', 1;
  INSERT INTO webhook_requests (...) OUTPUT inserted.id AS request_id VALUES (...);
]]></query>

<!-- Query 2: only fires if Query 1 succeeded -->
<query><![CDATA[
  DECLARE @p NVARCHAR(MAX) = {http{
    {"url": "{s{base_url}}/webhooks/process", "body": {"request_id": "{{request_id}}"}, "no_wait": true}
  }http};
  SELECT '{{request_id}}' AS request_id, 'pending' AS status;
]]></query>
```

### Cross-Database Validation

Each query in the chain can target a different database. Validate across systems before committing to background work:

```xml
<query connection_string_name="partners_db"><![CDATA[
  IF NOT EXISTS (SELECT 1 FROM partners WHERE api_key = {{partner_key}})
    THROW 50403, 'Invalid partner', 1;
  SELECT partner_id, callback_url FROM partners WHERE api_key = {{partner_key}};
]]></query>

<query><![CDATA[  -- main DB: rate limit check
  IF (SELECT COUNT(*) FROM webhook_requests
      WHERE partner_id = {{partner_id}}
        AND created_at > DATEADD(MINUTE, -1, GETUTCDATE())) >= 100
    THROW 50429, 'Rate limit exceeded', 1;
  INSERT INTO webhook_requests (...) OUTPUT inserted.id AS request_id VALUES (...);
]]></query>

<query><![CDATA[  -- fire background
  DECLARE @p NVARCHAR(MAX) = {http{
    {"url": "{s{base_url}}/webhooks/process", "body": {"request_id": "{{request_id}}"}, "no_wait": true}
  }http};
  SELECT '{{request_id}}' AS request_id, 'pending' AS status;
]]></query>
```

### Progress Callbacks

Send multiple callbacks at each processing stage using embedded HTTP calls in successive chained queries:

```xml
<!-- Query 2: notify 25% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{{callback_url}}", "method": "POST",
     "body": {"request_id": "{{request_id}}", "status": "validating", "progress": 25}}
  }http};
  -- ... validation work ...
  SELECT {{request_id}} AS request_id, '{{callback_url}}' AS callback_url;
]]></query>

<!-- Query 3: notify 50% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{{callback_url}}", "method": "POST",
     "body": {"request_id": "{{request_id}}", "status": "enriching", "progress": 50}}
  }http};
  -- ... enrichment work ...
  SELECT {{request_id}} AS request_id, '{{callback_url}}' AS callback_url;
]]></query>

<!-- Query 4: notify 100% -->
<query><![CDATA[
  DECLARE @cb NVARCHAR(MAX) = {http{
    {"url": "{{callback_url}}", "method": "POST",
     "body": {"request_id": "{{request_id}}", "status": "completed", "progress": 100}}
  }http};
  SELECT {{request_id}} AS request_id, 'completed' AS status;
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
| Validate first | Place HTTP calls in later chained queries |
| Cross-DB validation | `connection_string_name` per query in chain |
| Progress callbacks | Embedded HTTP calls in successive chained queries |
| Retry on failure | Built-in `retry` property with exponential backoff |

See the [tutorial walkthrough](../tutorial/18-webhooks.md) for a step-by-step guide with complete examples.
