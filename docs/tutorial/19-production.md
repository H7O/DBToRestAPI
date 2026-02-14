# 19 — Production & Deployment Tips

You have been running DBToRestAPI on `localhost` throughout this tutorial.  This
chapter covers the adjustments you should make before deploying to a real
environment.

---

## 1. Disable Debug Mode

During development the `debug_mode_header_value` setting lets you pass a header
to see raw SQL errors in the response:

```xml
<!-- settings.xml -->
<debug_mode_header_value>54321</debug_mode_header_value>
```

In production, **remove this tag entirely** or set it to a long, secret value
that only your operations team knows.  If the tag is absent, no debug header is
accepted and all database errors are replaced with the generic message:

```xml
<generic_error_message><![CDATA[An error occurred while processing your request.]]></generic_error_message>
```

> **Important:** Leaking SQL error messages can expose table names, column names,
> and database engine versions to attackers.

---

## 2. Encrypt Sensitive Configuration

DBToRestAPI can encrypt sections of your XML configuration at rest using the
.NET Data Protection API.  On first startup, plain-text values in the declared
sections are encrypted in-place and the application keeps decrypted copies in
memory only.

```xml
<!-- settings.xml -->
<settings_encryption>
  <encryption_prefix>encrypted:</encryption_prefix>
  <data_protection_key_path>c:/secure/vault/keys/</data_protection_key_path>

  <sections_to_encrypt>
    <!-- Encrypt all connection strings -->
    <section>ConnectionStrings</section>

    <!-- Encrypt API key collections -->
    <section>api_keys_collections:external_vendors</section>

    <!-- Encrypt auth provider secrets -->
    <section>authorize:providers:azure_b2c</section>

    <!-- Encrypt SFTP passwords -->
    <section>file_management:sftp_file_store:production_sftp:password</section>
  </sections_to_encrypt>
</settings_encryption>
```

After the first run, the encrypted values look like this in the file:

```
encrypted:CfDJ8N3...long_base64_string...
```

**Key points:**

- Encryption is **machine-specific** — values encrypted on one server cannot be
  decrypted on another.
- When migrating to a new server, deploy with **plain-text** values and let the
  application encrypt them on first startup.
- Store the `data_protection_key_path` in a secure, backed-up location.

---

## 3. Lock Down CORS

The default CORS configuration allows all origins (`*`).  In production, restrict
it to your actual domains:

```xml
<!-- settings.xml -->
<cors>
  <!-- Regex pattern: match only your domains -->
  <pattern><![CDATA[^(app\.example\.com|admin\.example\.com)$]]></pattern>

  <!-- Fallback if the caller's origin doesn't match -->
  <fallback_origin><![CDATA[app.example.com]]></fallback_origin>
</cors>
```

You can also override CORS per endpoint by adding a `<cors>` block inside
individual query or gateway nodes.

---

## 4. Use HTTPS and Real Certificates

The development profile uses a self-signed certificate.  For production, configure
Kestrel with a real certificate in `appsettings.Production.json`:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:5000" },
      "Https": {
        "Url": "https://*:5001",
        "Certificate": {
          "Path": "config/certs/production.pfx",
          "Password": "your-strong-password"
        }
      }
    }
  }
}
```

Alternatively, terminate TLS at a reverse proxy (nginx, Azure App Gateway, AWS
ALB) and let Kestrel listen on HTTP only behind it.

---

## 5. Set Appropriate Timeouts

The global database command timeout defaults to 30 seconds:

```xml
<db_command_timeout>30</db_command_timeout>
```

Review this for your workload.  You can also set per-endpoint timeouts:

```xml
<slow_report>
  <db_command_timeout>120</db_command_timeout>
  <query><![CDATA[ ... ]]></query>
