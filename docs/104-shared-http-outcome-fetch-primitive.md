# Task: Generalize the SEC HTTP fetch/outcome ladder into a shared primitive and route the 7 non-SEC readers through it

## Overview

A whole-codebase architecture checkpoint (trunk @ `7376c9e`, post-spec-103) surfaced exactly one
gating MEDIUM finding (M-1, reuse-over-copy — the recurring CLAUDE.md 76/77/83/102 theme). This
spec converts it into a **pure, behavior-preserving refactor** — no scoring change, no new feature,
no fingerprint movement.

**M-1 — the HTTP fetch/outcome ladder is hand-rolled in 7 readers while a proven shared primitive
exists for the SEC family.** `src/Radar.Infrastructure/Sec/SecHttpFetch.cs:16-54` (spec 102's
consolidation slice) already owns the canonical ladder for all 4 SEC readers: generic GET → status
mapping → `HttpRequestException`→Unreachable → `OperationCanceledException`-when-`ct` **rethrow** →
`TaskCanceledException`→Timeout, with caller-supplied projection lambdas so each reader keeps its
exact log wording. Seven non-SEC readers hand-roll the same ladder:

- `Hiring/GreenhouseBoardReader.cs:51-88` and `Hiring/LeverBoardReader.cs:50-87` (added together by
  spec 103, ~85% byte-identical to each other),
- `News/HttpNewsSearchReader.cs:64-112` (adds a 429→RateLimited branch),
- `Gdelt/HttpGdeltNewsReader.cs:54-123` (adds a 429 branch with a bounded exponential retry loop),
- `UsaSpending/HttpUsaSpendingAwardReader.cs:57-96` (POSTs via `PostAsJsonAsync`, not GET),
- `Prices/HttpPriceHistoryReader.cs:66-112` (adds a 429→RateLimited branch),
- `Rss/HttpRssFeedReader.cs:31-67`.

The ladder carries subtle correctness rules — the `catch (OperationCanceledException) when
(ct.IsCancellationRequested)` filter must come **before** the `TaskCanceledException` catch so
genuine caller cancellation rethrows while an HTTP timeout maps to a typed failure. A future fix
lands in one copy and silently misses six. Fix: generalize `SecHttpFetch` into
`Radar.Infrastructure.Sources.HttpOutcomeFetch` (the established shared home per CLAUDE.md —
alongside `CollectorCompanyHints`, `QueryFeedTarget`, `TwoKeyFeedToken`), with the 403/429 branches
becoming an optional **status hook**, and route the seven readers through it. `SecHttpFetch`
becomes a thin delegating wrapper so the 4 SEC readers are untouched.

---

## Assignment

Worktree: any
Dependencies: None
Conflicts with: None (docs/next/ is otherwise empty; touches only Infrastructure reader internals
plus one new shared file and tests).
Estimated time: ~1-2 hours

---

## Grounding facts (verified on disk @ `7376c9e`)

**The canonical ladder (`SecHttpFetch.GetAsync<TFailure, TBody>`), whose ordering is the contract:**

1. `try { using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)`
2. status hook first (`403 → onForbidden()`), **before** the generic non-success branch
3. `!IsSuccessStatusCode → onHttpError((int)StatusCode)`
4. `body = await readBody(response.Content, ct)` — the body read happens **inside** the try, so a
   timeout/transport failure during the body read maps through the same catches
5. `catch (HttpRequestException ex) → onUnreachable(ex)`
6. `catch (OperationCanceledException) when (ct.IsCancellationRequested) → throw` (genuine caller
   cancellation MUST re-throw, never map to a failure)
7. `catch (TaskCanceledException ex) → onTimeout(ex)` (non-ct cancellation = the request's own
   HTTP timeout)

The helper does **no logging** — logging lives in the caller-supplied projection lambdas so each
reader keeps its exact log wording. On success returns `(null, body)`; on failure `(failure,
default)`. `TFailure : class`.

