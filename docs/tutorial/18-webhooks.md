# 18 — Webhooks

In the previous tutorials you learned how to call external APIs from SQL
(`{http{...}http}`) and chain multiple queries together.  This tutorial shows
how to combine those features to build **webhook-style endpoints** — where your
API accepts a request, responds immediately, and continues processing in the
background.

---

## The Problem

A common integration pattern looks like this:

1. A partner sends your API an order (or event, or payment).
2. Your API must respond quickly (within seconds) with an acknowledgment.
3. After responding, your API processes the data—runs validations, updates
   databases, enriches records—and eventually calls back to the partner's
   webhook URL with the result.

With a traditional framework you'd need a message queue, a background worker,
and a separate notification service.  With DBToRestAPI, you can achieve the
same result with **two XML endpoints** and zero code.

---

## The Two-Endpoint Pattern

The approach uses two endpoints:

| Endpoint | Role | Speed |
|----------|------|-------|
| **Accept** | Receives the request, records it, responds instantly | Fast (milliseconds) |
| **Process & Notify** | Runs the heavy work, then calls the partner's callback URL | Slow (seconds/minutes) |

The Accept endpoint fires off the Process & Notify endpoint using `no_wait`,
so the caller gets an immediate response while the real work happens in the
background.

```
Partner                     Your API
  │                            │
  │── POST /webhooks/accept ──▶│
  │                            ├─ Record request in DB
  │                            ├─ Fire /webhooks/process (no_wait)
  │◀── 202 Accepted ──────────│
  │                            │  ← response sent
  │                            │
  │                            ├─ Run validations...
  │                            ├─ Update records...
  │                            ├─ Call partner callback URL
  │                            │
  │◀── POST /callback ─────────│  (background, eventual)
```

---

## Step 1: The Accept Endpoint

This endpoint does three things:

1. Records the incoming request in a `webhook_requests` table.
2. Fires off the processing endpoint using `no_wait`.
3. Returns `202 Accepted` immediately.

```xml
<webhook_accept>
  <route>webhooks/accept</route>
  <verb>POST</verb>
  <mandatory_parameters>callback_url,payload</mandatory_parameters>
  <success_status_code>202</success_status_code>

  <!-- Query 1: Record the request -->
  <query><![CDATA[
    DECLARE @callback_url NVARCHAR(2000) = {{callback_url}};
    DECLARE @payload NVARCHAR(MAX) = {{payload}};

    INSERT INTO webhook_requests (callback_url, payload, status, created_at)
    OUTPUT inserted.id AS request_id, inserted.status
    VALUES (@callback_url, @payload, 'pending', GETUTCDATE());
  ]]></query>

  <!-- Query 2: Fire the processing endpoint in the background -->
  <query><![CDATA[
    DECLARE @process NVARCHAR(MAX) = {http{
      {
        "url": "{s{base_url}}/webhooks/process",
        "method": "POST",
        "body": {
          "request_id": "{{request_id}}"
        },
        "no_wait": true
      }
    }http};

    -- @process is NULL (no_wait = background call)
    -- Return the acknowledgment
    SELECT '{{request_id}}' AS request_id, 'pending' AS status;
  ]]></query>
</webhook_accept>
```

Key details:

- **`<success_status_code>202</success_status_code>`** — the response is
  `202 Accepted` instead of the default 200.
- **`"no_wait": true`** — the HTTP call to `/webhooks/process` fires on a
  background thread.  The SQL variable (`@process`) receives `NULL`
  immediately, and the response is sent to the caller without waiting.
- **`{s{base_url}}`** — a [settings variable](19-settings-vars.md) holding
  the root URL of your own API (e.g., `https://api.example.com`).
- The `OUTPUT` clause on the `INSERT` returns the new `request_id`, which
  flows into Query 2 as `{{request_id}}` via
  [multi-query chaining](17-multi-query.md).

---

## Step 2: The Process & Notify Endpoint

This endpoint does the actual work.  It runs at its own pace — no caller is
waiting.

```xml
<webhook_process>
  <route>webhooks/process</route>
  <verb>POST</verb>
  <mandatory_parameters>request_id</mandatory_parameters>
  <api_keys_collections>internal_keys</api_keys_collections>

  <!-- Query 1: Load the original request -->
  <query><![CDATA[
    DECLARE @id INT = {{request_id}};

    UPDATE webhook_requests SET status = 'processing' WHERE id = @id;

    SELECT id AS request_id,
           callback_url,
           payload,
           status
    FROM webhook_requests
    WHERE id = @id;
  ]]></query>

  <!-- Query 2: Do the heavy processing and notify -->
  <query><![CDATA[
    DECLARE @request_id INT = {{request_id}};
    DECLARE @callback_url NVARCHAR(2000) = {{callback_url}};
    DECLARE @payload NVARCHAR(MAX) = {{payload}};

    -- =============================================
    -- Your business logic here
    -- =============================================
    -- e.g., validate the payload, run calculations,
    -- update other tables, call third-party APIs...

    DECLARE @result NVARCHAR(MAX) = 'processed';
    -- (replace with your actual processing result)

    -- Update the status
    UPDATE webhook_requests
    SET status = @result, processed_at = GETUTCDATE()
    WHERE id = @request_id;

    -- Notify the partner via their callback URL
    DECLARE @notification NVARCHAR(MAX) = {http{
      {
        "url": "{{callback_url}}",
        "method": "POST",
        "body": {
          "request_id": "{{request_id}}",
          "status": "completed",
          "result": "processed successfully"
        }
      }
    }http};

    -- Log the callback result
    UPDATE webhook_requests
    SET callback_status_code = JSON_VALUE(@notification, '$.status_code'),
        callback_response = @notification
    WHERE id = @request_id;

    SELECT @request_id AS request_id,
           @result AS status,
           JSON_VALUE(@notification, '$.status_code') AS callback_status_code;
  ]]></query>
</webhook_process>
```

