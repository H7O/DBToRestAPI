# Static file serving — design & implementation handoff

**Date:** 2026-05-30
**Status:** ✅ Implemented, tested (328 tests green incl. 18 new), security-reviewed (0 findings), and verified end-to-end against the running app. See the checklist in [§8](#8-implementation-checklist) and the review in [§10](#10-security-review).

---

## 1. TL;DR

Add **static-page serving as a fallback** to the engine. The app stays API-first: API
gateway routes and database-query routes are resolved first; only when **both** miss does the
request fall back to serving a file from a configured folder; if no file matches, the normal
JSON 404 is returned.

Priority order:

```
api_gateway  →  db_query  →  static file  →  404
```

Driven entirely by a new `static_files` block in `config/settings.xml`. No new NuGet packages —
we reuse ASP.NET Core's in-framework `StaticFileMiddleware` / `DefaultFilesMiddleware` over a
`PhysicalFileProvider`.

---

## 2. Goals & decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Should static content be public, or gated by the API-key/JWT checks? | **Public.** Static serving short-circuits *before* `Step2`–`Step8`, so it never touches CORS/api-key/JWT. Typical for landing pages, public assets, SPA shells. |
| 2 | What happens when a path matches no API route **and** no static file? | **Configurable** via `static_files:spa_fallback`. Default **strict 404** (return the existing JSON 404). When `true`, serve the default document (e.g. `index.html`) for browser navigation requests so a client-side router can handle the path. |

Non-goals (for this iteration): per-route auth on static content, directory listing, virtual path
prefixes (e.g. serve only under `/app/*`), pre-compression negotiation beyond what
`StaticFileMiddleware` does by default.

---

## 3. How the pipeline resolves a request today (context)

`ApiController` has a catch-all route `[Route("{*route}")]`
([DBToRestAPI/Controllers/ApiController.cs:223](DBToRestAPI/Controllers/ApiController.cs#L223)),
so ASP.NET routing matches **every** request to that endpoint. The real routing decision happens
in middleware that runs *after* routing matched but *before* the controller executes:

- [`Step1ServiceTypeChecks`](DBToRestAPI/Middlewares/Step1ServiceTypeChecks.cs) calls
  `RouteConfigResolver.ResolveRoute` (API gateway) then `QueryRouteResolver.ResolveRoute`
  (DB query).
- If both return `null`, **Step1 is the single chokepoint** that writes the
  `404 "API Endpoint not found"` and short-circuits the rest of the chain
  (`Step2`–`Step8` + controller never run).

That 404 branch is the natural fallback point. "API takes priority, static is the fallback"
maps cleanly onto "if both resolvers miss, try static before 404." No routing changes needed.

### Why not just `app.UseStaticFiles()` at the top of the pipeline?

That gives **static-first** priority (any path that exists as a file is served before API logic
runs), which is the opposite of the requirement and would let a file named `users` shadow an
`api/users` endpoint. We still reuse the *engine* inside `UseStaticFiles`, but invoke it as a
fallback from the existing chokepoint.

---

## 4. Design

### 4.1 New service: `StaticFileFallbackService` (singleton)

File: [DBToRestAPI/Services/StaticFileFallbackService.cs](DBToRestAPI/Services/StaticFileFallbackService.cs)

- Mirrors the existing resolver pattern (`QueryRouteResolver` / `RouteConfigResolver`): reads its
  config section, builds state, and **reloads on config change** via
  `ChangeToken.OnChange(() => config.GetSection("static_files").GetReloadToken(), Rebuild)` guarded
  by an `AtomicGate`.
- Holds an immutable `StaticState` snapshot (swapped atomically; old `PhysicalFileProvider`
  disposed on rebuild to release its file watcher).
- Builds a tiny reusable sub-pipeline by `new`-ing the framework middlewares directly (their
  constructors are public, so no `IApplicationBuilder` is required) over our own
  `PhysicalFileProvider` (so **no `wwwroot` is needed**):

  ```
  DefaultFilesMiddleware (index document)  →  StaticFileMiddleware (serve)  →  terminal (flag miss)
  ```

- Exposes `Task<bool> TryServeAsync(HttpContext)`:
  1. If disabled or verb ∉ {GET, HEAD} → return `false` (response untouched).
  2. Save and **clear the endpoint** (`context.SetEndpoint(null)`) — the static middlewares
     deliberately refuse to act when an endpoint is already selected, and routing already matched
     the catch-all controller. **This one line is what makes reuse work.**
  3. Run the sub-pipeline. If the terminal's "miss" flag is absent → a file was served → `true`.
  4. On a miss, if `spa_fallback` is enabled and the request is a browser navigation
     (`GET` + `Accept: text/html`), retry once serving the default document.
  5. Otherwise restore the endpoint and return `false` so the caller emits its normal 404.

Why reuse `StaticFileMiddleware` instead of hand-rolling file streaming: it already implements
`Range` requests, `ETag`/`Last-Modified`, conditional GET, content-type mapping, and — critically
— directory-traversal protection via `PhysicalFileProvider`. See [§6](#6-security).

### 4.2 Integration into `Step1ServiceTypeChecks`

Inject `StaticFileFallbackService` and call `if (await _staticFileFallback.TryServeAsync(context)) return;`
immediately before each "no API route" 404, at three sites:

| Site | Location (current line) | Case | Why |
|------|-------------------------|------|-----|
| A | empty route (`"Kindly specify an API endpoint"`) [Step1:129](DBToRestAPI/Middlewares/Step1ServiceTypeChecks.cs#L129) | request to `/` (and `dir/`) | serves the default document at the root |
| B | no `queries` section (`"No API Endpoints defined"`) [Step1:190](DBToRestAPI/Middlewares/Step1ServiceTypeChecks.cs#L190) | static-only deployments (no DB queries configured) | lets a pure-static site work |
| C | query not found (`"API Endpoint ``{route}`` not found"`) [Step1:242](DBToRestAPI/Middlewares/Step1ServiceTypeChecks.cs#L242) | the main "unknown path" case | the primary fallback |

The OPTIONS no-match branch is intentionally left as-is: `StaticFileMiddleware` only serves
GET/HEAD, so `TryServeAsync` would miss anyway; CORS-preflight 404 for a non-API path is fine.

Because static serving short-circuits inside Step1, the static path never reaches `Step2`–`Step8`
(all of which assume an API `section` exists in `context.Items` and would otherwise 500). Clean
separation, and it satisfies the "public" decision.

### 4.3 DI registration

`Program.cs`: `builder.Services.AddSingleton<StaticFileFallbackService>();` next to the other
resolvers. `IWebHostEnvironment` and `ILoggerFactory` (the service's other ctor deps) are already
in DI.

---

## 5. Configuration schema (`config/settings.xml` → `static_files`)

```xml
<static_files>
  <root_path><![CDATA[./web/]]></root_path>      <!-- required; dedicated folder, NOT the app root -->
  <default>index.html,index.htm</default>         <!-- optional; default documents, comma-separated -->

  <!-- all optional -->
  <enabled>true</enabled>                          <!-- set false to disable without deleting the block -->
  <cache_control_max_age_seconds>3600</cache_control_max_age_seconds>
  <serve_unknown_file_types>false</serve_unknown_file_types>  <!-- false = only known MIME types served -->
  <spa_fallback>false</spa_fallback>               <!-- true = serve index.html for unmatched navigations -->
</static_files>
```

Activation rule: feature is **on** when the block exists, `enabled` is not `false`, `root_path` is
non-empty, resolves to a safe folder, and that folder exists. Otherwise it is silently disabled
(with a logged warning/error explaining why). Relative `root_path` is resolved against
`AppContext.BaseDirectory` (consistent with how config paths and the HTTPS cert path are resolved
in `Program.cs`). Reloads automatically on `settings.xml` change.

---

## 6. Security

- **Directory traversal** is handled by `PhysicalFileProvider`: paths that escape the root
  (`..`, absolute paths, rooted tricks, encoded variants already decoded by Kestrel) resolve to
  `NotFound`. This is the "don't traverse backwards" requirement, solved by the framework rather
  than by hand-rolled string checks.
- **Dotfiles / hidden / system files** are excluded by default (`ExclusionFilters.Sensitive`).
- **`serve_unknown_file_types` defaults to `false`** — files whose extension has no known MIME
  mapping are not served (404), reducing accidental exposure of odd files.
- **Root-folder guard (engine-level):** `Rebuild()` refuses a `root_path` that resolves to (a) the
  application base directory **or an ancestor of it**, (b) the conventional `config/` folder, or
  (c) the configured `settings_encryption:data_protection_key_path` (DPAPI key ring) — any of which
  would expose `config/*.xml`, `appsettings*.json`, `demo.db`, or key material. A dedicated subfolder
  (`./web/`) is disjoint from all three, so it passes; `PhysicalFileProvider` rooted there then makes
  `../config` physically unreachable. **Operator guidance:** always point `root_path` at a dedicated
  folder. The guard protects against a bad root *location*; it does **not** police individual
  sensitive files placed *inside* a legitimate root — everything under the root is anonymous-readable.
- **Verb restriction:** only GET/HEAD are eligible.
- **Public by design:** static content is served before auth (per decision #1). Do not place
  sensitive content in the static root.

---

## 7. Behavior matrix

| Request | API match? | File at root? | Result |
|---------|-----------|---------------|--------|
| `GET /api/users` | yes (db_query) | — | DB query runs (unchanged) |
| `GET /loopback/x` | yes (api_gateway) | — | gateway proxy (unchanged) |
| `GET /` | no | `index.html` exists | serves `index.html` (via DefaultFiles) |
| `GET /about.html` | no | exists | serves `about.html` |
| `GET /css/site.css` | no | exists | serves with correct content-type + caching headers |
| `GET /missing.html` | no | no | strict 404 (or `index.html` if `spa_fallback` + `Accept: text/html`) |
| `GET /assets/app.js` | no | no | 404 even with `spa_fallback` (not an HTML navigation) |
| `POST /anything` | no | — | not GET/HEAD → 404 (static skipped) |
| `GET /../settings.xml` | no | escapes root | 404 (traversal blocked) |
| any, `static_files` absent | no | — | 404 (feature disabled — current behavior preserved) |

---

## 8. Implementation checklist

- [x] **`Services/StaticFileFallbackService.cs`** — new service (reload, security guards, sub-pipeline, `TryServeAsync`).
- [x] **`Middlewares/Step1ServiceTypeChecks.cs`** — inject the service; add the fallback call at sites A/B/C.
- [x] **`Program.cs`** — register `AddSingleton<StaticFileFallbackService>()`.
- [x] **`config/settings.xml`** — add a commented `static_files` example block (feature off by default).
- [x] **`web/index.html`** (+ csproj copy rule) — a small demo page so enabling is one step (uncomment config).
- [x] **`DBToRestAPI.Tests/StaticFileFallbackServiceTests.cs`** — 18 unit tests (serve / miss / traversal / verb / disabled / root-guard incl. config/ + key-path / spa_fallback). All green.
- [x] **Docs** — `docs/topics/22-static-files.md` (reference) + `docs/tutorial/22-static-files.md` (tutorial chapter; What's Next renumbered 22→23) + README feature bullet.
- [x] **Build + run tests green** — `0 errors`, `328 passed`.
- [x] **Manual verify** (feature enabled via `static_files__root_path` env var, no settings.xml edit):
  - `GET /server_time` → `200 application/json` (API priority intact)
  - `GET /` → `200 text/html` from `web/index.html` (with `ETag`/`Last-Modified`/`Accept-Ranges`)
  - `GET /missing.html` → `404` JSON (strict default)
  - `GET /../appsettings.json` and `/..%2f..%2fconfig%2fsettings.xml` → `404` JSON (traversal blocked)
- [x] **Security review** (multi-agent: 3 identifier lenses → adversarial false-positive filtering) — **0 findings** at confidence ≥ 8. See [§10](#10-security-review).

---

## 10. Security review

A multi-agent review (path-traversal / auth-bypass / injection lenses, then adversarial
false-positive filtering, keep only confidence ≥ 8) found **no exploitable vulnerabilities**. Three
candidates were raised and all dismissed (confidence 3/10) as operator-misconfiguration or
defense-in-depth rather than network-exploitable code defects:

1. *Descendant root (`./config`) accepted* — operator-controlled config is trusted, and the docs
   warn against it. **Acted on anyway** as cheap hardening: the root guard now also refuses `config/`
   and the DPAPI key path (see [§6](#6-security)), with two added tests.
2. *Junction/symlink inside the root not canonicalized* — requires a pre-planted reparse point via
   trusted deployment or a separate write primitive; the read-only feature can't create one. Left as
   a documented residual (see follow-ups).
3. *Files under the root are public* — the documented, intended design of a public static surface.

---

## 11. Open follow-ups / future ideas

- **Symlink/junction canonicalization** (low priority): resolve reparse points
  (`DirectoryInfo.ResolveLinkTarget`) and/or verify `GetFileInfo(...).PhysicalPath` stays under the
  root, to harden against a reparse point planted inside the served folder during deployment.

- **Virtual path prefix** (e.g. serve static only under `/app/*`) if API and static namespaces
  ever risk colliding.
- **Per-route / token-gated static** if a deployment needs protected assets (would route through a
  dedicated auth check rather than the API-key/JWT steps).
- **Sensitive-file startup warning:** optionally scan the root at startup and warn if files with
  risky extensions (`.json`/`.xml`/`.bak`/`.sql`/`.db`/…) exist under it.
- **OpenAPI/Swagger note:** static serving is independent of the OpenAPI middleware (which
  short-circuits its own paths earlier); no interaction expected.
