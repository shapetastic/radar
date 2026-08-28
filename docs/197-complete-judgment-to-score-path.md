# Task: Complete the judgment-to-score path — exact article identity, resilient citations and run-level diagnostics

## Overview

The first full post-194 baseline proved that Radar is now **honest** about news direction: accrued spec-191
article directions are neutralized, and a new direction can be created only by the judgment that cited its
facts. It also exposed the next bottleneck. The corrected path is fail-closed so aggressively at two
mechanical seams that most legitimate calls still do not reach scoring.

Measured on baseline run `0b48b865-76b8-4485-996c-9b9139b694aa` (2026-08-27):

- the materializer considered 19 judgments; 9 were eligible and directional, but only **2 materialized**;
  the other **7 failed observation-to-evidence resolution**;
- EOSE had a validated `Deteriorating` judgment with five grounded trajectory facts, but produced no signal;
- all five stage-2 `ValidationFailed` responses (CASS, WDFC, CAT, LBRT and IOSP) cited eight-character
  prefixes such as `11e52ee0` instead of the complete supplied fact GUIDs; and
- the otherwise-green run emitted approximately 462 Warnings: 397 unresolved-evidence lines and 63
  spec-191-neutralization lines, plus two genuine RSS transport failures. The first two categories repeat
  across strategy × company evaluations and bury the exceptional failures an operator needs to see.

The EOSE join failure is concrete, not a hypothetical edge. Its cited observation and the evidence records
share the exact Google landing URL, normalized headline and publication instant. Two evidence records carry
the same title and URL but different publication instants. The current title-only join sees two title
matches, declares the article ambiguous and discards the stronger identity fields that select one record.
The class comment's claim that the records “share no key” is therefore false: `NewsAttentionCollector`
writes the same article URL, title/headline and published instant onto both records.

This spec fixes all three findings together. Sections 1 and 2 are coupled deliberately: each changes which
judgments can produce a directional signal, so separating them would cause two scoring-identity moves and
two operator resets. Section 2 alone forks the stage-2 cohort and causes one one-time re-judge; combining the
work does not pretend section 1 needs a model call. Section 3 is a small diagnostic follow-through that does
not affect a score. Implement in section order; do not trim the fail-closed cases, identity work or replay
aggregation to fit the estimate.

## Assignment

Worktree: any

Dependencies: specs 187–196 merged. In particular, implement this **after spec 196** and recompute identity
pins from its corrected attention baseline; do not resolve the two pin-moving specs in parallel.

Every existing observation, evidence item, typing, family, judgment, signal, review, snapshot and efficacy
artifact remains immutable.

Estimated time: ~2–3 days.

## 1. Resolve cited observations by the strongest shared article identity

### 1.1 Replace the title-only decision with a deterministic match ladder

Keep `NewsObservationEvidenceJoin` derived-on-read and pure. Do not add a side index, mutate either store or
introduce fuzzy matching. Replace its single normalized-title key with this ordered, fail-closed ladder for
each observation:

1. **Exact article instant:** exact non-blank `GoogleLandingUrl == SourceUrl` under
   `StringComparer.Ordinal`, the existing normalized headline/title is non-blank and equal, and both
   publication instants are present and equal after conversion to UTC. Exactly one evidence item and one
   distinct company may claim the key.
2. **Exact article URL:** when tier 1 has no candidate, exact non-blank URL plus the same normalized
   headline/title. This may resolve a record whose timestamp is absent or was restated only when exactly one
   evidence item and one distinct company claim the key.
3. **Unique-title fallback:** only when neither stronger tier found a candidate, apply the current normalized
   headline rule: exactly one evidence item and one distinct company.

Precedence is load-bearing:

- one exact-instant match wins even when the title-only bucket contains several records — the live EOSE
  shape;
- if a stronger tier finds **multiple** candidates or multiple companies, the observation is ambiguous and
  must stop there. Never fall through to a weaker key to make ambiguity disappear;
- zero candidates may fall through; ambiguity may not;
- a blank URL or an absent timestamp records no equality fact and cannot enter the corresponding tier;
- multiple observations for the same company and article may all resolve to the same evidence item, while
  the forward representative remains the lowest observation GUID as today; and
- the same key claimed by multiple companies remains ambiguous. Do not attach one company's verdict to a
  multi-company observation merely because its URL is exact.

Use the URL bytes already persisted. Do not canonicalize URLs, strip tracking parameters, follow redirects,
casefold paths or add timestamp tolerances in this slice. Those policies would widen identity and need their
own measured evidence.

