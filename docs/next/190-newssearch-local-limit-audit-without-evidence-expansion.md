# Task: NewsSearch local-limit audit without evidence expansion

## Overview

The first nightly baseline on merged spec 187 (`a180298d-0606-483d-9dd9-67a23f5d5266`, selection instant
2026-08-24T21:39:13Z) reported `ResultLimitReached` for all 19 judged companies and every company row in
the NewsSearch capture. That does not prove a provider ceiling. The baseline explicitly configures
`Radar:News:MaxRecordsPerCompany = 25`; `HttpNewsSearchReader` stops retaining after that effective local
limit, and `NewsAttentionCollector` treats equality as possible truncation. The durable aggregate is even
named `AnyFeedHitProviderCap`, although the stored fact is only that Radar reached its own configured
limit.

The current implementation cannot distinguish “the response contained exactly 25 valid items” from “the
response contained more but Radar stopped reading at 25.” Raising the limit blindly would not be a
read-side-only change: the collector produces `NewsArticle` evidence that can change attention signals,
scores and ranks, and additional observations would increase the typing inflow spec 189 is trying to
control.

This spec therefore measures the already-returned response tail without admitting one additional evidence
item or observation. It corrects the provenance language but keeps coverage fail-closed. Any proposal to
raise collection remains a later, explicit scoring and capacity decision.

## Assignment

Worktree: any

Dependencies: spec 187 merged. This audit is independent of spec 189 and must remain a separately
reviewable collector change.

Estimated time: ~0.5–1 day. Do not turn the audit into a collection-limit increase to fit the estimate.

## 1. Inspect the bounded response tail while preserving the retained prefix

Extend the internal `NewsSearchReadResult` so `HttpNewsSearchReader`:

- continues returning the same retained `Items` prefix, in the same order, capped by the requested
  `MaxRecords`;
- scans the already-loaded RSS document beyond that prefix, under the existing absolute safety ceiling,
  and reports the number of structurally valid/link-bearing items observed in the response;
- reports whether at least one valid item was observed beyond the requested local retention limit;
- exposes bounded beyond-limit items only through an internal diagnostic-tail member so the collector can
  apply the existing title-relevance and URL-dedupe rules without admitting them as evidence or
  observations; and
- never fetches another page, makes another request, follows an article URL or changes pacing.

The scan is diagnostic only. `NewsAttentionCollector` must run evidence mapping, relevance retention,
within-feed dedupe and observation capture over exactly the same retained prefix as before. It may inspect
the bounded tail to count additional unique items that would pass the existing company phrase/ticker
relevance rule, with tail URLs deduped against both the retained prefix and earlier tail items, but it must
never call `MapToEvidence` or `MapToObservation` for them.

Add a golden regression with a feed containing more than 25 items. The evidence records and observation
candidates collected before and after this change must be byte-for-byte equivalent in count, identity,
order and content; only the new diagnostic reports the raw observed tail and company-relevant unique tail.
Also cover exactly 25, fewer than 25, malformed tail items, tail duplicates and tail items irrelevant to
the company.

## 2. Persist correctly named local-limit provenance

Add trailing nullable diagnostic fields to `CollectorCompanyCoverage` sufficient to distinguish:

- the effective local retention limit;
- the maximum valid response-item count observed across that company's successful feeds;
- whether any feed was **confirmed locally truncated** by an observed valid item beyond the limit; and
- the count of additional unique company-relevant items observed in the diagnostic tail but deliberately
  not admitted by the current collection contract.

For old rows, null means “not recorded,” never false. For current rows, keep `HitEffectiveResultLimit` and
the closed `ResultLimitReached` issue semantics fail-closed so AD-16 and news-risk coverage do not silently
upgrade. Correct comments and rendering to say **effective/local result limit**, not proven provider cap.

Add a correctly named trailing nullable aggregate to `NewsObservationCollectorCapture`,
`AnyFeedHitEffectiveResultLimit` or equivalent. Retain `AnyFeedHitProviderCap` as a readable compatibility
field for existing artifacts and document it as a historical misnomer. Because the old member is a
non-nullable boolean, new captures continue mirroring the effective-limit fact into it for old readers;
new code uses the correctly named nullable field and treats the old member only as legacy fallback. That
compatibility mirror is not evidence about provider behaviour. No historical batch is rewritten.

