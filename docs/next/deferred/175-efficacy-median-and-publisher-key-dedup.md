# Task: One sample-median definition in Efficacy; one definition of the `publisher` metadata key

## Overview

The 2026-08-19 architecture sweep (trunk checkpoint at `ee801cb`) left two MEDIUM reuse-over-copy
findings open after M-1 (spec 174, merged #181). This slice closes both. It is a pure refactor:
**every produced number, artifact and stored record is byte-identical** — the point is that the next
fix to either primitive cannot land on only one copy.

**M-2 — the sample median is implemented three times inside `Efficacy/`**, same convention
(odd n → the middle order statistic; even n → the arithmetic mean of the two middle), three homes,
three slices:

1. `Efficacy\Statistics\ExactMedianInterval.cs:158` `MedianOf` — public, spec 155, the established
   definition ("kept beside the interval so the point estimate and its interval can never be computed
   under two different median definitions");
2. `Efficacy\Attention\AttentionArrivalScreenEvaluator.cs:742` `Median` — spec 169, private,
   nullable-on-empty re-implementation;
3. `Efficacy\DenominatorAudit\ScoreMoveDenominatorAudit.cs:198` `MedianOfSorted` — spec 172,
   pre-sorted-input re-implementation.

The convention is load-bearing at AD-16's median-δ screen, which thresholds at exactly 0 — a
convention fix landing on one copy is precisely the drift CLAUDE.md's reuse rule names.

**M-3 — the `"publisher"` evidence-metadata key has two independent definitions on either side of a
precommitted metric.** Writer: bare literal at `src\Radar.Infrastructure\News\NewsAttentionCollector.cs:388`.
Reader: `private const string PublisherMetadataKey = "publisher"` at
`src\Radar.Application\Efficacy\Attention\AttentionPublisherCountBuilder.cs:126` — the AD-16 distinct-publisher
count. A drift would not corrupt the metric silently (the builder fails closed with a `Missing*Publisher`
reason) but would degrade every company-date with a confusing reason. The codebase already has the exact
pattern: `Radar.Application.Collectors.CollectionProvenanceMetadata` is "the ONE definition of the
`collector` metadata key + its reader" (spec 146), and spec 151's inference table references each
collector's own `MetadataMarkerKey` const rather than repeating the string.

Grep-verified 2026-08-19: `"publisher"` appears at exactly those two sites in `src/`; no other writer
or reader exists.

## Assignment

Worktree: any
Dependencies: spec 174 merged (#181) — main @ `1c1f663` or later.
Estimated time: ~1 hour.

## Changes

### 1. M-2 — delegate the two later medians to `ExactMedianInterval.MedianOf`

- **`AttentionArrivalScreenEvaluator.Median`**: keep the member and its nullable-on-empty contract at
  the call site (`values.Count == 0 ? null : ExactMedianInterval.MedianOf(values)`) — the empty-input
  contract belongs to the evaluator (AD-16: "an empty input has no median"), the arithmetic does not.
  The doc comment's statement that the even-count convention matters to a threshold test at exactly 0
  survives, now pointing at the shared definition.
- **`ScoreMoveDenominatorAudit.MedianOfSorted`**: delegate to `ExactMedianInterval.MedianOf` (or
  delete the wrapper and call `MedianOf` at the two call sites). `MedianOf` copies and sorts its
  input; sorting an already-sorted array of doubles returns the same order, and the two
  implementations' odd-n indices are equal by integer division (`(n-1)/2 == n/2` for odd n), so the
  result is bit-identical. The audit runs over small bins — the redundant O(n log n) sort is noise and
  NOT a reason to keep a second implementation. `Percentile90OfSorted` is untouched (one copy exists;
  nearest-rank has no shared home to converge on).
- **Do NOT build a new `Statistics.Median` type.** `MedianOf`'s home beside the interval is argued in
  its own doc comment and both new consumers are already in `Radar.Application.Efficacy.*`. Update
  `MedianOf`'s doc comment to state it is THE one sample-median definition for all of `Efficacy/`
  (three consumers: the paired interval, the AD-16 screen, the denominator audit).
- **No behaviour change**: existing pinned tests for the evaluator and the audit pass UNMODIFIED —
  that is the byte-identical proof. If any test targets the internal helpers directly, retarget it to
  the surviving seam with the SAME expected values (do not delete the expectations).

### 2. M-3 — one definition of the `publisher` key, following the `CollectionProvenanceMetadata` pattern

- Add the single definition on the **Application side** (Infrastructure references Application, so
  both sides can reach it; the reverse is a layering violation). Home: beside
  `CollectionProvenanceMetadata` in `Radar.Application.Collectors` — e.g. a small static
  `NewsEvidenceMetadata` (or an additional key + reader on a suitable existing type in that
  namespace if one fits better; implementer's choice, but the spec-146 shape — key const + the ONE
  reader — is the template).
- `NewsAttentionCollector` writes through the shared const; `AttentionPublisherCountBuilder` reads
  through it (its private const is deleted). If the builder has inline read/trim logic that a shared
  reader would naturally own, move it; do not move the builder's fail-closed `Missing*Publisher`
  classification — that is the metric's business, not the key's.
- The metadata bag remains NOT an input to evidence identity or `ContentHash` (spec 145) — this
  change renames nothing on disk and no evidence id moves. Key string is unchanged (`"publisher"`),
  so every accrued evidence file keeps reading exactly as before.

### 3. What does NOT change

- No numeric output changes anywhere: leaderboard, paired comparison, AD-16 evaluator artifacts, and
  the denominator-audit CSV/markdown are byte-identical for identical inputs.
- No scoring change, no fingerprint input, no `_formula.Version` / `RuleSetVersion` bump; the
  spec-148 pin table stands and `ScoringConfigFingerprintTests` is unedited.
- No store format change; no evidence file is rewritten (AD-1/AD-8).

## Tests

- Existing evaluator / denominator-audit / interval tests pass unmodified (byte-identical proof).
- A convention test asserting the three consumers agree at the sensitive points if not already
  covered: even-count mean (two central values averaging to exactly 0 — the AD-16 threshold case)
  and odd-count middle, computed through the shared definition.
- M-3: one test asserting the collector's written metadata carries the shared key and the builder
  resolves a publisher through it (round-trip through the real key const, not a re-typed literal);
  the builder's fail-closed missing-publisher path still classifies as before.
- Grep-level guard in review (not a test): `"publisher"` as a metadata-key literal appears exactly
  once in `src/` — at the shared definition.

## Constraints

- Layering: the shared key lives in Application; `IConfiguration` is not involved; no Domain change.
- Reuse over copy: no third median, no second key definition, no new statistics type.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

## Acceptance criteria

- [ ] `ExactMedianInterval.MedianOf` is the only sample-median arithmetic in `Efficacy/`; the
      evaluator keeps its nullable-on-empty contract at the call site; the audit's sorted-input
      wrapper delegates or is deleted.
- [ ] The `publisher` metadata key has ONE definition in `Radar.Application.Collectors`, written by
      the news collector and read by the AD-16 publisher-count builder through it.
- [ ] Existing pinned tests pass unmodified; artifacts byte-identical; no fingerprint input changed.
- [ ] Build + tests green.
