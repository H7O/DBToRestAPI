# Multi-Provider OIDC Authentication (Concept)

> **Status:** ✅ Implemented (v1) — see the status checklist at the end.
> **Goal:** Let a single protected endpoint accept tokens from **more than one** OIDC
> provider, so app developers can offer their users *"Log in with Google"*,
> *"Log in with Microsoft B2C"*, *"Log in with Auth0"*, etc., all hitting the
> same API endpoints.

---

## 1. Motivation

Today each endpoint binds to exactly **one** provider via
`<authorize><provider>name</provider></authorize>`, resolved per-request against
`auth_providers.xml` ([Step4JwtAuthorization.cs:90](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L90)).

App developers building on this solution want their *end users* to choose how they
sign in. The user picks a provider **at login time in the developer's app**, so by
the time a token reaches the API it is already a valid token issued by **one** of
the allowed providers. The engine's job is therefore not to validate against
everyone — it only needs to **figure out which allowed provider issued the token**
and then run the existing validation against that one.

**This is a *selection / routing* problem, not a new *validation* problem.** That is
what makes the feature cheap, safe, and fully backward compatible.

---

## 2. Decisions (agreed)

| # | Decision | Choice |
|---|----------|--------|
| 1 | How to select the provider | **Client hint header first, token `iss` claim as fallback, reject if neither resolves.** Hint is consulted only when an endpoint lists more than one provider. |
| 2 | How the engine learns each provider's issuer | **Config-derived issuer** — route on `<issuer> ?? <authority>`, the *same* value validation trusts as `ValidIssuer`, so routing and validation can never disagree. (Discovery-document derivation — the other half of "support both" — is deferred; it would need a paired change to validation's `ValidIssuer` semantics. See §6.) |
| 3 | Expose the resolved provider to SQL | **Yes** — inject it as a claim, accessible via `{auth{auth_provider}}`. |
| 4 | List semantics | **Explicit comma-separated list, plus a `*` wildcard** meaning "any provider configured in `auth_providers.xml`". |
| 5 | Hint header name | Built-in default **`X-Auth-Provider`**, overridable per-endpoint (and optionally globally). |

---

## 3. Configuration surface

### 3.1 Endpoint (`sql.xml`)

The existing `<provider>` tag is widened to accept a **comma-separated list** or `*`.
A single value behaves **exactly** as today.

```xml
<get_my_orders>
  <authorize>
    <enabled>true</enabled>

    <!-- One provider (today's behavior, unchanged) -->
    <!-- <provider>azure_b2c</provider> -->

    <!-- Several allowed providers -->
    <provider>google,azure_b2c,auth0</provider>

    <!-- ...or accept any provider defined in auth_providers.xml -->
    <!-- <provider>*</provider> -->

    <!-- Optional: override the hint header for THIS endpoint
         (default: X-Auth-Provider) -->
    <provider_hint_header>X-Auth-Provider</provider_hint_header>

    <!-- Endpoint-global, applied AFTER whichever provider validates -->
    <required_scopes></required_scopes>
    <required_roles></required_roles>
  </authorize>

  <query><![CDATA[ ... ]]></query>
</get_my_orders>
```

> **Override rule:** the route-level overrides of `authority` / `audience` / `issuer`
> ([Step4JwtAuthorization.cs:120-134](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L120))
> only make sense for a single provider. When `<provider>` lists more than one (or is
> `*`), those route-level overrides are **ignored** and each candidate provider uses
> its own config block. `required_scopes` / `required_roles` remain endpoint-global.

### 3.2 Provider definition (`auth_providers.xml`)

Unchanged, except `<issuer>` becomes the **recommended** key for any provider that
participates in a multi-provider endpoint (it lets the `iss` fallback resolve without
a network call). If omitted, the issuer is derived from the discovery document.

```xml
<google>
  <authority>https://accounts.google.com</authority>
  <audience>your-google-client-id.apps.googleusercontent.com</audience>
  <issuer>https://accounts.google.com</issuer>   <!-- recommended -->
  ...
</google>
```