Key details:

- **`<api_keys_collections>internal_keys</api_keys_collections>`** — protects
  this endpoint so only your own API (or trusted services) can call it, not
  the outside world.
- **Query 1** loads the original request data.  Its single-row output flows
  into Query 2 via chaining (`{{callback_url}}`, `{{payload}}`).
- **Query 2** performs the processing, sends the callback notification using a
  normal (waited) `{http{...}http}` call, and logs the result.

---

## Settings Configuration

Add your API's base URL and the internal API key to `settings.xml`:

```xml
<!-- settings.xml -->
<vars>
  <base_url>https://api.example.com</base_url>
  <internal_api_key>your-internal-secret</internal_api_key>
</vars>

<!-- Encrypt the key -->
<sections_to_encrypt>
  <section>vars:internal_api_key</section>
</sections_to_encrypt>
```

And create an `internal_keys` collection in `api_keys.xml`:

```xml
<!-- api_keys.xml -->
<api_keys>
  <internal_keys>
    <api_key>{s{internal_api_key}}</api_key>
  </internal_keys>
</api_keys>
```

The Accept endpoint sends the internal key when calling the Process endpoint
by including it in the embedded HTTP call headers:

```json
{
  "url": "{s{base_url}}/webhooks/process",
  "method": "POST",
  "headers": {
    "x-api-key": "{s{internal_api_key}}"
  },
  "body": { "request_id": "{{request_id}}" },
  "no_wait": true
}
```

---

## Database Table

Here is a simple table to track webhook requests (SQL Server):

```sql
CREATE TABLE webhook_requests (
    id                   INT IDENTITY(1,1) PRIMARY KEY,
    callback_url         NVARCHAR(2000)    NOT NULL,
    payload              NVARCHAR(MAX),
    status               NVARCHAR(50)      NOT NULL DEFAULT 'pending',
    callback_status_code INT,
    callback_response    NVARCHAR(MAX),
    created_at           DATETIME2         NOT NULL DEFAULT GETUTCDATE(),
    processed_at         DATETIME2
);
```

---

## The Client's Perspective

From the partner's point of view, the interaction is simple:

**1. Send the request:**

```
POST /webhooks/accept
Content-Type: application/json

{
  "callback_url": "https://partner.example.com/callback",
  "payload": "{\"order_id\": 42, \"amount\": 129.99}"
}
```

**2. Get an immediate acknowledgment:**

```json
HTTP/1.1 202 Accepted

{
  "request_id": "17",
  "status": "pending"
}
```

**3. Receive the callback later (seconds/minutes):**

```
POST https://partner.example.com/callback
Content-Type: application/json

{
  "request_id": "17",
  "status": "completed",
  "result": "processed successfully"
}
```

---

## Variations

### Adding retry logic

If the callback fails, you can add retry logic in the processing query:

```sql
DECLARE @notification NVARCHAR(MAX) = {http{
  { "url": "{{callback_url}}", "method": "POST",
    "body": { "request_id": "{{request_id}}", "status": "completed" } }
}http};

-- If callback failed, try once more
IF CAST(JSON_VALUE(@notification, '$.status_code') AS INT) NOT BETWEEN 200 AND 299
BEGIN
    WAITFOR DELAY '00:00:05';  -- wait 5 seconds

    SET @notification = {http{
      { "url": "{{callback_url}}", "method": "POST",
        "body": { "request_id": "{{request_id}}", "status": "completed" } }
    }http};
END
```

### Status polling endpoint

Let clients check the status of their request without waiting for a callback:

```xml
<webhook_status>
  <route>webhooks/status/{{request_id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>request_id</mandatory_parameters>
  <query><![CDATA[
    DECLARE @id INT = {{request_id}};

    IF NOT EXISTS (SELECT 1 FROM webhook_requests WHERE id = @id)
      THROW 50404, 'Request not found', 1;

    SELECT id AS request_id, status, created_at, processed_at
    FROM webhook_requests WHERE id = @id;
  ]]></query>
</webhook_status>
```

### Multiple background tasks

You can fire several `no_wait` calls from the Accept endpoint if you need
parallel background processing:

```sql
-- Fire enrichment service
DECLARE @enrich NVARCHAR(MAX) = {http{
  { "url": "{s{base_url}}/internal/enrich",
    "body": { "request_id": "{{request_id}}" },
    "no_wait": true }
}http};

-- Fire notification service
DECLARE @notify NVARCHAR(MAX) = {http{
  { "url": "{s{base_url}}/internal/notify",
    "body": { "request_id": "{{request_id}}" },
    "no_wait": true }
}http};

-- Both fire in the background; response returns immediately
SELECT '{{request_id}}' AS request_id, 'processing' AS status;
```

---

## What You Learned

- How to build a webhook pattern using two SQL-only endpoints.
- The Accept endpoint uses `no_wait` to fire background processing and
  responds immediately with `202 Accepted`.
- The Process & Notify endpoint runs the heavy work and calls back the
  partner's URL.
- Protecting internal endpoints with API keys keeps the processing endpoint
  private.
- Multi-query chaining passes request data between the Accept and Process
  stages.
- The same pattern works for any "accept now, process later" scenario:
  payment processing, order fulfillment, data imports, report generation.

---

**Next:** [Settings Variables →](19-settings-vars.md)

[← Back to Index](index.md)
