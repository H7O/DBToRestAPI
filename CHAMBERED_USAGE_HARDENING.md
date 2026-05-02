# Chambered usage hardening — handoff note

**Date:** 2026-04-21
**Status:** Not urgent. No silent bugs in current behavior. Do this *after* Com.H ships the `ChamberedEnumerable.Dispose` / `ChamberedAsyncEnumerable.DisposeAsync` fix — tracked in [Com.H/CHAMBERED-DISPOSAL-HANDOFF.md](../Com.H/CHAMBERED-DISPOSAL-HANDOFF.md).

---

## TL;DR

DBToRestAPI uses `ToChamberedEnumerableAsync` in four places inside [DBToRestAPI/Controllers/ApiController.cs](DBToRestAPI/Controllers/ApiController.cs). All four work correctly today — we audited each one in a session following the Com.H.Data.Common v10.1.0.7 release (auto-close-on-iterator-exit). This note captures a small opportunistic cleanup for Site 1 that becomes available once Com.H tightens its chambered-disposal logic.

---

## Why this note exists

Com.H has a latent nuance where `ChamberedEnumerable<T>.Dispose()` / `ChamberedAsyncEnumerable<T>.DisposeAsync()` is a **no-op** in the "more rows than chamberSize" branch, because the underlying `_enumerable` is a `Concat(...)` that isn't `IDisposable` / `IAsyncDisposable`. See the Com.H handoff note for the full fix shape.

DBToRestAPI already works around that gap in every call site — either by fully draining the chambered result (`.ToList()` / `.ToArray()` / `.FirstOrDefault()` + explicit `CloseReaderAsync()`) or by registering the **outer** wrapper (`resultWithNoCount` / `result`) for request-end disposal via `HttpContext.Response.RegisterForDisposeAsync(...)`.

Once Com.H ships the fix, we can simplify Site 1 and reduce the risk surface for future edits.

---

## Audit results (at the time of writing)

### Site 1 — Intermediate query in chain ([ApiController.cs:773](DBToRestAPI/Controllers/ApiController.cs#L773))

```csharp
var chamberedResult = await result.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);
// ... branch on WasExhausted(2), fully materialize in both branches ...
await result.CloseReaderAsync();   // explicit close on the outer wrapper
```

**Safe today**: both branches fully drain the chambered enumerable (`FirstOrDefault` for exhausted, `ToList` for multi-row), so the iterator's `finally` closes the reader before `CloseReaderAsync()` runs. The explicit close is a belt-and-suspenders no-op.

**Narrow future risk**: there's no `RegisterForDisposeAsync(result)` safety net on intermediate queries (only the last-in-chain case registers the connection at [line 747](DBToRestAPI/Controllers/ApiController.cs#L747)). If a future edit adds an early `return` between the chamber call and the close, the reader would leak silently on that intermediate query. The connection gets disposed in `finally` at [line 837](DBToRestAPI/Controllers/ApiController.cs#L837), which incidentally cleans up the reader on most providers, but leaning on provider behavior rather than our own contract is fragile.

### Site 2 — Auto response structure ([ApiController.cs:950](DBToRestAPI/Controllers/ApiController.cs#L950))

```csharp
HttpContext.Response.RegisterForDisposeAsync(resultWithNoCount);  // outer safety net
var chamberedResult = await resultWithNoCount.ToChamberedEnumerableAsync(2, ...);
```

**Safe today**: exhausted branch calls `CloseReaderAsync()` explicitly; non-exhausted branch either drains (`.ToArray()`) or streams to ASP.NET with the outer wrapper registered for request-end disposal.

### Sites 3 & 4 — Count-query branch ([ApiController.cs:1045, 1050](DBToRestAPI/Controllers/ApiController.cs#L1045))

```csharp
HttpContext.Response.RegisterForDisposeAsync(result);  // outer safety net
return StatusCode(..., new { ..., data = await result.ToChamberedEnumerableAsync() });
```

