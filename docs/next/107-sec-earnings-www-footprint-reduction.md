# Task: Cut the earnings reader's www.sec.gov footprint — analysis-result cache + per-run 429 circuit breaker + polite pacing

## Overview

A live investigation (baseline @ `72e8696`, after spec 105 merged) found the AI directional-filing path
— the **only** signal path that can lift Opportunity above the keyword ceiling — produces zero signals
because **SEC's edge is intermittently IP-blocking the entire `www.sec.gov` host** from the run machine.
This was proven by isolated `curl` outside any Radar run:

| Endpoint | Result |
|---|---|
| `data.sec.gov/submissions/CIK…json` (the 3 SEC collectors) | **200 OK** |
| `www.sec.gov/Archives/edgar/data/…-index.htm` (the earnings reader) | **429** |
| `www.sec.gov/` (plain homepage, any/no User-Agent) | **429** |

The block is **host-wide, immediate (~100 ms), persistent, and intermittent across days** (AI produced
signals on 07-14, then zero on 07-15/16). `data.sec.gov` is unaffected. The EX-99.1 earnings-exhibit
bodies the AI needs live **only** on `www.sec.gov/Archives` — there is no `data.sec.gov` mirror — so the
one reader that fetches filing *documents* (`HttpSecEarningsReleaseReader`, hit by
`DirectionalFilingSignalSource`) is walled off while the metadata collectors sail through.

**Spec 105's 429 backoff-retry is correct but cannot overcome a host-level IP block** — and worse, on a
blocked day it *increases* the footprint: 5 filings × (1 initial + up to 2 retries) × up to 2 requests
(index + exhibit) ≈ **30 rejected `www.sec.gov` requests per run**, feeding exactly the fair-access
flagging that keeps the IP blocked. The daily scheduled run has been doing this for days.

**The real lever is reducing the sustained `www.sec.gov` footprint so the IP stops being flagged.** This
spec does that three ways, none of which is a scoring change:

1. **Analysis-result cache keyed by accession** — the structural fix. `DirectionalFilingSignalSource`
   re-selects the newest ~5 earnings 8-Ks every run and re-fetches + re-analyzes the **same** filings
   daily (they don't change until the next quarter's earnings), so the reader makes ~10 `www.sec.gov`
   requests *every day* for filings it already read. Cache each filing's analysis **result** by
   accession and replay it on subsequent runs — turning "10 requests/day forever" into "fetch each
   earnings 8-K exactly once, ever" (~one per company per quarter, bursty at earnings season).
2. **Per-run 429 circuit breaker** — after K consecutive rate-limited reads in a run, stop trying the
   remaining filings this run. Cuts a fully-blocked run from ~30 rejected requests to ~2×K, breaking
   the flagging feedback loop precisely on the days it's happening.
3. **Polite inter-request pacing on `www.sec.gov`** — a configurable minimum delay between the reader's
   requests, so the residual first-time fetches stay well under SEC fair access (≤10 req/s).

> **This is operational plumbing, not a scoring change.** No formula / weight / `RuleSetVersion` /
> descriptor / fingerprint change; the default fingerprint `radar-scoring-fp-c9e609ed53e9` does not
> move. The cache is **behavior-preserving for scoring**: it replays the *same* `DirectionalFilingSignal`
> a fresh read would have produced (same direction/confidence/strength/excerpt, same filing-date
> `ObservedAtUtc`), so the scored signal set is identical — only the redundant network/AI work is
> eliminated. This is safe because scoring reads **persisted Approved signals in-window** from the
> signal store (`ScoringEngine.cs:124-131`), not just this-run's freshly-extracted signals.

---

## Assignment

Worktree: unassigned (ready to dispatch)
Dependencies: Spec 105 merged (`72e8696`, the 429 backoff-retry + `SecEarningsReleaseReaderOptions` this
spec extends). Plan/implement against the current `origin/main` tip.
Conflicts with: None. Independent of the queued spec 106 (config-bind AI magnitudes + fold into
fingerprint) — 106 touches the scoring descriptor; this touches the fetch/analyze loop and a new cache.
If both land, either order works. **Sequence this BEFORE 106** — this addresses the live blocker (the
AI path is currently dead); 106 is plumbing for a downstream, measurement-gated recalibration that
cannot be exercised until this restores AI supply. (The spec numbers are IDs, not priority.)
Estimated time: ~2 hours

