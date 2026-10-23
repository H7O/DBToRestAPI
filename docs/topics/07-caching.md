# Caching

Cache API responses to improve performance and reduce database load.

## Basic Configuration

```xml
<cached_endpoint>
  <response_structure>array</response_structure>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
    </memory>
  </cache>
  
  <query><![CDATA[SELECT * FROM data;]]></query>
</cached_endpoint>
```

Response cached for 60 seconds.

The duration is in milliseconds, under `<memory>`. A tag the engine doesn't read, such as a misspelled or misplaced duration, is ignored without a word. A route whose `<cache>` has no `<memory>` block is not cached, and runs its query on every request. A `<memory>` block without a duration the engine reads takes the global default from `settings.xml` (`<settings><cache><memory><duration_in_milliseconds>`), and without one the route is not cached either. To check that a route is cached, add the current time as a column while you test (`strftime('%Y-%m-%d %H:%M:%f', 'now') AS checked_at` on SQLite, `SYSDATETIME() AS checked_at` on SQL Server) and call it twice within the duration: a cached route answers the same time both times. This checks that the route is cached, not for how long.

Only GET and HEAD requests use the cache. Any other verb always runs the query and leaves the cached entry alone, so an endpoint with no `<verb>` can still take writes.

## Cache Invalidators

Invalidate cache when specific parameters change:

```xml
<cache>
  <memory>
    <duration_in_milliseconds>300000</duration_in_milliseconds>
    <invalidators>user_id,category</invalidators>
  </memory>
</cache>
```

- `GET /data?user_id=1` → Cache A
- `GET /data?user_id=2` → Cache B (different user_id)
- `GET /data?user_id=1&category=books` → Cache C (different category)

## Configuration Options

| Setting | Default | Description |
|---------|---------|-------------|
| `duration_in_milliseconds` | Required | Cache lifetime |
| `invalidators` | None | Names of the inputs that change the answer, separated by commas, or by `\|` when a name contains a comma. A name that contains a space, a comma or `;` stays whole only when one of the route's queries (or its `count_query`) uses it in a marker, such as `{{sort by}}` (or a marker with the route's own delimiters). Otherwise it is split on them, so a list separated by spaces or `;` still names every input. An API gateway route runs no query, so its names are always split that way. |

> Invalidator values are hashed (64-bit xxHash3) before they enter the cache key, so a value of any length — a bearer token, say — is safe to nominate, and two requests that differ only in a long value never share an entry. A `max_per_value_cache_size` setting is ignored.

## Example: User-Specific Cache

```xml
<get_user_data>
  <route>users/{{id}}/data</route>
  <response_structure>array</response_structure>
  
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
      <invalidators>id</invalidators>
    </memory>
  </cache>
  
  <query><![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    SELECT * FROM user_data WHERE user_id = @id;
  ]]></query>
</get_user_data>
```

Each user ID gets its own cached response.

## Example: Search with Cache

```xml
<search_products>
  <response_structure>array</response_structure>
  <cache>
    <memory>
      <duration_in_milliseconds>120000</duration_in_milliseconds>
      <invalidators>category,min_price,max_price</invalidators>
    </memory>
  </cache>
  
  <query><![CDATA[
    DECLARE @category NVARCHAR(100) = NULLIF({{category}}, '');
    DECLARE @min_price DECIMAL(10, 2) = TRY_CONVERT(DECIMAL(10, 2), NULLIF({{min_price}}, ''));
    DECLARE @max_price DECIMAL(10, 2) = TRY_CONVERT(DECIMAL(10, 2), NULLIF({{max_price}}, ''));
    
    SELECT * FROM products
    WHERE (@category IS NULL OR category = @category)
      AND (@min_price IS NULL OR price >= @min_price)
      AND (@max_price IS NULL OR price <= @max_price);
  ]]></query>
</search_products>
```

## API Gateway Caching

Cache proxied API responses:

```xml
<!-- api_gateway.xml -->
<external_api>
  <url>https://api.external.com/data</url>
  
  <cache>
    <memory>
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      <invalidators>category</invalidators>
      <exclude_status_codes_from_cache>401,403,429</exclude_status_codes_from_cache>
    </memory>
  </cache>
</external_api>
```

### Status Code Filtering

By default, all responses are cached (including errors). Exclude specific codes:

```xml
<exclude_status_codes_from_cache>401,403,429,500</exclude_status_codes_from_cache>
```

A response with an excluded status, or with a `Set-Cookie` header, is passed to its caller and not stored, so the next request is forwarded again.

## Cache Behavior

### Cache Key Components

For a query endpoint, the key is built from:
- the endpoint (its element in the configuration);
- the verb (GET or HEAD);
- the route values, such as the `{{id}}` in `users/{{id}}/data`. Spellings of one route that run the same SQL (`Items/1`, `items//1`, `items/1/`) share an entry;
- the values of the parameters named in `<invalidators>`, read from every source separately: the query string, the body, a header, a route value, a token claim and a `<vars>` key. A header and a query-string value with the same name are different parts of the key, so a name that a claim or a var shares only splits entries further.

Nothing else is in the key. A query-string value, a body field, a header or the caller's identity separates entries only when its name is in `<invalidators>`. A cache hit runs no SQL, so authorization written in SQL is skipped: cache only answers that are the same for every caller, or name the caller (for example `user_id`) in `<invalidators>`.

For an API gateway route, the key is the route, the verb, the requested path, and the query-string values and headers named in `<invalidators>`. A header and a query-string value with the same name are separate parts of the key, query-string names keep their spelling, and a value sent several times (`?tag=a&tag=b`) is not the same key as one comma-joined value (`?tag=a,b`).

### First Request

1. Check cache → Miss
2. Execute query
3. Store response in cache
4. Return response

### Requests that arrive while the query runs

A request with the same key that arrives while the first one's query runs waits for that query instead of running its own, and gets its result. The query keeps running for the callers still waiting when the caller who started it disconnects, and stops only when all of them have gone. On a query route, a caller who disconnects is logged as one `Information` line naming the route; on a gateway route, nothing is logged. Its command timeout (`db_command_timeout`) still applies, as does a gateway route's `target_route_timeout_seconds`.

On a gateway route, a response that is not stored (an excluded status, or a `Set-Cookie` header) goes only to the caller whose request was forwarded, and each caller who waited for it is forwarded on its own.

### Subsequent Requests (within duration)

1. Check cache → Hit
2. Return cached response (no query execution)

### After Expiration

1. Check cache → Expired
2. Execute query
3. Update cache
4. Return response

## Multi-Query Chaining

Caching works with query chains — entire chain result is cached:

```xml
<chained_with_cache>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
      <invalidators>lookup_id</invalidators>
    </memory>
  </cache>
  
  <query><![CDATA[SELECT * FROM local WHERE id = {{lookup_id}};]]></query>
  <query connection_string_name="remote"><![CDATA[SELECT * FROM remote WHERE ref = {pq{id}};]]></query>
</chained_with_cache>
```

**Note:** Only final query result is cached. Intermediate queries always execute when cache expires.

## Cache vs No Cache

| With Cache | Without Cache |
|------------|---------------|
| First request: query executes | Every request: query executes |
| Subsequent: from memory | Every request: database hit |
| Faster response times | Real-time data |
| Reduced DB load | Higher DB load |
| Potentially stale data | Always fresh |

## When to Cache

✅ **Good candidates:**
- Read-only data that changes infrequently
- Expensive queries
- External API calls
- Aggregated/computed data

❌ **Avoid caching:**
- Data that changes frequently
- User-specific sensitive data
- Transactions that need real-time accuracy

## Related Topics

- [API Gateway](08-api-gateway.md) - Gateway caching
- [Query Chaining](14-query-chaining.md) - Caching chained queries
