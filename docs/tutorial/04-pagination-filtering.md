# Pagination & Filtering

Your contacts API can already list and search contacts. In this topic, we'll add proper pagination — with total counts — and implement sorting. We'll also explore how the `count_query` tag works and how `response_structure` interacts with pagination.

## Why Pagination Matters

Without pagination, a `GET /contacts` query with 10,000 rows returns all 10,000 rows in a single response. This is slow, wastes bandwidth, and can crash clients that aren't expecting large payloads.

Pagination solves this with two parameters:
- **`take`** (or `limit`) — how many records to return
- **`skip`** (or `offset`) — how many records to skip

Combined with a count of total records, clients can build page controls like "Page 2 of 50".

## Adding Pagination to the Search Endpoint

Replace your `search_contacts` endpoint with this enhanced version:

```xml
<search_contacts>
  <route>contacts</route>
  <verb>GET</verb>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @take int = {{take}};
    declare @skip int = {{skip}};
    declare @sort_by nvarchar(50) = {{sort_by}};
    declare @sort_order nvarchar(10) = {{sort_order}};

    -- Default take to 100, cap at 1000
    if (@take is null or @take < 1) set @take = 100;
    if (@take > 1000) set @take = 1000;

    -- Default skip to 0
    if (@skip is null or @skip < 0) set @skip = 0;

    -- Validate sort column (whitelist approach)
    if (@sort_by is null or @sort_by not in ('name', 'phone'))
        set @sort_by = 'name';

    -- Validate sort direction
    if (@sort_order is null or @sort_order not in ('asc', 'desc'))
        set @sort_order = 'asc';

    select id, name, phone, active 
    from [contacts]
    where
      (@name is null or [name] like '%' + @name + '%')
      and (@phone is null or [phone] like '%' + @phone + '%')
    order by 
      case when @sort_by = 'name' and @sort_order = 'asc' then [name] end asc,
      case when @sort_by = 'name' and @sort_order = 'desc' then [name] end desc,
      case when @sort_by = 'phone' and @sort_order = 'asc' then [phone] end asc,
      case when @sort_by = 'phone' and @sort_order = 'desc' then [phone] end desc
    offset @skip rows
    fetch next @take rows only;
    ]]>
  </query>
  <count_query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};

    select count(*) from [contacts]
    where
      (@name is null or [name] like '%' + @name + '%')
      and (@phone is null or [phone] like '%' + @phone + '%');
    ]]>
  </count_query>
</search_contacts>
```

Let's walk through what's new.

## The `<count_query>` Tag

```xml
<count_query>
  <![CDATA[
  declare @name nvarchar(500) = {{name}};
  declare @phone nvarchar(100) = {{phone}};

  select count(*) from [contacts]
  where
    (@name is null or [name] like '%' + @name + '%')
    and (@phone is null or [phone] like '%' + @phone + '%');
  ]]>
</count_query>
```

When you add a `<count_query>`, two things happen:

1. **Both queries run**: The `<query>` returns the paginated data; the `<count_query>` returns the total count (without pagination).
2. **The response shape changes**: Instead of returning a plain array, the response wraps everything in an object with `count` and `data` fields.

### Response With `<count_query>`

```bash
curl "http://localhost:5165/contacts?take=2&skip=0"
```

```json
{
  "count": 3,
  "data": [
    {
      "id": "a1b2c3d4-...",
      "name": "Alice Smith",
      "phone": "555-0101",
      "active": true
    },
    {
      "id": "b2c3d4e5-...",
      "name": "Bob Jones",
      "phone": "555-0102",
      "active": true
    }
  ]
}
```

- `count: 3` — total contacts matching the filters (regardless of pagination)
- `data` — the 2 records for this page

### Response Without `<count_query>`

If you remove the `<count_query>` tag, the same request returns just the array:

```json
[
  {
    "id": "a1b2c3d4-...",
    "name": "Alice Smith",
    "phone": "555-0101",
    "active": true
  },
  {
    "id": "b2c3d4e5-...",
    "name": "Bob Jones",
    "phone": "555-0102",
    "active": true
  }
]
```

