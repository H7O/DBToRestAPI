# Settings Encryption

Automatically encrypt sensitive configuration values like connection strings and API secrets.

## Overview

- Sensitive values encrypted on first startup
- Decrypted transparently at runtime
- Supports Windows DPAPI and cross-platform Data Protection API

## Quick Start (Windows)

Windows uses DPAPI automatically — no setup required.

### Step 1: Configure Encryption

Add to `/config/settings.xml`:

```xml
<settings_encryption>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
    <section>authorize:providers:azure_b2c:client_secret</section>
  </sections_to_encrypt>
</settings_encryption>
```

### Step 2: Run Application

On first startup, unencrypted values are automatically encrypted:

**Before:**
```xml
<ConnectionStrings>
  <default>Server=myserver;Password=MySecret123!</default>
</ConnectionStrings>
```

**After:**
```xml
<ConnectionStrings>
  <default>encrypted:CfDJ8NhY2kB...long-base64-string...</default>
</ConnectionStrings>
```

Values are decrypted in memory — your code accesses them normally.

## Cross-Platform Setup

For Linux, macOS, Docker, or Kubernetes, configure a key directory:

```xml
<settings_encryption>
  <data_protection_key_path>./keys/</data_protection_key_path>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
  </sections_to_encrypt>
</settings_encryption>
```

Or via environment variable:
```bash
DATA_PROTECTION_KEY_PATH=./keys/
```

⚠️ **Important:** Persist the keys directory! Losing keys = losing access to encrypted values.

## Encryption Method Priority

1. **If `data_protection_key_path` configured** → ASP.NET Core Data Protection API
2. **Else if Windows** → DPAPI (machine-bound)
3. **Else** → Encryption disabled (passthrough mode)

## What to Encrypt

Specify paths in `sections_to_encrypt`:

| Path | What Gets Encrypted |
|------|---------------------|
| `ConnectionStrings` | All connection strings |
| `ConnectionStrings:default` | Only the "default" connection |
| `authorize:providers:azure_b2c` | All azure_b2c settings |
| `authorize:providers:azure_b2c:client_secret` | Only client_secret |

## Configuration Examples

### Encrypt All Connection Strings

```xml
<settings_encryption>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
  </sections_to_encrypt>
</settings_encryption>
```

### Encrypt Specific Items

```xml
<settings_encryption>
  <sections_to_encrypt>
    <section>ConnectionStrings:production</section>
    <section>authorize:providers:azure_b2c:client_secret</section>
    <section>authorize:providers:auth0:client_secret</section>
  </sections_to_encrypt>
</settings_encryption>
```

### Cross-Platform with Key Directory

```xml
<settings_encryption>
  <data_protection_key_path>/app/keys/</data_protection_key_path>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
  </sections_to_encrypt>
</settings_encryption>
```

## Docker/Kubernetes

### Docker Compose

```yaml
services:
  api:
    image: dbtorestapi
    volumes:
      - ./keys:/app/keys
    environment:
      - DATA_PROTECTION_KEY_PATH=/app/keys/
```

### Kubernetes

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: encryption-keys
spec:
  accessModes: [ReadWriteOnce]
  resources:
    requests:
      storage: 100Mi
---
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: api
          volumeMounts:
            - name: keys
              mountPath: /app/keys
          env:
            - name: DATA_PROTECTION_KEY_PATH
              value: /app/keys/
      volumes:
        - name: keys
          persistentVolumeClaim:
            claimName: encryption-keys
```

## Behavior

### First Run
1. Reads unencrypted values
2. Encrypts and writes back to XML
3. Continues with decrypted values in memory

### Subsequent Runs
1. Reads encrypted values
2. Decrypts in memory
3. Original XML unchanged

### Adding New Values
1. Add unencrypted value to config (or add a new section to `sections_to_encrypt`)
2. Save the file — the application detects the change automatically
3. Value encrypted without a restart

## Security Notes

### Windows DPAPI
- Keys bound to machine/user account
- Moving config files to different machine = values unreadable
- No additional key management

### Data Protection API
- Keys stored in specified directory
- Portable across machines with same keys
- **Must secure and backup key directory**

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "encrypted:" values not decrypting | Check key directory exists and has correct permissions |
| Values not encrypting on startup | Verify `sections_to_encrypt` paths are correct |
| Cross-platform: encryption fails | Ensure `data_protection_key_path` is configured |
| Lost keys | Restore from backup, or re-enter unencrypted values |

## Best Practices

1. **Backup keys** when using Data Protection API
2. **Use environment-specific keys** (dev, staging, prod)
3. **Restrict access** to config files and key directories
4. **Don't commit** encrypted values to source control (still reveals structure)
5. **Test decryption** after deployment

## Related Topics

- [Configuration](02-configuration.md) - settings.xml structure
- [Authentication](12-authentication.md) - Encrypting auth secrets
- [Multi-Database](13-databases.md) - Encrypting connection strings
