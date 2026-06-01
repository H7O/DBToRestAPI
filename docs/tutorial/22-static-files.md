# 22 — Static Files

Your contacts API is built, secured, and production-ready. Often the last piece
is a **frontend** — a landing page, an admin dashboard, or a single-page app that
*calls* that API. DBToRestAPI can serve those static files itself, so you don't
need a second web server just to host `index.html`.

The key idea: serving is **API-first**. Your API routes always win; a static file
is served only when **no** API endpoint matches the request. If neither matches,
you get the normal `404`.

```
api_gateway  →  db_query  →  static file  →  404
```

---

## What You'll Get

| Request | Served by |
|---------|-----------|
| `GET /contacts` | your DB query endpoint (unchanged) |
| `GET /` | `web/index.html` (default document) |
| `GET /app.js` | `web/app.js` (static file) |
| `GET /nope` | `404` (no endpoint, no file) |

No packages, no build step — just a config block and a folder.

---

## Step 1: Create a Web Folder

Create a `web/` folder next to your config and drop an `index.html` in it. Here's
a tiny page that calls the `contacts` endpoint you built earlier:

```html
<!-- web/index.html -->
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Phonebook</title>
</head>
<body>
  <h1>Contacts</h1>
  <ul id="list"></ul>

  <script>
    fetch('/contacts')
      .then(r => r.json())
      .then(rows => {
        const ul = document.getElementById('list');
        for (const c of rows) {
          const li = document.createElement('li');
          li.textContent = `${c.first_name} ${c.last_name}`;
          ul.appendChild(li);
        }
      });
  </script>
</body>
</html>
```

> The folder must be a **dedicated** assets folder. Never point the root at your
> app directory or `config/` — see [Step 4](#step-4-stay-safe).

---

## Step 2: Enable Static Serving

Add a `static_files` block to `config/settings.xml`:

```xml
<settings>
  <!-- ... existing configuration ... -->

  <static_files>
    <root_path><![CDATA[./web/]]></root_path>
    <default>index.html,index.htm</default>
  </static_files>
</settings>
```

Save the file (the engine hot-reloads). You'll see a line in the log confirming
the resolved root:

```
Static file serving enabled. Root: `.../web/`; default documents: [index.html, index.htm]; ...
```

---

## Step 3: Try It

Open `http://localhost:5000/` in a browser — you should see your **Contacts**
page, populated by a live call to `/contacts`. A few things to notice:

```bash
# Root serves the default document
curl -i http://localhost:5000/

# Your API still takes priority — this is the DB endpoint, not a file
curl -i http://localhost:5000/contacts

# An unknown path with no file is a normal 404
curl -i http://localhost:5000/does-not-exist
```

Because the built-in static engine does the serving, you also get `ETag` /
`Last-Modified` headers, conditional `GET` (`304 Not Modified`), and `Range`
request support for free — handy for images and downloads.

---

## Step 4: Stay Safe

Static content is **public** — it is served *before* the API-key and JWT checks,
which is normal for a website or SPA shell. Two rules keep it safe:

1. **Put only public assets in the root.** Everything under `root_path` is
   readable by anyone. Don't drop backups, exports, `appsettings.*`, or `.bak`
   files there.
2. **Use a dedicated folder.** The engine *refuses* to serve if `root_path`
   resolves to the application base directory (or an ancestor), the `config/`
   folder, or your encryption key path — those would leak connection strings and
   secrets. It logs an error and stays disabled instead.

Directory traversal is blocked for you: requests like `GET /../config/settings.xml`
resolve to `404`. Hidden/dotfiles (`.env`, `.git`) are never served, and by
default only files with a known type are served (`serve_unknown_file_types` is
`false`).

---

## Step 5: Single-Page Apps (Optional)

If you're hosting a SPA (React, Vue, Angular) with client-side routing, a deep
link like `/dashboard/settings` is not a real file — but the browser still needs
`index.html` so the router can render the right view. Turn on `spa_fallback`:

```xml
<static_files>
  <root_path><![CDATA[./web/]]></root_path>
  <default>index.html</default>
  <spa_fallback>true</spa_fallback>
</static_files>
```

Now an unmatched **navigation** (a `GET` with `Accept: text/html`) is served
`index.html`, while a missing **asset** (e.g. `GET /assets/app.js`) still returns
a real `404` — so broken asset links don't silently get HTML.

---

## All the Options

| Setting | Default | Purpose |
|---------|---------|---------|
| `root_path` | — (required) | Folder to serve from. Relative to the app base directory. |
| `default` | `index.html,index.htm` | Default documents for `/` and directory paths. |
| `enabled` | `true` | Set `false` to disable without removing the block. |
| `cache_control_max_age_seconds` | (none) | Adds `Cache-Control: public, max-age=<n>`. |
| `serve_unknown_file_types` | `false` | Serve files with unmapped extensions (leave off unless needed). |
| `spa_fallback` | `false` | Serve the default document for unmatched HTML navigations. |

For the full reference, see [Static Files (topic)](../topics/22-static-files.md).

---

## What You Learned

- How to serve a website or SPA directly from the engine, as an **API-first**
  fallback that never shadows your endpoints.
- How to wire a static page to a live API endpoint you built earlier.
- Why static content is public, and the two rules (dedicated folder, public
  assets only) plus the built-in guards that keep it safe.
- How `spa_fallback` supports client-side routing without masking missing assets.

---

**Next:** [What's Next? →](23-whats-next.md)

[← Back to Index](index.md)