**Per-reader variations the shared primitive must absorb (verified):**

| Reader | Verb / body read | Status hook | Notes |
|---|---|---|---|
| 4 SEC readers (via `SecHttpFetch`) | GET, bytes or string | `403 → onForbidden` (UA-guidance log) | unchanged callers |
| `GreenhouseBoardReader` | GET `Uri`, `ReadAsByteArrayAsync` | none | logs keyed on `{BoardToken}` |
| `LeverBoardReader` | GET `Uri`, `ReadAsByteArrayAsync` | none | logs keyed on `{BoardToken}` |
| `HttpNewsSearchReader` | GET `Uri`, `ReadAsStringAsync` | `429 → RateLimited` (with its own log) | logs keyed on `{QueryPhrase}` |
| `HttpGdeltNewsReader` | GET `Uri`, `ReadAsByteArrayAsync` | `429 → RateLimited` **inside a retry loop** | keeps its `while (true)` + `ComputeBackoff` + `attempt` state in the reader |
| `HttpUsaSpendingAwardReader` | **POST** `PostAsJsonAsync(Endpoint, body, ct)`, `ReadAsByteArrayAsync` | none | `PostAsJsonAsync` does NOT use `ResponseHeadersRead` — the send delegate must preserve that exactly |
| `HttpPriceHistoryReader` | GET string url, `ReadAsStringAsync` | `429 → RateLimited` (with its own log) | keeps its pre-request `ct.ThrowIfCancellationRequested()` in the reader; logs keyed on `{Ticker}` |
| `HttpRssFeedReader` | GET string url, bytes → `new MemoryStream(bytes, writable: false)` | none | logs keyed on `{FeedUrl}` |

- Every reader's failure type is its own typed `*ReadResult` (`JobBoardReadResult`,
  `NewsSearchReadResult`, `GdeltReadResult`, `UsaSpendingReadResult`, `PriceHistoryReadResult`,
  `RssFeedReadResult`) — these all stay exactly as-is; `TFailure` binds to them.
- All seven readers already share the identical catch-ladder text and comments; only the log
  message wording/keys and the 429 branches differ — those are the per-caller hooks.
- GDELT specifics: the 429 branch logs a **retry** message (`"retry {Attempt}/{MaxRetries} after
  {BackoffSeconds:0.#}s"`) when `attempt < maxRetries` (incrementing `attempt` before the log),
  else a **final** message (`"... after retries; skipping"`) and returns
  `GdeltReadResult.Failure(RateLimited, "HTTP 429 (rate limited)")`. The log always happens
  **before** `Task.Delay(backoff, ct)`. `ComputeBackoff` is `internal static` and directly tested.
- Existing behavior pins (must stay green **without edits**):
  `tests/Radar.Infrastructure.Tests/Hiring/GreenhouseBoardReaderTests.cs`,
  `Hiring/LeverBoardReaderTests.cs`, `News/HttpNewsSearchReaderTests.cs`,
  `Gdelt/HttpGdeltNewsReaderTests.cs`, `UsaSpending/HttpUsaSpendingAwardReaderTests.cs`,
  `Prices/HttpPriceHistoryReaderTests.cs`, `Rss/HttpRssFeedReaderTests.cs`, plus the four SEC
  reader test suites (`Sec/HttpSecFilingReaderTests.cs`, `Sec/HttpSecForm4ReaderTests.cs`,
  `Sec/HttpSec13DGReaderTests.cs`, `Sec/HttpSecEarningsReleaseReaderTests.cs`). They exercise
  non-success status, transport error, timeout-vs-cancellation, and the 429 outcomes end-to-end.

---

## Design

### 1. New shared primitive — `src/Radar.Infrastructure/Sources/HttpOutcomeFetch.cs`

An `internal static class` with two entry points:

