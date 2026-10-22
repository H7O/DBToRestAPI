---
title: CORS
summary: The Access-Control-* headers the engine sends so that a browser page on another origin can read an endpoint's answer, their defaults, and how an <authorize> section anywhere changes them.
keywords: [cors, pattern, fallback_origin, max_age, allow_credentials, allowed_headers, exposed_headers, Origin, Access-Control-Allow-Origin, Access-Control-Allow-Credentials, Access-Control-Allow-Headers, Access-Control-Allow-Methods, Access-Control-Max-Age, Access-Control-Expose-Headers, Content-Disposition, Retry-After, OPTIONS, preflight, 204, X-Auth-Provider, Vary]
---

# CORS

> For AI agents: read [AGENTS.md](../../AGENTS.md) first. The documentation index is [llms.txt](../../llms.txt). Errors: [errors.md](../reference/errors.md).

CORS headers tell a browser whether a page from another origin may read an endpoint's answer. The engine sends them on every response of a route, from a `<cors>` block on the route or in `settings.xml`, and answers the browser's `OPTIONS` preflight itself. CORS is not access control: the engine runs every request whatever its origin, and only a browser enforces the headers. Protect an endpoint with [API keys](06-api-keys.md) or [`<authorize>`](12-authentication.md).

## Rules