---

## Grounding facts (verified on disk @ `72e8696`)

**The reader** — `src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs`: `ReadAsync(cik,
accession, ct)` fetches the `{accession}-index` page then the selected EX-99.1 exhibit — **two
`www.sec.gov/Archives` requests per filing** — each via `FetchAsync`, which (spec 105) bounded-retries
429 and returns a typed `SecEarningsReleaseReadResult` whose `Outcome` can be `RateLimited`,
`Forbidden`, `HttpError`, `Unreachable`, `Timeout`, `Malformed`, `NoEarningsExhibit`, or success.
Retry knobs live in `SecEarningsReleaseReaderOptions` (`MaxRetriesOn429` default 2, `RetryBackoff` 2s).

**The reader's HttpClient** — `AddSecEarningsReleaseReader`
(`src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`): a typed
`HttpClient` with the SEC UA (`TryAddWithoutValidation`) and gzip/deflate. **No inter-request pacing
today** — the pacing knob (lever 3) is added here or in the reader.

**The source (the loop)** — `src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs`,
`ProduceAsync` (lines ~63-101): filters candidate evidence to earnings 8-Ks (form 8-K + item 2.02),
orders newest-first, `Take(_options.MaxFilingsPerRun)` (=5), then `foreach` calls
`AnalyzeFilingAsync(evidence, cik, accession, ct)`. That private method (lines ~108-165) reads the
EX-99.1 body, gates on `MinConfidence`, maps `Improving/Deteriorating → Positive/Negative` (Mixed/
Unknown → null), and returns an `ExtractedSignal?`. **Today it returns `null` for BOTH "read failed"
and "read succeeded, no directional signal"** — this spec must distinguish them (only successful reads
are cacheable; failures must be retried next run). One bad filing already degrades gracefully (the
`catch` at ~89-97 logs and continues); genuine `ct` cancellation rethrows.

**Scoring reads persisted signals, not this-run signals** —
`src/Radar.Application/Scoring/ScoringEngine.cs:124-131`: `GetByCompanyAsync` → filter
`ObservedAtUtc ∈ (windowStart, windowEnd]` + `ReviewStatus == Approved`. So a once-produced, persisted
GuidanceChange signal is scored every run until its filing-date `ObservedAtUtc` ages out — **replaying
it from cache changes nothing about what's scored.** (This is the correctness key for lever 1.)

**Reusable persistence scaffolding** (CLAUDE.md reuse-over-copy): `RadarFileStoreJson`,
`GracefulFileWriter`, `FileTickerKey`, and the `File*Store` family under
`src/Radar.Infrastructure/FileSystem/` (e.g. `FileSignalStore`, `FileScoreSnapshotStore`). Route the new
cache store through these — do **not** hand-roll JSON/file scaffolding.

**AD-14 analogue:** like price history, this cache is **reference/operational data, NOT a scoring
input** — it is not evidence, not a signal source, not in the collector `IEnumerable`, and not a
fingerprint input. It only changes *whether* a `www.sec.gov` fetch happens; the signal it yields is
identical. State this explicitly (a guardrail test, below, pins it).

---

## Design

### 1. Analysis-result cache keyed by accession (the structural fix)

- New Application seam `IAnalyzedFilingCache` (`src/Radar.Application/Filings/`):
  - `Task<AnalyzedFilingRecord?> TryGetAsync(string accession, CancellationToken ct)`
  - `Task PutAsync(AnalyzedFilingRecord record, CancellationToken ct)`
  - `AnalyzedFilingRecord` (Application record): `Accession`, `Outcome` (`DirectionalSignalProduced` |
    `NoDirectionalSignal`), and — when a signal was produced — the replayable fields
    (`Direction`, `Strength`, `Novelty`, `Confidence`, `SupportingExcerpt`, `Reason`) plus the
    `ObservedAtUtc`/`CompanyMention` needed to reconstruct the identical `DirectionalFilingSignal`.
    All in UTC; deterministic.