### 3.3 Optional global hint-header default (`settings.xml`)

```xml
<authorize>
  <provider_hint_header>X-Auth-Provider</provider_hint_header>
</authorize>
```

**Hint header resolution cascade:** endpoint override → global setting → built-in default `X-Auth-Provider`.

---

## 4. Resolution algorithm

Runs inside [Step4JwtAuthorization](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs)
**only after** the existing "is authorization enabled?" checks
([:68-86](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L68)) and Bearer-token
extraction ([:200-257](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L200)).

```
allowed = parse <provider>            # ["google","azure_b2c"]  OR  expand "*" to all configured

# Fast path — preserves today's behavior exactly
if allowed has exactly 1 provider:
    selected = that provider
    # (no hint, no iss pre-parse, route-level overrides still apply)
else:
    # 1) HINT (only for multi-provider endpoints)
    hint = request.Headers[hintHeaderName]          # default "X-Auth-Provider"
    if hint present:
        if hint ∈ allowed:   selected = hint
        else:                401   # present-but-not-allowed → fail fast (residual decision §9)

    # 2) ISS FALLBACK
    if selected is null:
        iss = ReadJwtToken(token).Issuer            # UNVALIDATED, cheap (base64 + JSON)
        candidates = providers in `allowed` whose issuer == iss   # via the index (§6)
        if candidates has 1:   selected = that candidate
        if candidates has >1:  disambiguate by matching the token's unvalidated `aud`  # shared-issuer case
        if candidates is 0:    401

    # 3) NEITHER
    if selected is null:  401   # "unable to determine identity provider"

# Existing validation, unchanged, against the single `selected` provider:
#   signature vs selected.JWKS  +  iss == selected.issuer  +  audience  +  lifetime
principal = ValidateToken(token, paramsFor(selected))      # Step4:281-294
claims["auth_provider"] = selected.name                     # §5
... existing UserInfo fallback, scopes/roles checks ...
```

---

## 5. Security model (why this is safe)

The hint and the unvalidated `iss` are used **only to choose which provider's
validation parameters to apply**. They are *never* trusted as proof of identity. The
trust decision remains 100% the existing cryptographic validation:

- **Signature** must verify against the **selected provider's JWKS**.
- **`iss` claim** must equal the **selected provider's configured issuer**
  (`validate_issuer` stays on).
- **Audience** and **lifetime** are checked as today.

Consequences:

- A **forged or wrong hint** → the token gets routed to a provider whose keys won't
  verify it → **rejected**. It can *never* cause a foreign token to be accepted. The
  worst case is a *valid* token being rejected because the client sent the wrong hint
  (a client bug, not a vulnerability).
- A **forged `iss`** → same outcome: routed to a provider whose private key the
  attacker doesn't have → signature fails → rejected.

> **Why route to one provider instead of unioning all issuers/audiences/keys into one
> `TokenValidationParameters`?** Because unioning *decouples* the
> (issuer ↔ audience ↔ signing-keys) triple — a token could then validate with
> Provider A's keys but Provider B's audience. Selecting a single provider keeps that
> triple intact, which is strictly safer.

> **`*` wildcard trade-off (accepted):** `<provider>*</provider>` trusts *every*
> provider in `auth_providers.xml`. Adding a new provider there will **silently** make
> it acceptable on every wildcard endpoint. This is documented as the explicit
> trade-off of choosing the wildcard over an enumerated list.

---

## 6. The issuer → provider index

A small, in-memory singleton (working name `IOidcProviderIndex`) — **not** HybridCache,
since it's a tiny computed map.