1. **Anchor the pattern with `^` and `$`.** `<pattern>` is a .NET regular expression matched against the host of the request's `Origin` only, without its scheme or port, ignoring case. Unanchored, `example\.com` also matches `example.com.attacker.net`. A pattern with a scheme or a port in it (`^https://app\.example\.com$`, `^localhost:3000$`) matches nothing.
2. **Set `<fallback_origin>` to your own origin, or the pattern restricts nothing.** An origin the pattern doesn't match gets `fallback_origin`, and without one `*`, which lets a page on any origin read the answer unless it sends cookies. The shipped `settings.xml` has a pattern and no fallback.
3. **To allow every origin, leave out both `<pattern>` and `<fallback_origin>`,** or set `<fallback_origin>*</fallback_origin>`.
4. **Expect credentials to be on.** When an `<authorize>` section exists on the route or at the root of any loaded file, every response says `Access-Control-Allow-Credentials: true`. The shipped `auth_providers.xml` has one at its root, so in the shipped config every route does. `<allow_credentials>false</allow_credentials>` turns it off.
5. **With credentials on, list in `<allowed_headers>` every request header the page sends that's outside the default list.** The default is then `Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key`, not `*`. A page that sends `X-Auth-Provider`, the header that picks the token's provider on a route with several ([12](12-authentication.md#how-a-provider-is-selected)), or a header of its own fails the preflight, and the browser never sends the request. `<allowed_headers>` replaces the default list, so repeat the defaults the page needs.
6. **A page that sends cookies (`credentials: 'include'`) needs a pattern that matches its origin.** For such a request the browser accepts only its own origin in `Access-Control-Allow-Origin`, never `*` or a fallback for another site.
7. **Edit the `<cors>` block that `settings.xml` already has.** A second `<cors>` beside it is numbered by the configuration reader and stops matching ([AGENTS.md](../../AGENTS.md) rule 12).

## How it works

1. The engine finds the route. A request that matches no route, and a file served from [static content](22-static-files.md), get no CORS headers.
2. Each setting is looked up on its own: in the route's `<cors>`, then in the `<cors>` at the root of `settings.xml`, then its default. A route that sets only `<allowed_headers>` keeps the global pattern and fallback.
3. For any method but `OPTIONS`, the engine sets the headers before it checks an API key, a token or a rate limit, so the errors those return carry them too, and so does an error from the query:
   - `Access-Control-Allow-Origin`: the request's `Origin` exactly as sent, when it has the form a browser sends (`scheme://host[:port]` in ASCII, without a path, user info or query) and its host matches `pattern`. Otherwise `fallback_origin`, or `*` without one. A request without an `Origin` header gets the fallback or `*` too.
   - `Access-Control-Allow-Methods`: the route's `<verb>` values in upper case, plus `OPTIONS`, in alphabetical order. A route without `<verb>` sends `GET, POST, PUT, DELETE, PATCH, OPTIONS`.
   - `Access-Control-Allow-Credentials: true` when credentials are on (rule 4). When they are off, the header is left out.
   - `Access-Control-Allow-Headers`: `allowed_headers`. Without it, the default list when credentials are on, and `*` when they are off.
   - `Access-Control-Max-Age`: `max_age`, `86400` without it.
   - `Access-Control-Expose-Headers`: `exposed_headers`, `Content-Disposition, Retry-After` without it. These are the response headers a page may read besides the few every browser shows, such as `Content-Type`: a [download](10-file-downloads.md)'s file name and a `429`'s wait. A `429` exposes `Retry-After` only.
   - `Vary: Origin`, when a pattern applies: the answer then depends on the request's origin, so a shared cache in front of the engine, such as a CDN, keeps one copy per origin.
4. An `OPTIONS` request is a preflight. The engine answers it `204`, without a body, before API keys, tokens and rate limits, and runs no query. It combines every route with the request's path and host, whatever their verbs:
   - methods: all their verbs plus `OPTIONS`, or the default list when none has a `<verb>`;
   - origin: the request's origin when one route's pattern matches it, otherwise the fallback of the first route that has one, otherwise `*`;
   - allowed headers: those of the first route that sets them, otherwise the default;
   - credentials: on when one of the routes has them on, each deciding as it does for its own responses;
   - max age: the shortest.

   A preflight sends no `Access-Control-Expose-Headers`: only the answer that follows needs it. A path that no route has gets `404`. A gateway route's preflight is answered the same way, from that route's `<cors>`.
5. On a [gateway](08-api-gateway.md) route, a response header the target sends replaces the engine's: a target that sends its own `Access-Control-Allow-Origin` decides it.

## Complete example: a page on another origin calls a protected route

A page at `https://app.example.com` calls the engine at `https://api.example.com` with a token from one of two providers, and says which one in `X-Auth-Provider`. No other site's pages may read the answers.

`config/settings.xml`: edit the `<cors>` block that is already there.

```xml
<settings>
  <cors>
    <pattern><![CDATA[^(app\.example\.com|localhost)$]]></pattern>
    <fallback_origin>https://app.example.com</fallback_origin>
  </cors>
</settings>
```

`config/sql.xml`, inside `<settings><queries>`, SQL Server:

```xml
<my_orders>
  <route>my/orders</route>
  <verb>GET</verb>
  <response_structure>array</response_structure>
  <authorize>
    <provider>google,azure_b2c</provider>
  </authorize>
  <cors>
    <allowed_headers>Authorization, Content-Type, X-Auth-Provider</allowed_headers>
    <max_age>600</max_age>
  </cors>
  <query><![CDATA[
    SELECT id, placed_at, total
    FROM orders
    WHERE owner_id = {auth{user_id}}
    ORDER BY placed_at DESC;
  ]]></query>
</my_orders>
```

The page:

```js
const res = await fetch('https://api.example.com/my/orders', {
  headers: { Authorization: `Bearer ${token}`, 'X-Auth-Provider': 'google' }
});
```

| Request | Answer |
|---|---|
| The browser's preflight from `https://app.example.com`, asking for `authorization,x-auth-provider` | `204` with `Access-Control-Allow-Origin: https://app.example.com`, `Access-Control-Allow-Credentials: true`, `Access-Control-Allow-Headers: Authorization, Content-Type, X-Auth-Provider`, `Access-Control-Allow-Methods: GET, OPTIONS`, `Access-Control-Max-Age: 600` |
| The `GET` that follows | `200` with the orders, the same headers, and `Access-Control-Expose-Headers: Content-Disposition, Retry-After` |
| The same page served from `https://other.example.org` | The preflight gets `Access-Control-Allow-Origin: https://app.example.com`, so the browser doesn't send the `GET` |
| `curl` without an `Origin` header | `200` with the orders. `Access-Control-Allow-Origin` is the fallback, and curl ignores it |
| The route without its `<allowed_headers>` | The preflight lists the default headers, without `X-Auth-Provider`: the browser reports a CORS error and doesn't send the `GET`. The engine logs nothing |

To check the headers without a browser:

```bash
curl -i -X OPTIONS https://api.example.com/my/orders \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: GET" \
  -H "Access-Control-Request-Headers: authorization,x-auth-provider"
```

## Settings

Each one goes in `<cors>` on a route or at the root of `settings.xml`. The route's value wins, tag by tag.

| Tag | Default | Notes |
|---|---|---|
| `pattern` | none: no origin matches | .NET regular expression matched against the origin's host, ignoring case, with a one-second limit. An invalid pattern is logged at `Error` and matches nothing. |
| `fallback_origin` | `*` | Sent when the origin doesn't match, and when there's no `Origin` header. A value that doesn't start with `http`, other than `*`, gets `https://` in front. |
| `allow_credentials` | `true` when an `<authorize>` section exists on the route or at the root, otherwise `false` | `true` or `false`. Anything else counts as not set. |
| `allowed_headers` | `Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key` with credentials on, `*` with them off | Comma-separated. Replaces the default list. |
| `max_age` | `86400` | Seconds a browser may keep a preflight's answer. A value that isn't a whole number of 0 or more counts as not set. |
| `exposed_headers` | `Content-Disposition, Retry-After` | Comma-separated. The response headers a page may read besides `Cache-Control`, `Content-Language`, `Content-Length`, `Content-Type`, `Expires`, `Last-Modified` and `Pragma`, which every browser shows. Replaces the default list, so keep `Content-Disposition` on a route that serves downloads. Not sent on a preflight. |

`Access-Control-Allow-Methods` comes from the route's `<verb>` and has no setting of its own.

## Errors

The body is `{"success":false,"message":"..."}`, with each backtick in the message written as `\u0060`.

| Request | Status | Message |
|---|---|---|
| A preflight to a path that no route has | `404` | ``API Endpoint `PATH` not found`` |

Silent failures, where the engine answers as usual and logs nothing:

- An unanchored pattern lets a lookalike host through (rule 1).
- A pattern without a fallback lets every page read the answers (rule 2).
- A request header outside the allowed list makes the browser drop the request after the preflight (rule 5). The engine sees only the preflight.
- A pattern with a scheme or a port in it matches nothing, so every origin gets the fallback.
- A response header the page reads that is neither in `exposed_headers` nor shown by every browser, such as a gateway target's `X-Total-Count`, is `null` in the page.

## Do and don't

```xml
<!-- Do: anchored, host only, with your own origin as the fallback -->
<pattern><![CDATA[^(www|app)\.example\.com$]]></pattern>
<fallback_origin>https://www.example.com</fallback_origin>

<!-- Don't: unanchored, and with the scheme in it -->
<pattern><![CDATA[https://app\.example\.com]]></pattern>
```

```xml
<!-- Do: allow every origin, by leaving both tags out or with a * fallback -->
<cors>
  <fallback_origin>*</fallback_origin>
</cors>
```

Keep `localhost` out of a production pattern. The pattern can't check the scheme or the port, so `localhost` in it lets any page served from `localhost` on a visitor's own computer read the answers. Set the development pattern per machine instead, with an environment variable such as `cors__pattern=^(app\.example\.com|localhost)$` ([environment variables](02-configuration.md#environment-variables)).

## Related

- [12 Authentication](12-authentication.md): the provider hint header, and the `<authorize>` sections that turn credentials on.
- [08 API gateway](08-api-gateway.md): headers from the target.
- [02 Configuration](02-configuration.md): where `settings.xml` and the other files live.
