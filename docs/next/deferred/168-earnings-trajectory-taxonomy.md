# Task: Signal-type taxonomy — `EarningsTrajectory` + structured `GuidanceAction`; reserve `GuidanceChange` for explicit guidance actions

> ⚠️ **DEFERRED — do not dispatch before the un-defer gate at the bottom is met.** This spec bumps
> `KeywordSignalExtractor.RuleSetVersion` AND adds an analyzer-contract version to the AI descriptor, so
> **BOTH the AI-OFF and AI-ON fingerprint pins move and ALL strategies trip `StrategyIdentityGuard`** — not
> just the AI-ON subset. It also re-keys a control strategy and starts a new filing-read cache epoch, and it
> interacts with the AD-16 primary-screen boundary. Implementing it mid-accrual would reset exactly the
> series the current comparison window exists to rank. Documented now; implemented later.
>
> **Revised 2026-08-01 after maintainer review** (5×P1 + 2×P2): direction now derives from a structured
> `GuidanceAction` (the tri-state `Basis` could emit a semantically INVERTED `GuidanceChange (Positive)` on
> an Improving-results-plus-guidance-cut print); one-signal-per-filing is secured via an earnings-signal
> FAMILY through suppression AND supersede; the cache-epoch mechanism is corrected to a
> `CurrentCacheVersion` bump (no re-reads occur — the directional source only ever sees new evidence); the
> extractor repartition and its `RuleSetVersion`/all-pins consequence is stated; and the AD-16 boundary
> amendment is now part of the spec.

## Overview

Fix the spec-75 taxonomy misnomer at its root. Today `ChatFilingAnalyzer` is asked to classify the business
trajectory **as reported** — it is never asked about guidance — and `DirectionalFilingSignalSource`
hardcodes every passing directional read to `SignalType: "GuidanceChange"` with constant Strength 8.
Measured consequences (2026-08-01, MSEX skeptic review + spec-162 corpus): all 145 calibration directional
reads carry the token while 49 rationales mention neither guidance nor outlook, and the weekly report's #2
company was labelled with a guidance change it never issued. The label is false; the underlying trajectory
read is legitimate.

After this spec:

- The trajectory concept is named what it is: **`EarningsTrajectory`**.
- The analyzer read carries a **structured `GuidanceAction`**: `Raised` | `Cut` | `Withdrawn` |
  `Introduced` | `Reaffirmed` | `None` — the explicit guidance event stated in the release, if any.
