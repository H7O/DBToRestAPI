# Protecting Endpoints with API Keys

Not every endpoint should be public. In this topic, you'll learn how to protect endpoints using API key collections — a simple, effective way to control access for machine-to-machine (B2B) scenarios.

## How API Key Protection Works

The flow is straightforward:

1. **Define** key collections in `/config/api_keys.xml`
2. **Reference** collections in your endpoint configuration
3. **Clients send** the key in an `x-api-key` HTTP header
4. The application **validates** the key before running the SQL

If the key is missing or invalid → HTTP 401 Unauthorized. No SQL executes.

## Step 1: Define Key Collections

Open `/config/api_keys.xml`:

```xml
<settings>
  <api_keys_collections>

    <external_vendors>
      <key>vendor-key-abc123</key>
      <key>vendor-key-def456</key>
    </external_vendors>

    <internal_solutions>
      <key>internal-svc-key-001</key>
    </internal_solutions>

    <mobile_apps>
      <key>mobile-ios-key-xyz</key>
      <key>mobile-android-key-xyz</key>
    </mobile_apps>

  </api_keys_collections>
</settings>
```

Each **collection** groups related keys. Collections represent a category of consumers:
- `external_vendors` — partner companies integrating with your API
- `internal_solutions` — your own microservices
- `mobile_apps` — your mobile applications

Each collection can have **multiple keys** — useful for key rotation or per-client tracking.

## Step 2: Protect an Endpoint

Add the `<api_keys_collections>` tag to any endpoint in `sql.xml`:

```xml
<protected_contacts>
  <route>api/contacts</route>
  <verb>GET</verb>
  <api_keys_collections>external_vendors,internal_solutions</api_keys_collections>
  <query>
    <![CDATA[
    select id, name, phone, active from [contacts] order by name;
    ]]>
  </query>
</protected_contacts>
```

The value is a comma-separated list of collection names. A key from **any** listed collection is accepted.

In this example:
- `vendor-key-abc123` → accepted (from `external_vendors`)
- `vendor-key-def456` → accepted (from `external_vendors`)
- `internal-svc-key-001` → accepted (from `internal_solutions`)
- `mobile-ios-key-xyz` → **rejected** (not in a listed collection)
- `random-key` → **rejected** (not in any collection)

## Step 3: Call the Protected Endpoint

### With a valid key:

```bash
curl -H "x-api-key: vendor-key-abc123" \
  http://localhost:5165/api/contacts
```

Response: normal JSON data.

### Without a key:

```bash
curl http://localhost:5165/api/contacts
```

Response (HTTP 401):
```json
{
  "error": "Unauthorized"
}
```

### With an invalid key:

```bash
curl -H "x-api-key: wrong-key" \
  http://localhost:5165/api/contacts
```

Response (HTTP 401):
```json
{
  "error": "Unauthorized"
}
```

## Tiered Access Pattern

A common pattern is offering different access levels:

```xml
<!-- Public - no protection -->
<public_search>
  <route>public/contacts</route>
  <verb>GET</verb>
  <query><![CDATA[
    select name, phone from contacts where active = 1;
  ]]></query>
</public_search>

<!-- Partner level - limited data -->
<partner_search>
  <api_keys_collections>external_vendors</api_keys_collections>
  <route>partner/contacts</route>
  <verb>GET</verb>
  <query><![CDATA[
    select id, name, phone, active from contacts;
  ]]></query>
</partner_search>

<!-- Internal only - full data -->
<internal_contacts>
  <api_keys_collections>internal_solutions</api_keys_collections>
  <route>internal/contacts</route>
  <verb>GET</verb>
  <query><![CDATA[
    select * from contacts;
  ]]></query>
</internal_contacts>
```

Each tier has different route prefixes and different data visibility — controlled entirely by which API key collection is required.

## Encrypting API Keys

Since API keys are secrets, you should encrypt them at rest. Add the collection to the encryption config in `settings.xml`:

```xml
<settings_encryption>
  <sections_to_encrypt>
    <section>api_keys_collections:external_vendors</section>
    <section>api_keys_collections:internal_solutions</section>
  </sections_to_encrypt>
</settings_encryption>
```

On first run, the keys in `api_keys.xml` will be encrypted automatically.

## Logging API Key Usage

You can access the API key in your SQL to log which client made the request:

```sql
declare @api_key nvarchar(500) = {h{x-api-key}};

-- Log the access
insert into api_access_log (endpoint, api_key, accessed_at)
values ('contacts', @api_key, getdate());

-- Return the data
select id, name, phone from contacts;
```

The `{h{x-api-key}}` decorator forces reading from the HTTP header specifically.

## Key Rotation

To rotate a key without downtime:

1. Add the new key to the collection alongside the old one:
   ```xml
   <external_vendors>
     <key>vendor-key-abc123</key>      <!-- Old key -->
     <key>vendor-key-new-789</key>     <!-- New key -->
   </external_vendors>
   ```

2. Hot-reload picks up the change immediately

3. Notify the client to switch to the new key

4. Once confirmed, remove the old key:
   ```xml
   <external_vendors>
     <key>vendor-key-new-789</key>
   </external_vendors>
   ```

Both keys work simultaneously during the transition period.

## Practical Exercise: Protect Your Contacts API

Let's add API key protection to the write operations (create, update, delete) while leaving read operations public:

1. Define a collection in `api_keys.xml`:
   ```xml
   <settings>
     <api_keys_collections>
       <contacts_writers>
         <key>my-secret-write-key</key>
       </contacts_writers>
     </api_keys_collections>
   </settings>
   ```

2. Add protection to write endpoints in `sql.xml`:
   ```xml
   <create_contact>
     <api_keys_collections>contacts_writers</api_keys_collections>
     <!-- ... rest of the endpoint ... -->
   </create_contact>

   <update_contact>
     <api_keys_collections>contacts_writers</api_keys_collections>
     <!-- ... rest of the endpoint ... -->
   </update_contact>

   <delete_contact>
     <api_keys_collections>contacts_writers</api_keys_collections>
     <!-- ... rest of the endpoint ... -->
   </delete_contact>
   ```

3. Test it:
   ```bash
   # Read still works without key
   curl http://localhost:5165/contacts

   # Write requires the key
   curl -X POST http://localhost:5165/contacts \
     -H "Content-Type: application/json" \
     -H "x-api-key: my-secret-write-key" \
     -d "{\"name\": \"Dave\", \"phone\": \"555-0104\"}"

   # Write without key → 401
   curl -X POST http://localhost:5165/contacts \
     -H "Content-Type: application/json" \
     -d "{\"name\": \"Eve\", \"phone\": \"555-0105\"}"
   ```

---

### What You Learned

- How to define API key collections in `api_keys.xml`
- How to protect endpoints with `<api_keys_collections>`
- That keys from any listed collection are accepted
- Tiered access patterns with different protection levels
- How to encrypt API keys at rest
- How to access the API key in SQL for logging
- Key rotation strategy with zero downtime

---

**Next:** [JWT & OIDC Authentication →](09-jwt-auth.md)

**[Back to Tutorial Index](index.md)**
