# Using Claims in Queries

The previous topic showed how to protect endpoints and access basic claims. In this topic, we'll dig deeper into practical patterns for using authenticated user identity inside SQL — multi-tenant data isolation, audit trails, and role-based query logic.

## Quick Recap: The `{auth{claim}}` Syntax

When a JWT-protected endpoint executes, all claims from the token (plus any fetched from UserInfo) are available:

```sql
declare @user_id nvarchar(100) = {auth{sub}};
declare @email nvarchar(500) = {auth{email}};
declare @name nvarchar(500) = {auth{name}};
```

These work just like `{{param}}` — they're injected into the SQL before execution. But unlike regular parameters which come from the HTTP request, `{auth{}}` values come from the **validated JWT token** and **cannot be spoofed** by the caller.

## Pattern 1: User-Owned Data

The most common pattern — users only see their own data:

```xml
<my_contacts>
  <route>my/contacts</route>
  <verb>GET</verb>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query>
    <![CDATA[
    declare @owner_email nvarchar(500) = {auth{email}};

    select id, name, phone, active
    from contacts
    where owner_email = @owner_email
    order by name;
    ]]>
  </query>
</my_contacts>
```

Every user sees only their own contacts. The `owner_email` filter comes from the JWT — clients can't bypass it.

### Create With Ownership

```xml
<create_my_contact>
  <route>my/contacts</route>
  <verb>POST</verb>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <success_status_code>201</success_status_code>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query>
    <![CDATA[
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @owner_email nvarchar(500) = {auth{email}};

    insert into contacts (id, name, phone, active, owner_email)
    output inserted.id, inserted.name, inserted.phone, inserted.active
    values (newid(), @name, @phone, 1, @owner_email);
    ]]>
  </query>
</create_my_contact>
```

The `owner_email` is set from the JWT — the caller can't forge ownership.

## Pattern 2: Multi-Tenant Isolation

For SaaS applications, isolate data by organization:

```sql
declare @tenant_id nvarchar(100) = {auth{tenant_id}};
declare @user_email nvarchar(500) = {auth{email}};

-- All queries are scoped to the tenant
select * from projects
where tenant_id = @tenant_id
order by created_at desc;
```

This is a powerful pattern because the tenant filter is injected from the token — no chance of cross-tenant data leaks.

## Pattern 3: Audit Trails

Track who did what:

```xml
<update_contact_audited>
  <route>contacts/{{id}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id,name,phone</mandatory_parameters>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query>
    <![CDATA[
    declare @id UNIQUEIDENTIFIER = {{id}};
    declare @name nvarchar(500) = {{name}};
    declare @phone nvarchar(100) = {{phone}};
    declare @modified_by nvarchar(500) = {auth{email}};

    -- Update the record
    update contacts set
      name = @name,
      phone = @phone,
      modified_by = @modified_by,
      modified_at = getutcdate()
    output inserted.id, inserted.name, inserted.phone,
           inserted.modified_by, inserted.modified_at
    where id = @id;

    -- Log the change
    insert into audit_log (entity, entity_id, action, performed_by, performed_at)
    values ('contact', @id, 'update', @modified_by, getutcdate());
    ]]>
  </query>
</update_contact_audited>
```

The `modified_by` comes from the JWT — trustworthy audit data.

## Pattern 4: Role-Based Query Logic

Different users see different data:

```sql
declare @email nvarchar(500) = {auth{email}};
declare @role nvarchar(100);

-- Look up role in our database
select @role = role from app_users where email = @email;

-- Admins see all contacts, regular users see only active ones
if @role = 'admin'
begin
  select id, name, phone, active, owner_email from contacts order by name;
end
else
begin
  select id, name, phone from contacts where active = 1 order by name;
end
```

Same endpoint, different behavior based on who's asking.

## Pattern 5: First-Login Auto-Registration

Create user accounts automatically on first authenticated request:

```xml
<whoami>
  <route>whoami</route>
  <verb>GET</verb>
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  <query>
    <![CDATA[
    declare @email nvarchar(500) = {auth{email}};
    declare @name nvarchar(500) = {auth{name}};
    declare @sub nvarchar(100) = {auth{sub}};

    -- Auto-register on first login
    if not exists (select 1 from app_users where email = @email)
    begin
      insert into app_users (id, email, name, provider_sub, role, created_at)
      values (newid(), @email, @name, @sub, 'user', getutcdate());
    end

    -- Return user profile
    select id, email, name, role, created_at
    from app_users
    where email = @email;
    ]]>
  </query>
</whoami>
```

This is a common pattern for SPA/mobile apps — the first API call after login creates the user record.

## Pattern 6: Delegation Checks

Allow actions only if the user has specific permission:

```sql
declare @user_email nvarchar(500) = {auth{email}};
declare @target_contact_id UNIQUEIDENTIFIER = {{id}};

-- Check if user owns this contact or is an admin
declare @is_owner bit = 0;
declare @is_admin bit = 0;

if exists (select 1 from contacts where id = @target_contact_id and owner_email = @user_email)
  set @is_owner = 1;

if exists (select 1 from app_users where email = @user_email and role = 'admin')
  set @is_admin = 1;

if @is_owner = 0 and @is_admin = 0
begin
  throw 50403, 'You can only modify your own contacts', 1;
  return;
end

-- Proceed with the operation...
```

## Combining Regular Parameters with Claims

An endpoint can use both `{{param}}` (from request) and `{auth{claim}}` (from token):

```sql
-- From the HTTP request:
declare @search nvarchar(500) = {{search}};
declare @take int = {{take}};

-- From the JWT token:
declare @user_email nvarchar(500) = {auth{email}};

-- Use both: search within user's own data
select * from contacts
where owner_email = @user_email
  and (@search is null or name like '%' + @search + '%')
order by name
offset 0 rows fetch next ISNULL(@take, 50) rows only;
```

The user controls the search filters, but the ownership filter comes from the JWT and can't be bypassed.

---

### What You Learned

- How `{auth{claim}}` values differ from `{{param}}` — they're tamper-proof
- User-owned data patterns with JWT-based ownership
- Multi-tenant data isolation using token claims
- Audit trail implementation with authenticated identity
- Role-based query logic (different results for different roles)
- Auto-registration on first login
- Delegation and ownership checks

---

**Next:** [Response Caching →](11-caching.md)

**[Back to Tutorial Index](index.md)**