- The literal **`GuidanceChange` signal type is reserved** for `Raised`/`Cut`/`Withdrawn`, and **its
  direction derives from the ACTION, never from the overall trajectory** (see the mapping table — this is
  the review's central correction).
- Still **exactly one scored earnings signal per filing**, secured structurally by an earnings-signal
  FAMILY through both the collection-time suppression and the scoring-time supersede.

## Assignment

Worktree: any
Dependencies: spec 167 (display relabel) merged; un-defer gate met.
Estimated time: ~3 hours.

## Changes

### 1. Analyzer contract — `GuidanceAction`, and an explicit authoritative/failure seam

- The typed analyzer result gains a required `GuidanceAction` field with the closed set
  `Raised` / `Cut` / `Withdrawn` / `Introduced` / `Reaffirmed` / `None`. The system instruction defines it:
  an action requires an EXPLICIT statement about forward guidance (raised, cut, withdrawn, first-time
  introduced, or reaffirmed); outlook commentary, vision statements, and "we remain confident" language are
  `None`. The overall trajectory classification (Improving/Deteriorating/Mixed/Unknown) is unchanged.
- **Explicit authoritative/failure state (review P2).** Today malformed analyzer output degrades to
  `FilingSentiment.Unknown`, which the source cannot distinguish from a legitimate authoritative Unknown and
  therefore caches as no-signal. The successor result type must carry an explicit failure/authoritative
  marker (e.g. `IsAuthoritative`): a malformed or missing `GuidanceAction`/direction is a FAILED read —
  never cached, retried on a later run — while a genuine low-confidence/Unknown read remains cacheable
  no-signal exactly as today.

### 2. Signal typing — the mapping table (direction from the ACTION, never the trajectory)

| `GuidanceAction` | Emitted type | Direction | Note |
|---|---|---|---|
| `Raised` | `GuidanceChange` | Positive | from the action |
| `Cut` | `GuidanceChange` | Negative | from the action — **even when the overall trajectory read is Improving** |
| `Withdrawn` | `GuidanceChange` | Negative | withdrawal is adverse; no prior-range ambiguity applies |
| `Introduced` | `EarningsTrajectory` | trajectory direction | first-time guidance has no prior range — not inherently directional; the action is recorded in metadata |
| `Reaffirmed` | `EarningsTrajectory` | trajectory direction | reaffirmation is not a change; recorded in metadata |
| `None` | `EarningsTrajectory` | trajectory direction | the common case |

- One emitted signal per filing, chosen by this table; the same confidence gate and the spec-160 cap apply
  identically to both types (cap before gate, unchanged).
- The rationale for a `GuidanceChange` row must name the guidance action; conflict prints (strong results +
  cut) are the explicitly tested case — the pre-168 contract would have scored that filing Mixed/no-signal,
  the post-168 contract surfaces the cut as `GuidanceChange (Negative)`. Both behaviours are defensible;
  the new one is chosen because an explicit guidance action is the more decision-relevant fact and must
  never be emitted with an inverted sign.
- The action lands in signal/cache metadata (additive) so provenance shows what grounded the type.

### 3. The earnings-signal FAMILY — one signal per filing, secured through the pipeline (review P1)

- Today's guarantee is type-literal on BOTH sides: `CollectionPass` suppresses the deterministic signal only
  when both sides are `GuidanceChange`, and `GuidanceChangeSupersede` supersedes only that type. Once the AI
  can emit either type, a filing could retain a deterministic Neutral `EarningsTrajectory` AND a directional
  AI `GuidanceChange` — both reaching scoring.
- Define ONE constant family `EarningsSignalTypes = { GuidanceChange, EarningsTrajectory }` (single
  definition, Application layer) and re-key BOTH mechanisms on family membership: collection-time
  suppression (`CollectionPass`) and scoring-time supersede (rename `GuidanceChangeSupersede` →
  `EarningsSignalSupersede`, updating its tests). Asserted: for any combination of deterministic and AI
  earnings signals on one filing, exactly one survives to scoring.
- The deterministic spec-57 Neutral 8-K marker moves to `EarningsTrajectory` (it marks an earnings FILING,
  not a guidance event — and per spec 167's review, it is a filing marker, not a trajectory read; keep its
  existing Neutral direction and weights).

### 4. Extractor repartition — this is what moves the AI-OFF pins (review P1)

- `KeywordSignalExtractor`'s phrase table currently types BOTH real guidance phrases ("raises guidance",
  "cuts outlook", …) AND results phrases ("record revenue", "beats expectations", "exceeded outlook",
  "above the high end") as `GuidanceChange`. Repartition: explicit guidance-action phrases keep
  `GuidanceChange`; results phrases and the Neutral "results of operations" marker move to
  `EarningsTrajectory`. Magnitudes unchanged — this is a rule-STRUCTURE change.
- **Therefore `KeywordSignalExtractor.RuleSetVersion` is bumped**, which re-stamps every strategy: the
  AI-OFF and AI-ON pins BOTH move, once, with a lineage note in `ScoringConfigFingerprintTests` at every
  window (30d/60d/120d).
- **Operator acknowledge step (spec-160 precedent), stated in the PR and here:** on first post-merge run
  `StrategyIdentityGuard` trips for ALL strategies; acknowledge by deleting the per-name records under
  `data/scoring-configs/strategies/` and letting the next run re-record them.

### 5. AI descriptor — add the missing contract version (review P1)

- The `directional-filing:` descriptor currently carries `str/nov/minconf/model/cmpscan/cmpcap` and **no
  analyzer-contract identity** — a changed contract would re-stamp nothing on its own. Append
  `contract=earnings-read-v2` (new fields LAST, per the spec-119/160 precedent, so the existing prefix stays
  byte-stable), and pin it: perturbing the contract version must move the AI-ON fingerprint.

### 6. Cache epoch — corrected mechanism (review P1)

- **No re-reads occur and none should be claimed.** The directional source receives only NEW evidence from
  the collection pass; durable filings never re-enter because their cache is stale. The established
  mechanism for a material contract change is bumping `AnalyzedFilingRecord.CurrentCacheVersion` — do that,
  so any pre-168 record is a structural MISS if its accession is ever presented again, while nothing is
  proactively reprocessed.