- New Infra impl `FileAnalyzedFilingCache` (`src/Radar.Infrastructure/Filings/`): one JSON file per
  accession under a configured dir (`Radar:…Directory`, supplied by `run-radar.ps1` like the other
  stores), routed through `RadarFileStoreJson`/`GracefulFileWriter`; per-file read failures degrade to
  "cache miss" (fail-safe, never throws into the pipeline). Accession is filename-sanitized via the
  shared `FileTickerKey` idiom (or an equivalent shared key helper — do not paste a new sanitizer).
- Wire `IAnalyzedFilingCache` into `DirectionalFilingSignalSource` (new ctor dependency). In
  `ProduceAsync`, for each eligible filing:
  1. `TryGetAsync(accession)` — **cache hit** ⇒ reconstruct the `DirectionalFilingSignal` from the
     record (or emit nothing for `NoDirectionalSignal`) **without any `www.sec.gov` fetch or AI call**,
     add to `produced`, continue. No counter/circuit-breaker interaction.
  2. **cache miss** ⇒ `AnalyzeFilingAsync` as today (fetch + AI), then:
     - success **with** a directional signal ⇒ `PutAsync(DirectionalSignalProduced, …)`;
     - success **with no** directional signal (read OK, AI Mixed/Unknown/below-confidence) ⇒
       `PutAsync(NoDirectionalSignal)` — so we never re-fetch it;
     - **read failure** (`RateLimited`/`Unreachable`/`Timeout`/`HttpError`/`Malformed`/`Forbidden`) ⇒
       **do NOT cache** — leave it for a later run (once `www.sec.gov` unblocks / the filing re-appears).
- Change `AnalyzeFilingAsync` to return a small result type distinguishing **fetch-failure** vs
  **success-no-signal** vs **success-with-signal** (e.g. `(SecEarningsReleaseReadOutcome outcome,
  ExtractedSignal? signal)` or a dedicated enum+signal record), so the loop can make the cache/
  circuit-breaker decisions above. Preserve the existing debug logging.

**Behavior-parity invariant (must hold, pinned by test):** for a given filing, the
`DirectionalFilingSignal` produced from a cache hit is **field-identical** to the one a fresh read would
produce (same direction/strength/novelty/confidence/excerpt/reason/observedAt/companyMention) — the
cache is a pure fetch/AI-elision, not a scoring change.

### 2. Per-run 429 circuit breaker

- Add `MaxConsecutiveRateLimited` (default e.g. **2**) to `SecEarningsReleaseReaderOptions` (or the
  directional-filing options — pick the one the source already reads; state which).