**The ladder's premise is measured, not assumed.** Over the live store, **all 14,574 news evidence records
carry BOTH a non-blank `sourceUrl` and a non-blank `publishedAt` (100 %)**, and that URL is in the same
Google-News RSS article form the observation stores as `GoogleLandingUrl`. Tier 1 is therefore universally
eligible rather than a rarely-firing branch, which is what makes putting it first worthwhile.

⚠ **THE HIGHEST-RISK ASSUMPTION IN THIS SPEC, and the first thing to test.** Tiers 1 and 2 both require
`GoogleLandingUrl == SourceUrl` under `StringComparer.Ordinal`. Both sides are Google-News RSS URLs written
from the same feed item, but they have **not** been proven byte-identical for the same article — a differing
`?oc=` parameter, or any re-rendering between the observation and evidence write paths, would make both
strong tiers dead-fire and silently degrade the whole ladder to today's title-only behaviour.

**Establish this FIRST**, before building the ladder: take the §5.1 read-only join and report how many
observations have an exact ordinal URL twin in evidence. If the exact-URL match rate is materially below the
title-match rate, **stop and report it** rather than shipping two tiers that never fire. The fallback is not
to loosen the comparison in this slice — URL canonicalization is an explicit non-goal precisely because it
widens identity — but to record the measured mismatch shape as the evidence for a separate, targeted spec.

### 1.2 Make the disposition observable

Replace the opaque null reverse lookup with one typed disposition shared by the forward/reverse indexes and
the materializer. At minimum distinguish:

- `ExactArticleInstant`;
- `ExactArticleUrl`;
- `UniqueHeadlineFallback`;
- `NoMatch`; and
- `Ambiguous`.

The existing joined/no-match/ambiguous totals may be retained for compatibility, but add the three joined
route counts. Every supplied observation must contribute to exactly one terminal bucket:

`ExactArticleInstant + ExactArticleUrl + UniqueHeadlineFallback + NoMatch + Ambiguous == Observations`.

Thread the join counts onto `NewsJudgmentSignalMaterializationSummary` as a trailing nullable value and
render them in the live news-risk artifact. `null` means the join was not attempted; measured zero stays
zero. Split the materializer's generic `UnresolvedObservation` skip into no-match and ambiguous reasons (or
carry an equally explicit typed subreason) so a future run can distinguish missing evidence from deliberately
rejected identity. The eligible-outcome conservation identity must continue to hold.

Advance the live document `news-risk-live-v4` → `news-risk-live-v5`. A v4 document hydrates the nested join
measurement as null/not recorded; every newly attempted v5 materialization carries the measured buckets,
including honest zero. This is current-run diagnostic provenance only: it enters no bundle hash, cache key,
cohort, judgment, signal or score.

The all-or-nothing citation rule remains unchanged: if any distinct trajectory observation is `NoMatch` or
`Ambiguous`, create no signal. This spec improves identity; it does not weaken provenance.

### 1.3 Fork the materialized-signal identity honestly

This changes which judgments produce scoring inputs, so it is not a silent fix under
`news-judgment-signal-v1`:

- advance the current materializer/metadata token to `news-judgment-signal-v2`;
- derive v2 signal IDs from the v2 token plus `JudgmentId`, preserving one signal per judgment and the
  existing idempotency rules;
- stamp the v2 provenance envelope through the shared `NewsDirectionalSignalMetadata` composer; and
- fold v2 into `NewsJudgmentScoringIdentity`, which deliberately moves every judgment-enabled fingerprint.

Historical v1 signals remain valid grounded judgment signals. The one shared metadata classifier must
recognize well-formed v1 and v2 envelopes, while a present but unsupported/blank
`newsJudgmentSignalVersion` is `MalformedJudgmentEnvelope` and fails closed. Do not let an unknown version
fall through as an unrelated metadata bag. Supersede, media collapse and legacy neutralization must continue
to route through that one classifier, never three copied version checks.

Do not mint the same judgment twice merely because the materializer version changed. Before reviewing or
writing v2, derive/check both its v2 ID and the retired deterministic v1 ID for the same `JudgmentId`:

- an existing v2 is the ordinary `AlreadyMaterialized` path;
- an existing, structurally valid v1 is prior-version occupancy and creates no v2 duplicate; count that
  migration path explicitly rather than hiding it inside a generic skip; and
- a missing or malformed record at the v1 ID is not valid occupancy and must not suppress an honest v2
  retry.

