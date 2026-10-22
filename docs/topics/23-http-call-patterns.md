---
title: HTTP call patterns
summary: Worked {http{ ... }http} patterns on SQL Server - enriching rows, validating input with another service, skipping a call, letting an earlier query decide whether to call, several calls at once, using a result only when a condition holds, and a KYC check.
keywords: ["{http{", skip, "{pq{skip_http}}", no_wait, enrichment, validation, KYC, dedupe, "Task.WhenAll", JSON_VALUE, OPENJSON, status_code]
---

# HTTP call patterns

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). The contract of a `{http{ ... }http}` block (its settings, its answer, escaping, errors) is [17 HTTP calls from SQL](17-embedded-http-calls.md): read it first.

Each pattern below is SQL Server. Every one follows 17's rules: check `$.status_code` before reading `$.data`, put a caller's value in `query`, and keep keys in `<vars>`.

## Enrich rows with another service

```xml
<get_weather_enriched_locations>
  <route>locations/weather</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <query><![CDATA[
    DECLARE @weather NVARCHAR(MAX) = {http{
      {
        "url": "https://api.weatherapi.com/v1/current.json",
        "query": { "key": "{s{weather_api_key}}", "q": "{{city}}" }
      }
    }http};

    IF JSON_VALUE(@weather, '$.status_code') <> '200'
      THROW 50502, 'Weather service unavailable', 1;

    SELECT l.id, l.name, l.city,
           JSON_VALUE(@weather, '$.data.current.temp_c') AS temperature_celsius,
           JSON_VALUE(@weather, '$.data.current.condition.text') AS weather_condition
    FROM locations l
    WHERE l.city = {{city}};
  ]]></query>
</get_weather_enriched_locations>
```

## Validate input with another service before writing

```xml
<validate_email>
  <route>users/validate-email</route>
  <verb>POST</verb>
  <query><![CDATA[
    DECLARE @validation NVARCHAR(MAX) = {http{
      {
        "url": "https://api.emailvalidation.example/v1/validate",
        "method": "POST",
        "headers": { "X-API-Key": "{s{email_validation_key}}" },
        "body": { "email": "{{email}}" }
      }
    }http};

    IF JSON_VALUE(@validation, '$.status_code') <> '200'
      THROW 50502, 'Email validation service unavailable', 1;

    -- A missing is_valid counts as invalid
    IF ISNULL(JSON_VALUE(@validation, '$.data.is_valid'), 'false') <> 'true'
      THROW 50400, 'Invalid email address', 1;

    INSERT INTO users (email, name, email_verified) VALUES ({{email}}, {{name}}, 1);

    SELECT 'User created successfully' AS message;
  ]]></query>
</validate_email>
```

## Skip a call

With a truthy `skip` (`true`, or the text `true`, `1` or `yes`, or a non-zero number), the call isn't made and the parameter is `NULL`. So the answer has three states:

```sql
-- Skip validation when the caller passes skip_validation=true
DECLARE @validation NVARCHAR(MAX) = {http{
  {
    "url": "https://api.example.com/validate",
    "method": "POST",
    "body": { "email": "{{email}}" },
    "skip": "{{skip_validation}}"
  }
}http};

IF @validation IS NULL                                               -- skipped
  INSERT INTO users (email, name) VALUES ({{email}}, {{name}});
ELSE IF TRY_CONVERT(INT, JSON_VALUE(@validation, '$.status_code')) = 0   -- no answer
BEGIN
  DECLARE @err NVARCHAR(500) = JSON_VALUE(@validation, '$.error.message');
  THROW 50503, @err, 1;
END
ELSE IF JSON_VALUE(@validation, '$.status_code') <> '200'             -- the service's error
  THROW 50502, 'Validation service error', 1;
ELSE IF JSON_VALUE(@validation, '$.data.is_valid') = 'false'
  THROW 50400, 'Invalid email', 1;
ELSE
  INSERT INTO users (email, name, validated) VALUES ({{email}}, {{name}}, 1);
```

A `skip` from the request, as here, lets the caller turn the call off: use it only for a call the caller may skip.

## Let the database decide whether to call

`skip` is read before any SQL of its query runs, so it can't come from that query. It can come from:

| Source | Decides `skip`? |
|---|---|
| A request value (`{{name}}`) | Yes, but the caller decides |
| A claim (`{auth{claim}}`) or a settings variable (`{s{name}}`) | Yes |
| SQL in the same query | No: it hasn't run yet |
| An earlier query of a [chain](14-query-chaining.md), read as `{pq{column}}` | Yes |

