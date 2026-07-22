# Task: Apply the per-run earnings-read cap AFTER the cache check, and replay all in-window cached directional signals

> **CODE CHANGE (scoring COVERAGE, not scoring MATH) + commit an already-live config knob.** Run through the
> `run-next` coder/reviewer loop. Fixes a defect in `DirectionalFilingSignalSource` where the per-run filing cap is
> applied to the newest candidates *before* the cache is consulted, so cache hits consume cap slots and an older
> uncached backlog can starve — and, coupled to it, an in-window directional signal stops contributing to scoring
> the moment it falls out of the "newest N" window. **No fingerprint move** (the cap is deliberately excluded from
> `ScoringConfigVersion` — spec 105/107 — and this change touches only which filings are *read/replayed*, never the
> scoring formula, weights, tiers, or model identity). **But scores WILL shift** because more directional signals
> contribute per run — see the prominent consequence note below.

## Overview

`DirectionalFilingSignalSource.ProduceAsync` (`src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs`)
builds its eligible set like this today (lines ~132-137):

```csharp
var eligible = candidateEvidence
    .Select(ev => (Evidence: ev, Read: TryResolveFiling(ev)))
    .Where(x => x.Read is not null)
    .OrderByDescending(x => x.Evidence.PublishedAtUtc ?? x.Evidence.CollectedAtUtc)
    .ThenBy(x => x.Evidence.Id)
    .Take(_options.MaxFilingsPerRun)   // <-- cap applied BEFORE the per-item cache check
    .ToList();
```

The per-item cache lookup then happens *inside* the loop (line ~159, `_cache.TryGetAsync`). Because `.Take(N)` runs
first, this produces **two** coupled defects:

1. **Cache hits consume cap slots (backlog starvation).** Once the N newest earnings releases are analyzed and
   cached, every later run picks those same N, gets N cache hits, does **zero** new analysis, and never descends to
   the (N+1)-th uncached filing. The older uncached backlog does not drain over time — it sits below the cut until
   it ages out of the scoring window entirely. (This is why the maintainer had to raise `MaxFilingsPerRun` to 50
   for the 2026-07-22 21:24 live run just to read the spec-125 expansion's backlog in one pass.)

2. **In-window cached signals fall out of scoring prematurely.** Only the N filings that survive `.Take(N)` ever
   enter `produced`, so a company's directional `GuidanceChange` signal — even when its 8-K is still inside the
   60-day scoring window and its verdict is cached — **stops contributing** the moment N *other* companies have
   newer earnings releases. A directional read should persist in scoring for as long as its evidence is in the
   window; today it blinks out based on universe-wide recency ranking that has nothing to do with that company.

The root cause of both is the same: the cap is a **cost limiter on new AI analyses**, but it is being applied as a
**limiter on total scoring contribution**. Those are different things. The pacer (`SecRequestPacer`, spec ~110)
already owns SEC fair-access burst protection process-wide, so the cap's only remaining legitimate job is bounding
the number of new fetch+AI reads per run.

## The fix — two-pass loop

Restructure `ProduceAsync` so the cap bounds **new analyses only**, and all in-window cached signals replay
unconditionally. Do **not** apply `.Take()` to `eligible` any more — order it newest-first but keep the whole
in-window eligible set:

```csharp
var eligible = candidateEvidence
    .Select(ev => (Evidence: ev, Read: TryResolveFiling(ev)))
    .Where(x => x.Read is not null)
    .OrderByDescending(x => x.Evidence.PublishedAtUtc ?? x.Evidence.CollectedAtUtc)
    .ThenBy(x => x.Evidence.Id)
    .ToList();                         // NO .Take — the cap now gates pass 2 only
```

**Pass 1 — replay (unbounded, SEC-independent).** For every eligible filing, consult the cache. A hit replays its
result with no SEC fetch and no AI call: if the cached outcome is `DirectionalSignalProduced` with a non-null
signal, add `new DirectionalFilingSignal(cached.Signal, evidence)` to `produced`. Collect cache **misses** into an
ordered list (already newest-first from `eligible`). Cache hits never touch the cap and never touch the 429 breaker.

**Pass 2 — analyze (capped + breaker-guarded).** Walk the misses newest-first. Analyze at most
`MaxFilingsPerRun` of them (`fetch + AnalyzeFilingAsync`), incrementing a `newAnalyses` counter only on a genuine
new analysis attempt. Once `newAnalyses` reaches the cap, stop analyzing and leave the remaining misses for a later
run (do **not** cache them — same as today's "a failed/unattempted read is never cached" discipline). The existing
per-run 429 circuit breaker (`MaxConsecutiveRateLimited`, spec 107) moves into this pass unchanged in meaning: after
that many consecutive `RateLimited` reads, stop pass 2 (the host appears blocked). Because replay is pass 1, cache
hits still contribute even when SEC is blocking and the breaker has tripped — a strict improvement over today, where
a tripped breaker `break`s the single loop and drops remaining cached replays too.

All the per-read semantics inside `AnalyzeFilingAsync` and the caching rules stay **exactly** as they are:
- Non-authoritative read (empty/implausibly short body, spec 114) → not cached, left to re-attempt.
- Successful read, no directional signal (Mixed/Unknown/below-confidence) → cached as `NoDirectionalSignal`.
- Any fetch failure (incl. 429) → not cached.
- `AnalyzedFilingRecord.CurrentCacheVersion` handling → unchanged.

Only the **loop structure** changes; the read/analyze/cache unit is untouched.

## Also: commit the baseline `MaxFilingsPerRun = 50` (already in the working tree)

`scripts/run-profiles/default.json` was edited during the 2026-07-22 live run to add `"MaxFilingsPerRun": 50` under
the `Radar:Ai` block (with a rationale comment), but that edit lives only in the **main repo's working tree** and is
uncommitted — so `origin/main` does not have it, and the `run-next` worktree (which resets to `origin/main`) will
**not** see it. **The coder must ADD `"MaxFilingsPerRun": 50`** to the `Radar:Ai` block of `default.json` in the
worktree, so `origin` gains the value the baseline already runs with locally. Add a concise rationale comment in the
existing verbose `_comment` style: the value overrides `DirectionalFilingSignalOptions`' default of 5; that default
predated the global `SecRequestPacer`, which now owns SEC fair-access burst protection, so a low cap is a stale
pre-pacer cost limiter, not a safety mechanism; with this spec's post-cache semantics it bounds only *new* AI
analyses per run; it is a cost/operational knob, NOT a `ScoringConfigVersion` fingerprint input, so fingerprints are
unchanged.

Leave the **code defaults at 5** (`DirectionalFilingSignalOptions.MaxFilingsPerRun` and
`RadarWorkerOptions.Ai.MaxFilingsPerRun`). With the post-cache semantics, 5 now means "≤5 *new* AI analyses per
run", so a fresh environment with an empty cache still drains its backlog over successive runs (it no longer
starves) while keeping the conservative default cheap. The baseline profile carries the explicit 50.

## ⚠ Consequence to flag prominently (maintainer awareness, not a blocker)

After this change, **scores will shift** on the next run in the expected direction: more in-window directional
`GuidanceChange` signals contribute (all cached ones replay, not just the newest N), so Trajectory will move for any
company whose directional read was previously being dropped by the `.Take(N)` cut. This is the same class of
coverage-driven shift that already happens when `MaxFilingsPerRun` is raised (the 21:24 run read 43 filings vs the
morning's 5 at the same fingerprint) — it is **coverage, not math**, and the fingerprint stays
`c908f03a554a` (AI-ON) / `cb80a5809882` (AI-OFF). Note the efficacy layer segments by `ScoringConfigVersion`, so
this shift lands **within** a frozen segment rather than at a segment boundary — consistent with the deliberate
spec-105/107 decision to exclude these caps from the fingerprint, but worth expecting when reading the next efficacy
overlay. If the maintainer would rather preserve the old "only the newest N directional reads contribute to scoring"
behaviour, say so before implementation — but the recommendation is unbounded replay, because a cached directional
signal blinking out of scoring merely because five newer filings exist elsewhere in the universe is itself the bug.

## Non-goals (explicitly out of scope)

- **No DeepInfra/AI-path retry.** The OpenAI-compatible client (`ChatClientFactory`) has no 429 backoff; an AI 429
  currently surfaces as a thrown exception caught by the per-filing catch-all (no crash, no cache poison, one lost
  read). That is a separate resilience concern — leave it for its own spec.
- No change to the SEC pacer, the reader's SEC-429 backoff, collectors, the universe, or any scoring weight/formula.

## Tests (`tests/Radar.Infrastructure.Tests/...` mirroring the existing `DirectionalFilingSignalSource` tests)

Use the existing fake cache / fake reader test doubles. Add:

1. **Cache hits do not consume the cap.** Seed a cache with M already-analyzed earnings filings and provide K > 0
   *uncached* earnings filings, with `MaxFilingsPerRun` set below M. Assert: all M cached signals appear in the
   output (replayed), AND exactly `min(K, MaxFilingsPerRun)` new reads were attempted (assert via a call-counting
   reader), NOT `MaxFilingsPerRun` total minus cache hits.
2. **All in-window cached signals replay (no newest-N truncation).** Seed more cached `DirectionalSignalProduced`
   filings than `MaxFilingsPerRun`; provide zero uncached. Assert every cached signal is in the output and the
   reader was called zero times.
3. **New-analysis cap enforced.** Provide more uncached earnings filings than `MaxFilingsPerRun`, empty cache.
   Assert exactly `MaxFilingsPerRun` reads attempted, newest-first (assert the analyzed accessions are the newest
   ones), and the un-analyzed remainder is neither in the output nor written to the cache.
4. **Breaker still trips in pass 2, but cache hits still replay.** Mix cached hits with uncached filings whose reads
   return consecutive `RateLimited`; set `MaxConsecutiveRateLimited` low. Assert pass 2 stops after the breaker
   trips AND all pass-1 cache hits are still present in the output.
5. **Regression parity.** With an empty cache and `MaxFilingsPerRun` ≥ eligible count, output equals today's
   behaviour (every eligible filing analyzed once).

## Acceptance criteria

- [ ] `.Take(_options.MaxFilingsPerRun)` removed from the `eligible` projection; the cap now bounds only new
      analyses in a distinct second pass.
- [ ] All in-window cached `DirectionalSignalProduced` signals replay every run (unbounded, no SEC fetch/AI call),
      independent of the cap and independent of a tripped 429 breaker.
- [ ] Up to `MaxFilingsPerRun` cache **misses** are analyzed per run, newest-first; the remainder is left uncached
      for a later run.
- [ ] Per-read caching semantics unchanged (spec 114 non-authoritative not cached; no-signal cached; 429/failure not
      cached; cache-version handling intact).
- [ ] `scripts/run-profiles/default.json` `Radar:Ai:MaxFilingsPerRun = 50` committed; code defaults remain 5.
- [ ] Fingerprints unchanged (AI-OFF `cb80a5809882`, AI-ON `c908f03a554a`); no `_formula.Version` /
      `KeywordSignalExtractor.RuleSetVersion` bump.
- [ ] New tests above pass; existing `DirectionalFilingSignalSource` tests updated for the two-pass structure.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
