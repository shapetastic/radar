# Task: `radar-formula-v3` — retune the attention saturation + under-the-radar discount so Attention separates again

> **VERSION + APPROVAL (read first).** This is a **scoring formula-math change** driven by live data, so it
> is a legitimate **AD-6 amendment** via the sanctioned mechanism ("*to change the formula, bump `Version`
> and update this entry*") — exactly how `radar-formula-v1 → v2` was done (spec 58). It ships a new
> `IScoreFormula` identity **`radar-formula-v3`**, deletes/supersedes `RadarScoreFormulaV2` (spec-impl
> checklist: delete deprecated code, port tests), amends the AD-6 ledger, updates `ScoringVersion`
> composition automatically (via `_formula.Version`), and bumps `ScoringEngine.ScoringConfigVersion`
> (AD-10) to the next integer. The proposed constants below are **maintainer co-designed like v1/v2**.
> **MAINTAINER SIGN-OFF: GRANTED 2026-07-04** — the maintainer reviewed the exact constants (half-saturation
> 5→12, media reach weight 0.5→0.25, opportunity discount divisor 200→250) and the 8-company before/after
> Opportunity table in this session and approved proceeding with these values. Record the AD-6 `radar-formula-v3`
> subsection **Accepted · 2026-07-04** (not Proposed). This is a **directed** slice, **NOT** the generic
> planner loop, and **NOT** architecture-gated.

## Overview

Two live runs on 2026-07-04 (the 8-company watch universe, after spec 84 made attention breadth real by
mapping `SourceName` to the actual publisher) exposed that **`AttentionScore` no longer discriminates**.
The v2 formula is `AttentionScore = 100·reach/(reach+5)`, `reach = distinctThirdPartySourceNames +
0.5·mediaSignals`, and `OpportunityScore = Trajectory·(EvidenceConfidence/100)·(1 − Attention/200)` — the
"under-the-radar" discount. The live Attention values were:

| MRCY | HLIO | AGYS | ERII | AEHR | CYRX | EOSE | SPNS |
|---|---|---|---|---|---|---|---|
| 81 | 81 | 82 | 85 | 81 | 78 | 76 | 29 |

**The problem.** Seven of the eight companies cluster tightly at **76–85**; only the thinly-covered SPNS
(~4 articles) sits low. The `+5` half-saturation constant is so small that any ticker with normal coverage
(reach ≈ 16–28) **saturates near the ceiling**, so Attention stops distinguishing "widely-noticed" from
"under-the-radar". Consequently the `(1 − Attention/200)` discount haircuts a near-uniform **38–43%** off
almost everyone (only SPNS escapes at ~15%), which:

- **compresses the whole board** (the quality cluster jams at Opportunity ~40), and
- **penalises the most-covered quality names** rather than surfacing genuinely under-followed ones — MRCY
  slid Investigate→Watch (Opp 46→40→41) and AGYS fell to Ignore purely because good coverage inflated its
  Attention into the flat top of the saturation curve.

The discount is both **over-tuned** (too steep) and fed by an Attention signal that **saturates too easily**
to be a useful discriminator.

**Deeper root (evaluated; deliberately NOT fully fixed here).** The distinct "publishers" driving reach are
dominated by algorithmic finance-content mills every ticker gets (MarketBeat, Zacks, Simplywall.st,
StockStory, Moomoo, TradingView, Stock Titan, GuruFocus, …). Counting these as "market attention" measures
**media-noise breadth**, not genuine institutional notice — which is *why* everyone saturates — and one event
spawning many near-duplicate articles inflates `mediaSignals`. The principled fix (source-quality tiering
that down-weights content mills) is a list-maintenance-heavy effort and is **deferred to its own slice** (see
Out of scope). This slice does the smaller, evidence-based **recalibration**: make Attention spread again and
soften the discount, while **preserving the under-the-radar principle** (low attention still raises
Opportunity; high attention still discounts it, just proportionately).

---

## Assignment

Worktree: any
Dependencies: existing trunk (`radar-formula-v2`, spec 58; news-attention breadth by publisher, spec 84;
`ScoringConfigVersion` stamp + AD-10, specs 69/70; current tree is `radar-scoring-config-v8` after spec 86).
Conflicts with: touches the formula (`RadarScoreFormulaV2.cs` → `RadarScoreFormulaV3.cs`), its DI
registration (`InfrastructureServiceCollectionExtensions.cs`), its tests, `ScoringEngine.cs`
(`ScoringConfigVersion` constant + comment), and the AD-6 ledger entry. Must **NOT** run in parallel with
any scoring / formula / engine / extractor / directional-filing slice — sequence it.
Estimated time: ~2–2.5 h (a versioned, maintainer-co-designed formula — the highest-care class of slice).

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **v2 formula (exact constants).** `RadarScoreFormulaV2.cs`: `AttentionHalfSaturation = 5.0`,
  `MediaReachWeight = 0.5`; `AttentionScore = Score(100·reach/(reach+AttentionHalfSaturation))` with
  `reach = distinctThirdPartySources + MediaReachWeight·mediaCount` (lines 33–34, 139–150).
  `OpportunityAttentionDivisor = 200.0`; `OpportunityScore = Score(Trajectory·(EvidenceConfidence/100)·(1 −
  AttentionScore/OpportunityAttentionDivisor))` (lines 54–55, 171–175). `distinctThirdPartySources` counts
  distinct `Evidence.SourceName` among `EvidenceSourceTypes.IsThirdPartyAttentionSource` types
  (`NewsArticle`/`SocialMedia`/`ConferenceMention`); `mediaCount` counts `SignalType.MediaAttention` signals.
- **What v3 changes and what it does NOT.** ONLY `AttentionScore` (the saturation constant and the media
  term) and `OpportunityScore` (the discount shape) change. **Trajectory, EvidenceConfidence,
  SignalVelocity, the recency weighting, the empty-window behaviour, the `PreviousSignals`/window/provenance
  rules, contributions (one per current-window signal, in order), and every clamp/round are byte-for-byte
  unchanged** from v2 (AD-6). `Version => "radar-formula-v3"`.
- **`ScoringVersion` composition is automatic.** `ScoringEngine` builds
  `scoringVersion = $"{EngineVersion}+{_formula.Version}"` (`ScoringEngine.cs:138`), so registering
  `RadarScoreFormulaV3` makes snapshots record `mvp-engine-v1+radar-formula-v3` with **no** `EngineVersion`
  edit. `EngineVersion` stays `"mvp-engine-v1"`.
- **`ScoringConfigVersion` is a code constant.** `ScoringEngine.cs:38` currently reads
  `private const string ScoringConfigVersion = "radar-scoring-config-v8";` and stamps every snapshot at
  line 159. This formula-math change moves scoring output → per AD-10 bump to the **next integer**
  (order-robust: read the current value and `+1`; expected `v8 → v9`).
- **DI registration.** `InfrastructureServiceCollectionExtensions.cs:66` reads
  `services.TryAddSingleton<IScoreFormula, RadarScoreFormulaV2>();` — repoint at `RadarScoreFormulaV3`.
- **Tests to update.** `ScoringEngineTests.Versioning_StampsScoringConfigVersion` asserts
  `"radar-scoring-config-v8"` (line 331) — bump to the new value.
  `ScoringEngineTests.DirectionalGuidanceChange_…` news up `new RadarScoreFormulaV2()` (line 342) — repoint
  to V3. `RadarScoreFormulaV2Tests.cs` is ported wholesale to `RadarScoreFormulaV3Tests.cs`. Search the tree
  for any other `RadarScoreFormulaV2` / `radar-formula-v2` reference and update.
- **Provenance/determinism unaffected.** The formula stays pure (no clock/IO/randomness); contributions and
  `ScoreEvidenceLink` construction in `ScoringEngine` are untouched; existing on-disk snapshots keep their
  recorded `ScoringVersion` (`…+radar-formula-v2`) and remain reproducible under it — only the live formula
  advances to v3.

---

## The v3 recalibration (precise)

Implement `RadarScoreFormulaV3 : IScoreFormula`, `Version => "radar-formula-v3"`. Copy v2 verbatim and change
**only** the three constants below (and the two component expressions that read them). Every other component,
clamp, contribution, and the explanation shape stay identical (the explanation prefix becomes
`radar-formula-v3`).

### 1. Attention saturation — raise the half-saturation so normal coverage stops maxing out

- `AttentionHalfSaturation`: **`5.0 → 12.0`**. `AttentionScore = 100·reach/(reach+12)`.
  Rationale: at the live reach values (≈16–28 for the covered cluster) the old `+5` puts every ticker on the
  flat top of the curve (Att 76–85). `+12` moves the half-saturation point out so the covered cluster lands
  at **57–70** and the thinly-covered SPNS (reach ≈ 2) lands at **15** — restoring a real 42–55-point spread
  between under-followed and widely-covered names. It keeps the same saturating *shape* (still asymptotic to
  100, still monotone in reach), just with a gentler slope.
- `MediaReachWeight`: **`0.5 → 0.25`**. Halve the raw-`mediaSignals` contribution to reach. Rationale: one
  event routinely spawns many near-duplicate articles, so raw media volume is duplication-prone and a poor
  proxy for genuine breadth; distinct-publisher breadth (`distinctThirdPartySources`, unchanged) is the
  cleaner signal. This dampens raw-count inflation without dropping the term entirely (a media collector with
  no distinct-publisher mapping still contributes *something*).
  > **Design note (evaluated).** Dropping the media term to 0 entirely was considered — distinct-publisher
  > breadth alone is arguably the honest signal. It is **not** taken this round because
  > `SignalType.MediaAttention` is the only reach contribution when a source lacks a distinct publisher name,
  > and zeroing it is a larger behavioural change than a recalibration; `0.25` is the conservative dampen.
  > Fully retiring the raw-count term (and the news-volume multiplicity dedup) is noted Out of scope.

### 2. Under-the-radar discount — soften the slope so it stops crushing covered quality names

- `OpportunityAttentionDivisor`: **`200.0 → 250.0`**. `OpportunityScore = Trajectory·(EC/100)·(1 −
  Attention/250)`. Rationale: at `/200`, Attention 80 costs a **40%** haircut — applied near-uniformly across
  the saturated cluster, which is what compressed the board and demoted MRCY/AGYS. Combined with the raised
  saturation (which already lowers the cluster's Attention to 57–70), `/250` softens the haircut on the
  covered cluster to **~24–28%** while a genuinely under-followed name (SPNS, Att 15) keeps ~94% of its base.
  The **principle is preserved**: Opportunity still falls monotonically as Attention rises, still never
  zeroes (max haircut at Attention 100 is `100/250 = 40%`, still leaving 60%), and low attention still yields
  a strictly larger multiplier than high attention.

> Keep the same multiplicative Opportunity **shape** and the "never zeroes" guarantee (AD-6). This is a
> re-tuning of two constants + one divisor, not a new formula family. A floor/cap or nonlinear discount shape
> was evaluated and rejected as heavier than the data warrants — raising the saturation point already does
> most of the de-compression, and the gentler divisor finishes it.

### Unchanged (byte-for-byte from v2)

- **TrajectoryScore** — weighted mean of directional strength over only Positive/Negative signals (AD-6 v2).
- **EvidenceConfidenceScore** — best-anchored + saturating diversity bonus (AD-6 v2).
- **SignalVelocityScore** — `50·(now+10)/(prev+10)`.
- Recency weighting, empty-window behaviour, `PreviousSignals`/window/provenance/contribution rules, clamps.

---

## Before/after Opportunity table (the 8-company universe — this is the evidence base)

Derived from the live Attention values above. `reach_v2` is back-solved from `Att = 100·reach/(reach+5)`;
`Att_v3` applies the new `+12` saturation to the same reach (the covered cluster is publisher-breadth
dominated, so the `MediaReachWeight` change moves it only slightly — shown conservatively as unchanged
reach). `disc = 1 − Att/divisor`. `Opp_v2` is the observed/inferred live Opportunity (top cluster ~40,
SPNS ~44); `Opp_v3 = Opp_v2·(disc_v3/disc_v2)` since only the discount multiplier changes for a fixed
Trajectory·EC base.

| Company | reach | Att v2 | Att v3 | disc v2 (÷200) | disc v3 (÷250) | Opp v2 | **Opp v3** |
|---|---:|---:|---:|---:|---:|---:|---:|
| MRCY | 21.3 | 81 | 64 | 0.595 | 0.744 | 41 | **51** |
| HLIO | 21.3 | 81 | 64 | 0.595 | 0.744 | 40 | **50** |
| AGYS | 22.8 | 82 | 65 | 0.590 | 0.738 | 40 | **50** |
| ERII | 28.3 | 85 | 70 | 0.575 | 0.719 | 40 | **50** |
| AEHR | 21.3 | 81 | 64 | 0.595 | 0.744 | 40 | **50** |
| CYRX | 17.7 | 78 | 60 | 0.610 | 0.761 | 41 | **51** |
| EOSE | 15.8 | 76 | 57 | 0.620 | 0.772 | 40 | **50** |
| SPNS | 2.0 | 29 | 15 | 0.855 | 0.942 | 44 | **48** |

**Reading the recalibration.**
- **Attention spreads again**: the covered cluster moves from a jammed **76–85** to **57–70**, and SPNS to
  **15** — a genuine ~42–55-point gap between under-followed and widely-covered (vs ~47 collapsed to noise
  before). Attention is a discriminator once more.
- **The board de-compresses upward for quality names**: MRCY/CYRX recover to **51** (out of the Watch trough,
  back toward Investigate) and the covered cluster lifts to ~**50** instead of being crushed to ~40 — the
  most-covered quality names are no longer penalised for having good coverage.
- **The under-the-radar principle holds**: SPNS (genuinely thin coverage) *still* carries the smallest
  haircut (~6% vs ~26% for the cluster) — low attention still earns the biggest opportunity uplift; a
  saturated-attention name is now *modestly* discounted, not halved.

> Exact `Opp_v3` values depend on each company's Trajectory·EC base, which the live snapshots pin; the table
> shows the discount recalibration's effect. **Flag for maintainer sign-off:** the three constants
> (`+12`, `0.25`, `÷250`) are the proposed calibration — confirm before merge (mark the AD-6 v3 subsection
> Proposed until then).

---

## Version, DI, and ledger

- New `RadarScoreFormulaV3 : IScoreFormula`, `Version => "radar-formula-v3"`. Per the spec-impl checklist,
  **delete `RadarScoreFormulaV2`** (do not leave it dormant) and **port its tests** to
  `RadarScoreFormulaV3Tests`. On-disk snapshots keep their recorded `ScoringVersion` string — that is
  provenance and does not require the V2 code to remain.
- Repoint DI: `InfrastructureServiceCollectionExtensions.cs:66`
  `TryAddSingleton<IScoreFormula, RadarScoreFormulaV2>()` → `RadarScoreFormulaV3`.
- `ScoringVersion` composition updates automatically to `mvp-engine-v1+radar-formula-v3` via
  `_formula.Version` — **no** `EngineVersion` edit.
- **Bump `ScoringEngine.ScoringConfigVersion`** to the next integer (order-robust: current `+1`; expected
  `radar-scoring-config-v8 → radar-scoring-config-v9`), and update the constant's comment to record this
  generation: "*`radar-formula-v3` attention-discount retune — raised Attention half-saturation 5→12 and
  down-weighted the raw media term 0.5→0.25 so Attention separates under-followed from widely-covered again,
  and softened the under-the-radar discount divisor 200→250 so it stops uniformly crushing covered quality
  names. Formula-math change (Attention + Opportunity), so `ScoringVersion` advances via `_formula.Version`
  and per AD-10 the stamp bumps.*"
- **Amend `docs/architecture-decisions.md` AD-6** with a new subsection **`Refinement — radar-formula-v3
  (spec 87): re-tune attention saturation and the under-the-radar discount`**, mirroring the v2 refinement
  subsection: state the live finding (Attention saturating at 76–85, uniform ~40% haircut compressing the
  board and penalising covered quality names), the exact three constant changes and their rationale, that
  **only Attention and Opportunity change** (Trajectory/EC/Velocity/window/provenance unchanged), and that
  the principle (low attention → higher opportunity) is preserved. Update the AD-6 **Status** line to add
  `Refined · 2026-07-04 (spec 87, radar-formula-v3 — Proposed until maintainer sign-off)`, and mark the v1/v2
  Attention+Opportunity component formulas as *superseded by radar-formula-v3* for those two components.
  **Mark the v3 subsection `Proposed` until the maintainer approves the exact constants** (as AD-11 was
  Proposed→Accepted); the coder ships the constants as proposed.

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  RadarScoreFormulaV3.cs           # ADD: port of V2; ONLY AttentionHalfSaturation 5->12,
                                   #   MediaReachWeight 0.5->0.25, OpportunityAttentionDivisor 200->250;
                                   #   Version => "radar-formula-v3"; explanation prefix "radar-formula-v3".
  RadarScoreFormulaV2.cs           # DELETE (spec-impl checklist: no dormant deprecated code).
  ScoringEngine.cs                 # MODIFIED: bump ScoringConfigVersion to next integer (v8 -> v9) +
                                   #   update the comment to record this generation. No EngineVersion change.

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: IScoreFormula -> RadarScoreFormulaV3.

tests/Radar.Application.Tests/Scoring/
  RadarScoreFormulaV3Tests.cs      # ADD: port of RadarScoreFormulaV2Tests with recomputed expectations +
                                   #   new saturation/discount pins (see Tests).
  RadarScoreFormulaV2Tests.cs      # DELETE (ported).
  ScoringEngineTests.cs            # MODIFIED: ScoringConfigVersion assertion v8 -> v9; repoint the
                                   #   new RadarScoreFormulaV2() usages to RadarScoreFormulaV3.

docs/architecture-decisions.md     # MODIFIED: AD-6 v3 refinement subsection (Proposed) + Status line.
```

No `Radar.Domain` change (no enum/record). No collector, report-renderer, or DI-shape change. No new package.
No provider SDK. No DB (AD-8, files-first).

---

## Implementation details

### `RadarScoreFormulaV3` (Application)

- Copy `RadarScoreFormulaV2` verbatim, rename the class to `RadarScoreFormulaV3`, and change **only**:
  - `AttentionHalfSaturation = 5.0` → `12.0` (update the constant's comment to note the raised
    half-saturation point and why: normal coverage was saturating at 76–85).
  - `MediaReachWeight = 0.5` → `0.25` (comment: raw media volume is duplication-prone; lean on
    distinct-publisher breadth).
  - `OpportunityAttentionDivisor = 200.0` → `250.0` (comment: gentler under-the-radar discount so covered
    quality names are not uniformly crushed; still never zeroes).
  - `Version => "radar-formula-v3"`; the explanation string prefix and the empty-window explanation string
    become `radar-formula-v3:`.
- **Everything else is identical**: Trajectory (Positive/Negative-only weighted mean → `50 + 5·T_raw`),
  EvidenceConfidence (best-anchored + diversity), Velocity, recency, clamps, contributions (one per
  current-window signal in input order, Neutral/Mixed weight 0, never from `PreviousSignals`), the
  `ScoreComponents`/`ScoreComputation` shape, and the `ComponentJson`.
- Pure and deterministic (AD-3): no clock/IO/randomness.

### `ScoringEngine` (Application)

- Bump `ScoringConfigVersion` to the next integer (order-robust: read current `+1`; expected
  `radar-scoring-config-v8` → `radar-scoring-config-v9`) and update the comment as above.
- Do **NOT** touch `EngineVersion` (`"mvp-engine-v1"`); `ScoringVersion` advances automatically through
  `_formula.Version`.

### AD-6 amendment (ledger)

Add the `radar-formula-v3` refinement subsection (Proposed) and update the Status line as described under
**Version, DI, and ledger**. This is the sanctioned formula-change mechanism, not drift.

---

## Tests

Port `RadarScoreFormulaV2Tests` → `RadarScoreFormulaV3Tests` (delete the V2 file), recompute any
value-pinned expectations, and add the recalibration pins. Match the existing test style
(`BuildSignal`/`InputFrom` helpers, `[Fact]`/`Assert`).

Ported/kept green (recompute expected numbers where the changed constants move them):
- **Version:** `Version == "radar-formula-v3"` and it appears in the explanation.
- **Trajectory** — neutral baseline, Positive>50 / Negative<50, neutral-exclusion, only-neutral→50, recency,
  clamp-at-extremes — **unchanged** (Trajectory did not move).
- **EvidenceConfidence** — quality+diversity rewards, monotonic-under-corroboration, best-qual-weight —
  **unchanged**.
- **Velocity** — acceleration>50, deceleration<50, equal=50, empty-previous>50 — **unchanged**.
- **Contributions / Neutral-zero-weight / empty-window / determinism** — **unchanged** in shape.
- **Attention breadth** — distinct-publisher breadth still raises Attention; repeated same publisher does not
  inflate breadth (still `Distinct(SourceName)`); first-party-only → 0; third-party raises it. Recompute the
  absolute values under `+12` where a test pins one; keep the monotonicity/`< 100` assertions.
- **`Opportunity_FallsAsAttentionRises_NeverZeroes`** — still holds under `÷250` (higher attention ⇒
  `Opp ≤` lower attention, and `Opp > 0`). Keep it.

New pins locking the v3 recalibration:
1. **Attention half-saturation raised (de-compression).** A signal set producing reach ≈ 21 (e.g. enough
   distinct third-party publisher names to reach ~21) yields **`AttentionScore` in the ~60s, materially below
   the old ~81** — assert `AttentionScore < 70` for a reach that under v2 gave ~81 (lock that normal coverage
   no longer saturates near the top). Assert the same reach under a direct `100·r/(r+12)` expectation
   in-range.
2. **Media term down-weighted.** Two inputs with the **same** distinct-publisher breadth but different
   `SignalType.MediaAttention` counts: the extra media signals raise Attention by **less** than they would
   have at `0.5` (i.e. the media contribution is `0.25` per media signal). Assert the media-only delta is
   small relative to a distinct-publisher delta — lock that breadth dominates raw media volume.
3. **Discount softened for a saturated-attention company (modest, not halved).** A high-attention input
   (many distinct third-party publishers → Att in the 60s–70s) keeps **more than 70%** of its
   Trajectory·EC base — i.e. `OpportunityScore > 0.70·(Trajectory·EC/100)` — proving the covered quality
   name is only modestly discounted, not haircut ~40%.
4. **Under-the-radar uplift preserved for a genuinely low-attention company.** A low-attention input (one or
   two distinct third-party publishers → Att ~15) keeps **≥ 90%** of its base
   (`OpportunityScore ≥ 0.90·(Trajectory·EC/100)`), and its Opportunity multiplier is **strictly greater**
   than the saturated company's from pin 3 (same Trajectory·EC drivers). Locks that low attention still earns
   the biggest uplift and the discount still orders under-followed above widely-covered.
5. **Never zeroes.** Even at maximal attention (Att clamped near 100), `OpportunityScore > 0` for a positive
   Trajectory·EC base (max haircut `100/250 = 40%`).

`ScoringEngineTests`:
6. **`ScoringConfigVersion` stamp** — update the assertion to the new value (expected
   `"radar-scoring-config-v9"`; order-robust current `+1`).
7. **Repoint `new RadarScoreFormulaV2()`** usages (e.g. `DirectionalGuidanceChange_…`) to
   `RadarScoreFormulaV3`; those directional assertions stay green (Trajectory unchanged).

Keep all other scoring / pipeline / report / extractor tests green. Search the tree for any remaining
`RadarScoreFormulaV2` / `radar-formula-v2` reference and update or remove it.

---

## Constraints

- Target `net10.0`, C# 14. Pure Application-layer formula + scoring-stamp edit; no provider SDK, no AI, no
  HTTP, no DB (AD-8, files-first). Layering (AD-5) unchanged.
- **AD-6 change via the sanctioned mechanism:** bump the formula `Version` (`radar-formula-v3`), amend the
  ledger (Proposed until maintainer sign-off), delete v2, port tests, preserve v1/v2 snapshot provenance
  (recorded `ScoringVersion` strings unchanged). Change **only** Attention + Opportunity; keep
  Trajectory/EC/Velocity/window/provenance byte-for-byte.
- **Determinism (AD-3):** pure formula, no clock/IO/randomness; contributions and ordering unchanged.
- **Provenance is sacred and preserved:** one contribution per current-window signal, in input order, from
  current-window signals only; `ScoreEvidenceLink` construction in `ScoringEngine` untouched.
- **AD-9:** no advice language; all component scores stay clamped in `[0,100]`. The recalibration only moves
  ranking/labels via legitimate score changes (e.g. MRCY recovering toward Investigate) — no banned tokens.
- **Bump `ScoringEngine.ScoringConfigVersion`** to current `+1` (AD-10); `EngineVersion` unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (note, do NOT implement this round)

- **Source-quality / aggregator tiering.** Down-weighting algorithmic finance-content mills (MarketBeat,
  Zacks, Simplywall.st, StockStory, Moomoo, TradingView, Stock Titan, GuruFocus, …) is the *principled* fix
  for saturation, but it is a list-maintenance-heavy source-reputation effort with its own upkeep and
  false-positive surface. It is **deferred to its own slice** — do not start a source-reputation project
  inside this recalibration. This slice's raised saturation + softened discount is the evidence-based
  interim recalibration.
- **News-volume signal-multiplicity dedup.** Collapsing many near-duplicate articles about one event into a
  single reach contribution — a separate candidate slice (this round only *dampens* the raw media term via
  `MediaReachWeight 0.25`, it does not dedup).
- **Fully retiring the raw media term** (dropping `MediaReachWeight` to 0 / reach on distinct publishers
  alone) — evaluated; larger behavioural change than a recalibration, deferred.
- **Any other formula-math retune** (Trajectory scale, EC anchoring, Velocity smoothing) — not touched; only
  Attention + Opportunity move.
- **Going-concern / skeptic-reviewer wiring**, any collector / report-renderer / DI-shape / Domain change.

---

## Acceptance criteria

- [ ] `RadarScoreFormulaV3` (`Version = "radar-formula-v3"`) is added and changes **only**:
      `AttentionHalfSaturation 5.0 → 12.0`, `MediaReachWeight 0.5 → 0.25`, and
      `OpportunityAttentionDivisor 200.0 → 250.0`. Trajectory, EvidenceConfidence, SignalVelocity, recency,
      clamps, contributions, the empty-window behaviour, and the `PreviousSignals`/window/provenance rules are
      byte-for-byte unchanged from v2. The explanation prefix is `radar-formula-v3`.
- [ ] `RadarScoreFormulaV2` is **deleted** (no dormant deprecated code); DI (`InfrastructureServiceCollection
      Extensions.cs`) points `IScoreFormula` at `RadarScoreFormulaV3`; snapshots now record
      `mvp-engine-v1+radar-formula-v3` (via `_formula.Version`); `EngineVersion` unchanged.
- [ ] Attention no longer saturates for normal coverage: a reach that gave ~81 under v2 yields Attention in
      the ~60s under v3 (tested), restoring separation from thinly-covered names; the raw media term is
      down-weighted so distinct-publisher breadth dominates raw media volume (tested).
- [ ] The under-the-radar discount is softened but the principle holds: a saturated-attention company keeps
      `> 70%` of its Trajectory·EC base (modest discount, not ~40% haircut); a genuinely low-attention company
      keeps `≥ 90%` and its Opportunity multiplier is strictly greater than the saturated one's; Opportunity
      still falls monotonically with attention and never zeroes — all tested.
- [ ] `ScoringEngine.ScoringConfigVersion` bumped to the next integer (order-robust; expected
      `radar-scoring-config-v8 → v9`) with an updated comment; every test asserting the old stamp updated;
      `new RadarScoreFormulaV2()` test usages repointed to `RadarScoreFormulaV3`.
- [ ] `docs/architecture-decisions.md` AD-6 is amended with a `radar-formula-v3` refinement subsection
      (marked **Proposed** until maintainer sign-off on the exact constants) recording the live finding, the
      three constant changes + rationale, and that only Attention + Opportunity change; the Status line adds
      the v3 refinement date.
- [ ] `RadarScoreFormulaV2Tests` ported to `RadarScoreFormulaV3Tests` (V2 file deleted) with recomputed
      expectations and the new saturation/discount pins; all scoring / pipeline / report / extractor tests
      green.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance, and AD-9 label/advice rules
      preserved; no Domain / collector / DI-shape / report / other-formula-component change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
```
