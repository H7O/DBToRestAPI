# DBToRestAPI Tutorial

Welcome! This tutorial walks you through building a complete REST API from scratch — a **phonebook contacts** application — using nothing but SQL queries and XML configuration.

By the end, you'll have a fully functional API with:
- CRUD operations (Create, Read, Update, Delete)
- Pagination and search
- Custom actions (activate/deactivate)
- Error handling with custom HTTP status codes
- API key protection
- Multiple database support
- Response caching
- API gateway routing
- File uploads and downloads
- CORS configuration
- JWT/OIDC authentication
- Query chaining across databases
- Embedded HTTP calls from SQL
- Settings encryption

No API coding knowledge required — just basic SQL.

---

## Tutorial Topics

Each topic builds on the previous one. Follow them in order for the best learning experience.

### Getting Started

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 0 | [Introduction](00-introduction.md) | Prerequisites, project setup, how the solution works |
| 1 | [Your First API Endpoint](01-hello-world.md) | Run the app, call your first endpoint, understand `sql.xml` |

### Building the Phonebook — CRUD

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 2 | [Creating Contacts](02-creating-contacts.md) | POST endpoints, mandatory parameters, `success_status_code` |
| 3 | [Reading Contacts](03-reading-contacts.md) | GET endpoints, pagination, search, `count_query` |
| 4 | [Updating Contacts](04-updating-contacts.md) | PUT endpoints, route parameters like `{{id}}` |
| 5 | [Deleting Contacts](05-deleting-contacts.md) | DELETE endpoints, returning deleted data |

### Shaping Responses & Actions

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 6 | [Custom Actions](06-custom-actions.md) | Multi-parameter routes, activate/deactivate pattern |
| 7 | [Response Formats](07-response-formats.md) | `response_structure`, nested JSON with `{type{json{}}}` |

### Error Handling

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 8 | [Error Handling](08-error-handling.md) | SQL → HTTP error codes, all databases, debug mode |

### Security

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 9 | [API Key Protection](09-api-keys.md) | API key collections, protecting endpoints |

### Databases

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 10 | [Multiple Databases](10-multiple-databases.md) | Connection strings, providers, per-endpoint databases |

### Performance

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 11 | [Caching](11-caching.md) | Memory cache for SQL endpoints and gateway routes |

### API Gateway

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 12 | [API Gateway](12-api-gateway.md) | Proxy routes, wildcards, protecting gateway routes |

### File Management

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 13 | [File Uploads](13-file-uploads.md) | Attach documents to contacts, local/SFTP stores |
| 14 | [File Downloads](14-file-downloads.md) | Stream files from stores, database, or HTTP |

### Cross-Origin & Authentication

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 15 | [CORS](15-cors.md) | Pattern matching, credentials, preflight handling |
| 16 | [Authentication](16-authentication.md) | OIDC/JWT, `{auth{email}}`, database-driven authorization |

### Advanced Features

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 17 | [Query Chaining](17-query-chaining.md) | Cross-database workflows, parameter passing between queries |
| 18 | [Embedded HTTP Calls](18-embedded-http-calls.md) | `{http{...}http}` syntax, calling APIs from SQL |
| 19 | [Settings Encryption](19-settings-encryption.md) | DPAPI, cross-platform encryption, key management |

---

## Reference Documentation

For quick lookups and comprehensive details on any feature, see the [reference topics](../topics/):

- [01-overview.md](../topics/01-overview.md) — Architecture and quick start
- [02-configuration.md](../topics/02-configuration.md) — All configuration files
- [03-crud-operations.md](../topics/03-crud-operations.md) — CRUD patterns
- [04-parameters.md](../topics/04-parameters.md) — Parameter injection
- [05-response-formats.md](../topics/05-response-formats.md) — Response shaping
- [06-api-keys.md](../topics/06-api-keys.md) — API key reference
- [07-caching.md](../topics/07-caching.md) — Caching reference
- [08-api-gateway.md](../topics/08-api-gateway.md) — Gateway reference
- [09-file-uploads.md](../topics/09-file-uploads.md) — Upload reference
- [10-file-downloads.md](../topics/10-file-downloads.md) — Download reference
- [11-cors.md](../topics/11-cors.md) — CORS reference
- [12-authentication.md](../topics/12-authentication.md) — Auth reference
- [13-databases.md](../topics/13-databases.md) — Multi-database reference
- [14-query-chaining.md](../topics/14-query-chaining.md) — Chaining reference
- [15-encryption.md](../topics/15-encryption.md) — Encryption reference
- [16-tls-certificates.md](../topics/16-tls-certificates.md) — TLS/HTTPS setup
- [17-embedded-http-calls.md](../topics/17-embedded-http-calls.md) — HTTP calls reference

---

**Ready?** Start with [Introduction →](00-introduction.md)