- **Hint map:** just the set of configured provider names (validate a hint in O(1)).
- **Issuer map:** `expected issuer → provider name(s)`, where *expected issuer* =
  `<issuer> ?? <authority>` — exactly the value Step4 uses for `ValidIssuer`. Matched
  **ordinally/exactly**, so routing can never pick a provider whose validation then
  rejects the token. *(Discovery-document derivation for providers whose real `iss`
  differs from both `<issuer>` and `<authority>` is a documented future enhancement —
  it needs a paired change to validation's `ValidIssuer`.)*
- **Shared issuer:** two providers can map to the same expected issuer (e.g. two app
  registrations in one tenant), so the value is a small list; the token's unvalidated
  `aud` selects the one whose configured `<audience>` matches. Validation re-checks the
  audience, so a wrong guess can only cause a rejection.

**Hot-reload:** rebuild the index when `auth_providers.xml` changes, using the **same
pattern the existing resolvers already use** —
`ChangeToken.OnChange(() => _configuration.GetSection("authorize:providers").GetReloadToken(), Rebuild)`
(cf. [RouteConfigResolver.cs:37](DBToRestAPI/Services/RouteConfigResolver.cs#L37),
[QueryRouteResolver.cs:49](DBToRestAPI/Services/QueryRouteResolver.cs#L49)).

---

## 7. Performance & caching

The feature adds **effectively nothing** for the common case and almost nothing for
multi-provider endpoints:

- **Single-provider endpoints:** unchanged. No hint read, no `iss` pre-parse — the
  fast path selects directly.
- **Multi-provider endpoints:** one header lookup, and (only if the hint is absent) one
  `ReadJwtToken` — a base64 decode + small JSON parse, **no crypto**, microseconds.
  Plus an O(1) dictionary lookup.
- **Discovery docs** are cached per-authority for 24h
  ([Step4JwtAuthorization.cs:588](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L588)).
  Each candidate provider simply gets its own cache entry — no collisions, no extra
  network calls after warm-up.
- **UserInfo** is cached per **token hash**
  ([Step4JwtAuthorization.cs:644-648](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L644)).
  A given token deterministically belongs to one provider, so the token-hash key is
  **already collision-free** across providers — no change needed.

---

## 8. Claims into SQL: `{auth{auth_provider}}`

After successful validation, inject the resolved provider's **name** (the XML key,
e.g. `google`, `azure_b2c`) into the claims dictionary where `user_claims` is built
([Step4JwtAuthorization.cs:475-500](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs#L475)),
so it flows through [ParametersBuilder](DBToRestAPI/Services/ParametersBuilder.cs)
and the existing `{auth{...}}` substitution
([DefaultRegex.cs:17](DBToRestAPI/Settings/DefaultRegex.cs#L17)).

```sql
-- Branch logic on the issuing provider
SELECT * FROM orders
WHERE customer_email = {auth{email}}
  AND login_source   = {auth{auth_provider}};   -- 'google' | 'azure_b2c' | ...
```

This directly serves the login-with-X use case (partition users by IdP, provider-specific
rules, audit which login was used). *(Optional future addition: `{auth{auth_issuer}}`.)*

---

## 9. Edge cases & residual decisions

| Case | Behavior |
|------|----------|
| `<provider>` single value | Today's behavior, byte-for-byte. Fast path. |
| `*` but only one provider configured | Resolves to a single candidate → fast path, no hint/iss needed. |
| Hint present, names an allowed provider | Select it. |
| **Hint present, names a *disallowed* provider** | **401, fail fast** (implemented; to prefer leniency, remove the single guarded block in `ResolveProviderForTokenAsync` so it falls through to `iss`). |
| Hint absent, `iss` resolves to 1 allowed provider | Select it. |
| Hint absent, `iss` matches 2+ allowed providers (shared issuer) | Try each; first to fully validate wins. |
| Hint absent, no readable `iss` (opaque token) | 401 — see §10. |
| Neither hint nor `iss` usable | 401 "unable to determine identity provider" (generic to client; detailed in logs). |
| Token fails validation against the selected provider | Existing 401 paths (expired / bad signature / invalid). For the shared-issuer try-each case, return a generic "Invalid token". |

---

## 10. Scope / non-goals (v1)

- **JWT / ID tokens only.** Routing relies on a readable `iss`. **Opaque** access
  tokens (some Google/Facebook *access* tokens) carry no readable `iss` — and the
  current `ValidateToken` path doesn't really handle opaque tokens anyway. In a
  login-with-X flow the developer's app should send the **ID token** (a JWT), which is
  the correct token for API authentication regardless. Opaque-token support is a
  separate, later effort.
- **Per-provider `required_roles` / `required_scopes`** are out of scope — these stay
  endpoint-global. (Claim shapes differ per provider; document that SQL should prefer
  the normalized claims or branch on `{auth{auth_provider}}`.)

---

## 11. Implementation sketch (for later)

| Area | File | Change |
|------|------|--------|
| Provider index | *new* `Services/OidcProviderIndex.cs` | Singleton; hint-set + issuer-map; lazy discovery-derived issuers; `ChangeToken.OnChange` rebuild. |
| Selection | [Step4JwtAuthorization.cs](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs) | Parse `<provider>` as list / `*`; fast path for one; hint→`iss` resolution; inject `auth_provider`. |
| Hint header config | [Step4JwtAuthorization.cs](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs) + `settings.xml` | Read endpoint `provider_hint_header` → global → default `X-Auth-Provider`. |
| Sample config | [auth_providers.xml](DBToRestAPI/config/auth_providers.xml), [sql.xml](DBToRestAPI/config/sql.xml) | Add `<issuer>` to samples; add a multi-provider endpoint example. |
| Tests | `DBToRestAPI.Tests` | Selection by hint; by `iss`; shared-issuer try-each; disallowed hint → 401; wildcard; single-provider unchanged; forged hint/iss rejected. |

### Docs to propagate (house workflow)
[docs/topics/12-authentication.md](docs/topics/12-authentication.md) (reference),
[docs/tutorial/09-jwt-auth.md](docs/tutorial/09-jwt-auth.md) (tutorial),
[docs/tutorial/10-claims-in-queries.md](docs/tutorial/10-claims-in-queries.md)
(`{auth{auth_provider}}`),
[docs/topics/02-configuration.md](docs/topics/02-configuration.md) (the `<authorize>`
tag row), plus [README.md](README.md), [llms.md](llms.md), [llms.txt](llms.txt).

---

## 12. Backward compatibility

- A single `<provider>name</provider>` is a one-element list → **fast path → identical
  behavior**, including route-level `authority`/`audience`/`issuer` overrides.
- The hint read and `iss` pre-parse only run when an endpoint lists **2+** providers or
  `*`.
- No change to provider config files is *required* (`<issuer>` is only recommended).
- **No breaking changes; existing deployments behave exactly as before.**

---

## 13. Status checklist

- [x] **Provider index** — [`Services/OidcProviderIndex.cs`](DBToRestAPI/Services/OidcProviderIndex.cs):
  immutable snapshot + `AtomicGate` + `ChangeToken.OnChange` on `authorize:providers`;
  `*` expansion, issuer→name(s) map, audience disambiguation. Pure logic in
  `OidcProviderResolution` for testability.
- [x] **Selection wired in** — [`Middlewares/Step4JwtAuthorization.cs`](DBToRestAPI/Middlewares/Step4JwtAuthorization.cs):
  token extracted before resolution; `ResolveProviderForTokenAsync` (hint → `iss` → `aud`);
  route-level overrides gated to single-provider mode; `auth_provider` claim injected.
- [x] **DI registration** — [`Program.cs`](DBToRestAPI/Program.cs) singleton.
- [x] **Unit tests** — [`DBToRestAPI.Tests/OidcProviderIndexTests.cs`](DBToRestAPI.Tests/OidcProviderIndexTests.cs)
  (17 tests). Full suite green (345 passed).
- [x] **Build** — solution builds with 0 errors (pre-existing warnings only).
- [ ] **Security review** — run `/security-review`; act on hardening findings.
- [ ] **Docs propagation** — `docs/topics/12-authentication.md`, a tutorial chapter,
  `docs/topics/02-configuration.md` tag row, README, `llms.md` + `llms.txt`, and a
  config sample (multi-provider endpoint + `provider_hint_header`).

### Deferred (post-v1)
- Discovery-document issuer derivation (paired with a `ValidIssuer` change).
- Per-provider `required_roles` / `required_scopes`.
- Opaque (non-JWT) token routing.