```csharp
// Core: caller supplies the request itself (covers USASpending's POST).
public static async Task<(TFailure? Failure, TBody? Body)> SendAsync<TFailure, TBody>(
    Func<CancellationToken, Task<HttpResponseMessage>> send,
    Func<HttpContent, CancellationToken, Task<TBody>> readBody,
    Func<int, TFailure?>? onStatus,          // optional pre-generic status hook (403/429); non-null short-circuits
    Func<int, TFailure> onHttpError,
    Func<HttpRequestException, TFailure> onUnreachable,
    Func<TaskCanceledException, TFailure> onTimeout,
    CancellationToken ct)
    where TFailure : class;

// Convenience: GET with ResponseHeadersRead (the shape every GET reader uses today).
public static Task<(TFailure? Failure, TBody? Body)> GetAsync<TFailure, TBody>(
    HttpClient httpClient,
    Uri requestUri,          // plus a string-url overload, or accept string and let callers pass what they have —
                             // pick ONE minimal shape; do not re-parse/re-build URLs a caller already built
    Func<HttpContent, CancellationToken, Task<TBody>> readBody,
    Func<int, TFailure?>? onStatus,
    Func<int, TFailure> onHttpError,
    Func<HttpRequestException, TFailure> onUnreachable,
    Func<TaskCanceledException, TFailure> onTimeout,
    CancellationToken ct)
    where TFailure : class;
```

`SendAsync` implements the ladder in **exactly** the `SecHttpFetch` order (Grounding facts, steps
1–7): response via `send(ct)` (disposed with `using`), then `onStatus?.Invoke((int)StatusCode)` —
a non-null result returns `(failure, default)` — then the generic non-success branch, then
`readBody` inside the try, then the three catches with the `when (ct.IsCancellationRequested)`
filter **before** the `TaskCanceledException` catch. The helper does **no logging** (port
`SecHttpFetch`'s XML doc, generalized: the 403 note becomes the status-hook note; keep the
"callers MUST check `Failure` before using `Body`" and the rethrow-comment verbatim in spirit).

### 2. `SecHttpFetch` delegates (SEC readers untouched)

`SecHttpFetch.GetAsync` keeps its exact current signature (with `onForbidden`) and becomes a
one-line delegation to `HttpOutcomeFetch` with `onStatus: code => code == 403 ? onForbidden() :
null`. Its XML doc gains a pointer to the shared primitive. The 4 SEC readers and their tests do
not change. (Fully subsuming `SecHttpFetch` — editing all 4 SEC call sites — is NOT required and
expands the diff for no behavior gain; keep the thin wrapper.)

### 3. Route the seven readers

In each reader, replace the hand-rolled `try/catch` fetch block with a call to
`HttpOutcomeFetch.GetAsync`/`SendAsync`, moving each existing `_logger.LogWarning(...)` + typed
`Failure(...)` pair **verbatim** into the corresponding projection lambda. Everything after the
fetch (JSON/XML parsing, malformed handling, item mapping) stays exactly where it is.

- **`GreenhouseBoardReader` / `LeverBoardReader`** — `GetAsync` with `TBody = byte[]`
  (`ReadAsByteArrayAsync`), `onStatus: null`. Per-platform parse halves stay put (their JSON shapes
  genuinely differ — object-root `jobs[]`/`title` vs array-root `text`; merging them is out of
  scope, the finding is the fetch ladder only).
- **`HttpNewsSearchReader`** — `GetAsync` with `TBody = string`, `onStatus` mapping 429 → (existing
  429 log, then) `NewsSearchReadResult.Failure(RateLimited, "HTTP 429 (rate limited)")`.
- **`HttpPriceHistoryReader`** — same 429 hook shape with its own log wording; the pre-request
  `ct.ThrowIfCancellationRequested()` stays in the reader before the call.
- **`HttpUsaSpendingAwardReader`** — `SendAsync` with
  `send: ct2 => _httpClient.PostAsJsonAsync(Endpoint, body, ct2)` (preserving the current
  default-completion-option POST byte-for-byte), `TBody = byte[]`, `onStatus: null`.