Here the first query decides, and the call to a paid enrichment API is made only for a contact that hasn't been enriched yet:

```xml
<enrich_contact>
  <route>contacts/{{id}}/enrich</route>
  <verb>POST</verb>

  <!-- Query 1: does the contact need the call? -->
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});

    IF NOT EXISTS (SELECT 1 FROM contacts WHERE id = @id)
      THROW 50404, 'Contact not found', 1;

    SELECT id, email,
           CASE WHEN enriched_at IS NOT NULL THEN '1' ELSE '0' END AS skip_http
    FROM contacts
    WHERE id = @id;
  ]]></query>

  <!-- Query 2: the call, only when query 1 said so -->
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {pq{id}};

    DECLARE @enrichment NVARCHAR(MAX) = {http{
      {
        "url": "{s{enrichment_api_url}}/lookup",
        "method": "POST",
        "headers": { "X-API-Key": "{s{enrichment_api_key}}" },
        "body": { "email": "{pq{email}}" },
        "skip": "{pq{skip_http}}"
      }
    }http};

    IF @enrichment IS NULL
      SELECT id, name, email, company, enriched_at, 'already_enriched' AS status
      FROM contacts WHERE id = @id;
    ELSE IF TRY_CONVERT(INT, JSON_VALUE(@enrichment, '$.status_code')) = 0
      THROW 50503, 'Enrichment service unreachable', 1;
    ELSE IF TRY_CONVERT(INT, JSON_VALUE(@enrichment, '$.status_code')) NOT BETWEEN 200 AND 299
      THROW 50502, 'Enrichment service returned an error', 1;
    ELSE
    BEGIN
      UPDATE contacts
      SET company     = JSON_VALUE(@enrichment, '$.data.company'),
          location    = JSON_VALUE(@enrichment, '$.data.location'),
          enriched_at = SYSUTCDATETIME()
      WHERE id = @id;

      SELECT id, name, email, company, enriched_at, 'enriched' AS status
      FROM contacts WHERE id = @id;
    END
  ]]></query>
</enrich_contact>
```

