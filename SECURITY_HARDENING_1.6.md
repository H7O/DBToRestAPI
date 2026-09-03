# Security hardening in 1.6.0 — handoff note

**Date:** 2026-09-02
**Status:** Done. Ships in v1.6.0 together with multi-provider OIDC. This note records the *why* behind four defensive changes so the next person does not undo them.

---

## TL;DR

Four fixes, each found while running the engine behind a real front end and each confirmed by exploitation or a failing probe before it was fixed:

| Area | Before | After |
|---|---|---|
| Embedded HTTP (`{http{...}http}`) | A caller value containing `"` closed the JSON string it sat in and appended a second `url` key. `System.Text.Json` keeps the **last** duplicate, so the call went to the attacker's host carrying the block's own credential headers. | Values landing inside a JSON string are escaped before the block is parsed; values used structurally (`"body": {{doc}}`) stay raw. Headers containing CR/LF are dropped. |
| Cache keys | Invalidator values were embedded raw when short and **dropped** when long. Raw embedding let `?tenant=a\|user=victim` forge the key of `?tenant=a&user=victim`; dropping let two callers share one entry. The hash helper stack-allocated 3n+3 bytes for any input. | Every value is xxHash3-hashed. Short strings hash on a 1 KB stack buffer; long ones on a pooled buffer cleared on return. `max_per_value_cache_size` is gone. |
| Bearer tokens | A malformed bearer returned **500**. The first guard (`CanReadToken`) only checked the three-segment shape, so `abc.def.ghi` still reached `ValidateToken` and threw `ArgumentException`. | The guard parses header and payload (`TryReadJwt`). `ValidateToken` is additionally wrapped so an `ArgumentException` from *that call only* becomes a 401. |
| Claims for SQL | `{auth{roles}}` was written only when there were **no** roles (inverted condition) and never otherwise, because .NET renames inbound `roles`/`role` claims to the long `ClaimTypes.Role` URI. | `roles` is written when roles exist (pipe-delimited). New unified `{auth{auth_time}}` (`auth_time`, else `iat`) for "reject tokens issued before logout" checks. |

---

## 1. Embedded HTTP: context-aware escaping

**Files:** [EmbeddedHttpTemplate.cs](DBToRestAPI/Services/HttpExecutor/Internal/EmbeddedHttpTemplate.cs) (new), [ApiController.cs](DBToRestAPI/Controllers/ApiController.cs) (the single `.Fill()` call for embedded HTTP), [RequestMessageBuilder.cs](DBToRestAPI/Services/HttpExecutor/Internal/RequestMessageBuilder.cs) (`SetHeaders`).

**The design decision worth preserving: do not escape everything.** `"body": {{body_add}}` deliberately injects a whole JSON document, and escaping it produces the broken literal `{\"a\":1}`. So `EmbeddedHttpTemplate.MarkersInsideJsonStrings` scans the block once, tracking whether the cursor is inside a string literal (honouring `\"` escapes and skipping `//` and `/* */` comments, which `JsonRequestParser` accepts), and returns the marker names that land inside a string. The controller passes a `valueConverter` to `Fill` that JSON-escapes exactly those. A name used in both contexts is escaped (security wins) and a warning is logged, because that block needs two markers.