- **`HttpRssFeedReader`** — `GetAsync` with `TBody = byte[]`; wrap in
  `new MemoryStream(bytes, writable: false)` after a successful return (or return the stream from
  `readBody` — either is fine; do not change what is disposed when).
- **`HttpGdeltNewsReader`** — the `while (true)` retry loop, `attempt` state, `ComputeBackoff`, and
  `MaxBackoff` all **stay in the reader** (genuinely per-source behavior — do not force retry into
  the shared type). Each attempt's fetch goes through `GetAsync` with an `onStatus` hook mapping
  429 → a `RateLimited` failure. Two equivalent shapes for preserving the exact retry/final log
  wording and log-before-delay ordering; pick one and say which in the PR:
  (a) the hook returns the `RateLimited` failure **without logging**, and the loop — on seeing a
  failure with `Outcome == RateLimited` — emits the existing retry log (incrementing `attempt`
  first), delays, `continue`s; or when retries are exhausted emits the existing final log and
  returns the failure; or
  (b) the hook closure captures `attempt`/`maxRetries` and emits the correct message itself.
  Either way the observable log sequence and returned results must be byte-identical to today
  (pinned by `HttpGdeltNewsReaderTests`).

### 4. Tests for the new core

Add `tests/Radar.Infrastructure.Tests/Sources/HttpOutcomeFetchTests.cs` covering the ladder rules
directly (the reason this primitive exists): status hook fires before the generic non-success
branch; non-success maps through `onHttpError` with the status code; `HttpRequestException` →
`onUnreachable`; caller-cancelled `ct` → the call **throws** (never maps); a timeout-style
`TaskCanceledException` with a non-cancelled `ct` → `onTimeout`; success returns `(null, body)`.
Follow the existing reader-test harness idiom (stub `HttpMessageHandler`).

Do not weaken, delete, or edit any existing reader test — the eleven suites passing unchanged is
the behavior-parity proof.

---

## Project structure changes

- `src/Radar.Infrastructure/Sources/HttpOutcomeFetch.cs` — **NEW**: the shared fetch/outcome ladder
  (`SendAsync` core + `GetAsync` convenience).
- `src/Radar.Infrastructure/Sec/SecHttpFetch.cs` — **MODIFIED**: thin delegation to
  `HttpOutcomeFetch` (403 branch via the status hook); signature and the 4 SEC call sites unchanged.
- `src/Radar.Infrastructure/Hiring/GreenhouseBoardReader.cs` — **MODIFIED**: fetch block routed.
- `src/Radar.Infrastructure/Hiring/LeverBoardReader.cs` — **MODIFIED**: fetch block routed.
- `src/Radar.Infrastructure/News/HttpNewsSearchReader.cs` — **MODIFIED**: fetch block routed (429 hook).
- `src/Radar.Infrastructure/Gdelt/HttpGdeltNewsReader.cs` — **MODIFIED**: per-attempt fetch routed;
  retry loop/backoff stays in the reader.
- `src/Radar.Infrastructure/UsaSpending/HttpUsaSpendingAwardReader.cs` — **MODIFIED**: routed via
  `SendAsync` with the POST send delegate.
- `src/Radar.Infrastructure/Prices/HttpPriceHistoryReader.cs` — **MODIFIED**: fetch block routed
  (429 hook).
- `src/Radar.Infrastructure/Rss/HttpRssFeedReader.cs` — **MODIFIED**: fetch block routed.
- `tests/Radar.Infrastructure.Tests/Sources/HttpOutcomeFetchTests.cs` — **NEW**: direct ladder tests.
- Clean up now-unused `using`s per file only where nothing else needs them.

---

## Tests

