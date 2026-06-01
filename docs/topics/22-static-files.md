# Static Files

Serve static content (HTML, CSS, JS, images, a SPA bundle, …) directly from the engine — as a
**fallback** behind your API routes.

## Overview

The engine is **API-first**. When a request arrives, it resolves API gateway routes and
database-query routes first; only when **no API route matches** does it fall back to serving a file
from the configured folder. If no file matches either, the normal `404` is returned.

```
api_gateway  →  db_query  →  static file  →  404
```

This means an endpoint like `api/users` always wins over a file of the same name — APIs are never
shadowed by static content.

Static serving reuses ASP.NET Core's built-in static file engine, so you get correct content types,
`Range` requests, `ETag` / `Last-Modified` and conditional `GET`, and directory-traversal
protection for free.

## Enabling it

Add a `static_files` block to `config/settings.xml`. The feature turns on automatically when the
block is present and `root_path` points at an existing, dedicated folder.

```xml
<static_files>
  <root_path><![CDATA[./web/]]></root_path>
  <default>index.html,index.htm</default>
</static_files>
```

A demo page ships in `./web/index.html`. With the block above, `GET /` serves it.

## Options

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `root_path` | Yes | — | Folder to serve from. Relative paths resolve against the app base directory. Must be a **dedicated** folder (see [Security](#security)). |
| `default` | No | `index.html,index.htm` | Comma-separated default documents served for `/` and directory paths. |
| `enabled` | No | `true` | Set to `false` to disable without removing the block. |
| `cache_control_max_age_seconds` | No | (none) | When set, adds `Cache-Control: public, max-age=<n>` to served files. |
| `serve_unknown_file_types` | No | `false` | When `false`, files whose extension has no known MIME type return `404` instead of being served. |
| `spa_fallback` | No | `false` | When `true`, an unmatched **browser navigation** (a `GET` request with `Accept: text/html`) is served the default document so a client-side router can handle the path. Asset requests (e.g. `*.js`) still `404`. Enable this for single-page apps (React/Vue/Angular). |

All settings **hot-reload** when `settings.xml` is saved, like the rest of the engine.

## How it fits the pipeline

Route resolution happens in middleware. When neither the API gateway resolver nor the database-query
resolver matches a request, the engine attempts static serving before emitting its `404`. Because
this short-circuits inside the routing middleware, static content is served **before** the
API-key / JWT checks — it is **public**.

> Static content is public. Do not place sensitive files in `root_path`.

## Security

- **Directory traversal is blocked.** Requests that try to escape the root (`/../config/...`,
  encoded variants, absolute paths) resolve to `404`. Serving is rooted at `root_path`, so anything
  outside it is unreachable.
- **Hidden / dotfiles** (e.g. `.env`, `.git`) are not served.
- **Use a dedicated folder.** The engine **refuses** (logs an error and stays disabled) a `root_path`
  that resolves to the application base directory or an ancestor of it, the `config/` folder, or the
  configured encryption key path (`settings_encryption:data_protection_key_path`) — any of which would
  otherwise expose `config/*.xml`, `appsettings*.json`, `demo.db`, or DPAPI key material. Point
  `root_path` at a folder like `./web/` that holds only public assets.
- **Everything under the root is public and anonymous-readable.** The guard above only protects against
  pointing the root *at* a sensitive folder — it does not police individual files you place *inside* a
  legitimate root. Keep backups, exports, `appsettings.*`, `.bak`/`.sql`/`.csv` dumps, and the like out
  of the served folder.
- **GET / HEAD only.** Other verbs fall through to the normal `404`.

## Examples

### Plain static site alongside an API

```xml
<static_files>
  <root_path><![CDATA[./web/]]></root_path>
  <default>index.html</default>
  <cache_control_max_age_seconds>3600</cache_control_max_age_seconds>
</static_files>
```

`GET /` → `web/index.html`; `GET /about.html` → `web/about.html`; `GET /api/...` → your API.

### Single-page application

```xml
<static_files>
  <root_path><![CDATA[./web/]]></root_path>
  <default>index.html</default>
  <spa_fallback>true</spa_fallback>
</static_files>
```

Deep links like `GET /dashboard/settings` (a browser navigation) are served `index.html`, letting
your client-side router render the right view, while `GET /assets/app.js` still returns the real file
(or `404` if missing).

### Overriding the folder per environment

Like any setting, `root_path` can be set via environment variable — no file edit needed:

```
static_files__root_path=/var/www/site
static_files__default=index.html
```
