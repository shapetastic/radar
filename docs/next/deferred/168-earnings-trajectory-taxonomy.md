# Task: Signal-type taxonomy — `EarningsTrajectory` for AI reads; `GuidanceAction` recorded diagnostically; `GuidanceChange` reserved for strict literal rules

> ⚠️ **DEFERRED — do not dispatch before the un-defer gate at the bottom is met.** This spec bumps
> `KeywordSignalExtractor.RuleSetVersion` AND adds an analyzer-contract version to the AI descriptor, so
> **BOTH the AI-OFF and AI-ON fingerprint pins move and ALL strategies trip `StrategyIdentityGuard`** — not
> just the AI-ON subset. It also re-keys a control strategy, starts a new filing-read cache epoch, and
> interacts with the AD-16 primary-screen boundary. Documented now; implemented later.
>
> **Revision history.** r2 (2026-08-01): 5×P1 review — `GuidanceAction` replaced the tri-state basis, the
> family supersede and cache epoch were specified, fingerprint scope corrected to all-pins. r3 (2026-08-01,
> this version): second review round **NARROWED the scope** — r2's action-derived direction was a new,
> unvalidated scoring behaviour (the spec-162 audit never measured action-classification accuracy), its
> confidence semantics were incoherent (`FilingSentiment.Confidence` describes the trajectory, not the
> action), and the family supersede winner was undefined. This version completes the TAXONOMY repair only:
> **no signal changes direction, strength, or confidence relative to today.** Action-derived
> `GuidanceChange` is a separately-specced promotion, gated on a shadow audit.

## Overview

Fix the spec-75 taxonomy misnomer at its root — without introducing any new scoring behaviour. Today
`ChatFilingAnalyzer` is asked to classify the business trajectory **as reported** (never guidance) and
`DirectionalFilingSignalSource` hardcodes every passing read to `SignalType: "GuidanceChange"`. Measured:
all 145 spec-162 calibration directional reads carry the token while 49 rationales mention neither guidance
nor outlook; the weekly report's #2 company (MSEX) was labelled with a guidance change it never issued.

After this spec:

1. **Every AI directional read emits `EarningsTrajectory`** — same direction, same confidence, same gate,
   same spec-160 cap as today. The only change is the honest type name.
2. **`GuidanceAction` is recorded DIAGNOSTICALLY**: the analyzer additionally classifies the explicit
   guidance event (`Raised` | `Cut` | `Withdrawn` | `Introduced` | `Reaffirmed` | `None`), persisted on the
   signal and the cache record, **consumed by nothing that scores**. It exists to be audited.
3. **Literal `GuidanceChange` survives only in the deterministic extractor**, for phrases whose wording
   states an actual guidance/outlook action.
4. **Exactly one scored earnings signal per filing**, secured by an earnings-signal FAMILY with a
   predeclared total order through both suppression and supersede.

## Assignment

Worktree: any
Dependencies: spec 167 (display relabel) merged; un-defer gate met.
Estimated time: ~2–3 hours.

## Changes

### 1. Analyzer contract — diagnostic `GuidanceAction` + an explicit authoritative/failure seam

- The typed analyzer result gains a required `GuidanceAction` field (closed set above). The system
  instruction defines it: an action requires an EXPLICIT statement about forward guidance; outlook
  commentary, vision statements, and "we remain confident" language are `None`. The trajectory
  classification and its confidence are UNCHANGED — `FilingSentiment.Confidence` continues to describe the
  trajectory read, which is the only thing scored, so its semantics stay coherent (review r3-P1-2).
- **Explicit authoritative/failure state (review r2-P2).** Today malformed analyzer output degrades to
  `FilingSentiment.Unknown`, indistinguishable from a legitimate authoritative Unknown, and is cached as
  no-signal. The successor result carries an explicit marker (e.g. `IsAuthoritative`): malformed or missing
  fields ⇒ FAILED read — never cached, retried later; a genuine low-confidence/Unknown read remains
  cacheable no-signal exactly as today.

### 2. Signal typing — rename only, no behaviour change

- `DirectionalFilingSignalSource` emits **`EarningsTrajectory`** with the trajectory direction and
  confidence — byte-equivalent decision-making to today; only the type token differs. `SignalType` (Domain)
  gains the `EarningsTrajectory` member.
- The deterministic spec-57 Neutral 8-K marker moves to `EarningsTrajectory` (it marks an earnings FILING).
- **`GuidanceAction` changes NOTHING scored.** No mapping table, no action-derived direction, no
  `GuidanceActionConfidence`. The conflict print (Improving results + guidance cut) scores exactly as
  today (the prompt already forces Mixed ⇒ typically below the gate ⇒ no signal). Surfacing cuts as
  first-class signals is the promotion spec's business (below), if the shadow audit earns it.

