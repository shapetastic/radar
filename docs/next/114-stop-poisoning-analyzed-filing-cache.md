# Task: Stop the analyzed-filing cache from poisoning on a failed/empty read + add an invalidation key

> **EARNINGS-READ UN-STICK — slice 2 of 3.** Diagnosed live on 2026-07-18. This slice stops a transient fetch
> failure from permanently suppressing a filing's AI read, and auto-invalidates the block-era poison so the
> remediation re-measure re-analyzes correctly. Pairs with 113 (which makes the resulting read count).

## Overview

The AI earnings read (`DirectionalFilingSignalSource`) is **cache-first** (spec 107): a cache hit by accession
replays the prior result with **no www.sec.gov fetch and no AI call**. On the 2026-07-18 baseline this made the
whole feature silently dead — **all five** cached earnings filings were `NoDirectionalSignal`, every one cached at
the same `2026-07-17 12:11` run (the www.sec.gov self-block era). Busting one (AEHR) and re-running produced the
correct `DirectionalSignalProduced` (`Positive, strength 8, confidence 0.90`) — proving the cached no-signals are
**false negatives from empty EX-99.1 fetches during the block**, frozen in forever by cache-first.

Two defects:

1. **A degenerate read is cached as a genuine no-signal.** In `DirectionalFilingSignalSource.AnalyzeFilingAsync`,
   an `ISecEarningsReleaseReader` read that succeeds *structurally* but returns an **empty / near-empty body**
   flows to the analyzer, which returns `Unknown`, which the source caches as `NoDirectionalSignal` (a
   `Success`-outcome no-signal is cached so it is never re-fetched). An empty/too-short body is not a real
   "no directional signal" — it is an unreliable read that should be **left uncached** so a healthy later run
   re-attempts it.
2. **No invalidation.** `FileAnalyzedFilingCache` records have no schema/config version, so an entry produced
   under a broken fetch (or a different analyzer/prompt) is replayed indefinitely. There is no way to retire the
   block-era poison short of manually deleting files.

## Assignment

Worktree: any
Dependencies: none (pairs with 113)
Conflicts with: 113/115 lightly (earnings-path files); coordinate.
Estimated time: ~1–2 hours

## Project structure changes

Modify:
- `src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs` — do **not** cache a `NoDirectionalSignal`
  when the fetched EX-99.1 body was empty or below a minimum content length (treat like a failed read: not
  cached, so a later run re-attempts). Only cache a no-signal when the model saw **real content** and still
  returned below-confidence / Mixed / Unknown.
- `src/Radar.Infrastructure/Filings/FileAnalyzedFilingCache.cs` + `AnalyzedFilingRecord`
  (`src/Radar.Application/Filings/IAnalyzedFilingCache.cs`) — add a **cache-schema/version key** (e.g. a small
  `CacheVersion` const, and/or fold the analyzer/prompt identity) so entries stamped with a different version are
  treated as a miss (re-analyzed) rather than replayed. Bumping the version auto-invalidates the block-era poison
  — the remediation re-measure then re-reads all five filings with no manual file deletion.
- The reader seam (`ISecEarningsReleaseReader` / its result) may need to surface the fetched body length (or an
  "empty body" signal) so the source can make the don't-cache decision without re-reading. Keep it minimal.

## Implementation details

- **Empty/short-body guard.** Define a small minimum plausible EX-99.1 length (an earnings release is never a few
  bytes). Below it → treat as a non-authoritative read: **return without caching** (leave for a later run), and
  log at Debug. This preserves spec 107's cost control for genuine no-signals while refusing to freeze in a
  degenerate one. A truly empty release that the model legitimately can't classify from *real* short text is an
  edge case — bias toward NOT caching when the body is implausibly short.
- **Invalidation key.** A `CacheVersion` on the record; the cache read treats a version mismatch as a miss. Bump
  it in this spec so all existing (block-era) entries invalidate. Document the value in the record.
- **Operational-only, no scoring change.** Per the class contract, the cache "only changes WHETHER a fetch
  happens — the scored signal set is unchanged." So this slice is **operational**: **no `_formula.Version`,
  no `RuleSetVersion`, no fingerprint change.** It changes cache hit/miss behaviour, not scoring config. (The
  *downstream* score changes only because a correct read now happens and 113 lets it count — that is 113's
  concern, not a fingerprint input here.)
- Never cache a failed (non-`Success`) read — unchanged existing behaviour, keep it.

## Tests

- A `Success` read with an **empty / below-minimum** body does **not** write a cache entry (re-attempted next
  run); a `Success` read with real content that yields Unknown/below-confidence **is** cached as
  `NoDirectionalSignal` (unchanged).
- A cache entry stamped with an **older `CacheVersion`** is treated as a miss (re-analyzed), not replayed.
- A cache entry at the **current** version still hits (cost control preserved; no needless re-fetch).
- The 429 circuit breaker and "never cache a failed read" behaviours are unchanged (regression).

## Constraints

- Target .NET 10 / `net10.0`, C# 14. No provider SDK outside Infrastructure (AD-5).
- Operational change only — **no formula/ruleset/fingerprint version bump**; confirm the default fingerprint is
  unchanged.
- Determinism and graceful degradation preserved (a bad filing never aborts the batch).

## Acceptance criteria

- [ ] A structurally-successful read with an empty/near-empty body is **not** cached as `NoDirectionalSignal`
      (left for a later healthy run); real-content no-signals are still cached.
- [ ] Analyzed-filing cache carries a version key; a version mismatch is a miss, so the block-era poison
      auto-invalidates on the bump (no manual file deletion needed for the remediation re-measure).
- [ ] No `_formula.Version` / `RuleSetVersion` / fingerprint change; default fingerprint unchanged (confirm).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
