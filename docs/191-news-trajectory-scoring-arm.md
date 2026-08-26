# Task: Make news directional in the signal layer — stop scoring news as volume

## Overview

`KeywordSignalExtractor` turns **every** news article into exactly one **Neutral `MediaAttention`** signal
(the code says so at the branch itself: "NewsArticle evidence is the attention event"). It never reads the
headline for meaning. Measured over a 4,000-signal sample of 2026/08 signals: **98.4% Neutral, 96.75%
`MediaAttention`**. So scoring consumes news as *volume* — close to a size proxy — and the spec-140
leaderboard's out-of-sample correlations are negative or straddle zero across every arm.

Meanwhile specs 177–190 built a two-stage read that produces exactly the missing fact: cited, typed facts and
a grounded `BusinessTrajectory`. Every one of those specs carried "no score, label, rank or fingerprint
moves", so **none of it reaches a score**. Ten slices of news comprehension currently render one marker column.

Earlier drafting of this spec proposed an eleventh strategy arm consuming the judgment, leaving v8/v9/v10 and
the Neutral-`MediaAttention` rule untouched as "controls". That was rejected by the maintainer, correctly: it
preserves ten measurements of a broken input and adds an arm rather than fixing the cause.

**This slice fixes the cause.** News gains direction in the ONE place every strategy reads — the signal
layer — so every arm improves at once and the existing comparison starts measuring something real.

### What this costs, stated up front

This is a scoring-behaviour change by design, and it is not free:

- `KeywordSignalExtractor.RuleSetVersion` **bumps**, which is the documented mechanism for a rule-structure
  change (CLAUDE.md checklist item 7). It is folded into `ScoringConfigVersion` via `SignalSourceDescriptor`.
- **All four spec-148 fingerprint pins move**, and **every** strategy re-stamps. `StrategyIdentityGuard` will
  trip on the next run until the per-name records are consciously updated. Spec 148 set the precedent and the
  wording: moving a pin "is a normal, intended act that requires a conscious update plus a lineage note — not
  scope leakage."
- **The score series gets a discontinuity.** Snapshots before and after mean different things. History is
  **not** regenerated, rewritten or backfilled (AD-8/AD-1) — the discontinuity is taken, exactly as spec 148
  took it, and noted in the lineage.

No new strategy is added. No arm is renamed. The 2026-07-31 "no new strategies" line was a scope fence inside
spec 166, not a standing rule, and is irrelevant either way.

## Assignment

Worktree: any

Dependencies: specs 187–190 merged. Every existing evidence, signal, observation, typing, family, judgment,
score and efficacy artifact stays immutable.

Estimated time: ~2 days.

## 1. Join an observation back to its evidence — measured, fail-closed

The same article exists twice with no key between them: a `news-observation-v1` record carries `companyId`,
`collector`, `feedId`, `publisher`, `headline` and `payloadHash` but **no evidence id**. Spec 145 made
evidence identity the normalized **title+body** hash, so a title-only join is a heuristic, not an identity.

Build `NewsObservationEvidenceJoin` in `Radar.Application`, keyed on `(CompanyId, normalized headline)` using
the **existing** normalization primitive the fact layer already uses — extract and share it, do not write a
second normalizer.

Rules, all fail-closed:

- a candidate matches only within the **same company**;
- **exactly one** evidence item must match. Zero matches ⇒ unjoined. **Two or more ⇒ unjoined**, never a
  guess — an ambiguous join would attach one article's direction to another's evidence;
- an unjoined observation changes nothing: its evidence keeps today's Neutral `MediaAttention` behaviour; and
- the join is computed at extraction time from stores already in memory; **no side index is persisted**, per
  spec 151's precedent that a derived-on-read function beats a materialized cache that can silently drift.

**Measure and report the join rate.** A pilot over a 398-observation sample against 4,001 news evidence files
found 77 matches, so the join demonstrably works, but its true rate, collision rate and miss rate are unknown
and must be counted per run: joined / unjoined-no-match / unjoined-ambiguous. If the ambiguous rate is
material, that is a finding to record, not a reason to relax the rule.

## 2. Direction from the read, with today's behaviour as the fallback

Extend the `NewsArticle` branch of `KeywordSignalExtractor`. For evidence joined to an observation carrying an
**admitted** judgment (§3), emit `MediaAttention` with a direction:

| trajectory | direction | strength |
| --- | --- | --- |
| `Improving` | `Positive` | scaled by the judge's finding count and typing completeness |
| `Deteriorating` | `Negative` | same scaling |
| `Mixed` | `Neutral` | genuine both-ways evidence is not a direction |
| `Unknown` | `Neutral` | the judge declined to call |
| unjoined / no admitted judgment | `Neutral` | **exactly today's behaviour, unchanged** |