- **Legacy signals stand and age out of the scoring window** (append-only, AD-8). Reprocessing accrued
  filings under the new contract would be a separately-specced backfill/correction path — recorded, not
  built, and the standing never-retro-heal rule applies.

### 7. Strategy re-key

- `baseline-earnings-only` declares `SignalTypes: [GuidanceChange]`, which is fingerprint-folded and now
  means something narrower (explicit guidance actions only). Its hypothesis is "earnings-read signals
  only", so add **`baseline-earnings-only-v2`** with `SignalTypes: [EarningsTrajectory, GuidanceChange]`
  under the new name (spec 141 immutable-by-convention); the old name's series stops accruing and stays
  intact.

### 8. Report and policy

- Spec 167's display mapping becomes an identity mapping for the new member; the legend line stays (it
  correctly describes the historical token on accrued rows).
- **Corroboration floor: the two earnings types are ONE axis.** `WeeklyReportActionPolicyV1`'s
  distinct-positive-types count must treat the family as a single type — two signals from one reader
  contract must not self-corroborate. Decided here, tested explicitly.

## Tests

- Mapping table: one fixture per row, including the conflict print (Improving + `Cut` ⇒
  `GuidanceChange (Negative)`) and the inversion guard (no combination of trajectory and action can emit
  `GuidanceChange` whose direction contradicts the action).
- Family: deterministic + AI earnings signals on one filing ⇒ exactly one survives suppression AND
  supersede, for every type pairing.
- Authoritative seam: malformed output ⇒ FAILED read, not cached, retried; authoritative Unknown ⇒ cached
  no-signal (both pinned).
- Contract guard: system-instruction text pinned; `contract=` segment present, appended last, and
  fingerprint-moving when perturbed.
- Cache: pre-168 `CurrentCacheVersion` record is a MISS; post-168 round-trips.
- Identity: AI-OFF and AI-ON pins move once each per window with lineage notes; `baseline-earnings-only-v2`
  stamps a new fingerprint; the reflection guard still passes.
- Policy: corroboration floor counts the family once.

## Constraints

- One reader invocation per filing (no second AI call for the action).
- Provider isolation (AD-5), structured-output validation before persistence, append-only stores, and the
  advice-language ban all hold.
- No change to cmpscan (spec 160) semantics; the cap applies identically to both types.
- Constant Strength 8 untouched — materiality encoding needs its own validation pass first (spec 162);
  record, don't build.

## AD-16 boundary amendment (part of this spec, review P1)

Deploying 168 makes legacy-taxonomy and new-taxonomy signals coexist in the scoring window for ~60 days.
Therefore, whichever comes first:

- If 168 merges BEFORE the first binding AD-16 primary screen has run: amend the AD-16 boundary to the
  **later of its existing date and the first post-168 baseline run + 60 days**, in the same change, with the
  ledger updated.
- Otherwise (first binding screen already ran): no boundary move, but the screen's next reading must state
  the taxonomy epoch beside it.

## Un-defer gate (ALL must hold before dispatch)

1. The current strategy-comparison window has produced its first real multi-strategy ranking (v9 arms and
   baselines present on the leaderboard), OR the maintainer explicitly waives waiting.
2. Spec 167 is merged (so the report is honest in the interim).
3. The AD-16 consequence above has been explicitly chosen by the maintainer: either accept the boundary
   move, or hold 168 until after the first binding screen.
4. Maintainer sign-off recorded in this file (replace this line with the date + decision).

## Acceptance criteria

- [ ] `GuidanceAction` closed set; explicit authoritative/failure seam (malformed ⇒ not cached, retried).
- [ ] Mapping table implemented exactly; direction always derives from the action for `GuidanceChange`;
      inversion guard asserted.
- [ ] Earnings-signal family defined once and enforced through suppression AND supersede; exactly one
      earnings signal per filing (asserted for every pairing).
- [ ] Extractor repartitioned; `RuleSetVersion` bumped; ALL pins updated once with lineage notes; operator
      acknowledge step documented in the PR.
- [ ] `contract=earnings-read-v2` appended to the descriptor and pinned fingerprint-moving.
- [ ] `CurrentCacheVersion` bumped; no proactive reprocessing; legacy signals untouched.
- [ ] `baseline-earnings-only-v2` added under a new name; old series intact.
- [ ] Corroboration floor treats the family as one axis (tested).
- [ ] AD-16 boundary amendment applied per the rule above; ledger updated.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
