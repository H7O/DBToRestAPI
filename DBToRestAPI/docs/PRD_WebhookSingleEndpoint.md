# Product Requirements Document (PRD)

## Single-Endpoint Webhook Support via Early Response

**Version:** 1.0  
**Status:** Proposal  
**Author:** GitHub Copilot  
**Created:** 2026-03-29  
**Last Updated:** 2026-03-29

---

## Table of Contents

1. [Overview](#1-overview)
2. [Background & Motivation](#2-background--motivation)
3. [Current State (v1): Two-Endpoint Workaround](#3-current-state-v1-two-endpoint-workaround)
4. [Proposed Design (v2): Single-Endpoint with Early Response](#4-proposed-design-v2-single-endpoint-with-early-response)
5. [XML Configuration Schema](#5-xml-configuration-schema)
6. [Execution Flow](#6-execution-flow)
7. [Implementation Details](#7-implementation-details)
8. [Cancellation Token Lifecycle](#8-cancellation-token-lifecycle)
9. [Error Handling](#9-error-handling)
10. [Logging](#10-logging)
11. [Usage Examples](#11-usage-examples)
12. [Migration from v1](#12-migration-from-v1)
13. [Future Considerations](#13-future-considerations)
14. [Acceptance Criteria](#14-acceptance-criteria)

---

## 1. Overview

### 1.1 Purpose

Enable webhook/async notification patterns within a **single API endpoint** by allowing an endpoint to return an early HTTP response (e.g., `202 Accepted`) after executing some queries, then continue executing remaining queries in the background — including embedded HTTP calls to deliver webhook payloads to callback URLs.

### 1.2 Problem Statement

Webhook patterns require three distinct phases:

1. **Accept** — Validate the request and return an immediate acknowledgment (202).
2. **Process** — Execute the actual work (potentially long-running queries).
3. **Notify** — Call the client's callback URL with the result.

Currently, DBToRestAPI processes all queries in a chain synchronously and returns the final query's result as the HTTP response. There is no mechanism to return a response mid-chain and continue processing in the background.

### 1.3 Relationship to `no_wait`

The v1 `no_wait` feature (shipped in v1.0.9+) provides the building block for launching background HTTP calls that survive after the response is sent. This proposal builds on that foundation by introducing the ability to **split a query chain** into a "response phase" and a "background phase," with `no_wait` embedded HTTP calls serving as the notification mechanism in the background phase.

---

## 2. Background & Motivation

### 2.1 Real-World Use Case

A client registers a webhook endpoint to receive notifications when data processing completes:

```
Client → POST /api/process-report
         Body: { "report_id": 42, "callback_url": "https://client.example.com/webhook/report-done" }

Server → 202 Accepted  (immediately)
         Body: { "status": "accepted", "report_id": 42 }

... server processes report in background ...

Server → POST https://client.example.com/webhook/report-done
         Body: { "report_id": 42, "status": "complete", "download_url": "https://..." }
```

### 2.2 Why Single-Endpoint Matters

The two-endpoint workaround (see §3) requires:
- Maintaining two separate XML endpoint configurations
- The first endpoint knowing the internal URL of the second endpoint
- Extra network overhead from an internal HTTP call (localhost loopback)
- Shared state management between endpoints (correlating the request)

A single-endpoint solution eliminates all of this complexity.

---

## 3. Current State (v1): Two-Endpoint Workaround

With `no_wait` embedded HTTP calls, webhooks can be implemented today using **two endpoints**:

### Endpoint 1: Accept & Dispatch

```xml
<webhook_accept>
  <allowed_methods>POST</allowed_methods>
  <query>
    <![CDATA[
      -- Validate the request and generate a tracking ID
      SELECT 'accepted' AS status, NEWID() AS tracking_id;
    ]]>
  </query>
  <query>
    <![CDATA[
      -- no-wait: trigger the background worker endpoint
      SELECT 
        {http{
          {
            "url": "http://localhost:5000/api/webhook_worker?report_id={{report_id}}&callback_url={{callback_url}}&tracking_id={{tracking_id}}",
            "method": "POST",
            "no_wait": true
          }
        }http} AS ignored;
    ]]>
  </query>
  <query>
    <![CDATA[
      -- Return immediate 202 response
      SELECT '{{status}}' AS status, '{{tracking_id}}' AS tracking_id;
    ]]>
  </query>
</webhook_accept>
```

### Endpoint 2: Background Worker

```xml
<webhook_worker>
  <allowed_methods>POST</allowed_methods>
  <query>
    <![CDATA[
      -- Do the actual heavy work
      EXEC dbo.GenerateReport @report_id = {{report_id}};
    ]]>
  </query>
  <query>
    <![CDATA[
      -- Notify the client's callback URL with results
      SELECT 
        {http{
          {
            "url": "{{callback_url}}",
            "method": "POST",
            "body": {
              "tracking_id": "{{tracking_id}}",
              "status": "complete",
              "download_url": "https://files.example.com/reports/{{report_id}}.pdf"
            }
          }
        }http} AS webhook_response;
    ]]>
  </query>
</webhook_worker>
```

### Limitations

| Concern | Impact |
|---------|--------|
| Two endpoint configs | More to maintain, harder to reason about |
| Internal HTTP call | Extra latency, network overhead, possible localhost resolution issues in containers |
| URL coupling | Endpoint 1 must hardcode or derive the URL of Endpoint 2 |
| Error correlation | Tracking state across two separate request contexts |
| Security surface | The worker endpoint must be secured to prevent external calls, or use API keys internally |

---

## 4. Proposed Design (v2): Single-Endpoint with Early Response

### 4.1 Core Concept: `<early_response_after>`

Introduce a new XML configuration tag that splits the query chain into two execution phases:

- **Foreground phase** — Queries 1 through N execute normally, and query N's result becomes the HTTP response.
- **Background phase** — Queries N+1 onward continue executing on a background thread after the response has been sent.

```
  Request
    │
    ▼
┌──────────┐     ┌──────────┐     ┌──────────┐
│ Query 1  │ ──▶ │ Query 2  │ ──▶ │ Query 3  │ ── ✉ 202 Accepted ──▶ Client
│ validate │     │ generate │     │ response │
└──────────┘     └──────────┘     └──────────┘
                                        │
                            ─ ─ ─ ─ ─ ─ ┘  (early_response_after="3")
                           │
                           ▼  background thread
                      ┌──────────┐     ┌──────────┐
                      │ Query 4  │ ──▶ │ Query 5  │
                      │ process  │     │ webhook  │
                      └──────────┘     └──────────┘
```

### 4.2 Key Properties

| Property | Description |
|----------|-------------|
| **Response is real** | The early response is a fully-formed query result (supports `response_structure`, `root_node`, etc.) |
| **Background inherits params** | Background queries receive all accumulated `qParams` from the foreground chain |
| **Background uses app lifetime token** | `IHostApplicationLifetime.ApplicationStopping` instead of `HttpContext.RequestAborted` |
| **No `HttpContext` in background** | Background queries cannot read headers, query strings, or modify the response |
| **Best-effort execution** | Background failures are logged but cannot affect the already-sent response |

---

## 5. XML Configuration Schema

### 5.1 Endpoint-Level Configuration

```xml
<process_report>
  <allowed_methods>POST</allowed_methods>
  
  <!-- Return the HTTP response after query #2 completes.
       Queries 3+ continue in the background. -->
  <early_response_after>2</early_response_after>
  
  <!-- Query 1: Validate and prepare -->
  <query>
    <![CDATA[
      INSERT INTO jobs (report_id, status, callback_url, created_at)
      VALUES ({{report_id}}, 'processing', '{{callback_url}}', GETUTCDATE());
      
      SELECT {{report_id}} AS report_id, 'processing' AS status;
    ]]>
  </query>
  
  <!-- Query 2: Build the early response (this is what the client receives) -->
  <query>
    <![CDATA[
      SELECT 
        'accepted' AS status, 
        '{{report_id}}' AS report_id,
        '{{status}}' AS processing_status;
    ]]>
  </query>
  
  <!-- Query 3: Heavy processing (runs in background) -->
  <query>
    <![CDATA[
      EXEC dbo.GenerateReport @report_id = {{report_id}};
    ]]>
  </query>
  
  <!-- Query 4: Notify client via webhook (runs in background) -->
  <query>
    <![CDATA[
      UPDATE jobs SET status = 'complete' WHERE report_id = {{report_id}};
      
      SELECT
        {http{
          {
            "url": "{{callback_url}}",
            "method": "POST",
            "body": {
              "report_id": "{{report_id}}",
              "status": "complete",
              "download_url": "https://files.example.com/reports/{{report_id}}.pdf"
            }
          }
        }http} AS webhook_result;
    ]]>
  </query>
</process_report>
```

### 5.2 Tag Specification

| Tag | Type | Default | Description |
|-----|------|---------|-------------|
| `<early_response_after>` | `int` | *(none — disabled)* | 1-based query index. The response is sent after this query completes. Remaining queries execute in the background. Must be `≥ 1` and `< total query count`. |

### 5.3 Validation Rules

- If `early_response_after` is absent or empty, the endpoint behaves normally (all queries are foreground).
- If `early_response_after >= total query count`, log a warning and treat it as a normal endpoint (no background phase).
- If `early_response_after < 1`, log an error and treat it as a normal endpoint.
- `early_response_after` is incompatible with caching (`<cache>` settings). If both are present, caching is disabled for this endpoint and a warning is logged.

---

## 6. Execution Flow

### 6.1 Detailed Sequence

```
1. Request arrives at ApiController.Index()
2. Middleware chain processes (CORS, API keys, JWT, etc.)
3. QueryConfigurationParser.Parse() returns List<QueryDefinition>
4. Read early_response_after from section config
5. Determine splitIndex = early_response_after (or queries.Count if absent)

── FOREGROUND PHASE (queries[0..splitIndex-1]) ──────────────────

6. For each foreground query (using HttpContext.RequestAborted):
   a. PrepareEmbeddedHttpCallsParamsIfAny()
   b. Execute query
   c. Materialize result into qParams for next query
   
7. Final foreground query (queries[splitIndex-1]):
   a. Build HTTP response using GetResultFromDbFinalQueryAsync()
   b. ASP.NET Core writes response to client

── CLIENT HAS RECEIVED RESPONSE ──────────────────────────────

── BACKGROUND PHASE (queries[splitIndex..end]) ────────────────

8. Snapshot qParams (deep copy to avoid mutation issues)
9. Launch Task.Run with _appLifetime.ApplicationStopping token:
   
   For each background query:
   a. Create new DbConnection (not shared with foreground)
   b. PrepareEmbeddedHttpCallsParamsIfAny()
   c. Execute query
   d. Materialize result into backgroundQParams
   e. Dispose connection
   
10. Log completion or failure of background phase

── BACKGROUND PHASE COMPLETE ──────────────────────────────────
```

### 6.2 State Diagram

```
                    ┌──────────────┐
                    │   Request    │
                    │   Arrives    │
                    └──────┬───────┘
                           │
                    ┌──────▼───────┐
                    │  Parse       │
                    │  Config      │
                    └──────┬───────┘
                           │
              ┌────────────▼────────────┐
              │  early_response_after   │
              │  configured?            │
              └────┬──────────────┬─────┘
                   │ No           │ Yes
                   ▼              ▼
           ┌───────────┐  ┌──────────────┐
           │  Normal   │  │  Foreground  │
           │  Execute  │  │  Queries     │
           │  All      │  │  (1..N)      │
           └─────┬─────┘  └──────┬───────┘
                 │               │
                 ▼               ▼
           ┌───────────┐  ┌──────────────┐
           │  Return   │  │  Send Early  │
           │  Response │  │  Response    │
           └───────────┘  └──────┬───────┘
                                 │
                          ┌──────▼───────┐
                          │  Background  │
                          │  Queries     │
                          │  (N+1..end)  │
                          └──────┬───────┘
                                 │
                          ┌──────▼───────┐
                          │  Log         │
                          │  Completion  │
                          └──────────────┘
```

---

## 7. Implementation Details

### 7.1 Key Code Changes

#### 7.1.1 `GetResultFromDbMultipleQueriesAsync` Modification

The multi-query execution method needs to be split at `early_response_after`:

```csharp
private async Task<IActionResult> GetResultFromDbMultipleQueriesAsync(
    IConfigurationSection serviceQuerySection,
    List<QueryDefinition> queries,
    List<DbQueryParams> qParams,
    bool disableDeferredExecution = false)
{
    int? earlyResponseAfter = serviceQuerySection.GetValue<int?>("early_response_after");
    
    // Validate and determine the split point
    int splitIndex = DetermineEarlyResponseSplitIndex(earlyResponseAfter, queries.Count);
    
    // ... existing foreground query execution loop ...
    // Modified: loop only over queries[0..splitIndex-1]
    
    // The "final foreground query" (at splitIndex-1) returns the response.
    // After the response is built but before returning, launch background if needed.
    
    if (splitIndex < queries.Count)
    {
        LaunchBackgroundQueries(
            serviceQuerySection, 
            queries, 
            qParams,       // snapshot
            splitIndex,
            sectionTimeout, 
            globalTimeout);
    }
    
    return foregroundResponse;
}
```

#### 7.1.2 `LaunchBackgroundQueries` — New Method

```csharp
private void LaunchBackgroundQueries(
    IConfigurationSection serviceQuerySection,
    List<QueryDefinition> queries,
    List<DbQueryParams> accumulatedParams,
    int startIndex,
    int? sectionTimeout,
    int? globalTimeout)
{
    // Deep-copy qParams to prevent mutation from affecting background queries
    var backgroundParams = DeepCopyQueryParams(accumulatedParams);
    var route = HttpContext.Items.TryGetValue("route", out var r) ? r?.ToString() : "unknown";
    var backgroundQueries = queries.Skip(startIndex).ToList();
    
    _ = Task.Run(async () =>
    {
        var bgStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "{Time}: [Background] Route: {Route} — Starting {Count} background queries " +
            "(early_response_after={SplitIndex}).",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, 
            backgroundQueries.Count, startIndex);
        
        try
        {
            foreach (var query in backgroundQueries)
            {
                int? commandTimeout = query.DbCommandTimeout ?? sectionTimeout ?? globalTimeout;
                var connection = _dbConnectionFactory.Create(query.ConnectionStringName);
                
                try
                {
                    var queryText = await PrepareEmbeddedHttpCallsBackground(
                        query.QueryText, backgroundParams, serviceQuerySection);
                    
                    var result = await connection.ExecuteQueryAsync(
                        queryText,
                        backgroundParams,
                        commandTimeout: commandTimeout,
                        cToken: _appLifetime.ApplicationStopping);
                    
                    // Materialize and add to params for next background query
                    // (same logic as foreground intermediate queries)
                    await MaterializeAndAddToParams(result, backgroundParams, ...);
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
            
            _logger.LogInformation(
                "{Time}: [Background] Route: {Route} — All {Count} background queries " +
                "completed in {ElapsedMs}ms.",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route,
                backgroundQueries.Count, bgStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (_appLifetime.ApplicationStopping.IsCancellationRequested)
        {
            _logger.LogWarning(
                "{Time}: [Background] Route: {Route} — Background queries cancelled " +
                "due to application shutdown.",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Time}: [Background] Route: {Route} — Background query failed: {Message}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, ex.Message);
        }
    }, _appLifetime.ApplicationStopping);
}
```

#### 7.1.3 `PrepareEmbeddedHttpCallsBackground` — Background-Safe Variant

The existing `PrepareEmbeddedHttpCallsParamsIfAny` uses `HttpContext` for logging and the request-scoped cancellation token. A background-safe variant is needed that:

- Does **not** access `HttpContext` (it's disposed after response is sent)
- Uses `_appLifetime.ApplicationStopping` for all HTTP calls (the `no_wait` distinction is irrelevant — they're all background already)
- Takes the route string as a parameter instead of reading from `HttpContext.Items`

#### 7.1.4 `DeepCopyQueryParams` — Parameter Snapshot

```csharp
private static List<DbQueryParams> DeepCopyQueryParams(List<DbQueryParams> original)
{
    return original.Select(p => new DbQueryParams
    {
        DataModel = p.DataModel switch
        {
            Dictionary<string, string> dict => new Dictionary<string, string>(dict),
            Dictionary<string, object> dict => new Dictionary<string, object>(dict),
            _ => p.DataModel  // dynamic/ExpandoObject — immutable snapshot by this point
        },
        QueryParamsRegex = p.QueryParamsRegex
    }).ToList();
}
```

### 7.2 Critical Design Decisions

#### 7.2.1 No `HttpContext` in Background

Once the foreground response is sent, ASP.NET Core recycles the `HttpContext`. Background tasks **must not** access:

- `HttpContext.Request` (headers, query string, body)
- `HttpContext.Response`  
- `HttpContext.RequestAborted`
- `HttpContext.Items`

**Solution:** All values needed by background queries must be captured into local variables or the `qParams` snapshot before the response is sent. The route string, section config, and any request-derived values are already materialized in `qParams` by the time the foreground phase completes.

#### 7.2.2 Why Not `IHostedService` / Background Queue?

A queue-based approach (e.g., `BackgroundService` with `Channel<T>`) was considered but adds complexity without clear benefit for this use case:

| Approach | Pros | Cons |
|----------|------|------|
| `Task.Run` + app lifetime token | Simple, no new abstractions, consistent with v1 no-wait | No retry, no persistence |
| `Channel<T>` + `BackgroundService` | Bounded concurrency, backpressure | New abstraction, config surface, still no persistence |
| External queue (RabbitMQ, etc.) | Persistent, scalable, retries | External dependency, Docker complexity |

`Task.Run` is chosen for consistency with the v1 `no_wait` implementation and because DBToRestAPI is a configuration-driven proxy — it should remain lightweight. Users needing durable webhooks should use an external queue at the database or application level.

#### 7.2.3 Parameter Snapshot Timing

Parameters are deep-copied at the split point. This ensures:

- Background queries see a consistent snapshot of foreground results.
- No race conditions from foreground connection cleanup mutating shared state.
- The `qParams` list itself is not modified by background execution.

---

## 8. Cancellation Token Lifecycle

This is the most critical technical concern and the primary reason this feature wasn't shipped in v1.

### 8.1 Token Flow

```
Request arrives
    │
    │  HttpContext.RequestAborted ← active
    ▼
┌─────────────────────────────────┐
│  Foreground Queries (1..N)      │  ← uses HttpContext.RequestAborted
│  Embedded HTTP calls            │  ← uses HttpContext.RequestAborted (normal)
│                                 │     or ApplicationStopping (no_wait)
└─────────────────┬───────────────┘
                  │
                  ▼
            Response sent to client
                  │
                  │  HttpContext.RequestAborted ← NOW CANCELLED (or recycled)
                  ▼
┌─────────────────────────────────┐
│  Background Queries (N+1..end)  │  ← uses _appLifetime.ApplicationStopping
│  Embedded HTTP calls            │  ← uses _appLifetime.ApplicationStopping
│  DB connections                 │  ← uses _appLifetime.ApplicationStopping
└─────────────────────────────────┘
```

### 8.2 Graceful Shutdown

When the application receives a shutdown signal:

1. `ApplicationStopping` fires.
2. Background queries receive cancellation.
3. In-flight DB commands and HTTP calls throw `OperationCanceledException`.
4. The catch block logs the cancellation as a warning (not error).
5. Connections are disposed in `finally` blocks.

ASP.NET Core's default shutdown timeout is 30 seconds (`HostOptions.ShutdownTimeout`). For long-running background queries, consider documenting that users may need to increase this value in `appsettings.json`:

```json
{
  "HostOptions": {
    "ShutdownTimeout": "00:02:00"
  }
}
```

---

## 9. Error Handling

### 9.1 Background Error Strategy

Since the HTTP response has already been sent, background errors cannot affect the client. The strategy is **log and continue** for non-fatal errors, **log and abort** for fatal errors.

| Scenario | Behavior |
|----------|----------|
| Background query fails | Log error, abort remaining background queries |
| Embedded HTTP call fails | Log warning, SQL param receives error JSON (same as foreground) |
| Connection failure | Log error, abort remaining background queries |
| App shutdown during background | Log warning, cancel gracefully |
| Background query returns no rows | Normal — `qParams` gets null/empty, next query sees NULL params |

### 9.2 Error Callback Pattern

For robust webhook implementations, the background phase should include error handling in SQL:

```xml
<!-- Background query with error notification -->
<query>
  <![CDATA[
    BEGIN TRY
      EXEC dbo.HeavyProcessing @id = {{report_id}};
      UPDATE jobs SET status = 'complete' WHERE id = {{report_id}};
      SELECT 'complete' AS status, NULL AS error;
    END TRY
    BEGIN CATCH
      UPDATE jobs SET status = 'failed', error = ERROR_MESSAGE() WHERE id = {{report_id}};
      SELECT 'failed' AS status, ERROR_MESSAGE() AS error;
    END CATCH
  ]]>
</query>

<!-- Always notify — even on failure -->
<query>
  <![CDATA[
    SELECT
      {http{
        {
          "url": "{{callback_url}}",
          "method": "POST",
          "body": {
            "report_id": "{{report_id}}",
            "status": "{{status}}",
            "error": "{{error}}"
          }
        }
      }http} AS webhook_result;
  ]]>
</query>
```

---

## 10. Logging

### 10.1 Log Events

All background-phase log entries use the `[Background]` prefix for filtering:

| Level | Event | Message Pattern |
|-------|-------|-----------------|
| Information | Background start | `[Background] Route: {Route} — Starting {Count} background queries (early_response_after={N}).` |
| Debug | Background query exec | `[Background] Route: {Route} — Executing background query {Index}/{Total}.` |
| Information | Background complete | `[Background] Route: {Route} — All {Count} background queries completed in {ElapsedMs}ms.` |
| Warning | Shutdown cancellation | `[Background] Route: {Route} — Background queries cancelled due to application shutdown.` |
| Error | Background failure | `[Background] Route: {Route} — Background query {Index} failed: {Message}` |
| Warning | Invalid config | `[Background] Route: {Route} — early_response_after={Value} is >= query count ({Count}). Ignoring.` |

---

## 11. Usage Examples

### 11.1 Minimal Webhook Endpoint

```xml
<process_order>
  <allowed_methods>POST</allowed_methods>
  <early_response_after>1</early_response_after>
  
  <!-- Foreground: Accept immediately -->
  <query>
    <![CDATA[
      INSERT INTO orders (product_id, quantity, callback_url, status)
      VALUES ({{product_id}}, {{quantity}}, '{{callback_url}}', 'accepted');
      
      SELECT SCOPE_IDENTITY() AS order_id, 'accepted' AS status;
    ]]>
  </query>
  
  <!-- Background: Process and notify -->
  <query>
    <![CDATA[
      EXEC dbo.ProcessOrder @order_id = {{order_id}};
      UPDATE orders SET status = 'shipped' WHERE id = {{order_id}};
      SELECT 'shipped' AS status;
    ]]>
  </query>
  
  <query>
    <![CDATA[
      SELECT
        {http{
          {
            "url": "{{callback_url}}",
            "method": "POST",
            "body": { "order_id": "{{order_id}}", "status": "{{status}}" }
          }
        }http} AS notified;
    ]]>
  </query>
</process_order>
```

**Client interaction:**

```
POST /api/process_order
Body: { "product_id": 123, "quantity": 2, "callback_url": "https://shop.example.com/hooks/order" }

→ 200 OK (immediate)
  { "order_id": 456, "status": "accepted" }

... seconds/minutes later ...

→ POST https://shop.example.com/hooks/order  (background webhook)
  { "order_id": "456", "status": "shipped" }
```

### 11.2 Multi-Step Background Pipeline

```xml
<etl_pipeline>
  <allowed_methods>POST</allowed_methods>
  <early_response_after>1</early_response_after>
  
  <!-- Foreground: acknowledge -->
  <query>
    <![CDATA[
      SELECT NEWID() AS job_id, 'queued' AS status;
    ]]>
  </query>
  
  <!-- Background step 1: Extract -->
  <query connection_string_name="source_db">
    <![CDATA[
      SELECT * FROM raw_data WHERE batch_date = CAST(GETDATE() AS DATE);
    ]]>
  </query>
  
  <!-- Background step 2: Transform & Load -->
  <query connection_string_name="warehouse_db">
    <![CDATA[
      INSERT INTO fact_table 
      SELECT * FROM OPENJSON('{{json}}') WITH (...);
      
      SELECT COUNT(*) AS rows_loaded;
    ]]>
  </query>
  
  <!-- Background step 3: Notify on completion -->
  <query>
    <![CDATA[
      SELECT
        {http{
          {
            "url": "https://monitoring.internal/etl-complete",
            "method": "POST",
            "body": {
              "job_id": "{{job_id}}",
              "rows_loaded": "{{rows_loaded}}"
            }
          }
        }http} AS notification;
    ]]>
  </query>
</etl_pipeline>
```

### 11.3 With Custom HTTP Status Code

To return a `202 Accepted` instead of `200 OK`, use a database vendor's custom error mechanism:

```xml
<async_task>
  <allowed_methods>POST</allowed_methods>
  <early_response_after>1</early_response_after>
  
  <query>
    <![CDATA[
      -- SQL Server: RAISERROR with severity 16 and error number 50202
      -- maps to HTTP 202 via DBToRestAPI's custom error handling (50000 + status code)
      DECLARE @response NVARCHAR(MAX) = 
        '{"task_id": "' + CAST(NEWID() AS NVARCHAR(36)) + '", "status": "accepted"}';
      
      RAISERROR(@response, 16, 1) WITH NOWAIT;  -- Does NOT work this way; see note.
    ]]>
  </query>
  <!-- ... background queries ... -->
</async_task>
```

> **Note:** The custom HTTP status code mechanism (`RAISERROR 50000+N`) raises an exception that aborts the query chain. For v2, consider adding a `<response_status_code>` tag that sets the HTTP status without aborting:
> ```xml
> <response_status_code>202</response_status_code>
> ```

---

## 12. Migration from v1

### 12.1 v1 to v2 Migration

Migrating a two-endpoint webhook to a single endpoint:

**Before (v1 — two endpoints):**

```xml
<!-- Endpoint 1: accept -->
<webhook_accept>
  <query>SELECT ... validation ...</query>
  <query>SELECT {http{ {"url":"http://localhost/api/webhook_worker?...", "no_wait":true} }http}</query>
  <query>SELECT ... response ...</query>
</webhook_accept>

<!-- Endpoint 2: worker -->
<webhook_worker>
  <query>EXEC dbo.HeavyWork ...</query>
  <query>SELECT {http{ {"url":"{{callback_url}}", ...} }http}</query>
</webhook_worker>
```

**After (v2 — single endpoint):**

```xml
<webhook>
  <early_response_after>1</early_response_after>
  
  <!-- Foreground: validate and respond -->
  <query>SELECT ... validation + response ...</query>
  
  <!-- Background: heavy work -->
  <query>EXEC dbo.HeavyWork ...</query>
  
  <!-- Background: notify -->
  <query>SELECT {http{ {"url":"{{callback_url}}", ...} }http}</query>
</webhook>
```

### 12.2 Backward Compatibility

- Endpoints without `<early_response_after>` behave identically to today.
- The `no_wait` property continues to work in both foreground and background phases.
- No breaking changes to existing XML configurations.

---

## 13. Future Considerations

### 13.1 `<response_status_code>` Tag

Allow setting the HTTP status code for the early response without relying on the database error mechanism:

```xml
<early_response_after>1</early_response_after>
<response_status_code>202</response_status_code>
```

### 13.2 Background Retry

Add optional retry configuration for background queries:

```xml
<background_retry_count>3</background_retry_count>
<background_retry_delay_ms>1000</background_retry_delay_ms>
```

### 13.3 Background Timeout

Separate timeout for the entire background phase:

```xml
<background_timeout_seconds>300</background_timeout_seconds>
```

### 13.4 Progress Tracking

Expose an internal endpoint for checking background task status:

```
GET /api/_internal/background-tasks/{tracking_id}
→ { "status": "running", "current_query": 3, "total_queries": 5, "elapsed_ms": 4200 }
```

### 13.5 Concurrency Limits

Limit the number of concurrent background query chains to prevent resource exhaustion:

```xml
<!-- Global setting in settings.xml -->
<max_concurrent_background_chains>10</max_concurrent_background_chains>
```

---

## 14. Acceptance Criteria

### 14.1 Functional Requirements

| # | Requirement | Priority |
|---|-------------|----------|
| F1 | `<early_response_after>N</early_response_after>` causes the response to be sent after query N | Must |
| F2 | Background queries execute after the response is sent | Must |
| F3 | Background queries receive accumulated parameters from foreground queries | Must |
| F4 | Background queries can chain results between each other | Must |
| F5 | Embedded HTTP calls work in background queries | Must |
| F6 | Endpoints without `<early_response_after>` behave unchanged | Must |
| F7 | Invalid `early_response_after` values are logged and ignored gracefully | Must |
| F8 | `no_wait` still works independently in foreground queries | Must |
| F9 | `<response_status_code>` sets the HTTP status of the early response | Should |
| F10 | Background queries support `db_command_timeout` at all levels | Should |

### 14.2 Non-Functional Requirements

| # | Requirement | Priority |
|---|-------------|----------|
| NF1 | Background tasks are cancelled on application shutdown | Must |
| NF2 | No `HttpContext` access after response is sent | Must |
| NF3 | Background errors are logged at Error level | Must |
| NF4 | Background DB connections are properly disposed | Must |
| NF5 | Shutdown timeout documentation provided | Should |
| NF6 | Background phase completion is logged at Information level | Must |

### 14.3 Test Coverage

| # | Test | Type |
|---|------|------|
| T1 | Endpoint without `early_response_after` — normal behavior | Integration |
| T2 | `early_response_after=1` — response from query 1, background runs query 2+ | Integration |
| T3 | Background queries chain results between each other | Integration |
| T4 | Invalid `early_response_after` (0, negative, >= count) — graceful fallback | Unit |
| T5 | `DetermineEarlyResponseSplitIndex` — boundary values | Unit |
| T6 | `DeepCopyQueryParams` — mutation isolation | Unit |
| T7 | Background embedded HTTP calls use app lifetime token | Integration |
| T8 | Application shutdown cancels background queries | Integration |
| T9 | `no_wait` in foreground queries still works with early response | Integration |
| T10 | Background failure does not crash the application | Integration |