- **Behavior parity — rely on the existing suites.** All eleven reader test suites (7 non-SEC +
  4 SEC, listed in Grounding facts) must pass **with no test edits**. They already pin non-success
  status, transport error, the timeout-vs-cancellation distinction, the 429 outcomes, the GDELT
  retry/backoff sequence, and every log-dependent failure detail string.
- **New:** `HttpOutcomeFetchTests` (see Design §4).
- Full gate: `dotnet build Radar.sln -c Release` then `dotnet test Radar.sln -c Release --no-build`
  — entire suite green.

---

## Constraints

- Target `.NET 10` / `net10.0`, C# 14.
- **Behavior-preserving refactor ONLY.** No scoring change, no formula / `RuleSetVersion` /
  `_formula.Version` bump, and **no `CollectorName` renames** (renames re-stamp the
  `ScoringConfigVersion` fingerprint — the related L-1 finding was explicitly deferred). The
  default fingerprint **`radar-scoring-fp-c9e609ed53e9` must NOT move** — this spec touches no
  fingerprint input (no collector names, no rules, no weights, no attention/insider descriptors).
- **Byte-identical observable behavior**: exact cancellation/timeout semantics (the
  `when (ct.IsCancellationRequested)` rethrow ordering before the `TaskCanceledException` catch),
  each reader's existing log wording/keys, typed `*ReadResult` failure outcomes and detail strings,
  the GDELT retry log sequence and log-before-delay ordering, and USASpending's POST completion
  option — all unchanged, pinned by the existing tests.
- Reuse-over-copy (CLAUDE.md): share the common core; keep genuinely per-reader behavior — SEC's
  403 UA-guidance mapping, the 429 rate-limit handling, GDELT's retry loop, USASpending's POST,
  the price reader's pre-request cancellation check — as explicit caller hooks/delegates, never
  forced into the shared type.
- Layering (AD-5) unchanged: everything stays inside `Radar.Infrastructure`. Note for the reviewer:
  `HttpPriceHistoryReader` sharing a low-level Infrastructure HTTP helper does **not** breach the
  AD-14 price seam — AD-14 separates price from the *evidence pipeline* (no `CollectedEvidence`,
  not in the collector `IEnumerable`), not from Infrastructure-internal plumbing.
- Preserve provenance; keep changes scoped; do not implement unrelated features (no
  Greenhouse/Lever parse-half merge, no retry generalization, no new collectors).
- Do not commit — the maintainer will review.

---

## Acceptance criteria

- [ ] `Radar.Infrastructure.Sources.HttpOutcomeFetch` exists with the `SendAsync` core +
      `GetAsync` convenience, implementing the ladder in the exact `SecHttpFetch` order
      (status hook → generic non-success → body-read inside try → Unreachable →
      cancellation-rethrow → Timeout), no logging inside the helper.
- [ ] `SecHttpFetch.GetAsync` delegates to `HttpOutcomeFetch` (403 via the status hook); its
      signature and all 4 SEC reader call sites are unchanged.
- [ ] All seven non-SEC readers (`GreenhouseBoardReader`, `LeverBoardReader`,
      `HttpNewsSearchReader`, `HttpGdeltNewsReader`, `HttpUsaSpendingAwardReader`,
      `HttpPriceHistoryReader`, `HttpRssFeedReader`) route their fetch through the primitive; zero
      hand-rolled copies of the catch ladder remain in the codebase.
- [ ] GDELT keeps its retry loop/backoff in the reader; USASpending keeps its POST via a send
      delegate; the price reader keeps its pre-request `ThrowIfCancellationRequested`.
- [ ] All eleven existing reader test suites pass **unchanged** (behavior-parity proof).
- [ ] New `HttpOutcomeFetchTests` covers hook-before-non-success ordering, HttpError mapping,
      Unreachable mapping, caller-cancellation rethrow, timeout mapping, and success.
- [ ] No `CollectorName`, rule, weight, or descriptor change anywhere; the pinned default
      fingerprint test still pins `radar-scoring-fp-c9e609ed53e9` untouched.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
