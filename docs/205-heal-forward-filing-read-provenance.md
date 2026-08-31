# Task: Heal forward on filing reads and preserve the read before supersede

## Overview

Spec 204 fixed an important business-logic ambiguity: a confident Mixed read is evidence that Radar read a
filing, not permission to fall back to the same keyword Neutral used when no read happened. Its merged code
is sound for a genuinely new filing in one collection pass. Review against the accrued store found two
cross-run defects around that correct core:

1. **The promised v2 migration cannot run.** On 2026-08-31 the live cache held **451 v2 records — 225
   `NoDirectionalSignal`, 226 `DirectionalSignalProduced`, 0 v3**. `CollectionPass` gives the filing source
   only `newEvidence`; content-derived identity removes an accrued filing before any cache lookup. A v2
   no-signal MISS therefore schedules no re-read. Adding a historical sweep now would be a backfill and
   would violate AD-8's point-in-time rule.
2. **A Neutral read can disappear before the rule designed to prefer it sees it.**
   `SignalCrossRunDedupe.Key` is `(CompanyId, EvidenceId, Type, Direction)`. A keyword Neutral and a
   spec-204 Neutral read for the same filing share that key, so `FileSignalStore.GetByCompanyAsync` keeps
   the earliest copy and discards the read before `GuidanceChangeSupersede` can apply its
   `filingReadOutcome` preference. The normal new-evidence path suppresses the keyword in-process, but a
   correction, replayed accrued cohort or partially completed run can create both; the durable read path
   must remain correct in that state too.

This slice makes the cache explicitly heal-forward and preserves the one provenance distinction the
supersede rule needs. It does not invent historical model output and does not alter a numeric score.

## Assignment

Worktree: any. Dependency: spec 204 merged. Dispatch **before spec 206** because both touch collection/read
provenance; independent of spec 200 Phase B. Use `run-next.ps1 -Spec 205` rather than the default oldest-spec
selection while spec 200 is waiting on its three-run measurement.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Withdraw the impossible migration; retain a genuine re-admission read

Keep `AnalyzedFilingRecord.CurrentCacheVersion = 3`, every v3 cause/direction/confidence/rationale field from
spec 204, and the current outcome-scoped `FileAnalyzedFilingCache.IsAcceptedVersion` behavior:

- v2 `DirectionalSignalProduced` replays its intact cached signal, unchanged;
- v2 `NoDirectionalSignal` remains a cache MISS because it contains no reliable cause/read envelope to
  materialize;
- versions 0, 1 and any unsupported future version remain misses under the existing fail-safe rule;
- every genuinely new successful analysis writes v3.

The distinction is the upstream admission gate. An already-durable filing never reaches the cache, so the
225 accrued silent records are **not** a migration queue and do not cause 225 model calls. A v2 no-signal
MISS is reachable only when the same accession is genuinely admitted as new evidence — for example, an old
raw-evidence write failed and the item is now being made durable, or the filing content now has a distinct
content-derived evidence identity. Reading it at that new admission instant is point-in-time honest current
work, not a historical backfill; its successful v3 write may replace the v2 cache file through the existing
per-accession cache behavior.

Do not add an accrued-evidence scan, cache enumeration, retry queue or manual command. Do not rewrite any
evidence or signal file, and do not mutate a cache file except through the ordinary successful analysis of a
genuinely admitted candidate. Amend the cache class comments and any spec-204 implementation comments that
still promise a bounded sweep of every v2 no-signal record. The original spec's false migration claim has
already been corrected in place; do not restore it.

Tests must prove both gates separately:

1. an already-durable filing with a v2 no-signal cache record never enters `DirectionalFilingSignalSource`
   and makes **zero analyzer/model calls**;
2. the same accession genuinely re-admitted as new evidence reaches the source, treats v2 no-signal as a
   MISS, performs one current read and writes v3.

A v2 produced record remains a hit; v3 behaves by its recorded cause; 0/1/future versions remain misses.
Mutation: feed accrued evidence into the candidate list and the zero-call test fails; turn v2 no-signal into
a hit and the re-admission/read/v3 test fails.

## 2. Give filing-read provenance one bit of stable identity

Extend the shared `SignalCrossRunDedupe.Key` by exactly one discriminator:

`FilingReadOutcomeRecorded = signal.Type == GuidanceChange && FilingReadSignalMetadata.IsFilingReadSignal(signal)`

Use the existing metadata helper — no second JSON-envelope parser. The discriminator is deliberately a
boolean, not the outcome, confidence, rationale, model or signal id:

- a keyword Neutral and an AI-read Neutral remain distinct until `GuidanceChangeSupersede`, where the
  already-shipped read-first rule chooses the read and records the keyword removal;
