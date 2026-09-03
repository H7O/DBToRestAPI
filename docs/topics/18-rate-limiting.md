# Rate Limiting

Cap how many requests a caller may make to an endpoint per window, answered with `429 Too Many
Requests` and a `Retry-After` header, evaluated in the pipeline **before any database work**.

## Overview

Without a limit, a client can call any endpoint as fast as it can push requests: a burst tool
fires 200 requests at a user-creation endpoint and every one is processed. The only alternative
the engine offered was the SQL author writing `THROW 50429` after counting rows themselves. That
still has its place (a limit on *what a request does*, such as how many e-mails it may queue), but
it runs after JWT validation, the database connection, the parameter build and the query itself,
so it caps side effects without reducing load, and it cannot set `Retry-After`.

`<rate_limit>` is a per-endpoint request limit with a global default in `settings.xml`, configured
the same way every other per-endpoint-with-global-fallback setting in the engine is configured.
No block anywhere means the feature is off.

What it is **not**: a volumetric DoS defence. It runs after authentication, so a flood of requests
with bad tokens is rejected by the JWT step before it and never counted. Protecting the
token-validation path from anonymous floods is the job of whatever sits in front of the engine
(IIS dynamic IP restrictions, Azure Front Door, nginx, Cloudflare). See
[What it deliberately does not do](#what-it-deliberately-does-not-do).

## Enabling it

### Per endpoint

Inside any endpoint node in `sql.xml`, or any route node in `api_gateway.xml`:

```xml
<create_user>
  <route>users</route>
  <verb>POST</verb>
  <authorize><provider>my_provider</provider></authorize>

  <!-- At most 30 requests per 60 seconds, counted per caller -->
  <rate_limit>
    <max_requests>30</max_requests>
    <window_seconds>60</window_seconds>
  </rate_limit>

  <query><![CDATA[ ... ]]></query>
</create_user>
```

| Tag | Meaning | Default if omitted |
|---|---|---|
| `max_requests` | Permits per window. Whole number ≥ 1. **The one tag that must resolve for a limit to exist** | global `max_requests` |
| `window_seconds` | Window length. Whole number 1 – 86400 | global `window_seconds`, else **60** |
| `per` | Who shares an allowance: `caller` (each user / key / IP has its own), `ip` (always the client IP, even when authenticated), `endpoint` (one allowance for everyone) | global `per`, else `caller` |
| `enabled` | `false` switches limiting off for this endpoint even when a global limit exists; `true` switches it back on when the global is off | global `enabled`, else `true` |
| `message` | The 429 message. `{{retry_after_seconds}}` is substituted | global `message`, else built-in |

### Global default

Directly under `<settings>` in `settings.xml`:

```xml
<settings>
  <!-- Default for every endpoint that does not define its own value.
       Endpoints override single tags; <enabled>false</enabled> on an endpoint opts it out;
       <enabled>false</enabled> HERE switches the feature off engine-wide. -->
  <rate_limit>
    <max_requests>120</max_requests>
    <window_seconds>60</window_seconds>
    <per>caller</per>

    <!-- Only when the engine runs behind a reverse proxy that fills this header with the
         real client address, AND nothing but that proxy can reach the engine.
         Azure Front Door: X-Azure-ClientIP. Never set it when clients can reach the engine
         directly, because then the header is client-controlled. See "The client IP" below. -->
    <!-- <client_ip_header>X-Azure-ClientIP</client_ip_header> -->
    <!-- <client_ip_header_trusted_hops>1</client_ip_header_trusted_hops> -->
  </rate_limit>
  ...
</settings>
```

`client_ip_header` and `client_ip_header_trusted_hops` are global only. They describe the
deployment, not an endpoint.

Like every other XML setting, the global block can be overridden by environment variables:
`rate_limit__max_requests=60`. See [Environment Variables](02-configuration.md#environment-variables).

## How settings resolve

Each tag resolves independently: **endpoint → global → default**. A missing `<rate_limit>` block
everywhere means the feature is off; this is the engine-wide rule ("a missing block = feature off")
and exactly how `permitted_file_extensions`, `max_number_of_files` and `success_status_code`
already resolve.

| Global block | Endpoint block | Result |
|---|---|---|
| absent | absent | no limit |
| `120 / 60` | absent | 120 per 60 s per caller |
| `120 / 60` | `<max_requests>30</max_requests>` only | 30 per 60 s (window inherited) |
| `120 / 60` | `<enabled>false</enabled>` | no limit on this endpoint |
| absent | `<max_requests>30</max_requests>` | 30 per **60** s (built-in window) |
| absent | `<window_seconds>10</window_seconds>` only | no limit, **and a warning** (block present, nothing resolves) |
| `<enabled>false</enabled>` + `120 / 60` | `30 / 60` | **no limit** — the global switch is the kill switch |
| `<enabled>false</enabled>` + `120 / 60` | `30 / 60` + `<enabled>true</enabled>` | 30 per 60 s (the endpoint re-enabled itself) |

## Values that do not parse

A throw on this path would turn a typo in `settings.xml` into a 500 on every request, so every tag
is read as text and parsed by the engine itself, never through the configuration binder:

- `max_requests` / `window_seconds`: whole numbers only. `abc`, `2.5`, `1e3`, `0`, `-5`, out of
  range, empty or whitespace → **not set at that level**, fall through to the next level.
- `enabled`: `true/false`, `1/0`, `yes/no`, `on/off`, any case. Anything else → not set.
- `per`: `caller`, `ip`, `endpoint`, any case, trimmed. Anything else → not set.
- `client_ip_header_trusted_hops`: whole number 1 – 64. Anything else → 1, with a warning. Only
  read when `client_ip_header` is set.
- Two `<rate_limit>` siblings in one node (the XML provider indexes them as `rate_limit:0` and
  `rate_limit:1`, so every tag would read as "not set"): the first is used, with a warning.

Each problem is logged **once** per distinct value, as a warning naming the configuration path,
and the engine keeps serving. All tags are read before anything is decided, so one save reports
every typo. A block that is present but resolves to no `max_requests` is also logged once, so an
endpoint cannot look protected while being open. A limiter configuration mistake never takes the
API down, and it is never silent either.

## Where it runs, and what is counted

The check sits between JWT validation and the API gateway step:

```
route → CORS → API keys → JWT → [rate limit] → API gateway → parameters → files → controller
```

After JWT validation so the caller can be the authenticated user. Before the gateway step because
gateway routes are answered there and never reach anything placed later.

**Counted:** every request that reaches the step, for both database-query and API-gateway routes,
**including requests the engine later answers from its response cache** (the cache is consulted in
the controller, after this step).

**Not counted:** `OPTIONS` preflight (the CORS step answers it), anything the API-key or JWT step
rejected (401/403), `/openapi.json`, `/swagger` and the static-file fallback (all short-circuit in
route resolution and touch no database).

## Who the caller is

With `<per>caller</per>` (the default) the caller is resolved in this order, first match wins:

1. **Authenticated user** — from the validated token's claims: `user_id` (the subject), else `oid`
   (short form or the long `…/identity/claims/objectidentifier` URI that .NET's claim mapping can
   produce), else `email`, which is hashed before use so an address never reaches a log line.
   Prefixed with the token's issuer (`iss`) when present, else the provider name, so two providers
   that issue the same subject value are not one caller and a client's provider hint on a
   [multi-provider endpoint](12-authentication.md#multiple-providers-per-endpoint) cannot multiply
   its allowance. The literal `Not supported`, which some identity providers emit when the
   subject claim is switched off, is treated as absent.
   **An authenticated endpoint never falls through to the IP.** Behind a proxy that would merge
   every user into one bucket. A token with no usable identity goes into one shared bucket and an
   Error is logged once per endpoint, so the degradation is visible.
2. **API key** — when the endpoint declares `api_keys_collections`, a short hash of the key the
   API-key step actually validated. The key itself is never stored, logged or re-read from the
   header (a request can carry more than one `x-api-key` value). On an endpoint without
   `api_keys_collections` an `x-api-key` header is client-controlled noise and is ignored. If a
   key-gated endpoint reaches this step with no recorded hash, every such request shares one
   allowance and an Error is logged once per endpoint, the same rule as the user case above.
3. **Client IP** — see the next section.

**An API key is a system, not a person.** Every request behind one key shares one allowance.
This matters for endpoints the engine calls *on itself*: if endpoint A makes an
[embedded HTTP call](17-embedded-http-calls.md) to endpoint B with a shared key, then every one of
A's users shares B's single allowance, even though A is limited per user. Such internal hops must
carry `<rate_limit><enabled>false</enabled></rate_limit>` (A already limits their only callers),
or a `per=endpoint` limit sized for the sum. The rule of thumb: internal hops opt out, the public
entry point carries the per-caller limit.

With `<per>endpoint</per>` the caller is ignored and all requests share one allowance; use it for
endpoints that call an external system with its own quota. With `<per>ip</per>` the client IP is
used even for authenticated callers.

## The client IP

By default the connection's remote address, with IPv4-mapped IPv6 (`::ffff:1.2.3.4`, which Kestrel
reports on a dual-stack socket) normalised to `1.2.3.4` so Kestrel and IIS agree on who a caller
is. A missing address (some test hosts) is the constant `unknown`.

Behind a reverse proxy the remote address is the proxy, so every caller shares one identity. In
that deployment set `client_ip_header` to a header the proxy fills with the client address. Two
rules:

- **Entries are read from the right, never the left.** Proxies that *append* (nginx, IIS ARR,
  Front Door, App Service) leave whatever the client sent as the left-most entry of
  `X-Forwarded-For`; using it hands every request a fresh identity and lets an attacker target a
  victim's bucket. `client_ip_header_trusted_hops` (1 – 64, default 1) selects the entry that many
  positions from the right: with one appending proxy the right-most entry is the client; with
  Front Door in front of App Service (two appenders) use 2. A header the proxy *overwrites*
  (`X-Azure-ClientIP`, `CF-Connecting-IP`, `True-Client-IP`) has one entry and needs no thought.
  The chosen entry must parse as an IP address (`1.2.3.4`, `1.2.3.4:5000`, `[::1]:5000` all do).
  Otherwise, and also when the configured header is absent from a request or has fewer entries
  than the hop count, the engine falls back to the connection address and logs an Error once per
  endpoint: each of those means the proxy is not doing what the configuration says, or the engine
  is reachable without it.
- **Enable it only when nothing but that proxy can reach the engine.** An App Service origin is
  reachable on its own hostname by default; lock it to the Front Door service tag (and check
  `X-Azure-FDID`) first, or the header is client-controlled again. The engine logs a warning once,
  on the first request to any rate-limited endpoint after `client_ip_header` is set (and again if
  the header name or hop count changes), so an operator reading the log knows the trust is in
  place.

## The window

A sliding window (`System.Threading.RateLimiting.SlidingWindowRateLimiter`) that rejects
immediately and never queues. The window is divided into `min(window_seconds, 10)` segments, so a
60-second window slides every 6 s and a 10-second window every second. Sliding rather than fixed
because a fixed window lets a client send twice the limit across a boundary, which is exactly what
a burst tool does.

One limiter for the whole engine, partitioned on `(endpoint, caller, max_requests, window_seconds)`:

- **endpoint** is the configuration node (`queries:create_user`), not the request URL, so
  `users/1` and `users/2` share the `get_user` allowance, and `GET users` and `POST users` are
  separate because they are separate nodes. Route-parameter, `json/` prefix, `$2F` and case
  variants all resolve to the same node in route resolution, so none of them mints a new
  allowance.
- **the limits are part of the key**, so a hot-reloaded change takes effect on the next request
  with no rebuild. The runtime evicts a cached limiter once it has been fully replenished and idle
  for 10 s, so old partitions age out on their own.

## Hot reload

A changed limit starts fresh partitions immediately: every caller on that endpoint gets the new
allowance from zero, and the old counts are abandoned. Editing the global block does this for
every endpoint at once. So **tighten before an attack, not during one**; during one, a tighter
limit hands the attacker one more (smaller) allowance.

## The 429 response

```
HTTP/1.1 429 Too Many Requests
Content-Type: application/json
Retry-After: 60
Access-Control-Expose-Headers: Retry-After

{"success":false,"message":"Too many requests. Please wait 60 seconds and try again.","retry_after_seconds":60}
```

`Retry-After` is the **whole window**, meaning "at least this long". The runtime's sliding-window
limiter reports no retry interval on a failed lease, and reporting a segment instead would make a
retry-aware client that burst the whole allowance retry into nine more rejections. The body repeats
the value because `Retry-After` is not a CORS-safelisted header; the response also exposes it so a
browser client that wants the header can read it.

The body has the same `{ success, message }` shape as every other pipeline rejection (401, 403),
so clients that display `message` show a sensible sentence. `<message>` overrides it, per endpoint
or globally, with `{{retry_after_seconds}}` substituted.

When [OpenAPI](20-openapi.md) is enabled, every limited operation carries this `429` response
(with the `Retry-After` header and the body schema) and the effective limit in its description,
resolved the same way the middleware resolves it.

## Logging

A rejection path is attacker-paced, so it must not write a log line per request: under IIS stdout
logging that becomes a blocking file write, and a burst turns the cheapest path in the engine into
the slowest. Each rejection is a **Debug** line. Per endpoint, one **Information** summary with the
count since the last summary goes out at most once a minute; the count is read and reset
atomically, so every rejection lands in exactly one summary, and the tail of a burst is reported
with the first rejection after the interval. The summary names the configuration node and the
caller only by kind (`user`, `key`, `ip`). Configuration problems are logged once per distinct
message, and a fault inside the decision once per endpoint and exception type. Header values, URL
paths and identities never appear in Information-level lines.

## Cost and limits of the design

One dictionary lookup plus one lease attempt per limited request; unlimited endpoints pay two
configuration lookups. Memory is one small limiter per live (endpoint, caller) pair, freed 10 s
after the pair goes quiet and fully replenishes.

- **Per process.** Two instances behind a load balancer each allow the full limit. Scale-out is a
  deployment precondition to record, not something the engine detects.
- **Any fault fails open.** An exception anywhere in the decision (never in the rest of the
  pipeline) lets the request through and logs an Error once.

## Choosing numbers

Measure the busiest legitimate endpoint over the **window length**, not over a few seconds: a
user hammering F5 produces a burst per reload, and it is the per-minute total that must fit. Set
the global to about twice the busiest endpoint's legitimate rate, then give the endpoints that
matter their own value:

- Endpoints with an expensive or amplifying side effect (sending mail, calling a metered
  third-party system, writing regulatory records) get a **tighter** per-caller limit.
- Endpoints the frontend hits several times per page (a `me` / current-user endpoint, for
  instance) get a **looser** one. A 429 there renders a hollow app.
- Health probes, and endpoints the engine calls on itself with a shared key, are **opted out**.
  Load balancers probe from a handful of addresses, and a 429 on the probe marks the origin
  unhealthy.
- Key-gated endpoints driven by a trusted batch worker are opted out or sized for the largest
  batch, and the worker treats 429 + `Retry-After` as "sleep, then continue".

Watch the once-a-minute rejection summaries for a week before tightening anything.

A worked example:

```xml
<!-- settings.xml: 2× the busiest legitimate list-page rate -->
<rate_limit>
  <max_requests>120</max_requests>
  <window_seconds>60</window_seconds>
</rate_limit>

<!-- sql.xml -->
<get_current_user>           <!-- called several times per page load -->
  <rate_limit><max_requests>300</max_requests></rate_limit>
  ...
</get_current_user>

<create_user>                <!-- queues an e-mail to every admin -->
  <rate_limit><max_requests>30</max_requests></rate_limit>
  ...
</create_user>

<health_check>               <!-- probed by the load balancer -->
  <rate_limit><enabled>false</enabled></rate_limit>
  ...
</health_check>

<internal_lookup>            <!-- called by the engine on itself with one shared key -->
  <api_keys_collections>internal</api_keys_collections>
  <rate_limit><enabled>false</enabled></rate_limit>
  ...
</internal_lookup>
```

## What it deliberately does not do

- **Pre-authentication, per-IP limiting.** It would protect JWT validation from anonymous floods,
  but behind a proxy every request shares one IP unless the proxy's header is trusted, and
  trusting it wrongly is worse than nothing. Hosting-layer controls do this properly.
- **Distributed counters.** Limits are per process; see [Cost and limits](#cost-and-limits-of-the-design).
- **Queueing.** Holding requests to smooth a burst hides the problem from the client and ties up
  a request slot.
- **Per-header allowances** (per tenant, per API client id). The value set `caller | ip | endpoint`
  leaves room for it.

## Related Topics

- [Authentication](12-authentication.md) - Where the user identity comes from
- [API Keys](06-api-keys.md) - Key-gated endpoints share one allowance per key
- [API Gateway](08-api-gateway.md) - Gateway routes accept the same `<rate_limit>` block; caching against a third party's limit is a different problem
- [Embedded HTTP Calls](17-embedded-http-calls.md) - Opt out internal hops the engine calls on itself
- [Configuration](02-configuration.md) - Endpoint tags and environment-variable overrides
- [OpenAPI](20-openapi.md) - Limited operations are documented with a `429` response
- [Production & Deployment](../tutorial/21-production.md) - Rate limiting in the pre-launch checklist