A genuinely new prompt-v3 judgment has a different `JudgmentId` and may create its own v2 signal; it is a
new model read, not a version duplicate. Existing v1 files are neither overwritten nor deleted, and replay
before v2's real creation instant must not see it. The one-run knowledge-time rule remains: never backdate
v2 to the judgment instant.

## 2. Recover only unambiguous shortened fact citations

### 2.1 Tighten the instruction and define a fail-closed recovery grammar

The prompt already says “verbatim”, but five of nineteen live calls still shortened GUIDs. Amend both the
fixed system instruction and the per-request family preamble to say plainly:

- copy the **complete 36-character hyphenated FactId** exactly as supplied;
- never abbreviate, truncate, paraphrase or invent an ID; and
- apply that rule to both `TrajectoryFactIds` and every finding's `FactIds`.

Prompt wording alone is not a sufficient recovery mechanism. Add one shared citation resolver used by both
trajectory and finding validation:

1. a parseable GUID is accepted only when it is in the supplied representative-fact set;
2. otherwise, a token may be recovered only when it is 8–31 ASCII hexadecimal characters with no hyphens,
   and is an ordinal-ignore-case **prefix** of the canonical 32-character `N` rendering of exactly one
   supplied representative FactId;
3. exactly one match expands to that full GUID;
4. zero matches, two-or-more matches, a prefix shorter than eight characters, a suffix/substring, or any
   other malformed token fails with a named reason; and
5. distinctness is checked **after** expansion, so a full GUID and its prefix in the same list are a
   duplicate, not two citations.

This is not fuzzy inference: the scoped supplied set has one deterministic referent or the response fails.
Do not accept a prefix against the global fact store, select the first collision, or relax the supplied-set,
assertion-strength, context-only, finding-category, attribution or rationale gates.

### 2.2 Fork and measure the validation contract

Because the accepted value grammar changes, advance:

- `news-judgment-prompt-v2` → `news-judgment-prompt-v3`; and
- `news-judgment-schema-v2` → `news-judgment-schema-v3`.

The JSON property shape is unchanged, but FactId's accepted grammar is part of the result schema. Both
versions enter the stage-2 cohort key, so the new contract earns a fresh retry budget and no v2 completed or
failed attempt is reused as v3.

**Expected operational consequence, stated as specs 186 and 194 stated theirs:** forking the cohort key
means **every candidate company is re-judged ONCE** on the first post-197 run — roughly 19 hosted judge
calls at the current candidate count — and the accrued v2 attempts (including the five `ValidationFailed`
that motivated this section) are not reused. That is the intended effect: those five earn fresh attempts
under a contract that can accept their citations. It drains within the configured `MaxCompaniesPerRun`; no
budget or retry-count change is requested (§6). The scoring identity moves through the resolved presentation cohort in
addition to §1's materializer token; there is still only one final recomputation.

Record `FactIdPrefixExpansionCount` as a trailing nullable field on `NewsJudgmentRecord` and advance its
record schema `news-judgment-v3` → `news-judgment-v4`:

- `null` = no validated model response was examined under this contract, or a pre-197 record;
- `0` = a response was examined and every accepted citation was already complete; and
- positive = number of raw citation occurrences deterministically expanded across trajectory plus findings,
  including expansions observed before a different validation error failed the response.

Carry the count through `NewsJudgmentValidationResult`, persist it on both `Judged` and `ValidationFailed`
call-producing records, and aggregate current-pass expansions once per cohort at Information. A cache/same-
run reuse retains its original durable count but must not be reported as a new current-pass normalization.
Thread the trailing nullable count onto `NewsRiskLiveJudgment` in the v5 artifact and render measured zero,
positive and not-recorded distinctly. This makes the recovery pressure measurable instead of silently
repairing the provider forever.

## 3. Aggregate scoring warnings at the pass boundary

`ScoringEngine` is one strategy, so “one Warning per company” becomes one Warning per strategy × company.
That produced hundreds of repeated lines in the live baseline. Preserve every count while moving ownership
to the caller that can see the whole operation.

Add a transient, typed `ScoreAssemblyDiagnostics` (name flexible) to `CompanyScoreResult`, carrying at least:

- unresolved-evidence signal count and per-evaluation distinct-evidence count;
- current-window accrued-legacy and malformed-envelope neutralization counts; and
- previous/velocity-window accrued-legacy and malformed-envelope neutralization counts.

`ScoringEngine` returns those facts and may emit one bounded Debug line for an affected strategy-company
evaluation. It emits no Warning for these two categories. Do not change the filtering, neutralization,
supersede, collapse, contribution reason, evidence link, snapshot or persistence behaviour.

