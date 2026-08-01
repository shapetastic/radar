# Task: Signal-type taxonomy — `EarningsTrajectory` for AI reads; `GuidanceAction` recorded diagnostically; `GuidanceChange` reserved for strict literal rules

> ⚠️ **DEFERRED — do not dispatch before the un-defer gate at the bottom is met.** This spec bumps
> `KeywordSignalExtractor.RuleSetVersion` AND adds an analyzer-contract version to the AI descriptor, so
> **BOTH the AI-OFF and AI-ON fingerprint pins move and ALL strategies trip `StrategyIdentityGuard`** — not
> just the AI-ON subset. It also re-keys a control strategy, starts a new filing-read cache epoch, and
> interacts with the AD-16 primary-screen boundary. Documented now; implemented later.
>
> **Revision history.** r2 (2026-08-01): 5×P1 review — `GuidanceAction` replaced the tri-state basis, the
> family supersede and cache epoch were specified, fingerprint scope corrected to all-pins. r3: second
> round **narrowed the scope to taxonomy-only** — action-derived direction deferred behind a shadow audit.
> r4 (2026-08-01, this version): third round — **the r3 family type-precedence rule is WITHDRAWN** (it
> could flip a multi-phrase filing's direction, e.g. record-revenue + cuts-guidance, contradicting
> taxonomy-only); the family now preserves TODAY'S winner and retypes it. The invariance claim is restated
> as code-level (a changed prompt can move live model output — the contract version and cache epoch exist
> to absorb exactly that), and the diagnostic `GuidanceAction` moves OFF the scored `Signal` schema into
> the cache + read-debug records, with a new `AnalysisFailed` debug outcome.

## Overview

Fix the spec-75 taxonomy misnomer at its root — without introducing any new scoring behaviour. Today
`ChatFilingAnalyzer` is asked to classify the business trajectory **as reported** (never guidance) and
`DirectionalFilingSignalSource` hardcodes every passing read to `SignalType: "GuidanceChange"`. Measured:
all 145 spec-162 calibration directional reads carry the token while 49 rationales mention neither guidance
nor outlook; the weekly report's #2 company (MSEX) was labelled with a guidance change it never issued.

After this spec:

1. **Every AI directional read emits `EarningsTrajectory`** — same direction, same confidence, same gate,
   same spec-160 cap. **Code-level guarantee: given the same trajectory result from the analyzer, every
   downstream scored field is unchanged.** (A changed system instruction can move the live model's
   trajectory answer itself — no fixture can promise otherwise; that possibility is exactly what the new
   `contract=` fingerprint segment and the cache-epoch bump account for.)
2. **`GuidanceAction` is recorded DIAGNOSTICALLY**: the analyzer additionally classifies the explicit
   guidance event (`Raised` | `Cut` | `Withdrawn` | `Introduced` | `Reaffirmed` | `None`), persisted on the
   **cache record and the read-debug record — NOT on the scored `Signal` schema** — and consumed by nothing
   that scores. It exists to be audited.
3. **Literal `GuidanceChange` survives only in the deterministic extractor**, for phrases whose wording
   states an actual guidance/outlook action.
4. **Exactly one scored earnings signal per filing — the SAME one today's code selects, retyped.**

## Assignment

Worktree: any
Dependencies: spec 167 (display relabel) merged; un-defer gate met.
Estimated time: ~2–3 hours.

## Changes

### 1. Analyzer contract — diagnostic `GuidanceAction` + an explicit authoritative/failure seam

- The typed analyzer result gains a required `GuidanceAction` field (closed set above, validated as part of
  the structured output). The system instruction defines it: an action requires an EXPLICIT statement about
  forward guidance; outlook commentary, vision statements, and "we remain confident" language are `None`.
  The trajectory classification and its confidence are UNCHANGED — `FilingSentiment.Confidence` continues
  to describe the trajectory read, the only thing scored.
