# DBToRestAPI Tutorial

Welcome! This tutorial walks you through building a complete REST API from scratch — a **phonebook contacts** application — using nothing but SQL queries and XML configuration.

By the end, you'll have a fully functional API with:
- CRUD operations (Create, Read, Update, Delete)
- Pagination and search
- Input validation (mandatory fields, regex, SQL error codes)
- Error handling with custom HTTP status codes
- API key protection
- JWT/OIDC authentication with claims in SQL
- Multiple database support
- Response caching
- API gateway routing
- File uploads and downloads
- Embedded HTTP calls from SQL
- Multi-query chaining across databases
- Production deployment guidance

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
| 2 | [Building CRUD Endpoints](02-basic-crud.md) | POST/GET/PUT/DELETE, mandatory parameters, `success_status_code` |
| 3 | [Parameters Deep Dive](03-parameters.md) | Parameter sources, priority, nested JSON, headers |
| 4 | [Pagination & Filtering](04-pagination-filtering.md) | `count_query`, search, `take`/`skip` |
| 5 | [Update & Delete](05-update-delete.md) | Route parameters like `{{id}}`, returning modified data |

### Configuration & Validation

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 6 | [XML Configuration Structure](06-xml-structure.md) | Config files, hot-reload, encryption, multi-file organization |
| 7 | [Input Validation](07-regex-validation.md) | Mandatory parameters, SQL error codes, regex delimiters |

### Security

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 8 | [API Key Protection](08-api-keys.md) | API key collections, protecting endpoints |
| 9 | [JWT & OIDC Authentication](09-jwt-auth.md) | Providers, `{auth{email}}`, roles, database-driven authorization |
| 10 | [Using Claims in Queries](10-claims-in-queries.md) | Accessing JWT claims in SQL, auto-registration patterns |

### Performance

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 11 | [Caching](11-caching.md) | Memory cache for SQL endpoints and gateway routes |

### API Gateway

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 12 | [API Gateway](12-api-gateway.md) | Proxy routes, wildcards, protecting gateway routes |

### Databases & Files

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 13 | [Multiple Databases](13-multi-database.md) | Connection strings, providers, per-endpoint databases |
| 14 | [File Uploads](14-file-uploads.md) | Attach documents to contacts, local/SFTP stores |
| 15 | [File Downloads](15-file-downloads.md) | Stream files from stores, database, or HTTP |

### Advanced Features

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 16 | [Embedded HTTP Calls](16-http-from-sql.md) | `{http{...}http}` syntax, calling APIs from SQL |
| 17 | [Multi-Query Chaining](17-multi-query.md) | Cross-database workflows, parameter passing between queries |
| 20 | [Settings Variables](20-settings-vars.md) | `{s{}}` / `{settings{}}`, `<vars>` config, encrypted secrets |

### Going to Production

| # | Topic | What You'll Learn |
|---|-------|-------------------|
| 18 | [Production & Deployment](18-production.md) | Environment config, TLS, Docker, reverse proxy |
| 19 | [What's Next?](19-whats-next.md) | Further resources and community |

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
- [18-settings-vars.md](../topics/18-settings-vars.md) — Settings variables reference

---

**Ready?** Start with [Introduction →](00-introduction.md)