No `count` field, so the client can't know how many total records exist.

> **Important**: When `<count_query>` is present, the `<response_structure>` tag is ignored. The application always returns the `{ count, data }` wrapper.

## Keep the Count Query in Sync

The `<count_query>` must apply the **same filters** as the `<query>`, but without the pagination (`OFFSET`/`FETCH`) and without sorting. If they're out of sync, the count won't match the actual results.

**Correct** — same WHERE clause:
```sql
-- In <query>:     WHERE (@name is null or name like '%' + @name + '%')
-- In <count_query>: WHERE (@name is null or name like '%' + @name + '%')
```

**Wrong** — count doesn't filter:
```sql
-- In <query>:     WHERE (@name is null or name like '%' + @name + '%')
-- In <count_query>: SELECT count(*) FROM contacts;  ← Missing filter!
```

## Dynamic Sorting

The sorting logic uses a CASE-based pattern since SQL Server doesn't allow dynamic column names in `ORDER BY` directly:

```sql
order by 
  case when @sort_by = 'name' and @sort_order = 'asc' then [name] end asc,
  case when @sort_by = 'name' and @sort_order = 'desc' then [name] end desc,
  case when @sort_by = 'phone' and @sort_order = 'asc' then [phone] end asc,
  case when @sort_by = 'phone' and @sort_order = 'desc' then [phone] end desc
```

Only the matching CASE expression returns a non-NULL value, so only one `ORDER BY` clause is effectively applied.

> **Security note**: Always **whitelist** sort columns. Never insert user input directly into `ORDER BY` without validation — this prevents SQL injection through column names:
> ```sql
> if (@sort_by not in ('name', 'phone'))
>     set @sort_by = 'name';
> ```

## Test the Pagination

Let's test various pagination scenarios:

**Page 1 (first 2 records):**
```bash
curl "http://localhost:5165/contacts?take=2&skip=0"
```

**Page 2 (next 2 records):**
```bash
curl "http://localhost:5165/contacts?take=2&skip=2"
```

**Sorted by phone, descending:**
```bash
curl "http://localhost:5165/contacts?sort_by=phone&sort_order=desc"
```

**Search + pagination:**
```bash
curl "http://localhost:5165/contacts?name=a&take=10&skip=0"
```

## The `response_structure` and `count_query` Relationship

Here's a quick reference for how these two tags interact:

| `response_structure` | `count_query` present? | Response Shape |
|----------------------|------------------------|----------------|
| `auto`               | No                     | Single row → object; Multiple → array |
| `auto`               | Yes                    | `{ count, data: [...] }` |
| `array`              | No                     | Always array, even for 1 row |
| `array`              | Yes                    | `{ count, data: [...] }` |
| `single`             | No                     | Always first row object |
| `single`             | Yes                    | `{ count, data: [...] }` (first row only in data) |

**Takeaway**: `count_query` always wins. When present, the response is always `{ count, data }`.

## Practical Pattern: Endpoint Without Count

Sometimes you want guaranteed array responses but don't need a total count. Use `response_structure` with `array`:

```xml
<search_contacts_no_count>
  <route>contacts_simple</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <query>
    <![CDATA[
    select top 100 id, name, phone, active 
    from [contacts]
    order by name asc;
    ]]>
  </query>
</search_contacts_no_count>
```

This always returns an array — even if there's only one contact — making it predictable for client code that expects `[]`.

---

### What You Learned

- How `<count_query>` adds total counts to paginated responses
- The `{ count, data }` response wrapper that count_query produces
- Why the count query must apply the same filters as the main query
- How to implement dynamic sorting with whitelisted column names
- The relationship between `response_structure` and `count_query`
- Pagination parameters: `take`, `skip`, `sort_by`, `sort_order`

---

**Next:** [Update & Delete Operations →](05-update-delete.md)

**[Back to Tutorial Index](index.md)**