- **Explicit authoritative/failure state (review r2-P2).** Today malformed analyzer output degrades to
  `FilingSentiment.Unknown`, indistinguishable from a legitimate authoritative Unknown, and is cached as
  no-signal. The successor result carries an explicit marker (e.g. `IsAuthoritative`): malformed or missing
  fields ⇒ FAILED read — never cached, retried later — while a genuine low-confidence/Unknown read remains
  cacheable no-signal exactly as today. **The read-debug sink gains a matching `AnalysisFailed` outcome**
  (review r4-P2) so a malformed action/direction is represented honestly rather than recorded as a
  no-signal conclusion.

### 2. Signal typing — rename only

- `DirectionalFilingSignalSource` emits **`EarningsTrajectory`** with the trajectory direction and
  confidence; `SignalType` (Domain) gains the `EarningsTrajectory` member. Given the same analyzer result,
  the emitted signal differs from today's ONLY in the type token (pinned by the rename-only guard test).
- The deterministic spec-57 Neutral 8-K marker moves to `EarningsTrajectory` (it marks an earnings FILING).
- **`GuidanceAction` changes NOTHING scored.** No mapping table, no action-derived direction, no
  `GuidanceActionConfidence`. Surfacing guidance cuts as first-class signals is the promotion spec's
  business (below), if the shadow audit earns it.

### 3. Diagnostic persistence — cache + debug records, NOT the scored schema (review r4-P2)

- `Signal`, `ExtractedSignal`, and the signal file are **untouched** — the scored path structurally cannot
  consume a field it does not carry, which is a stronger guarantee than any reflection guard.
- `AnalyzedFilingRecord` gains a **trailing nullable `GuidanceAction`** (`string?`, the closed-set token,
  validated on write; omitted from JSON when null so existing files stay byte-identical; legacy null =
  "not recorded (pre-168)", never defaulted). This is the corpus the shadow audit will read — the spec-164
  audit pattern already works off the cache scope.
- `FilingReadDebugRecord` gains the same field — it is specifically where the model's complete conclusion
  is recorded — plus the `AnalysisFailed` outcome above. Round-trip tests for both records (present / null
  / legacy file without the property).

### 4. The earnings-signal FAMILY — today's winner, retyped (review r4-P1; supersedes r3's order)

- `EarningsSignalTypes = { GuidanceChange, EarningsTrajectory }`, defined ONCE (Application layer); the
  `CollectionPass` suppression and the supersede (rename `GuidanceChangeSupersede` →
  `EarningsSignalSupersede`) key on family membership.
- ⚠ **The r3 type-precedence rule (`GuidanceChange` beats `EarningsTrajectory`) is WITHDRAWN — it was a
  scoring change in disguise.** Concrete case: a filing containing both "record revenue" and "cuts
  guidance". Today both phrases share the single `GuidanceChange` type slot and the extractor's
  first-match-per-type keeps "record revenue" ⇒ Positive survives. Under repartition + type precedence the
  filing would flip to `GuidanceChange (Negative)` — a direction change, contradicting this spec's hard
  constraint. Whether the cut SHOULD win is a legitimate question **for the promotion spec**, decided on
  measurement, not smuggled in through an ordering rule.