- In `ProduceAsync`, track **consecutive** `RateLimited` read failures across cache-miss filings. When
  the count reaches the threshold, **stop the loop** (skip the remaining eligible filings this run) and
  log one clear message (e.g. `"SEC www.sec.gov returned {N} consecutive HTTP 429s; skipping remaining
  {M} earnings reads this run (host appears blocked)."`). A success or a cache hit resets the counter.
  A non-429 failure does not trip it (that's a per-filing problem, not a host block).
- Effect: a fully-blocked run makes ~`2 × MaxConsecutiveRateLimited` `www.sec.gov` requests instead of
  ~30, and stops adding to the flagging. Setting `MaxConsecutiveRateLimited = 0` disables the breaker
  (unbounded — today's behavior) for parity testing.

### 3. Polite inter-request pacing on www.sec.gov

- Add `MinRequestInterval` (default e.g. **250 ms**, `TimeSpan`) to the reader options. Before each
  `www.sec.gov` request in `HttpSecEarningsReleaseReader.FetchAsync`, delay so consecutive requests are
  at least `MinRequestInterval` apart (track the last-request instant via the injected `TimeProvider`
  the reader/DI already uses; `await Task.Delay(remaining, ct)` honouring `ct`). Default keeps the
  reader well under SEC's 10 req/s. `MinRequestInterval = TimeSpan.Zero` restores today's un-paced
  behavior (offline tests set 0 so they never wait). Pace only actual network fetches — a cache hit
  makes no request and incurs no delay.
- Do **not** build a global cross-collector SEC throttle/handler — out of scope; the collectors use
  `data.sec.gov` (unaffected) and this reader is the only `www.sec.gov` consumer.

### Non-goals / correctness notes

- Not a scoring change; the pinned default fingerprint stays `radar-scoring-fp-c9e609ed53e9`.
- Does **not** fix the underlying SEC IP block (external/operational — the IP must cool down or be
  changed); it **reduces the footprint that causes and prolongs it**, and on unblocked days the cache
  means we barely touch `www.sec.gov` at all.
- Cache is opt-in-safe: an absent/empty cache dir ⇒ every filing is a miss ⇒ today's behavior (minus the
  breaker/pacing, which have parity-restoring defaults). No cache entry is ever written for a failed
  read, so a transient block never poisons the cache into skipping a filing forever.

---

## Project structure changes

- `src/Radar.Application/Filings/IAnalyzedFilingCache.cs` — **NEW**: cache seam + `AnalyzedFilingRecord`.
- `src/Radar.Infrastructure/Filings/FileAnalyzedFilingCache.cs` (+ `…Options.cs`) — **NEW**: file-backed
  impl via `RadarFileStoreJson`/`GracefulFileWriter`, fail-safe reads.
- `src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs` — **MODIFIED**: cache
  check/replay/populate; circuit breaker; `AnalyzeFilingAsync` returns outcome+signal.
- `src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs` — **MODIFIED**: inter-request pacing
  before each `www.sec.gov` fetch.
- `src/Radar.Infrastructure/Sec/SecEarningsReleaseReaderOptions.cs` — **MODIFIED**: add
  `MinRequestInterval` (+ `MaxConsecutiveRateLimited` if placed here).
- The Infra DI extension(s) — **MODIFIED**: register `IAnalyzedFilingCache`; bind the new options;
  supply the cache directory.
- `src/Radar.Worker/RadarWorkerOptions.cs` + `RadarWorkerServices.cs` — **MODIFIED**: surface
  `MinRequestInterval`, `MaxConsecutiveRateLimited`, and the cache directory (defaults as above).
- `scripts/run-radar.ps1` — **MODIFIED**: supply the `Radar:…Directory` override for the cache under
  `<outRoot>` (mirroring how it supplies `PricesDirectory`/`EfficacyDirectory`), so a named experiment
  profile caches under `data/experiments/<profile>/` and the baseline under `data/`.
- Tests — see below.

---

## Tests

- **Cache replay parity** (the load-bearing test): a stubbed reader/analyzer produces a directional
  signal on the first `ProduceAsync`; the cache is populated; a second `ProduceAsync` with the **same**
  candidate produces a **field-identical** `DirectionalFilingSignal` while the stub reader records
  **zero** further calls (no fetch, no AI).
- **No-signal caching:** a successful read that yields no directional signal is cached as
  `NoDirectionalSignal`; the next run does not call the reader for that accession and emits nothing.
- **Failure not cached:** a `RateLimited` (and separately an `Unreachable`) read is **not** cached; the
  next run retries it (reader called again).
- **Circuit breaker:** with `MaxConsecutiveRateLimited = 2` and a reader returning 429 for every filing,
  `ProduceAsync` attempts exactly 2 filings then stops (assert reader call count and the log); a success
  before the threshold resets the counter. `MaxConsecutiveRateLimited = 0` ⇒ all filings attempted
  (parity).
- **Pacing:** with a fake `TimeProvider`, two sequential fetches are ≥ `MinRequestInterval` apart;
  `MinRequestInterval = 0` ⇒ no delay; a cache hit incurs no delay/request.
- **Read-only-vs-scoring guardrail** (AD-14 style, structural): assert `DirectionalFilingSignalSource`/
  the cache have no dependency on scoring types and the cache is not an `IEvidenceCollector` — it cannot
  become a scoring/fingerprint input.
- **Options binding + fail-fast:** each new option binds from `Radar:*`; blank config reproduces the
  defaults; invalid values (negative interval/threshold) fail fast at registration.
- Do not weaken existing `HttpSecEarningsReleaseReader*`/`DirectionalFilingSignalSource` tests; add to
  them. Full gate: `dotnet build Radar.sln -c Release` then `dotnet test Radar.sln -c Release --no-build`.

---

## Constraints

- Target `.NET 10` / `net10.0`, C# 14.
- **Not a scoring change.** No formula / `RuleSetVersion` / `_formula.Version` / weight / attention /
  insider / descriptor change. The pinned default fingerprint `radar-scoring-fp-c9e609ed53e9` must not
  move (this touches no fingerprint input). The cache is reference/operational data (AD-14 analogue),
  never evidence/signal/scoring/fingerprint input.
- **Behavior-preserving for scoring:** a cache hit replays a field-identical `DirectionalFilingSignal`;
  the scored signal set is unchanged (safe because scoring reads persisted in-window signals). All new
  behavior has a parity-restoring default (`MinRequestInterval = 0`, `MaxConsecutiveRateLimited = 0`,
  empty cache) that reproduces today's semantics.
- **Never cache a failed read** — only successful `www.sec.gov` reads (signal or confirmed no-signal)
  are cacheable, so a transient block cannot permanently suppress a filing.
- Preserve the graceful-degradation and cancellation discipline: one bad filing never aborts the batch;
  genuine `ct` cancellation rethrows; `Task.Delay` honours `ct`; the cache store swallows per-file IO
  errors (→ miss), never `OperationCanceledException`.
- Store timestamps in UTC; deterministic serialization (AD-3). Layering (AD-5): the cache interface/
  record in Application, the file impl in Infrastructure; no provider SDK leakage.
- Reuse-over-copy: route the cache through `RadarFileStoreJson`/`GracefulFileWriter` and the shared
  filename-key helper; do not hand-roll JSON/file scaffolding or a second key sanitizer.
- Do not commit — the maintainer will review. (Orchestrator Steps 3–4 still run the reviewer loop + open
  the PR.)

---

## Acceptance criteria

- [ ] `IAnalyzedFilingCache` + `FileAnalyzedFilingCache` exist (routed through the shared file
      scaffolding, fail-safe reads); `DirectionalFilingSignalSource` checks the cache first, replays a
      **field-identical** signal on hit with **no** `www.sec.gov` fetch or AI call, and populates it only
      on a successful read (signal or confirmed no-signal) — never on a failure.
- [ ] Per-run circuit breaker stops after `MaxConsecutiveRateLimited` consecutive 429 reads (default 2;
      0 disables); a success/cache-hit resets the counter; non-429 failures don't trip it.
- [ ] `HttpSecEarningsReleaseReader` paces `www.sec.gov` requests ≥ `MinRequestInterval` (default 250 ms;
      0 disables); cache hits make no request and incur no delay.
- [ ] New options bind from `Radar:*` with parity-restoring defaults and fail-fast validation;
      `run-radar.ps1` supplies the cache directory under `<outRoot>`.
- [ ] Scored signal set is unchanged vs today for the same filings (cache-replay parity test green);
      the AI-off / no-cache path is behaviorally identical to today.
- [ ] The pinned default-fingerprint test still pins `radar-scoring-fp-c9e609ed53e9` untouched.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.

---

## Deferred / operational follow-ups (NOT this spec)

1. **Let the IP cool down / verify it clears.** This spec reduces the footprint but does not un-block an
   already-flagged IP; confirm via a plain `curl -A "<SEC UA>" https://www.sec.gov/` returning 200 before
   the next live AI re-measure. Consider a lightweight availability probe/alert so the re-measure is run
   only when `www.sec.gov` is serving.
2. **Confirm the declared User-Agent** meets SEC's current fair-access format once the host is reachable.
3. **Then** spec 106 (config-bind AI magnitudes + fold into fingerprint) and, after a live run confirms
   AI supply is restored, the measurement-gated AI-`Strength` recalibration (spec 105 deferred #1).
4. **Do not lower the Investigate threshold (60)** (spec 105 deferred #3, unchanged).