**Safe today**: streaming with the outer wrapper registered for request-end disposal.

---

## Proposed follow-up (post Com.H fix)

### 1. Simplify Site 1 with `await using`

Once Com.H's `ChamberedAsyncEnumerable.DisposeAsync()` cascades to the underlying enumerator / source wrapper, Site 1 becomes:

```csharp
await using var chamberedResult = await result.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);

// Get the NEXT query's JsonVariableName for the dictionary key
var nextQuery = queries[query.Index + 1];

if (chamberedResult.WasExhausted(2))
{
    var singleRow = chamberedResult.AsEnumerable().FirstOrDefault();
    qParams.Add(new DbQueryParams { DataModel = singleRow, QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern });

    var jsonArray = singleRow != null
        ? JsonSerializer.Serialize(new[] { singleRow })
        : "[]";
    qParams.Add(new DbQueryParams {
        DataModel = new Dictionary<string, object> { [nextQuery.JsonVariableName] = jsonArray },
        QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
    });
}
else
{
    var allRows = chamberedResult.AsEnumerable().ToList();
    var jsonArray = JsonSerializer.Serialize(allRows);
    qParams.Add(new DbQueryParams {
        DataModel = new Dictionary<string, object> { [nextQuery.JsonVariableName] = jsonArray },
        QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
    });
}

// No explicit CloseReaderAsync — the `await using` handles it at scope exit.
```

Changes:
- Add `await using` on the chambered result.
- Remove the explicit `await result.CloseReaderAsync();` (becomes redundant).
- Closes the narrow future-risk window described above — any early return from within the branch auto-cleans up.

### 2. Leave Sites 2–4 alone (optional)

Sites 2, 3, 4 work fine today and will continue to work after the Com.H fix. There's a stylistic argument for switching from `RegisterForDisposeAsync(outerWrapper)` to `RegisterForDisposeAsync(chamberedResult)` — it makes the "thing I'm returning is the thing I'm cleaning up" pattern more locally reasonable. But it's equivalent in behavior. Recommend leaving these as-is unless we're touching them for another reason.

### 3. Tests

Add one integration test per refactored site that simulates:
- An early exception mid-branch (to exercise the `await using` unwind path).
- A client disconnect mid-stream (to confirm reader cleanup still fires).

Use the existing `DBToRestAPI.Tests` infrastructure (whichever HTTP test fixture is already in place) against SQLite's `demo.db`.

---

## Sequencing

1. **First:** Com.H session — ship the `ChamberedEnumerable.Dispose` / `ChamberedAsyncEnumerable.DisposeAsync` fix per [Com.H/CHAMBERED-DISPOSAL-HANDOFF.md](../Com.H/CHAMBERED-DISPOSAL-HANDOFF.md). Bump Com.H patch version, publish.
2. **Then:** DBToRestAPI session — update Com.H package reference to the new version, apply the Site 1 refactor above, add tests, ship.

Do **not** start the DBToRestAPI refactor before the Com.H fix is published — `await using` on a `ChamberedAsyncEnumerable` whose `DisposeAsync` is a no-op would silently regress behavior (reader wouldn't close at scope exit, wouldn't be closed by explicit `CloseReaderAsync` either since we'd have removed it).

---

## Related files

- [DBToRestAPI/Controllers/ApiController.cs](DBToRestAPI/Controllers/ApiController.cs) — Sites 1–4 at lines 773, 950, 1045, 1050.
- [DBToRestAPI.Tests](DBToRestAPI.Tests) — test project, target for new regression tests.
- [Com.H/CHAMBERED-DISPOSAL-HANDOFF.md](../Com.H/CHAMBERED-DISPOSAL-HANDOFF.md) — prerequisite fix.
- Com.H.Data.Common v10.1.0.7 commit `27c21da` — the auto-close patch that triggered this audit.