</slow_report>
```

And per-query timeouts in multi-query chains:

```xml
<query db_command_timeout="120"><![CDATA[ ... ]]></query>
```

---

## 6. Limit Payload Size

The maximum request body size is configured in `settings.xml`:

```xml
<max_payload_size_in_bytes>367001600</max_payload_size_in_bytes>
```

That is roughly 350 MB — generous for file uploads but potentially too large
if you only handle JSON.  Reduce it to something appropriate for your use case:

```xml
<!-- 10 MB for a JSON-only API -->
<max_payload_size_in_bytes>10485760</max_payload_size_in_bytes>
```

---

## 7. Protect Every Endpoint

Before going live, audit your `sql.xml` and `api_gateway.xml` to make sure
every endpoint has at least one of:

| Protection | Tag |
|------------|-----|
| API key | `<api_keys_collections>your_collection</api_keys_collections>` |
| JWT auth | `<authorize>` block with a configured provider |
| Both | API key **and** JWT combined |

Endpoints without any protection are publicly accessible to anyone who can reach
your server.

---

## 8. Separate Configuration Files

Keep your config files organized and avoid a single monolithic `sql.xml`:

```xml
<!-- settings.xml -->
<additional_configurations>
  <path>config/contacts.xml</path>
  <path>config/orders.xml</path>
  <path>config/reports.xml</path>
  <path>config/api_gateway.xml</path>
  <path>config/api_keys.xml</path>
  <path>config/file_management.xml</path>
  <path>config/regex.xml</path>
  <path>config/auth_providers.xml</path>
</additional_configurations>
```

All files hot-reload independently — editing `contacts.xml` does not affect
endpoints defined in `orders.xml`.

If you add or remove `<path>` entries, the change requires a restart unless you
enable:

```xml
<restart_on_path_changes>true</restart_on_path_changes>
```

---

## 9. Caching Strategy

For production traffic, add caching to read-heavy endpoints
(see [Tutorial 11 — Caching](11-caching.md)):

```xml
<cache>
  <memory>
    <duration_in_milliseconds>60000</duration_in_milliseconds>
    <invalidators>relevant_param</invalidators>
  </memory>
</cache>
```

Good candidates for caching:

- Lookup / reference data (countries, categories, roles)
- Dashboard / aggregate queries
- API gateway proxy routes to slow third-party APIs

Avoid caching:

- User-specific data that changes frequently
- Write endpoints (POST / PUT / DELETE)

---

## 10. Logging and Monitoring

Configure ASP.NET Core logging levels in `appsettings.Production.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

In production you typically want `Warning` or `Error` level to reduce noise.
Pipe logs to a centralised system (Application Insights, ELK, Seq, etc.) for
alerting and troubleshooting.

---

## 11. Hosting Options

DBToRestAPI is a standard ASP.NET Core application.  Common hosting patterns:

| Host | Notes |
|------|-------|
| **IIS** (Windows) | Publish with `dotnet publish`, host behind IIS as a reverse proxy |
| **Kestrel behind nginx** (Linux) | Use systemd to manage the process; nginx handles TLS |
| **Docker** | Build a container image with `dotnet publish` output |
| **Azure App Service** | Deploy the publish output directly |
| **AWS Elastic Beanstalk / ECS** | Standard .NET deployment |

Publish command:

```bash
dotnet publish -c Release -o ./publish
```

The `publish` folder contains everything needed.  Copy your `config/` folder
into it (or mount it as a volume in Docker) to provide your XML configuration.

---

## Production Checklist

Use this quick reference before going live:

- [ ] Debug mode disabled or secret value set
- [ ] Connection strings encrypted
- [ ] API keys encrypted
- [ ] Auth provider secrets encrypted
- [ ] CORS restricted to your domains
- [ ] HTTPS with a real certificate
- [ ] Every endpoint protected (API key and/or JWT)
- [ ] Payload size limit appropriate for your use case
- [ ] Database timeouts reviewed
- [ ] Caching enabled for read-heavy endpoints
- [ ] Logging level set to Warning or Error
- [ ] `config/` folder deployed alongside the application

---

## What You Learned

- How to disable debug mode and hide SQL errors in production.
- How to encrypt connection strings and other secrets at rest.
- How to lock down CORS, enforce HTTPS, and set timeouts.
- How to audit endpoint protection and organise configuration files.
- Hosting options and a pre-deployment checklist.

---

**Next:** [What's Next? →](20-whats-next.md)

[← Back to Index](index.md)
