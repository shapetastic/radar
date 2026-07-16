# Task: Give the SEC earnings-release reader a bounded 429 backoff-retry so the AI directional-filing path stops starving

## Overview

A live-measurement investigation (baseline @ `8752748`, plus a `long-window` experiment run on
2026-07-16) found that **the AI directional-filing extractor — the only signal path that can lift a
company's Opportunity above the keyword-only ceiling — produces zero signals on most daily runs**,
and the root cause is **SEC HTTP 429 rate-limiting on the earnings-release reader**, not scoring
tuning.

### What the measurement showed (grounding, not speculation)

- `RadarScoreFormulaV5` Opportunity = `Trajectory × (EvidenceConfidence/100) × (1 − Attention/250)`.
  The `KeywordSignalExtractor` rule table caps keyword signals at **strength 6** (⇒ Trajectory ≤ 80)
  and **confidence 0.65** (⇒ EvidenceConfidence ≤ 65), so a keyword-only company cannot exceed
  **Opportunity ≈ 52** — below the `WeeklyReportActionPolicyV1.InvestigateOpportunity` gate of **60**.
  Across all 184 persisted `radar-formula-v5` snapshots, the highest Opportunity ever recorded is
  **50**, and **nothing has ever reached Investigate**.
- The AI path (`DirectionalFilingSignalSource` → `IFilingAnalyzer`) *can* clear that ceiling: it emits
  `GuidanceChange` signals at **confidence 0.9–0.95** (e.g. a real EOSE read: *"445% YoY revenue
  increase … gross loss improving 157 pts"*), which lift EvidenceConfidence toward its 89 max.
- **But the AI path fires only intermittently.** High-confidence AI signals were created in the
  baseline on 07-03/04/05/06/07/08/14 (a handful each, 22 on one backfill day) and produced **zero**
  on 07-09/10/13/15/16. The `long-window` experiment produced **zero** AI signals, and today's
  baseline daily run also produced **zero**.
- **The cause is in the logs.** Every `HttpSecEarningsReleaseReader` warning that run was
  `returned non-success status 429; skipping.` — all 5 of the `MaxFilingsPerRun=5` candidate filings
  got HTTP 429 on their EX-99.1 fetch, so `AnalyzeFilingAsync` never called the analyzer (it returns
  early on a non-success read). No document reaches the AI ⇒ no directional signal that day.

### Why this reader specifically gets 429'd

The heavy SEC collectors (`SecEdgarFilingCollector`, `Sec13DGCollector`, `SecForm4Collector`) hit the
**`data.sec.gov/submissions/CIK….json`** submissions API — **one request per company** (~18 total),
each returning all recent filings; they collected 985 filings with **0 failures** that run. The
earnings-release reader is different: for each candidate 8-K it fetches **two `www.sec.gov/Archives/…`
document pages** (the `{accession}-index.html`, then the EX-99.1 exhibit) — up to 10 Archives
requests fired right after the collection burst, against SEC's fair-access limit. It has **no 429
handling**: `SecHttpFetch` maps 429 straight to `onHttpError` → a typed skip, no retry.

**The GDELT reader already solved exactly this shape** and the fix should mirror it: `HttpGdeltNewsReader`
owns a bounded exponential 429 backoff-retry (`GdeltNewsQuery.MaxRetriesOn429` / `RetryDelay`,
`ComputeBackoff`, `MaxBackoff` 10 min cap) because "GDELT throttles hard and returns HTTP 429 on
back-to-back requests." SEC does the same under burst; the earnings reader needs the same treatment.

> This spec is a **reliability fix on the SEC earnings fetch path ONLY**. It is **not** a scoring
> change. It does **not** touch the formula, weights, `RuleSetVersion`, the AI signal `Strength`, or
> the scoring window. Those are separate, explicitly-deferred follow-ups (see "Deferred follow-ups").

---

## Assignment

Worktree: any
Dependencies: None (builds on spec 104's `HttpOutcomeFetch`/`SecHttpFetch`, already merged @ `8752748`)
Conflicts with: None (`docs/next/` is otherwise empty; touches the SEC earnings-fetch path + its DI
registration + a shared backoff helper + tests).
Estimated time: ~1-2 hours

---

## Grounding facts (verified on disk @ `8752748`)

**The reader** — `src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs`:
- `ReadAsync(cik, accession, ct)` calls the private `FetchAsync(url, ct)` **twice sequentially**
  (index page at line 55, EX-99 exhibit at line 83); each `FetchAsync` (line 102) delegates to
  `SecHttpFetch.GetAsync<SecEarningsReleaseReadResult, string>` with `onForbidden` (403),
  `onHttpError` (all other non-success incl. **429**), `onUnreachable`, `onTimeout`.
- A non-success read returns a typed `SecEarningsReleaseReadResult.Failure(...)`; the caller
  (`DirectionalFilingSignalSource.AnalyzeFilingAsync`) sees `!read.IsSuccess` and returns null → no
  signal. So **any 429 on either fetch silently drops that filing's AI read.**
- `FetchAsync` already calls `ct.ThrowIfCancellationRequested()` before each request (line 106) — keep
  that.

**The proven pattern to mirror** — `src/Radar.Infrastructure/Gdelt/HttpGdeltNewsReader.cs`:
- `while (true)` loop; each attempt fetches via `HttpOutcomeFetch`/`SecHttpFetch` with an
  `onStatus: status => status == 429 ? Failure(RateLimited, …) : null` hook that maps 429 to a
  **distinct** outcome **without logging** (the loop owns the retry-vs-final log wording and the
  log-before-delay ordering, which need the `attempt` state).
- On a `RateLimited` failure with `attempt < maxRetries`: compute `ComputeBackoff(baseDelay, attempt)`,
  increment `attempt`, log the retry message, `await Task.Delay(backoff, ct)`, `continue`. When
  retries are exhausted: log the final message and return the `RateLimited` failure (never throw).
- `internal static TimeSpan ComputeBackoff(TimeSpan baseDelay, int attempt)` =
  `min(baseDelay·2^attempt, MaxBackoff)` where `MaxBackoff = 10 min` (overflow guard).
- Config: `GdeltCollectorOptions.MaxRetriesOn429 = 2` (default), `RetryBackoff` base delay `2s`.
- Existing tests (`HttpGdeltNewsReaderTests`) pin the retry/backoff/final-log sequence for reference.

**SEC fetch helpers** (spec 104): `SecHttpFetch.GetAsync` currently exposes only an `onForbidden`
(403) hook and delegates to `Radar.Infrastructure.Sources.HttpOutcomeFetch`, whose `SendAsync`/
`GetAsync` core already takes an optional **`onStatus`** pre-generic status hook (403/429) that
short-circuits when it returns non-null. So a 429 hook is a first-class, already-supported extension
point — no new plumbing in the ladder itself.

**Reuse-over-copy (CLAUDE.md 76/77/83/102):** `ComputeBackoff` + the `MaxBackoff` cap now need a
**second** caller (GDELT + SEC-earnings). Do **not** paste a second copy. Extract the backoff
computation into a shared home and route both readers through it (see Design §1). Keep each reader's
own retry *loop* and log wording local (genuinely per-source, exactly as spec 104 left GDELT's loop in
the reader) — share the backoff math, not the divergent loop/log edges.

---

## Design

### 1. Extract the shared backoff primitive

Create `src/Radar.Infrastructure/Sources/ExponentialBackoff.cs` — an `internal static` helper owning the
one piece both readers share:

```csharp
internal static class ExponentialBackoff
{
    /// <summary>Upper bound on a single backoff delay; the exponential base·2^attempt would otherwise
    /// overflow TimeSpan / exceed Task.Delay's limit and throw. (Ported from HttpGdeltNewsReader.)</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>base·2^attempt, capped at <see cref="MaxDelay"/>.</summary>
    public static TimeSpan Compute(TimeSpan baseDelay, int attempt) =>
        /* min(baseDelay.Ticks * 2^attempt, MaxDelay), TimeSpan.FromTicks */ ;
}
```

Route `HttpGdeltNewsReader.ComputeBackoff`/`MaxBackoff` through it: replace the private `ComputeBackoff`
body + `MaxBackoff` field with calls to `ExponentialBackoff.Compute`/`MaxDelay`. This is
behavior-preserving for GDELT — same math, same cap, same result — so `HttpGdeltNewsReaderTests` must
stay green **unchanged** (that is the parity proof for the extraction). Keep GDELT's `while` loop, its
`attempt` state, and its log wording exactly where they are.

### 2. Add a bounded 429 backoff-retry to the earnings reader

In `HttpSecEarningsReleaseReader`, wrap each `FetchAsync` call in a retry loop that mirrors GDELT:

- Add a distinct 429 handling path. Two equivalent shapes (pick one, state which in the PR):
  - **(a)** Give `SecHttpFetch.GetAsync` an optional `onRateLimited` (429) hook analogous to
    `onForbidden`, defaulting to today's behavior (429 → `onHttpError`) so the **other 3 SEC readers
    are byte-for-byte unchanged**; the earnings reader passes a hook mapping 429 → a distinct
    `SecEarningsReleaseReadOutcome.RateLimited` failure **without logging**.
  - **(b)** Have the earnings reader call `HttpOutcomeFetch.GetAsync` directly with an
    `onStatus: s => s == 429 ? Failure(RateLimited, …) : null` hook (bypassing `SecHttpFetch` for this
    one reader), keeping `onForbidden`'s 403 mapping as an explicit `onStatus` 403 branch.
  - Prefer **(a)** — it keeps all SEC readers on the shared `SecHttpFetch` seam and confines the new
    outcome to the one reader that needs it.
- The retry loop lives in the earnings reader (per-source, like GDELT): on a `RateLimited` result with
  `attempt < MaxRetriesOn429`, log a retry message, `await Task.Delay(ExponentialBackoff.Compute(base,
  attempt), ct)`, increment, retry the **same** URL; when exhausted, log a final message and return the
  `RateLimited` failure (never throw). `ct.ThrowIfCancellationRequested()` / genuine caller
  cancellation still propagates (the `HttpOutcomeFetch` ladder already rethrows `OperationCanceledException`
  when `ct` is cancelled; `Task.Delay(…, ct)` honours it too).
- Apply the retry to **both** the index fetch and the exhibit fetch (both hit `www.sec.gov/Archives`
  and both 429 under burst). Simplest: the retry lives **inside** `FetchAsync`, so both call sites get
  it for free.
- Add a new outcome `SecEarningsReleaseReadOutcome.RateLimited` (parallel to `Forbidden`/`HttpError`/
  `Unreachable`/`Timeout`/`Malformed`/`NoEarningsExhibit`). When retries are exhausted, the reader
  returns `Failure(RateLimited, "HTTP 429 (rate limited)")` and `DirectionalFilingSignalSource` treats
  it exactly like any other non-success read (no signal) — no change needed there.

### 3. New tunables (config-bound, with safe defaults)

Add to the earnings-release reader's options (`SecCollectorOptions` or a small dedicated options object
the reader already receives via `AddSecEarningsReleaseReader`) two fields mirroring GDELT:

- `MaxRetriesOn429` (default **2**)
- `RetryBackoff` base delay (default **2s**)

Surface them through `RadarWorkerOptions` (under the SEC or AI section — wherever the earnings reader's
existing knobs live) so a live run can tune them, defaulting to the values above. Setting
`MaxRetriesOn429 = 0` **must** restore today's exact behavior (single attempt, 429 → skip) so the
change is provably opt-in-safe and unit tests can run the 429 path offline with `RetryBackoff = 0`.

> **Optional secondary (only if trivial):** a small polite inter-request delay before each Archives
> request further reduces 429s. If added, make it a config knob defaulting to **0** (no behavior change
> unless set) so it never slows the offline tests or changes the default run's timing contract. Do
> **not** implement any global SEC throttle/handler — out of scope.

### 4. Tests

Add `tests/Radar.Infrastructure.Tests/Sec/HttpSecEarningsReleaseReader429Tests.cs` (stub
`HttpMessageHandler`, `RetryBackoff = 0` so no real waiting), covering:

- **Retry then succeed:** handler returns `429` on the first exhibit fetch, then a valid EX-99.1 body
  → `ReadAsync` succeeds; the handler saw `1 + MaxRetriesOn429`-bounded attempts.
- **Exhausted:** handler returns `429` on every attempt → result is
  `Failure(RateLimited, …)` after exactly `MaxRetriesOn429` retries (assert attempt count).
- **`MaxRetriesOn429 = 0` ⇒ single attempt, 429 → RateLimited skip** (parity with today).
- **429 on the index fetch** (not just the exhibit) is retried too.
- **Caller cancellation during backoff throws** (cancel the `ct`, assert `OperationCanceledException`
  and no further attempts).
- Add a direct `ExponentialBackoff.Compute` test (base·2^attempt, and the `MaxDelay` cap for a large
  attempt).

Do not weaken or edit `HttpGdeltNewsReaderTests` or the other SEC reader suites — they passing
unchanged is the behavior-parity proof for the extraction and for keeping the other 3 SEC readers
untouched.

---

## Project structure changes

- `src/Radar.Infrastructure/Sources/ExponentialBackoff.cs` — **NEW**: shared `Compute` + `MaxDelay`.
- `src/Radar.Infrastructure/Gdelt/HttpGdeltNewsReader.cs` — **MODIFIED**: route `ComputeBackoff`/
  `MaxBackoff` through `ExponentialBackoff` (behavior-preserving; loop/logs unchanged).
- `src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs` — **MODIFIED**: bounded 429
  backoff-retry inside `FetchAsync`; new `RateLimited` outcome mapping.
- `src/Radar.Infrastructure/Sec/SecEarningsReleaseReadResult.cs` (or the outcome enum's file) —
  **MODIFIED**: add `SecEarningsReleaseReadOutcome.RateLimited`.
- `src/Radar.Infrastructure/Sec/SecHttpFetch.cs` — **MODIFIED** (approach (a)): optional `onRateLimited`
  hook, defaulting to preserve current 429→`onHttpError` behavior (other 3 SEC readers unchanged).
- SEC earnings reader options (`SecCollectorOptions` or dedicated) + `AddSecEarningsReleaseReader`
  registration — **MODIFIED**: `MaxRetriesOn429` / `RetryBackoff` wired from options.
- `src/Radar.Worker/RadarWorkerOptions.cs` + `RadarWorkerServices.cs` — **MODIFIED**: surface the two
  tunables (defaults 2 / 2s).
- `tests/Radar.Infrastructure.Tests/Sec/HttpSecEarningsReleaseReader429Tests.cs` — **NEW**.
- `tests/Radar.Infrastructure.Tests/Sources/ExponentialBackoffTests.cs` — **NEW** (or fold the direct
  Compute test into the 429 test file).

---

## Constraints

- Target `.NET 10` / `net10.0`, C# 14.
- **Not a scoring change.** No formula / `RuleSetVersion` / `_formula.Version` bump, no weight or
  descriptor change, no `CollectorName` rename. The default fingerprint
  **`radar-scoring-fp-c9e609ed53e9` must NOT move** (this spec touches no fingerprint input — reliability
  plumbing only). The AI signal `Strength` (6), `MinConfidence` (0.6), `MaxFilingsPerRun` (5), and the
  scoring window are **out of scope** — do not change them here.
- **Behavior-preserving where it must be:** GDELT byte-identical (parity via `HttpGdeltNewsReaderTests`
  unchanged); the other 3 SEC readers (`HttpSecFilingReader`, `HttpSecForm4Reader`, `HttpSec13DGReader`)
  byte-identical (approach (a) keeps `SecHttpFetch`'s 429→`onHttpError` default); `MaxRetriesOn429 = 0`
  restores the earnings reader's exact current single-attempt behavior.
- Preserve the `ct.ThrowIfCancellationRequested()` before each request and the `OperationCanceledException`-
  when-`ct`-cancelled rethrow discipline. Retries must never throw on 429 — always degrade to a typed
  `RateLimited` failure (graceful-degradation contract: one bad filing never aborts the batch).
- Layering (AD-5): everything stays in `Radar.Infrastructure`; the AI/HTTP specifics stay behind the
  existing interfaces. No provider SDK leakage.
- Reuse-over-copy: extract `ExponentialBackoff` and route BOTH readers through it; keep the per-reader
  retry loop and log wording per-reader.
- Do not commit — the maintainer will review. (Orchestrator note: Steps 3–4 still run the reviewer loop
  and open the PR; the "do not commit" line is the coder-agent boundary, not a block on the pipeline.)

---

## Acceptance criteria

- [ ] `Radar.Infrastructure.Sources.ExponentialBackoff` exists (`Compute` + `MaxDelay`); both the SEC
      earnings reader and `HttpGdeltNewsReader` route through it; no second copy of the backoff math
      remains.
- [ ] `HttpSecEarningsReleaseReader` retries an HTTP 429 on **both** the index and exhibit fetches with
      bounded exponential backoff (`MaxRetriesOn429`, base `RetryBackoff`), logs a retry-then-final
      sequence mirroring GDELT, and returns a typed `RateLimited` failure (never throws) when exhausted.
- [ ] `SecEarningsReleaseReadOutcome.RateLimited` added; `DirectionalFilingSignalSource` treats it as a
      non-success read (no signal) with no change to its logic.
- [ ] `MaxRetriesOn429` / `RetryBackoff` are config-bound via `RadarWorkerOptions` with defaults `2` /
      `2s`; `MaxRetriesOn429 = 0` reproduces today's exact single-attempt behavior.
- [ ] The other 3 SEC readers and `HttpGdeltNewsReader` are behavior-identical — their existing test
      suites pass **unchanged**.
- [ ] New tests cover: retry-then-succeed, retries-exhausted (attempt count), `MaxRetriesOn429 = 0`
      parity, 429-on-index, cancellation-during-backoff-throws, and `ExponentialBackoff.Compute` +
      `MaxDelay` cap.
- [ ] The pinned default-fingerprint test still pins `radar-scoring-fp-c9e609ed53e9` untouched.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.

---

## Deferred follow-ups (NOT this spec — recorded so they aren't lost)

1. **AI signal strength recalibration.** `DirectionalFilingSignalOptions.Strength = 6` equals the
   keyword-table maximum, so an AI-verified guidance change lifts EvidenceConfidence but **cannot lift
   Trajectory past the keyword ceiling of 80**. Raising it (and making it config-bindable — it currently
   is not wired through `AddDirectionalFilingSignals`) is what makes Opportunity ≥ 60 reachable *on
   merit*. This is a scoring-affecting change (re-measure required) — separate spec, do AFTER this
   reliability fix so the AI path actually produces signals to measure.
2. **Scoring window.** A `long-window` (120d) experiment showed widening `ScoringWindowDays` 60→120
   alone does **not** break the ceiling (max Opportunity 35→36, still 0 at Watch) — it only reshuffles
   which historical directional signals are in-window. Revisit only bundled with (1), so a quarterly AI
   signal survives between earnings releases. Note: `ScoringWindowDays` is **not** a fingerprint input
   (confirmed: the 120d experiment kept `radar-scoring-fp-c9e609ed53e9`).
3. **Do not lower the Investigate threshold (60).** Lowering it would relabel keyword-only companies
   (e.g. AEHR at phrase-match confidence) as Investigate — the "signals before stories" failure the
   project exists to avoid. Fix supply (this spec) + weighting (1) first, then re-measure before
   touching the gate.