- **Taxonomy-preserving selection instead:**
  - **Extractor:** the family shares ONE emission slot with today's semantics generalised — rules are
    evaluated in table order and the FIRST matching earnings-family rule wins the family slot (byte-for-byte
    today's outcome, since today every family rule shares the one `GuidanceChange` slot). The emitted
    signal then carries its winning rule's (possibly repartitioned) type. Non-family types unchanged.
  - **Suppression + supersede:** keep today's ordering semantics exactly, widened to the family:
    directional beats Neutral, then the existing time/ID tiebreak — **no type precedence**. The
    AI-over-deterministic priority that today's suppression implements is preserved verbatim, keyed on the
    family instead of the literal type.
- Pinned by test: the record-revenue + cuts-guidance fixture selects the SAME winner pre- and post-168
  (only its type token differs), and every deterministic/AI × directional/neutral pairing keeps exactly one
  survivor, order-independent.

### 5. Extractor repartition + phrase audit — this is what moves the AI-OFF pins

- Rule for the literal type: **a `GuidanceChange` phrase must state a guidance/outlook ACTION** (raise /
  cut / lower / withdraw of guidance or outlook) — not merely mention results near the word. Under that
  rule (final per-phrase decisions at implementation, each named in the PR):
  - Stay `GuidanceChange`: "raises guidance", "raises outlook", "cuts guidance", "lowers guidance",
    "cuts outlook", "lowers outlook".
  - Move to `EarningsTrajectory`: "record revenue", "beats expectations", "exceeded outlook" (results
    outperforming a prior outlook is a results fact, not a guidance action), "above the high end", and the
    Neutral "results of operations" marker.
  - **Audit the ambiguous**: "raises full-year" does not name guidance or outlook ("raises full-year
    dividend" would match) — tighten the phrase (e.g. "raises full-year guidance" / "raises full-year
    outlook") or retype it; decide explicitly, don't inherit.
- Magnitudes unchanged; this is a rule-STRUCTURE change ⇒ **`KeywordSignalExtractor.RuleSetVersion` bump**
  ⇒ every strategy re-stamps: AI-OFF and AI-ON pins BOTH move, once, lineage notes in
  `ScoringConfigFingerprintTests` at every window (30d/60d/120d).
- **Operator acknowledge step (spec-160 precedent):** on first post-merge run `StrategyIdentityGuard` trips
  for ALL strategies; acknowledge by deleting the per-name records under `data/scoring-configs/strategies/`.

### 6. AI descriptor — the missing contract version

- The `directional-filing:` descriptor (`str/nov/minconf/model/cmpscan/cmpcap`) carries no analyzer-contract
  identity. Append **`contract=earnings-read-v2`** (new fields LAST, spec-119/160 precedent), pinned:
  perturbing it must move the AI-ON fingerprint.

### 7. Cache epoch — `CurrentCacheVersion` bump, no re-reads

- The directional source receives only NEW evidence; durable filings never re-enter because their cache is
  stale, so no re-read backlog exists and none is claimed. Bump `AnalyzedFilingRecord.CurrentCacheVersion`
  so any pre-168 record is a structural MISS if its accession is ever presented again.
- **Legacy signals stand and age out** (append-only, AD-8). Reprocessing accrued filings would be a
  separately-specced backfill/correction path — recorded, not built.

### 8. Strategy re-key

- `baseline-earnings-only` (`SignalTypes: [GuidanceChange]`, fingerprint-folded) now means something
  narrower. Add **`baseline-earnings-only-v2`** with `SignalTypes: [EarningsTrajectory, GuidanceChange]`
  under the new name (spec 141); the old series stops accruing and stays intact.

### 9. Report and policy

- Spec 167's display mapping becomes an identity mapping for the new member; the legend stays (it correctly
  describes the historical token on accrued rows).
- **Corroboration floor: the family is ONE axis.** `WeeklyReportActionPolicyV1`'s distinct-positive-types
  count treats `{GuidanceChange, EarningsTrajectory}` as a single type — two earnings signals must not
  self-corroborate. Decided here, tested explicitly.

## Follow-up specs — recorded, NOT built here

- **Shadow audit of `GuidanceAction`** (spec-164 pattern): measure action-classification precision/recall
  against a labeled corpus using the diagnostically-persisted cache/debug values; no live signals.
- **Promotion spec**, only if the audit supports it: action-derived `GuidanceChange` direction (including
  whether an explicit cut should out-rank a positive results read — the family-order question r4 removed
  from this spec), a separate `GuidanceActionConfidence`, and the open cap-policy question (should the
  spec-160 comparability cap weaken a genuine guidance cut merely because the reported quarter contains
  one-offs?).

## Tests

- Rename-only guard (code-level): **given the same analyzer result**, post-168 emitted signals differ from
  pre-168 ONLY in the type token (direction, strength, confidence, gate and cap outcomes byte-equivalent).
- Family selection: the record-revenue + cuts-guidance fixture keeps today's winner (retyped); every
  deterministic/AI × directional/neutral pairing keeps exactly one survivor, order-independent; the
  extractor's one-per-family slot reproduces today's outcome on multi-phrase bodies.
- Diagnostic fields: cache + debug records round-trip (present / null / legacy); `Signal`/`ExtractedSignal`
  and the signal file are byte-unchanged (asserted, not assumed).
- Authoritative seam: malformed ⇒ FAILED read, not cached, retried, debug outcome `AnalysisFailed`;
  authoritative Unknown ⇒ cached no-signal with today's debug outcome.
- Extractor: each repartitioned phrase pinned; the tightened "raises full-year" decision pinned; the
  guidance-action wording rule asserted against the final table.
- Identity: AI-OFF and AI-ON pins move once each per window; `contract=` segment pinned fingerprint-moving;
  `baseline-earnings-only-v2` stamps fresh; reflection guard passes.
- Cache: pre-168 record is a MISS; post-168 round-trips.
- Policy: corroboration floor counts the family once.

## Constraints

- One reader invocation per filing. Provider isolation (AD-5), structured-output validation before
  persistence, append-only stores, and the advice-language ban all hold.
- No change to cmpscan semantics; the cap applies to the trajectory confidence exactly as today.
- Constant Strength 8 untouched (spec 162: materiality encoding needs its own validation pass).
- **Given the same analyzer result, no signal's scored direction, strength, or confidence changes, and the
  per-filing family winner is today's.** (Live model output may drift because the prompt changed — that is
  a model-behaviour epoch, absorbed by `contract=` + the cache-version bump, not a code-path change.)