Both production callers must aggregate:

- `ScoringPass` emits at most one unresolved-evidence Warning and one neutralization Warning across the
  combined/standalone score pass; and
- `ReplayRunner` emits the same bounded pair across the complete replay invocation, so moving the Warning
  out of the shared engine does not make replay silent.

The aggregate must label its population honestly. Counts summed across engines are **signal-evaluation
incidences**, not globally distinct signals: include affected strategy-company evaluation count, distinct
company count and distinct strategy count (and replay as-of count where applicable). Likewise, a sum of the
per-evaluation distinct-evidence counts must not be labelled globally distinct. Keep current/previous and
accrued-legacy/malformed axes separate; a malformed current writer must not disappear inside expected
spec-191 residue.

Other engine Warnings and Information lines are out of scope. In particular, do not suppress a real provider,
RSS, file-write or snapshot-write failure, and do not disturb spec 195's explicit file-writer logging modes.

## 4. Fingerprints, lineage and operator action

Sections 1 and 2 change scoring reach and judgment meaning. Recompute/assert all six 30/60/120-day
AI-off/AI-on pins from the **post-196** baseline, update `ScoringConfigFingerprintTests`, the operator-facing
`scripts/run-profiles/default.json` record and the `CLAUDE.md` lineage. The expected split is important:
the three judgment-enabled/live AI-on pins move because their `news=enabled` segment carries the
presentation cohort and materializer identity; the three code-default judgment-disabled/AI-off pins retain
their post-196 values because the disabled segment carries neither. A disabled pin moving here indicates
scope leakage, not a deliverable.

State the discontinuity precisely:

- post-194/v1 scores fail closed correctly but materially under-admit grounded judgments because the
  title-only join rejects stronger exact identity;
- post-197/v2 scores admit only citations resolved by the stronger deterministic ladder; and
- history is preserved, never regenerated, rewritten or backfilled. The pre-197 sparse-join segment must not
  be presented as equivalent judgment coverage when interpreting news-direction efficacy.

No formula, weight, keyword rule, media-collapse rule or supersede rule changes. The fingerprint moves
because the already-hashed presentation cohort and materializer identity move. Section 3 alone would move
nothing.

The gitignored `data/scoring-configs/strategies/{name}.json` records cannot ride in the PR. After 197 merges
and before its first baseline, the operator must consciously delete/re-record every configured strategy
identity and verify the new 60-day AI-on stamp. Missing that step must continue to halt before collection;
never bypass `StrategyIdentityGuard`. This is another close identity boundary after specs 194 and 196, so
the lineage must name it rather than bury it in the profile's existing history.

## 5. Live audit and tests

### 5.1 Live audit without mutation or provider calls

Before hand-off, run the pure v1-versus-v2 join over the current live observation/evidence stores in read-only
mode and record in the PR body:

- **FIRST — the URL-identity premise (§1.1's highest risk):** how many observations have an exact ordinal
  `GoogleLandingUrl == SourceUrl` twin in evidence, versus how many have a normalized-title twin. If exact-URL
  is materially below title, report it and stop rather than shipping two tiers that never fire;
- observation counts by exact-instant / exact-URL / unique-title / no-match / ambiguous;
- the 2026-08-27 eligible-judgment replay: materializable versus no-match versus ambiguous before and after;
- EOSE explicitly, including which cited observation/evidence instant now resolves; and
- every judgment that remains unresolved, by named reason.

The measured baseline is 9 eligible / 2 materializable / 7 unresolved. The success criterion is not “force
9 of 9”: EOSE's proven exact match must resolve, the materializable count must improve, and every remainder
must still fail for a specific demonstrable provenance reason. The audit writes no signal, review, judgment,
snapshot or artifact and makes no model call.

The first post-merge live run should report v3 citation expansions and validation failures, but that provider
measurement is an operator follow-up rather than a reason for an implementation PR to mutate live data.

### 5.2 Required regression and mutation proofs

At minimum:

1. The live EOSE shape: two evidence records share title and URL but have different publication instants;
   the observation's exact instant resolves exactly one.
2. Two evidence records sharing the full URL/title/instant remain ambiguous; restoring title-only joining or
   choosing the first record turns the test red.
3. An exact URL/title match resolves when one timestamp is absent; two URL/title matches remain ambiguous and
   never fall through.
4. A genuinely unique headline fallback still joins; a blank key, no evidence, null company and cross-company
   claim retain their fail-closed outcomes.
5. Input order cannot change the match, representative or disposition totals, and all observation buckets
   conserve exactly.
6. One unresolved trajectory observation still prevents the entire signal; no partially grounded signal is
   written.
7. A v2 signal ID/envelope is deterministic; well-formed accrued v1 and current v2 envelopes are accepted by
   every shared scoring transform; an unsupported version fails closed as malformed; and an existing valid
   v1 ID prevents a duplicate v2 signal for the same judgment without suppressing a genuinely absent v2.
8. A complete supplied GUID validates unchanged. A unique eight-character prefix expands in both trajectory
   and finding citations and persists the full GUID plus the exact expansion count.
9. Ambiguous prefixes, prefixes shorter than eight characters, non-prefix substrings, non-hex tokens and
   unknown full GUIDs fail with distinct named reasons; duplicate-after-expansion fails as duplicate.
10. Prompt v3 explicitly requires complete GUIDs; prompt/schema v3 fork the cohort and attempt budget while
    reader display-name changes still do not.
11. Same-run/cache reuse does not inflate the current-pass prefix-expansion diagnostic; a new validated
    response with no expansions records measured zero, while old/no-response records hydrate null.
12. N affected companies across M strategies produce zero engine-level Warnings and exactly one pass-level
    Warning per affected diagnostic category, with exact labelled incidence/company/strategy counts.
13. Replay produces the same bounded aggregation rather than N × M × as-of Warnings; an unaffected pass emits
    neither line.
14. Removing the diagnostic return/aggregation or restoring either engine Warning turns the logging tests
    red, while snapshots, components, contributions, evidence links and persistence remain byte-identical.

Use constructed fixtures for mutation tests. Never edit or regenerate live artifacts.

## 6. Explicit non-goals

- No fuzzy headline similarity, URL canonicalization, redirect resolution or timestamp tolerance.
- No partial citation materialization and no “best available evidence” guess.
- No relaxation of business-trajectory, assertion-status, context-only, finding or advice-language gates.
- No additional provider, judge, call budget or retry count; the v3 cohort receives the existing configured
  budget.
- No same-run score backdating. Newly materialized signals remain score-visible only from a later run.
- No deletion/rewrite of v1 signals, v2 judgments or pre-197 snapshots.
- No formula, weight, attention-tier, strategy-arm, Lead, marker vocabulary or collection change.
- No change to spec 195's file-write diagnostics or spec 196's attention calibration.

## Acceptance criteria

- [ ] Observation/evidence resolution uses exact URL + headline + publication instant first, exact URL +
      headline second and unique headline last; ambiguity never falls through to a weaker tier.
- [ ] Every observation has one typed disposition, route counts conserve exactly, and the materialization
      artifact distinguishes no-match from ambiguity; v5 writes measured buckets and v4 hydrates null.
- [ ] EOSE's measured same-title/same-URL/different-time article resolves while truly ambiguous fixtures stay
      fail-closed; the all-or-nothing judgment citation rule is unchanged.
- [ ] New signals use `news-judgment-signal-v2`; accrued v1 remains valid and immutable; unsupported version
      claims fail closed through the single shared classifier.
- [ ] Prompt/schema v3 demand full GUIDs and recover only unique supplied-set hexadecimal prefixes of at
      least eight characters; all other shorthand fails by named reason.
- [ ] Prefix expansions are durably recorded as null/zero/positive without reuse inflating current-pass
      diagnostics, are rendered with the same semantics in v5, and the first live run can measure whether
      the provider still abbreviates.
- [ ] Scoring emits at most two aggregated Warnings for these categories per forward/standalone pass and per
      replay invocation; the engine emits none, and every incidence axis remains exact and honestly labelled.
- [ ] Scoring results are unchanged by §3 alone; §1/§2 deliberately move the three post-196
      judgment-enabled pins while the three judgment-disabled pins are proven unchanged, with lineage and
      the ignored identity-record operator action stated.
- [ ] The live audit establishes the URL-identity premise FIRST — the exact-ordinal-URL match rate versus
      the title match rate — and the ladder is not shipped on the assumption that tiers 1 and 2 fire.
- [ ] The read-only live join audit reports the before/after distribution and named unresolved remainder;
      no live artifact or model state is mutated.
- [ ] The one-time re-judge caused by the prompt/schema v3 cohort fork is stated in the lineage and the PR,
      with no provider, budget or retry-count change requested.
- [ ] `dotnet build Radar.sln -c Release`, the full test suite and `git diff --check` pass; on Windows,
      `scripts/run-radar.ps1 -Profile default -WhatIf` resolves with the new identities and no new config key.