`JsonEscape` is deliberately minimal: `"`, `\` and control characters only. A value that was already well-formed passes through byte for byte, so no existing configuration changes behaviour.

**URL encoding needed no engine change.** The executor has had a `query` field since its first commit, and `QueryStringBuilder` percent-encodes it. The gap was documentation: tutorial chapter 16 never mentioned it, so authors put `{{caller_value}}` into the `url` string. The rule now stated in the docs is narrow: constants and `{s{settings}}` may sit in `url`; only a **caller-supplied** marker must move into `query`.

**Not done, and why.** `RequestValidator` has no host allow-list or private-range block, and it is not called on the execution path. A config author is trusted to pick hosts; this fix is that a *caller* can no longer pick them. An opt-in `<allowed_hosts>` block would be the next step.

## 2. Cache keys: hash every invalidator value

**File:** [CacheService.cs](DBToRestAPI/Cache/CacheService.cs).

Both alternatives to hashing let two different requests collide onto one entry. Dropping an over-long value left that parameter out of the key, so the second caller was served the first one's response. Embedding a value raw let a caller forge a key segment, because `|` and `=` are legal inside header and query-string values. A fixed-width hash is bounded, distinct and delimiter-free. Cost is a few hundred nanoseconds per value.

`ToXxHash3` previously did `stackalloc byte[GetMaxByteCount(n)]` for any input, which exhausts the request thread's stack somewhere in the low hundreds of thousands of characters; a `StackOverflowException` cannot be caught and takes the process down. It now uses the stack only up to 1 KB and rents a pooled buffer beyond that, returned with `clearArray: true` so a bearer token does not linger in a recycled array.

## 3. Malformed bearer → 401, not 500

**File:** [Step4JwtAuthorization.cs](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs).

Probe against `System.IdentityModel.Tokens.Jwt` 8.16.0:

| Bearer | `CanReadToken` | `TryReadJwt` |
|---|---|---|
| `abc.def.ghi` | true | null |
| `eyJhbGciOiJIUzI1NiJ9.bm90anNvbg.sig` | true | null |

The `ArgumentException` wrap is scoped to the single `ValidateToken` call on purpose. An `ArgumentException` raised elsewhere in the enclosing `try` (a malformed authority URL in *our* config, say) is a genuine server error and must keep returning 500.

## 4. Claims: `roles` fixed, `auth_time` added

**File:** [Step4JwtAuthorization.cs](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs), "Store claims in context" region.

The generic claim-copy loop never produces a key called `roles`, because the default inbound claim map renames `roles` and `role` to `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`. The explicit unification line is therefore the only thing that gives SQL authors `{auth{roles}}`, exactly as the `email` line is what gives them `{auth{email}}`. Its condition was inverted.

`{auth{auth_time}}` prefers `auth_time` (marks when the human authenticated; survives a silent token refresh) and falls back to `iat` (required in every ID token; changes on refresh).

**Behaviour change to know about:** with no roles, `{auth{roles}}` is now absent (bound as `NULL`) rather than an empty string, which matches how `email` and `name` already behave.

---

## Tests added

| File | Covers |
|---|---|
| [EmbeddedHttpTemplateTests.cs](DBToRestAPI.Tests/HttpExecutor/EmbeddedHttpTemplateTests.cs) | Classification (strings, structural, mixed, prefixed markers, escapes, comments, `https://`), `JsonEscape`, `ContainsHeaderBreak`, and the end-to-end fill the controller performs: the original SSRF payload ends up trapped as data |
| [RequestMessageBuilderTests.cs](DBToRestAPI.Tests/HttpExecutor/RequestMessageBuilderTests.cs) | CR/LF in a header name, value, or content header drops that header |
| [Step4JwtAuthorizationTests.cs](DBToRestAPI.Tests/Step4JwtAuthorizationTests.cs) | 1-, 2-, 3- and 5-segment garbage bearers all return 401 on both the provider-hint and single-provider paths |
| [CacheKeyHashingTests.cs](DBToRestAPI.Tests/CacheKeyHashingTests.cs) | Hash matches a reference on both the stack and pooled paths, survives a 2,000,000-character input, and is delimiter-free |

**Not covered offline:** the `roles`/`auth_time` claim shaping and the `ValidateToken` wrap. Both sit past the discovery fetch, which uses a direct `HttpDocumentRetriever` rather than the injected `IHttpClientFactory`, so there is no seam to serve a fake JWKS. Routing discovery through the factory would make them testable — a small refactor for a later release.

## Packages

`Com.H.Net.Ssh` 10.1.0 pulls SSH.NET 2026.0.0, which clears the high-severity advisory GHSA-q939-rpr3-3284 that 2025.1.0 carried; the audit (`NuGetAuditMode=all`) is now clean. The JWT stack is referenced directly at 8.22.0 (`System.IdentityModel.Tokens.Jwt`, `Microsoft.IdentityModel.Protocols.OpenIdConnect`) because Step4 calls it directly; the malformed-bearer tests above pass on 8.22.0, so the 8.16.0 probe result still holds.

## Docs touched

- [docs/tutorial/16-http-from-sql.md](docs/tutorial/16-http-from-sql.md), [docs/topics/17-embedded-http-calls.md](docs/topics/17-embedded-http-calls.md) — full request schema (seven fields were missing), the `query` rule, escaping and header behaviour, examples moved to `query`, `{{h{...}}}` typo → `{h{...}}`
- Retry keys corrected wherever they appeared ([docs/topics/17-embedded-http-calls.md](docs/topics/17-embedded-http-calls.md), [docs/topics/19-webhooks.md](docs/topics/19-webhooks.md), [docs/tutorial/18-webhooks.md](docs/tutorial/18-webhooks.md), [llms.md](llms.md), [llms.txt](llms.txt)): the parser reads `max_attempts`, `delay_ms`, `exponential_backoff`, `retry_status_codes`; the documented `delay_seconds`, `backoff_multiplier`, `retry_on_status_codes` were silently ignored
- The "OAuth 2.0 Client Credentials" auth example was removed from topic 17: `AuthenticationHandler` implements `basic`, `bearer` and `api_key` only
- The `api_key` examples in tutorial 16 and topic 17 used `key`/`value`/`in`; the parser reads `key` (the secret) plus `key_header` or `key_query_param`, so the documented form sent nothing. Corrected in both
- [DBToRestAPI/docs/PRD_HttpRequestExecutor.md](DBToRestAPI/docs/PRD_HttpRequestExecutor.md) — `timeout` alias, note on `skip`/`no_wait` and escaping
- [docs/topics/07-caching.md](docs/topics/07-caching.md), [docs/topics/08-api-gateway.md](docs/topics/08-api-gateway.md), [docs/tutorial/11-caching.md](docs/tutorial/11-caching.md), [API_GATEWAY_CACHE_IMPLEMENTATION.md](API_GATEWAY_CACHE_IMPLEMENTATION.md) — `max_per_value_cache_size` removed, hashing explained
- [docs/topics/04-parameters.md](docs/topics/04-parameters.md), [docs/topics/12-authentication.md](docs/topics/12-authentication.md), [llms.md](llms.md), [llms.txt](llms.txt) — `auth_time` claim; `query` and escaping pointers