Query 1 returns one row, so query 2 reads its columns as `{pq{...}}` ([14](14-query-chaining.md#single-row--named-parameters)). The block's `skip` is filled with `1` or `0` before query 2's SQL runs. Use `{pq{skip_http}}`, not `{{skip_http}}`: when an earlier query gives no value, `{{skip_http}}` takes a request value of the same name, and the caller decides. Use this for pay-per-call and rate-limited APIs, and for work that may run twice.

## Several calls at once

The calls of a query are made at the same time, so the request waits for the slowest. Blocks that come out identical are called once.

```xml
<dashboard_summary>
  <route>dashboard/summary</route>
  <verb>GET</verb>
  <query><![CDATA[
    DECLARE @users     NVARCHAR(MAX) = {http{ {"url": "https://users.internal.example/api/stats"} }http};
    DECLARE @orders    NVARCHAR(MAX) = {http{ {"url": "https://orders.internal.example/api/stats"} }http};
    DECLARE @inventory NVARCHAR(MAX) = {http{ {"url": "https://inventory.internal.example/api/stats"} }http};

    SELECT JSON_VALUE(@users, '$.data.total_users')        AS total_users,
           JSON_VALUE(@orders, '$.data.total_orders')      AS total_orders,
           JSON_VALUE(@inventory, '$.data.items_in_stock') AS items_in_stock,
           JSON_VALUE(@users, '$.status_code')             AS users_status,
           JSON_VALUE(@orders, '$.status_code')            AS orders_status,
           JSON_VALUE(@inventory, '$.status_code')         AS inventory_status;
  ]]></query>
</dashboard_summary>
```

## Use a result only when a condition holds

An `IF` decides whether the answer is used, not whether the call is made: the call below is made for every request, with whatever `national_id` the caller sent. Use it where a pointless call costs nothing; otherwise decide in an earlier query ([above](#let-the-database-decide-whether-to-call)).

```sql
DECLARE @national_id NVARCHAR(50) = {{national_id}};
DECLARE @registry_name NVARCHAR(200) = NULL;

IF @national_id IS NOT NULL AND @national_id NOT LIKE '%[^0-9]%'
BEGIN
  DECLARE @lookup NVARCHAR(MAX) = {http{
    {
      "url": "https://registry.example.com/lookup",
      "query": { "id": "{{national_id}}" }
    }
  }http};

  IF TRY_CONVERT(INT, JSON_VALUE(@lookup, '$.status_code')) BETWEEN 200 AND 299
    SET @registry_name = JSON_VALUE(@lookup, '$.data.full_name');
END

-- @registry_name is NULL when the id was missing or invalid, or the lookup failed
UPDATE people
SET registry_name = COALESCE(@registry_name, registry_name)
WHERE id = TRY_CONVERT(UNIQUEIDENTIFIER, {{id}});
```

## A KYC check

The first query checks the request, so the provider is called only for one of the caller's customers, with a valid document type. The provider's key is a settings variable, never a value the caller sends:

```xml
<verify_customer_kyc>
  <route>customers/{{customer_id}}/verify</route>
  <verb>POST</verb>
  <authorize><provider>my_provider</provider></authorize>
  <mandatory_parameters>id_number,id_type</mandatory_parameters>

  <!-- Query 1: validate before anything is sent to the provider -->
  <query><![CDATA[
    DECLARE @customer_id UNIQUEIDENTIFIER = TRY_CONVERT(UNIQUEIDENTIFIER, {{customer_id}});
    DECLARE @id_type NVARCHAR(20) = {{id_type}};

    IF NOT EXISTS (SELECT 1 FROM customers WHERE id = @customer_id AND owner_id = {auth{user_id}})
      THROW 50404, 'Customer not found', 1;
    IF @id_type IS NULL OR @id_type NOT IN ('passport', 'national_id')
      THROW 50400, 'id_type must be passport or national_id', 1;

    SELECT id AS customer_id, @id_type AS id_type, country
    FROM customers WHERE id = @customer_id;
  ]]></query>

  <!-- Query 2: the call, then the result -->
  <query><![CDATA[
    DECLARE @kyc_result NVARCHAR(MAX) = {http{
      {
        "url": "https://kyc-provider.example.com/api/v1/verify",
        "method": "POST",
        "headers": { "Authorization": "Bearer {s{kyc_api_key}}" },
        "body": {
          "document_number": "{{id_number}}",
          "document_type": "{pq{id_type}}",
          "country": "{pq{country}}"
        },
        "timeout_seconds": 60
      }
    }http};

    DECLARE @customer_id UNIQUEIDENTIFIER = {pq{customer_id}};
    DECLARE @kyc_status_code INT = TRY_CONVERT(INT, JSON_VALUE(@kyc_result, '$.status_code'));

    IF @kyc_status_code = 0 OR @kyc_status_code >= 500
      THROW 50503, 'KYC service temporarily unavailable', 1;
    IF @kyc_status_code <> 200
      THROW 50502, 'KYC service rejected the request', 1;

    DECLARE @kyc_status NVARCHAR(50) = JSON_VALUE(@kyc_result, '$.data.status');
    DECLARE @kyc_score INT = TRY_CONVERT(INT, JSON_VALUE(@kyc_result, '$.data.confidence_score'));

    -- A missing status or score fails the check too: NULL <> 'VERIFIED' is not true.
    IF @kyc_status IS NULL OR @kyc_status <> 'VERIFIED' OR @kyc_score IS NULL OR @kyc_score < 80
      THROW 50422, 'KYC verification failed', 1;

    UPDATE customers
    SET kyc_verified = 1,
        kyc_verified_at = SYSUTCDATETIME(),
        kyc_reference = JSON_VALUE(@kyc_result, '$.data.reference_id'),
        kyc_score = @kyc_score
    WHERE id = @customer_id;

    SELECT @customer_id AS customer_id, 'KYC verification successful' AS message, @kyc_score AS confidence_score;
  ]]></query>
</verify_customer_kyc>
```

The call has no `retry`: a provider that charges per verification may charge for each attempt.

## Performance

- The calls of a query run at the same time, and identical ones run once.
- Every block is called, whatever the query's `IF`s say, unless it is skipped.
- Set `timeout_seconds` below the time your caller waits: the default is 30.
- A route with [`<cache>`](07-caching.md) calls the API only when it fills the cache.
- Retries multiply the wait: three attempts with backoff from 1 s wait 1 s and 2 s between them.

## Related

- [17 HTTP calls from SQL](17-embedded-http-calls.md): the contract of a block, and a complete tested example.
- [14 Query chaining](14-query-chaining.md): `{pq{name}}`.
- [Tutorial 16](../tutorial/16-http-from-sql.md): HTTP calls step by step.