## AD-16 boundary (review r2-P1 + r3 refinement)

Deploying 168 makes legacy-taxonomy and new-taxonomy signals coexist in the scoring window for ~60 days.
Whichever applies:

- 168 merges BEFORE the first binding AD-16 primary screen: amend the boundary to the **later of its
  existing date and the first post-168 baseline run + 60 days**, in the same change, ledger updated.
- The first binding screen has already run: **start a NEW post-168 eligible segment** — later pooled
  readings must not mix semantic regimes. Annotating a mixed reading with its epoch is NOT sufficient.

## Un-defer gate (ALL must hold before dispatch)

1. The current strategy-comparison window has produced its first real multi-strategy ranking (v9 arms and
   baselines present on the leaderboard), OR the maintainer explicitly waives waiting.
2. Spec 167 is merged.
3. The AD-16 consequence above has been explicitly chosen by the maintainer (boundary move, or new
   segment, or hold 168 past the first binding screen).
4. Maintainer sign-off recorded in this file (replace this line with the date + decision).

## Acceptance criteria

- [ ] Every AI read emits `EarningsTrajectory`; rename-only guard passes at code level (same analyzer
      result ⇒ same scored fields).
- [ ] `GuidanceAction` diagnostic-only on the cache + read-debug records (closed-set validated, trailing
      nullable, null-omitted); `Signal`/`ExtractedSignal`/signal file byte-unchanged.
- [ ] Authoritative/failure seam with the `AnalysisFailed` debug outcome: malformed ⇒ not cached, retried.
- [ ] Family selection preserves today's winner (retyped) — the record-revenue + cuts-guidance fixture is
      pinned; no type precedence anywhere.
- [ ] Extractor repartitioned under the guidance-action wording rule; ambiguous phrases resolved
      explicitly; `RuleSetVersion` bumped; ALL pins updated once; operator acknowledge documented.
- [ ] `contract=earnings-read-v2` appended and pinned; `CurrentCacheVersion` bumped; no reprocessing.
- [ ] `baseline-earnings-only-v2` added; old series intact.
- [ ] Corroboration floor counts the family once.
- [ ] AD-16 handled per the rule above; ledger updated.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