- repeated persisted copies of the **same** AI read still collapse across runs;
- every non-`GuidanceChange` signal keeps the exact spec-142 identity it has today;
- Positive/Negative/Mixed read-vs-keyword pairs were already distinct by direction and remain so.

Update `SignalCrossRunDedupe`'s class documentation and the spec-142 architecture-history bullet in place:
the stable identity is now `(CompanyId, EvidenceId, Type, Direction, FilingReadOutcomeRecorded)`, with the
fifth field false for every pre-204/non-read signal. This is a provenance-class discriminator required by a
downstream winner rule, not a general invitation to hash extractor metadata into identity.

## 3. Pin the load-bearing order on the real durable read seam

Add a repository/scoring test using `FileSignalStore`, not only an in-memory list:

1. persist a keyword `GuidanceChange Neutral` at T0 and an otherwise same-key Neutral filing read carrying
   `filingReadOutcome` at T1;
2. `GetByCompanyAsync` must return both provenance classes rather than collapsing to the T0 keyword;
3. score at an as-of between T0 and T1: the known-at predicate exposes only the keyword;
4. score at/after T1: both reach `GuidanceChangeSupersede`, the filing read wins, the keyword removal is
   counted and attributed to the read;
5. reverse insertion/file enumeration order and obtain the same result.

This test protects both sides of spec 142's ordering contract: dedupe must not erase later knowledge before
the known-at filter, and supersede must not leak that later knowledge into an earlier replay.

## 4. Prove numerical and identity stability

The two Neutral signals use the same spec-204 strength, novelty and confidence envelope, and the scoring
engine already intends the read to supersede the keyword. Therefore this correction may change the
surviving link/reason/metadata and discard accounting, but **not any score component, OpportunityScore,
label, explanation text or `ComponentJson`**.

- Keep all six `ScoringConfigFingerprintTests` pins untouched. No formula, weight, descriptor, strategy or
  `RuleSetVersion` changes.
- Extend the spec-204 (a)/(b)/(c) parity test through the durable store and pin every numeric/rendered score
  field byte-identical. The evidence link may differ only where the intended read provenance replaces the
  keyword.
- Run the replay parity fixture at an as-of before and after T1. Excluding minted GUIDs, the post-T1 numeric
  snapshot is identical to the keyword-only control while its surviving link is the read; the pre-T1 result
  is the keyword.

No new statistical boundary is created: this makes already-declared supersede semantics reachable and is
numerically inert. If implementation moves a score or a fingerprint, stop — that is scope expansion, not an
expected pin update.

## 5. Live accounting after the first genuinely new filing

The PR body records the read-only 2026-08-31 baseline above. After the first successful post-205 full run
that actually contains at least one newly admitted filing candidate, report separately:

- legacy v2 records by outcome; any decrease must reconcile one-for-one to a genuinely re-admitted
  accession's successful v3 replacement, never to enumeration or a bulk retry;
- new v3 records by `NoSignalCause` plus produced direction;
- persisted current-window `GuidanceChange` counts by Positive / Negative / Mixed / Neutral-read /
  Neutral-keyword;
- the number of same-evidence keyword/read coexistence pairs before supersede and the number for which the
  read wins after supersede.

If the run contains no new filing candidate, record **not observed** and carry the measurement forward; do
not render an all-zero cause distribution as evidence about reader behaviour. Do not tune the prompt,
confidence gate or magnitudes from this first distribution.

## Non-goals

No historical enumeration, bulk re-analysis or backfill; no evidence/signal rewrite or deletion; no cache
rewrite except ordinary per-accession replacement after a genuinely re-admitted candidate is successfully
read; no AI call merely because an old cache record exists; no prompt/model/gate/magnitude/comparability
change; no score, strategy, report, efficacy or news-judgment change; no broad metadata-derived signal
identity.

## Acceptance criteria

- [ ] Accrued v2 no-signal records cause no calls or sweep; a genuinely re-admitted v2 no-signal candidate
      gets one current read and v3 replacement; v2 produced records remain hits; stale/future versions miss.
- [ ] Keyword Neutral and filing-read Neutral survive durable cross-run dedupe as separate provenance
      classes, then the read wins only after it is known; repeated copies within each class still collapse.
- [ ] Durable-store, known-at, supersede accounting and order-independence tests are mutation-proven.
- [ ] Numeric score/replay outputs and all six fingerprints remain byte-identical; only intended provenance
      may differ.
- [ ] Spec 204 and the spec-142 architecture-history identity wording tell the corrected truth; no stale
      migration promise remains.
- [ ] Build, full suite and `git diff --check` clean; actual elapsed time in the PR body.