The Neutral case is not deleted — it becomes the honest fallback for an article Radar has not read. As typing
coverage grows (spec 189's 350/150/25 budget), the directional share grows with it. Today's ~20% coverage
means most news stays Neutral on day one, and that is expected, not a failure.

`SignalType` stays `MediaAttention`: this is the same *kind* of fact, now with a direction. Do not invent a
new signal type — v10's channel budgets select on collector, and a new type would silently fall outside every
declared `SignalTypes` filter.

**Provenance is mandatory.** A directional news signal records the `JudgmentId`, the judge cohort key and the
matched `ObservationId` in its metadata, so a score walks back through signal → judgment → cited facts →
observation → the article URL and publisher. A signal whose provenance cannot be recorded is not emitted
directionally.

## 3. Admitting a judgment

A judgment contributes direction only when **all** hold:

- it comes from the **prospectively designated** presentation cohort
  (`Radar:NewsResearch:Judgment:PresentationCohort`), never one chosen after seeing results;
- its status is `Judged` — `ValidationFailed`, `InsufficientFacts` and `AttemptsExhausted` are **not**
  directions and fall to the Neutral fallback; and
- **point-in-time honest**: `CreatedAtUtc <= windowEndUtc` (the spec-136 predicate), so a replay at D cannot
  see a judgment written after D, and `replay ⊆ forward` still holds field-for-field.

Latest admitted judgment per company wins; ties break on lowest `JudgmentId` (AD-3).

## 4. The rule-set bump and the lineage note

Bump `KeywordSignalExtractor.RuleSetVersion`. Recompute and update all four spec-148 pins in
`ScoringConfigFingerprintTests` (30-day unit pair, 60-day live-baseline pair, 120-day long-window pair — the
window-dependence rule still applies; do not reconcile them onto one value).

Write the lineage note in `CLAUDE.md` beside spec 148's: what moved, why, the old and new values, and that
history was deliberately not regenerated. Update `data/scoring-configs/strategies/{name}.json` for every arm
in the same change, so `StrategyIdentityGuard` passes on the next run for the *intended* reason rather than
being bypassed.

## 5. Tests

- The Neutral fallback is **byte-identical** to today for unjoined evidence and for every non-`Judged` status:
  same signal, same direction, same strength, same metadata envelope.
- Each trajectory maps to its declared direction; `Mixed` and `Unknown` are Neutral, not weak-positive.
- Join: exact single match joins; zero matches and **two or more** matches both leave the signal Neutral;
  a same-headline article belonging to a different company never joins.
- Point-in-time: a judgment created after `windowEndUtc` is invisible; a replay at D reproduces the forward
  snapshot at D field-for-field excluding per-call minted `Guid`s.
- A directional signal always carries `JudgmentId`, cohort key and `ObservationId`; assert no directional
  signal exists without them.
- Fingerprint: the pins move to exactly the recomputed values, and the reflection guard over
  `ScoringWeights`/`ScoringOptions` still passes.
- Provenance: a score built from a directional news signal resolves its full chain back to an observation.

## 6. Out of scope, recorded not built

- Backfilling, regenerating or rewriting any historical signal, snapshot or efficacy artifact.
- Giving typed facts a per-fact direction (reversing spec 181's reflection-guarded rule) — the company-level
  trajectory is the input here.
- Persisting the join as a side index.
- Fixing `rationale-too-long` discarding otherwise-unexamined findings — a real defect and currently the
  largest single suppressor of directional coverage (~22% of judgments). **Its own slice, and it should go
  first if coverage matters more than direction.**
- Changing the judge prompt, result schema, taxonomy, fact-family identity or any cohort key.
- Retiring v8/v9/v10 or changing which arm is Lead.

## Acceptance criteria

- [ ] A news article Radar has read directionally produces a Positive/Negative `MediaAttention` signal
      carrying `JudgmentId`, cohort key and `ObservationId`; every unread or unjoined article produces exactly
      today's Neutral signal.
- [ ] The observation↔evidence join is exact-single-match, company-scoped, fail-closed on ambiguity, derived
      on read, and its joined / no-match / ambiguous counts are reported per run.
- [ ] Only a `Judged` record from the designated cohort with `CreatedAtUtc <= windowEndUtc` contributes
      direction; replay ⊆ forward still holds.
- [ ] `RuleSetVersion` is bumped, all four pins are updated to recomputed values, every strategy's identity
      record is updated in the same change, and `CLAUDE.md` carries the lineage note.
- [ ] No historical artifact is deleted, rewritten or backfilled; the discontinuity is taken and documented.
- [ ] No new strategy, no arm renamed, no Lead change, no formula class added.
- [ ] `dotnet build Radar.sln -c Release` and the full test suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.