Do not claim search enumeration is complete merely because no item beyond 25 was observed: equality still
cannot prove that the provider had no further results. Conversely, do not call a locally discarded tail a
provider cap. The honest states are possible truncation, confirmed local truncation, or below limit.

## 3. Keep the two similarly named configuration keys separate

Only **`Radar:News:MaxRecordsPerCompany`** governs the NewsSearch path audited here. Leave it at **25**.
There is also a separate `Radar:Gdelt:MaxRecordsPerCompany = 25`; it belongs to the GDELT collector and is
not in scope merely because a symbol or text search finds the same leaf key. Do not change either value.

The GDELT news collector is not enabled in the current baseline. This fact is explanatory only: the audit
must be correctly scoped by configuration path and reader type rather than by relying on current enablement.

## 4. First-run audit and later decision boundary

The first successful post-190 full run must summarize, for NewsSearch:

- companies at the effective local limit;
- companies confirmed to have a response tail beyond it;
- additional unique company-relevant tail items observed but not admitted;
- maximum and median observed valid response size; and
- the unchanged number of evidence items and observation candidates admitted under the retained prefix.

Update `CLAUDE.md` and stale code comments with the distinction between Radar's local retention limit and
a provider-side ceiling. Existing observations, evidence, signals, scores, typings, judgments and capture
artifacts remain immutable.

Any later proposal to raise `Radar:News:MaxRecordsPerCompany` must be a separate spec that states how extra
`NewsArticle` evidence affects scoring/fingerprints and how the extra observation inflow will be typed. A
sidecar-only expansion would also require an explicit provenance contract. Neither is smuggled into this
audit.

## 5. Out of scope

- Raising either `Radar:News:MaxRecordsPerCompany` or `Radar:Gdelt:MaxRecordsPerCompany`.
- Admitting a diagnostic-tail item as evidence, an observation candidate or a scoring input.
- Changing request count, query construction, pagination, pacing, article fetching or provider choice.
- Changing attention signals, scores, ranks, labels, strategies, scoring fingerprints or snapshots.
- Changing AD-15/AD-16 rules or upgrading `ResultLimitReached` to complete enumeration.
- Changing typing budgets, prompts, schemas, cohorts or completeness semantics from spec 189.
- Rewriting historical batches or replacing a missing historical diagnostic with a guessed value.

## Acceptance criteria

- [ ] NewsSearch retains exactly the same requested prefix, order and content while scanning only the
      already-loaded bounded response tail for diagnostics.
- [ ] A >25-item golden feed proves byte-identical collected evidence and observation candidates plus
      visible raw-tail, confirmed-local-truncation and relevant-unique-tail diagnostics.
- [ ] Tests cover exactly 25, fewer than 25, malformed, duplicate and company-irrelevant tail items without
      admitting any tail item to evidence or observations.
- [ ] No extra request, page, article fetch or pacing action is introduced.
- [ ] New nullable coverage and capture fields distinguish possible truncation, confirmed local
      truncation and below-limit responses; old artifacts hydrate with diagnostics absent, never false.
- [ ] Existing `ResultLimitReached` behaviour remains fail-closed and new comments/rendering do not call an
      effective local limit a proven provider cap.
- [ ] The historical non-nullable `AnyFeedHitProviderCap` field remains readable and is compatibility-
      mirrored for old readers, while a correctly named nullable aggregate carries current local-limit
      provenance for new code; no historical artifact is rewritten.
- [ ] `Radar:News:MaxRecordsPerCompany` and `Radar:Gdelt:MaxRecordsPerCompany` both remain 25, and tests pin
      the NewsSearch reader to the `Radar:News` path rather than the similarly named GDELT key.
- [ ] No score, rank, label, strategy, scoring fingerprint, score snapshot, marker policy or AD-15/AD-16
      decision rule changes.
- [ ] The first live audit reports at-limit companies, confirmed tails, relevant discarded-tail count,
      response-size distribution and unchanged admitted evidence/observation totals.
- [ ] `dotnet build Radar.sln -c Release` and the full serialized test suite pass; `git diff --check` is
      clean.
