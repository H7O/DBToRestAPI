# Caching

Cache API responses to improve performance and reduce database load.

## Basic Configuration

```xml
<cached_endpoint>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
    </memory>
  </cache>
  
  <query><![CDATA[SELECT * FROM data;]]></query>
</cached_endpoint>
```

Response cached for 60 seconds.

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
| `invalidators` | None | Comma-separated params for cache key |
| `max_per_value_cache_size` | 1000 | Max characters per invalidator value |

## Example: User-Specific Cache

```xml
<get_user_data>
  <route>users/{{id}}/data</route>
  
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
  <cache>
    <memory>
      <duration_in_milliseconds>120000</duration_in_milliseconds>
      <invalidators>category,min_price,max_price</invalidators>
    </memory>
  </cache>
  
  <query><![CDATA[
    DECLARE @category NVARCHAR(100) = {{category}};
    DECLARE @min_price DECIMAL = {{min_price}};
    DECLARE @max_price DECIMAL = {{max_price}};
    
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

## Cache Behavior

### Cache Key Components

Cache key is generated from:
- HTTP method
- Route path
- Query parameters
- Request headers
- Invalidator values

### First Request

1. Check cache → Miss
2. Execute query
3. Store response in cache
4. Return response

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
  
  <query>SELECT * FROM local WHERE id = {{lookup_id}};</query>
  <query connection_string_name="remote">SELECT * FROM remote WHERE ref = {{id}};</query>
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