### 3. Diagnostic persistence — named properties, not a metadata bag (review r3-P2)

- `Signal`, `ExtractedSignal`, and the `FileSignalStore` persisted record have NO metadata field — the spec
  names the real shape: a **trailing, nullable `GuidanceAction` property** (`string?`, the enum token) on
  all three, omitted from the persisted JSON when null (the `summary` precedent — existing files stay
  byte-identical). Legacy null semantics: "not recorded (pre-168)", never defaulted. Round-trip test
  required (value present, value null, legacy file without the property).
- `AnalyzedFilingRecord` gains the same trailing nullable field, under the `CurrentCacheVersion` bump below.

### 4. The earnings-signal FAMILY — one winner, predeclared total order (review r3-P1-3)

- `EarningsSignalTypes = { GuidanceChange, EarningsTrajectory }`, defined ONCE (Application layer); both the
  `CollectionPass` suppression and the supersede (rename `GuidanceChangeSupersede` →
  `EarningsSignalSupersede`) key on family membership.
- Today's `Beats` only orders directional-over-neutral, then time/ID — with two family types that no longer
  picks a unique winner. **Predeclared total order** (first difference wins):
  1. Directional beats Neutral (unchanged).
  2. Among directional: **`GuidanceChange` beats `EarningsTrajectory`** — explicit guidance wording is the
     more specific fact. Legacy `GuidanceChange` rows without action metadata rank as `GuidanceChange` by
     their token; no metadata inspection is needed.
  3. The existing time/ID tiebreak (unchanged, order-independent).
- Asserted: for every pairing of deterministic and AI earnings signals on one filing — including
  `GuidanceChange (Negative)` vs `EarningsTrajectory (Positive)` — exactly one survives, and which one is
  pinned by test, not left to input order.

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
  against a labeled corpus using the diagnostically-persisted values; no live signals.
- **Promotion spec**, only if the audit supports it: action-derived `GuidanceChange` direction, a separate
  `GuidanceActionConfidence`, and the open cap-policy question (should the spec-160 comparability cap weaken
  a genuine guidance cut merely because the reported quarter contains one-offs? — arguably not, but that is
  a measured decision for that spec, not this one).

## Tests

- Rename-only guard: for a fixture corpus, post-168 emitted signals differ from pre-168 ONLY in the type
  token (direction, strength, confidence, gate and cap outcomes byte-equivalent).
- Diagnostic field: round-trips (present / null / legacy file); consumed by nothing in `Scoring/` (guarded
  by reflection or compile-scope assertion).
- Authoritative seam: malformed ⇒ FAILED, not cached, retried; authoritative Unknown ⇒ cached no-signal.
- Family total order: every directional/neutral × type pairing pinned to its predeclared winner,
  order-independent.
- Extractor: each repartitioned phrase pinned; the tightened "raises full-year" decision pinned; the
  guidance-action wording rule asserted against the final table (no `GuidanceChange` phrase without
  guidance/outlook wording).
- Identity: AI-OFF and AI-ON pins move once each per window; `contract=` segment pinned fingerprint-moving;
  `baseline-earnings-only-v2` stamps fresh; reflection guard passes.
- Cache: pre-168 record is a MISS; post-168 round-trips.
- Policy: corroboration floor counts the family once.

## Constraints

- One reader invocation per filing. Provider isolation (AD-5), structured-output validation before
  persistence, append-only stores, and the advice-language ban all hold.
- No change to cmpscan semantics; the cap applies to the trajectory confidence exactly as today.
- Constant Strength 8 untouched (spec 162: materiality encoding needs its own validation pass).
- **No signal's scored direction, strength, or confidence changes.** That is the r3 review's core
  constraint and the rename-only guard test enforces it.

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

- [ ] Every AI read emits `EarningsTrajectory`; rename-only guard passes (no scored field changes).
- [ ] `GuidanceAction` diagnostic-only: named trailing nullable property on `Signal` / `ExtractedSignal` /
      signal file / cache record; round-trips; consumed by nothing that scores.
- [ ] Authoritative/failure seam: malformed ⇒ not cached, retried.
- [ ] Family + predeclared total order enforced through suppression AND supersede; every pairing pinned.
- [ ] Extractor repartitioned under the guidance-action wording rule; ambiguous phrases resolved
      explicitly; `RuleSetVersion` bumped; ALL pins updated once; operator acknowledge documented.
- [ ] `contract=earnings-read-v2` appended and pinned; `CurrentCacheVersion` bumped; no reprocessing.
- [ ] `baseline-earnings-only-v2` added; old series intact.
- [ ] Corroboration floor counts the family once.
- [ ] AD-16 handled per the rule above; ledger updated.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
