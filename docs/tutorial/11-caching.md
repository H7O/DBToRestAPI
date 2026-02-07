# Response Caching

Hitting the database on every request works fine for small loads. But as traffic grows, caching frequently-accessed data can dramatically improve performance. In this topic, you'll learn how to add in-memory caching to your endpoints — with parameter-aware cache keys.

## Basic Caching

Add a `<cache>` block to any endpoint:

```xml
<cached_contacts>
  <route>cached/contacts</route>
  <verb>GET</verb>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
    </memory>
  </cache>
  <query>
    <![CDATA[
    select id, name, phone, active 
    from contacts 
    where active = 1
    order by name;
    ]]>
  </query>
</cached_contacts>
```

This caches the response for **60 seconds** (60,000 milliseconds). Here's what happens:

| Request | Action |
|---------|--------|
| First `GET /cached/contacts` | SQL executes → response stored in cache → returned |
| Second request within 60s | SQL **skipped** → cached response returned instantly |
| Request after 60s | Cache expired → SQL executes again → new response cached |

## Cache Invalidators

Without invalidators, all requests to the same endpoint share one cache entry. That's a problem for parameterized endpoints:

```
GET /contacts?name=alice    → cached
GET /contacts?name=bob      → returns Alice's results! (wrong)
```

**Invalidators** solve this by including parameter values in the cache key:

```xml
<search_contacts_cached>
  <route>contacts</route>
  <verb>GET</verb>
  <cache>
    <memory>
      <duration_in_milliseconds>30000</duration_in_milliseconds>
      <invalidators>name,phone</invalidators>
    </memory>
  </cache>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};

    select id, name, phone, active from contacts
    where (@name is null or name like '%' + @name + '%')
      and (@phone is null or phone like '%' + @phone + '%')
    order by name;
    ]]>
  </query>
</search_contacts_cached>
```

Now each unique combination of invalidator values gets its own cache entry:

| Request | Cache Key |
|---------|-----------|
| `GET /contacts` | `name=null, phone=null` |
| `GET /contacts?name=alice` | `name=alice, phone=null` |
| `GET /contacts?name=bob` | `name=bob, phone=null` |
| `GET /contacts?name=alice&phone=555` | `name=alice, phone=555` |

Each gets cached independently. Searching for "alice" won't return "bob"'s cached results.

## Real-World Example: The Hello World Cache

The sample `sql.xml` includes a cache example that demonstrates this well:

```xml
<hello_world_with_cache>
  <cache>
    <memory>
      <duration_in_milliseconds>20000</duration_in_milliseconds>
      <invalidators>name</invalidators>
    </memory>
  </cache>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};

    if (@name is null or ltrim(rtrim(@name)) = '')
        set @name = 'world';

    select 'hello ' + @name + '! Time now is ' 
         + convert(nvarchar(50), getdate(), 121) as message_from_db;
    ]]>
  </query>
</hello_world_with_cache>
```

Try it:

```bash
# First call - note the timestamp
curl http://localhost:5165/hello_world_with_cache

# Second call within 20 seconds - same timestamp (cached!)
curl http://localhost:5165/hello_world_with_cache

# Different name - different cache entry
curl "http://localhost:5165/hello_world_with_cache?name=Alice"

# Wait 20 seconds, try again - new timestamp
curl http://localhost:5165/hello_world_with_cache
```

The timestamp in the response proves whether the SQL actually ran or the cache was used.

## Cache with Route Parameters

Invalidators work with route parameters too:

```xml
<get_contact_cached>
  <route>contacts/{{id}}</route>
  <verb>GET</verb>
  <cache>
    <memory>
      <duration_in_milliseconds>120000</duration_in_milliseconds>
      <invalidators>id</invalidators>
    </memory>
  </cache>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    select id, name, phone, active from contacts where id = @id;
    ]]>
  </query>
</get_contact_cached>
```

Each contact ID gets its own 2-minute cache entry.

## Configuration Options

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `<duration_in_milliseconds>` | Yes | — | How long to cache the response |
| `<invalidators>` | No | None | Comma-separated parameter names for cache key |
| `<max_per_value_cache_size>` | No | 1000 | Max characters per invalidator value (prevents huge cache keys) |

## Global Caching

You can define a default cache in `settings.xml` that applies to **all** endpoints:

```xml
<!-- settings.xml -->
<settings>
  <cache>
    <memory>
      <duration_in_milliseconds>60000</duration_in_milliseconds>
    </memory>
  </cache>
</settings>
```

Endpoint-level `<cache>` overrides the global setting. To disable caching for a specific endpoint when global caching is on, don't include a `<cache>` block on that endpoint.

## When to Cache (and When Not To)

**Good candidates for caching:**
- Read-only listing endpoints
- Search results that don't change frequently
- Aggregated/computed data (dashboards, reports)
- External API call results (via API Gateway)

**Don't cache:**
- Write operations (POST, PUT, DELETE)
- Data that must be real-time (financial transactions, live status)
- User-specific sensitive data (unless using invalidators with user ID)

**Rule of thumb**: If the data can be a few seconds old without causing problems, cache it.

## Cache Performance Impact

Without cache:
```
Client → App → SQL Server → Query → Result → Client
~50-200ms per request (depending on query complexity)
```

With cache (hit):
```
Client → App → Memory → Result → Client
~1-5ms per request
```

For high-traffic endpoints (thousands of requests/second), this is the difference between needing 10 servers and needing 1.

---

### What You Learned

- How to add in-memory caching with `<cache>` → `<memory>`
- How `<invalidators>` create parameter-aware cache keys
- That each unique parameter combination gets its own cache entry
- How to verify caching with timestamp-based queries
- Global vs per-endpoint cache configuration
- When caching is appropriate and when to avoid it

---

**Next:** [API Gateway & Proxy Routes →](12-api-gateway.md)

**[Back to Tutorial Index](index.md)**
