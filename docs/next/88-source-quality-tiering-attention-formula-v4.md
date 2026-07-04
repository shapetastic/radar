# Task: `radar-formula-v4` — source-quality tiering so attention breadth reflects genuine notice, not content-mill noise

> **VERSION + APPROVAL (read first).** This is a **scoring formula-math change** driven by live data
> (it changes what counts toward `AttentionScore`'s reach and re-tunes the saturation), so it is a
> legitimate **AD-6 amendment** via the sanctioned mechanism ("*to change the formula, bump `Version`
> and update this entry*") — exactly how `radar-formula-v1 → v2 → v3` were done (specs 58, 87). It ships a
> new `IScoreFormula` identity **`radar-formula-v4`** (a new class in the v1→v2→v3 precedent — it changes
> the reach *definition*, not just constant values, so a new version identity is warranted over an in-place
> v3 edit), deletes/supersedes `RadarScoreFormulaV3` (spec-impl checklist: delete deprecated code, port
> tests), amends the AD-6 ledger, updates `ScoringVersion` composition automatically (via `_formula.Version`),
> and bumps `ScoringEngine.ScoringConfigVersion` (AD-10) to the next integer.
> **MAINTAINER SIGN-OFF: GRANTED 2026-07-04 (authoritative — overrides every "Proposed pending sign-off"
> reference below).** The maintainer reviewed and approved this session: the tier weights (mill `0.1`,
> unknown `0.5`, genuine `1.0`), the seed mill/genuine publisher lists, and the re-tuned
> `AttentionHalfSaturation 12→3`. Therefore mark the AD-6 v4 subsection **Accepted · 2026-07-04** (NOT
> Proposed), and treat every "Proposed until maintainer sign-off" / "confirm before merge" note elsewhere in
> this spec as **SATISFIED** — ship these exact values. This is a **directed** slice (maintainer asked),
> **NOT** the generic planner loop, and **NOT** architecture-gated.

> **STRICT SEQUENCING — implement AFTER spec 87 merges. Do NOT run in parallel.** This slice builds directly
> ON `radar-formula-v3` (spec 87, currently in flight): it supersedes `RadarScoreFormulaV3` and re-tunes the
> **v3** saturation constant (`AttentionHalfSaturation = 12`) for a *smaller, filtered* reach distribution.
> Its grounding facts assume the tree is at `radar-formula-v3` / `radar-scoring-config-v9`. It touches the
> formula, its DI registration, `ScoringEngine`, the AD-6 ledger, and scoring tests — the **same files** spec
> 87 touches. It MUST NOT run in parallel with spec 87 or with any scoring / formula / engine / extractor /
> directional-filing / attention slice. Sequence it strictly after 87 is merged; re-read the merged v3 before
> starting so the port is from the real v3, not from this spec's description of it.

## Overview

Two live runs on 2026-07-04 (the 8-company watch universe, after spec 84 made `SourceName` the real
publisher) exposed the **root cause** behind the undifferentiated `AttentionScore` that spec 87 recalibrated
around: the distinct third-party "publishers" driving reach are dominated by **algorithmic finance-content
mills that cover essentially every ticker**. Observed across the runs: **MarketBeat, Zacks, Simplywall.st,
StockStory, Moomoo, TradingView, Stock Titan, GuruFocus, Defense World, Pluang, MarketScreener** and similar.
Because reach counts *distinct third-party `SourceName`s equally*, "20 content mills auto-generated a blurb"
scores the same breadth as "Reuters, Bloomberg, WSJ, CNBC and an industry trade all covered a real
development". Attention therefore measures **media-noise breadth**, not genuine market notice, so every
normally-covered small-cap saturates.

Spec 87 (`radar-formula-v3`) treated the *symptom* — it raised the saturation half-point (5→12), down-weighted
the raw media term (0.5→0.25) and softened the under-the-radar discount (200→250) so Attention *spread* again
across the **unfiltered** reach distribution (16–28 for covered names). It explicitly **deferred** the
principled fix to "its own slice" (spec 87 Out of scope: *"Source-quality / aggregator tiering … deferred to
its own slice"*). **This is that slice.**

**The fix (root cause).** Classify third-party attention publishers into tiers and weight them in the reach
breadth term: content-mill / aggregator publishers count for **little or nothing**, genuine outlets
(Reuters, Bloomberg, WSJ, CNBC, AP, Financial Times, industry trades such as SpaceNews) count **full**, and
**unknown publishers default to a sane non-zero weight** so real coverage is never silently zeroed. Because
this **shrinks reach** — a covered name may drop from ~20 distinct publishers to ~2–6 genuine ones — the
v3 half-saturation constant (`12`), which was tuned to the *unfiltered* 16–28 distribution, is now
**mis-tuned for the filtered distribution** and MUST be re-tuned down so Attention still spans a useful
range. This slice therefore does both: (1) introduce a config-driven source-tier weight applied to reach,
and (2) re-tune the saturation for the post-filtering reach distribution.

This slice is deliberately **pragmatic, not a reputation database**: a small, curated, **config-driven** weight
map with a documented unknown default and an honestly-noted maintenance burden.

---

## Assignment

Worktree: any (but see sequencing)
Dependencies: **spec 87 (`radar-formula-v3`) MUST be merged first** — this supersedes it. Also: spec 84
(news attention breadth by publisher), specs 69/70 + AD-10 (`ScoringConfigVersion` stamp). Expected tree
state at start: `radar-formula-v3` / `radar-scoring-config-v9`.
Conflicts with: touches the formula (`RadarScoreFormulaV3.cs` → `RadarScoreFormulaV4.cs`), its DI
registration (`InfrastructureServiceCollectionExtensions.cs`), `ScoringEngine.cs` (`ScoringConfigVersion`),
the scoring tests, and the AD-6 ledger — **the same files spec 87 touches**. Also adds a new Application
abstraction + Infrastructure config-bound implementation. Must **NOT** run in parallel with spec 87 or any
scoring / formula / engine / extractor / directional-filing / attention slice — **sequence it strictly after
87 merges**.
Estimated time: ~2–2.5 h (a versioned, maintainer-co-designed formula that also introduces a small config
seam — the highest-care class of slice).

---

## Grounding facts (verified against the current tree — do NOT re-research)

> These describe the tree **as it will be after spec 87 merges**. Re-read the merged `RadarScoreFormulaV3.cs`
> before starting; port from the real v3.

- **v3 formula (the base to port).** `RadarScoreFormulaV3.cs` (from spec 87): `AttentionHalfSaturation = 12.0`,
  `MediaReachWeight = 0.25`, `OpportunityAttentionDivisor = 250.0`. `AttentionScore = Score(100·reach/(reach +
  AttentionHalfSaturation))`, `reach = distinctThirdPartySources + MediaReachWeight·mediaCount`.
  `distinctThirdPartySources` = distinct `Evidence.SourceName` among
  `EvidenceSourceTypes.IsThirdPartyAttentionSource` types (`NewsArticle`/`SocialMedia`/`ConferenceMention`,
  Domain `EvidenceSourceTypes`); `mediaCount` = count of `SignalType.MediaAttention` signals.
  `OpportunityScore = Score(Trajectory·(EvidenceConfidence/100)·(1 − AttentionScore/OpportunityAttention
  Divisor))`. Verified shape in the current `RadarScoreFormulaV2.cs` (lines 32–34, 54–55, 139–150, 171–175);
  v3 is that file with those three constants changed.
- **The formula is a PURE function constructed parameterless.** `IScoreFormula.Compute(ScoringInput input)`
  is a pure function of its input (no clock/IO/randomness — `IScoreFormula.cs`), and the formula is
  registered as `TryAddSingleton<IScoreFormula, RadarScoreFormulaV3>()`
  (`InfrastructureServiceCollectionExtensions.cs:66`) and news-up'd as `new RadarScoreFormulaV3()` in tests.
  **This is the central design constraint for where the tier map lives — see "Design: where the tier list
  lives".**
- **Reach reads `Evidence.SourceName` + `Evidence.SourceType`.** The only per-source facts the formula reads
  are the publisher name (`ScoringSignal.Evidence.SourceName`) and the source type. `EvidenceItem` (Domain)
  carries `SourceName`, `SourceType`, `Quality`, `MetadataJson`; the tier lookup is keyed on `SourceName`.
- **The collector already knows the real publisher.** `NewsAttentionCollector` (Infrastructure) sets
  `CollectedEvidence.SourceName` to the article's actual outlet (Reuters, Yahoo Finance, MarketBeat, …) and
  declares `metadata["quality"] = "Medium"` (spec 84 / AD-7). `CollectedEvidenceMapper.ParseQuality` maps that
  declared quality to `EvidenceQuality`. **`Quality` is evidence *integrity* and feeds `EvidenceConfidence
  Score`; it MUST NOT be overloaded to carry the attention tier** (overloading it would move a second
  component — out of scope). The attention tier is a *separate* concept, keyed on the publisher name.
- **`ScoringVersion` composition is automatic.** `ScoringEngine` builds
  `scoringVersion = $"{EngineVersion}+{_formula.Version}"` (`ScoringEngine.cs:138`), so registering
  `RadarScoreFormulaV4` records `mvp-engine-v1+radar-formula-v4` with **no** `EngineVersion` edit
  (`EngineVersion` stays `"mvp-engine-v1"`).
- **`ScoringConfigVersion` is a code constant.** `ScoringEngine.cs:38` reads
  `private const string ScoringConfigVersion = "radar-scoring-config-v9";` **after spec 87** (v8→v9) and stamps
  every snapshot at line 159. This formula-math change moves scoring output → per AD-10 bump to the **next
  integer** (order-robust: read current `+1`; expected `v9 → v10`).
- **Options binding pattern.** Infrastructure options records (`NewsCollectorOptions`, `ScoringOptions`) are
  plain records bound from `Radar:*` config and registered as singletons; registration fails fast on invalid
  values. The new tier-weight config follows this pattern.
- **Provenance/determinism unaffected.** The formula stays pure; contributions and `ScoreEvidenceLink`
  construction in `ScoringEngine` are untouched; existing on-disk snapshots keep their recorded
  `ScoringVersion` and remain reproducible.

---

## Design: where the tier list lives, and how the pure formula reads it (evaluated; recommended)

Three placements were considered against the pure-formula contract and AD-5:

- **(a) Formula holds a hardcoded list.** Rejected — a curated media-publisher list is **config data**, not
  formula math; embedding it in the Application formula makes it un-maintainable-without-a-code-edit and
  bloats the pure function with data that belongs in Infrastructure config (AD-5 spirit).
- **(b) Tag each `NewsArticle` evidence at the collector via the existing `quality` metadata → mills get
  `Low` quality.** Rejected as the *sole* mechanism — `EvidenceQuality` already means evidence *integrity* and
  feeds `EvidenceConfidenceScore` (AD-6 v2). Down-grading a mill to `Low` quality would also lower its
  confidence contribution, silently moving a **second** component and conflating "low-integrity source" with
  "low-attention-value source" (a wire-service *is* high integrity but a mill blurb is *low attention value*).
  Overloading quality is out of scope and would break the "change only Attention + Opportunity" discipline.
- **(c) RECOMMENDED — a config-driven publisher→attention-weight lookup, defined as an Application abstraction,
  implemented from Infrastructure config, injected into the formula; reach becomes a weighted sum.** The
  curated map lives as **Infrastructure config data** (bound from `Radar:Attention:SourceTiers`), the
  Application defines only the *abstraction* (AD-5: Application depends on the abstraction, Infrastructure
  provides the config-bound data), and the formula reads a per-publisher weight instead of counting every
  distinct publisher as `1`.

### Recommended mechanism (c), precisely

- **New Application abstraction `IAttentionSourceWeights`** in `Radar.Application/Scoring/`:
  ```csharp
  public interface IAttentionSourceWeights
  {
      /// <summary>
      /// The attention-breadth weight for a third-party publisher SourceName, in [0,1].
      /// 1.0 = genuine market notice (Reuters, Bloomberg, WSJ, CNBC, AP, industry trades);
      /// low/zero = algorithmic content-mill / aggregator (MarketBeat, Zacks, ...);
      /// an UNKNOWN publisher returns the configured default (non-zero) so real coverage is never
      /// silently zeroed. Case-insensitive; blank/null returns the unknown default.
      /// </summary>
      double WeightFor(string? sourceName);
  }
  ```
- **`RadarScoreFormulaV4`** takes `IAttentionSourceWeights` via its constructor (this **breaks the
  parameterless construction** — update DI and every `new RadarScoreFormula…()` test call site to pass an
  instance/fake). It remains a **pure function of `(input, weights)`** — no clock/IO/randomness; determinism
  is preserved because `weights` is an immutable lookup. Reach's breadth term becomes a **weighted distinct
  sum**:
  ```csharp
  var breadth = signals
      .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
      .Select(s => s.Evidence.SourceName)
      .Where(name => !string.IsNullOrWhiteSpace(name))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Sum(name => _weights.WeightFor(name));      // was .Count()
  var reach = breadth + MediaReachWeight * mediaCount;
  ```
  (Distinct-by-publisher is preserved — a mill that appears 10× still contributes its weight once; the change
  is that each distinct publisher contributes its *tier weight* instead of a flat `1`.)
- **Infrastructure config-bound implementation `ConfiguredAttentionSourceWeights`** in
  `Radar.Infrastructure/News/` (or `…/Attention/`), constructed from a bound options record
  `AttentionSourceTierOptions`. It normalises publisher names (whitespace-collapse, case-insensitive) and
  resolves a weight by tier; unknown → the configured `UnknownWeight` default. Registered as a singleton in a
  new `AddRadarAttentionSourceTiers(...)` (or folded into the existing scoring registration) and bound from
  `Radar:Attention:SourceTiers`.
- **Config shape (`Radar:Attention`), curated + maintainable without a code edit:**
  ```jsonc
  "Radar": {
    "Attention": {
      "UnknownWeight": 0.5,          // a publisher not in any list still counts (never silently zeroed)
      "SourceTiers": {
        "Mill":    { "Weight": 0.1, "Publishers": [ "MarketBeat", "Zacks", "Simply Wall St", "StockStory",
                                                     "Moomoo", "TradingView", "Stock Titan", "GuruFocus",
                                                     "Defense World", "Pluang", "MarketScreener" ] },
        "Genuine": { "Weight": 1.0, "Publishers": [ "Reuters", "Bloomberg", "The Wall Street Journal",
                                                     "CNBC", "Associated Press", "Financial Times",
                                                     "SpaceNews" ] }
      }
    }
  }
  ```
  A default map (the curated lists above) ships as the code-level fallback so an operator who sets **no**
  config still gets sensible tiering; `appsettings` overrides/extends it. `UnknownWeight` default `0.5` keeps
  unmapped publishers **counted at half a genuine outlet** — deliberately conservative so a real outlet not
  yet on the allowlist is not zeroed (see False-positive/maintenance risk).

**Why (c) over (b) or a formula-held list.** It keeps the curated media data in **Infrastructure config**
(maintainable without a code edit, AD-5-clean), keeps the Application formula a **pure function** (now of
`input` + an injected immutable lookup), keeps `EvidenceQuality` meaning *integrity only* (no second
component moves), and localises the "what counts as market notice" policy to one documented, testable,
config-driven seam. Tagging at the collector (b) was considered as a complement but rejected for this round:
it would require a new evidence field the formula reads, which is a larger Domain change than injecting one
lookup, and it spreads the tier policy across collector + formula. The formula-side weighted-reach is the
single smallest change that respects purity and layering.

> **Note on the media term.** The raw `MediaReachWeight·mediaCount` term (v3 `0.25`) is left as-is — it is a
> `SignalType.MediaAttention` count, not a per-publisher term, so tier weighting does not apply to it. The
> news-volume multiplicity issue it partly represents is Out of scope (see below).

---

## The v4 recalibration (precise)

Implement `RadarScoreFormulaV4 : IScoreFormula`, `Version => "radar-formula-v4"`. Port `RadarScoreFormulaV3`
verbatim and change **only** the reach breadth term (weighted, as above) and the saturation constant. Every
other component, clamp, contribution, and the explanation shape stay identical (explanation prefix
`radar-formula-v4`).

### 1. Reach breadth becomes a tier-weighted distinct sum (the root-cause fix)

- `distinctThirdPartySources.Count()` → `Distinct(...).Sum(name => _weights.WeightFor(name))` as shown above.
  A content mill contributes ≈`0.1`, an unknown publisher `0.5`, a genuine outlet `1.0`. Mills no longer
  inflate breadth; genuine coverage dominates.

### 2. Re-tune the saturation for the *filtered* (smaller) reach distribution

- `AttentionHalfSaturation`: **`12.0` (v3) → `3.0`**. Rationale: v3's `12` was tuned to the *unfiltered*
  reach of 16–28. After tier-weighting, a covered name's reach falls to ≈**2–6** genuine-equivalent
  publishers (see the before/after table). At the filtered scale, `+12` would push everyone back down to
  near-zero Attention (re-collapsing it at the *bottom*). `+3` re-centres the saturation so the filtered
  covered cluster lands at **~40–70** and a genuinely thin/mill-only name lands **low**, restoring a useful
  spread on the new scale. It keeps the same saturating *shape* (asymptotic to 100, monotone in reach).
  > The exact constant is a **maintainer-approval item** — the table below justifies `3.0` against the
  > filtered distribution; confirm before merge (AD-6 v4 subsection stays **Proposed** until then). If the
  > maintainer prefers a different filtered target, adjust this single constant.

### Unchanged (byte-for-byte from v3)

- `MediaReachWeight = 0.25` (media term unchanged — Out of scope to retire it further).
- `OpportunityAttentionDivisor = 250.0` (the discount **shape** is unchanged; the filtered-and-re-saturated
  Attention now feeds it, which is the point — a mill-covered name gets a *low* Attention and thus a *smaller*
  discount, but it also has a low reach, so it is not spuriously boosted).
- **TrajectoryScore, EvidenceConfidenceScore, SignalVelocityScore**, recency weighting, empty-window
  behaviour, `PreviousSignals`/window/provenance/contribution rules, every clamp/round — identical to v3.

---

## Before/after Attention + Opportunity table (the 8-company universe — the evidence base)

Grounded in the observed publisher distribution. The live runs show each covered name's reach is dominated by
content mills (the majority of the ~16–28 distinct publishers) with a **minority of genuine outlets**
(Reuters/SpaceNews/Yahoo-tier). **Assumption (stated, since exact per-company publisher lists were not fully
captured):** for the covered cluster, of ~20 distinct publishers ≈**2–3 are genuine**, ≈**3–4 are unknown**,
and the rest are mills; SPNS is thinly-and-mostly-mill covered. Weighted reach `≈ Σ tier weights`:

| Company | v3 reach (unfiltered) | Att v3 (÷ +12) | assumed genuine/unknown/mill | **v4 weighted reach** | **Att v4 (÷ +3)** | disc v3 (÷250) | disc v4 (÷250) | **Opp direction** |
|---|---:|---:|---|---:|---:|---:|---:|---|
| MRCY | 21.3 | 64 | 3 / 4 / 14 | 3·1.0 + 4·0.5 + 14·0.1 = **6.4** | **68** | 0.744 | 0.728 | ≈ flat / modest |
| HLIO | 21.3 | 64 | 2 / 3 / 16 | 2 + 1.5 + 1.6 = **5.1** | **63** | 0.744 | 0.748 | ≈ flat |
| AGYS | 22.8 | 65 | 2 / 3 / 18 | 2 + 1.5 + 1.8 = **5.3** | **64** | 0.738 | 0.744 | ≈ flat |
| ERII | 28.3 | 70 | 3 / 5 / 20 | 3 + 2.5 + 2.0 = **7.5** | **71** | 0.719 | 0.716 | ≈ flat |
| AEHR | 21.3 | 64 | 1 / 3 / 17 | 1 + 1.5 + 1.7 = **4.2** | **58** | 0.744 | 0.768 | slight uplift |
| CYRX | 17.7 | 60 | 1 / 3 / 14 | 1 + 1.5 + 1.4 = **3.9** | **57** | 0.761 | 0.772 | slight uplift |
| EOSE | 15.8 | 57 | 2 / 2 / 12 | 2 + 1.0 + 1.2 = **4.2** | **58** | 0.772 | 0.768 | ≈ flat |
| SPNS | 2.0 | 15 | 0 / 1 / 1 | 0 + 0.5 + 0.1 = **0.6** | **17** | 0.942 | 0.932 | ≈ flat |

**Reading the recalibration (grounded in the assumed mix; the mechanism, not exact per-company values, is
what this locks):**

- **Attention now reflects genuine notice, not mill count.** A name whose ~20 "publishers" are mostly
  auto-generated blurbs no longer saturates on media noise — its Attention is driven by the **2–3 genuine
  outlets + a few unknowns** that actually covered it. The covered cluster lands ~**57–71** on the re-tuned
  `+3` scale (comparable spread to v3's 57–70, but now *earned* by genuine breadth, not mill volume), and a
  thinly/mill-only name (SPNS) stays **low (~17)**.
- **Differentiation is now on the RIGHT axis.** Under v3, a mill-flooded name and a genuinely-covered name
  both saturate; under v4, a name with **more genuine outlets** (ERII: 3 genuine → reach 7.5 → Att 71) sits
  **above** one with fewer (AEHR/CYRX: 1 genuine → reach ~4 → Att ~57), even at similar total article counts.
  That ordering — genuine breadth over mill breadth — is the whole point.
- **The under-the-radar principle holds (unchanged discount shape ÷250):** a genuinely under-followed name
  still carries the smallest haircut; the discount still falls monotonically with Attention and never zeroes.
- **Crucially, mills are down-weighted, not banned:** a mill still adds `0.1`, and an unknown outlet adds
  `0.5`, so no real coverage is silently zeroed — a name covered *only* by mills isn't pushed to exactly 0
  Attention, it just earns very little breadth from it.

> **Flag for maintainer sign-off:** the tier weights (mill `0.1`, unknown `0.5`, genuine `1.0`), the curated
> publisher lists, and the re-tuned `AttentionHalfSaturation = 3.0` are the **proposed** calibration —
> confirm before merge (mark the AD-6 v4 subsection **Proposed** until then). Exact per-company Opportunity
> values depend on each name's Trajectory·EC base (pinned by the live snapshots) and on the true genuine/mill
> split per company; the table shows the *mechanism* and its direction, from the stated assumption.

---

## Version, DI, and ledger

- New `RadarScoreFormulaV4 : IScoreFormula`, `Version => "radar-formula-v4"`. Per the spec-impl checklist,
  **delete `RadarScoreFormulaV3`** (do not leave it dormant) and **port its tests** to
  `RadarScoreFormulaV4Tests`. On-disk snapshots keep their recorded `ScoringVersion` string — provenance,
  no V3 code needed to remain.
- New Application abstraction `IAttentionSourceWeights`; new Infrastructure `ConfiguredAttentionSourceWeights`
  + `AttentionSourceTierOptions`, bound from `Radar:Attention` and registered as a singleton. `RadarScore
  FormulaV4` takes `IAttentionSourceWeights` via constructor.
- Repoint DI: `InfrastructureServiceCollectionExtensions.cs:66`
  `TryAddSingleton<IScoreFormula, RadarScoreFormulaV3>()` → `RadarScoreFormulaV4`, and register
  `IAttentionSourceWeights` (with a code-level default map so no config still works). Fail fast on invalid
  config (negative weight, weight outside [0,1]).
- `ScoringVersion` composition updates automatically to `mvp-engine-v1+radar-formula-v4` via `_formula.Version`
  — **no** `EngineVersion` edit.
- **Bump `ScoringEngine.ScoringConfigVersion`** to the next integer (order-robust: current `+1`; expected
  `radar-scoring-config-v9 → radar-scoring-config-v10`), and update the constant's comment to record this
  generation: source-quality tiering — reach breadth is now a tier-weighted distinct-publisher sum (content
  mills down-weighted, unknowns at a conservative default, genuine outlets full), and `AttentionHalf
  Saturation` re-tuned `12 → 3` for the smaller filtered reach distribution; a formula-math change so
  `ScoringVersion` advances via `_formula.Version` and per AD-10 the stamp bumps.
- **Amend `docs/architecture-decisions.md` AD-6** with a new subsection **`Refinement — radar-formula-v4
  (spec 88): source-quality tiering of attention breadth`**, mirroring the v2/v3 subsections: state the live
  finding (reach dominated by content mills every ticker gets, so Attention measured media-noise breadth not
  genuine notice), the tier-weighted reach change + the re-tuned saturation + rationale, that **only Attention
  changes** (Opportunity's discount *shape*, Trajectory, EC, Velocity, window, provenance all unchanged — the
  media term and `÷250` divisor are unchanged), and that the principle holds. Update the AD-6 **Status** line
  to add `Refined · 2026-07-04 (spec 88, radar-formula-v4 — Proposed until maintainer sign-off)`, and mark
  the v1/v2/v3 Attention component formula as *superseded by radar-formula-v4* for the Attention component.
  **Mark the v4 subsection `Proposed`** until the maintainer approves the tier weights, publisher lists, and
  the re-tuned constant; the coder ships them as proposed.

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  IAttentionSourceWeights.cs       # ADD: abstraction — WeightFor(string? sourceName) -> [0,1].
  RadarScoreFormulaV4.cs           # ADD: port of V3; reach breadth becomes a tier-weighted distinct sum via
                                   #   injected IAttentionSourceWeights; AttentionHalfSaturation 12 -> 3;
                                   #   Version => "radar-formula-v4"; explanation prefix "radar-formula-v4".
                                   #   Takes IAttentionSourceWeights via constructor (no longer parameterless).
  RadarScoreFormulaV3.cs           # DELETE (spec-impl checklist: no dormant deprecated code).
  ScoringEngine.cs                 # MODIFIED: bump ScoringConfigVersion v9 -> v10 + update the comment.
                                   #   No EngineVersion change.

src/Radar.Infrastructure/News/     (or a new .../Attention/ folder)
  AttentionSourceTierOptions.cs        # ADD: bound options — UnknownWeight + named tiers {Weight, Publishers[]}.
  ConfiguredAttentionSourceWeights.cs  # ADD: IAttentionSourceWeights over the options; case-insensitive,
                                       #   whitespace-normalised lookup; unknown -> UnknownWeight; ships a
                                       #   curated default map so no-config still tiers sensibly.

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: IScoreFormula -> RadarScoreFormulaV4; register
                                   #   IAttentionSourceWeights (bound from Radar:Attention, code default map);
                                   #   fail fast on invalid weights.

tests/Radar.Application.Tests/Scoring/
  RadarScoreFormulaV4Tests.cs      # ADD: port of RadarScoreFormulaV3Tests with recomputed expectations + new
                                   #   tiering/saturation pins (see Tests). Uses a small in-test fake/impl of
                                   #   IAttentionSourceWeights.
  RadarScoreFormulaV3Tests.cs      # DELETE (ported).
  ScoringEngineTests.cs            # MODIFIED: ScoringConfigVersion assertion v9 -> v10; repoint the
                                   #   new RadarScoreFormulaV3(...) usages to RadarScoreFormulaV4 (pass a
                                   #   weights instance).

tests/Radar.Infrastructure.Tests/... (if the project exists)
  ConfiguredAttentionSourceWeightsTests.cs  # ADD: config-driven lookup, unknown default, case-insensitivity,
                                            #   fail-fast on invalid weight.

docs/architecture-decisions.md     # MODIFIED: AD-6 v4 refinement subsection (Proposed) + Status line.
```

No `Radar.Domain` change (no enum/record — `EvidenceQuality` is NOT overloaded). No collector, report-renderer
change. No new package. No provider SDK. No DB (AD-8, files-first).

---

## Implementation details

### `IAttentionSourceWeights` (Application)

- Single method `double WeightFor(string? sourceName)` returning a weight in `[0,1]`. Documented contract:
  case-insensitive, whitespace-normalised; blank/null and unknown publishers return the configured
  **non-zero** unknown default. Pure/side-effect-free (an immutable lookup) so the formula stays deterministic.

### `RadarScoreFormulaV4` (Application)

- Port `RadarScoreFormulaV3` verbatim; add a `readonly IAttentionSourceWeights _weights` constructor field
  (`ArgumentNullException.ThrowIfNull`). Change **only**:
  - The Attention breadth term: `.Distinct(StringComparer.OrdinalIgnoreCase).Count()` →
    `.Distinct(StringComparer.OrdinalIgnoreCase).Sum(name => _weights.WeightFor(name))` (keep the third-party
    filter and the `!IsNullOrWhiteSpace` guard exactly as-is; distinct-by-publisher is preserved).
  - `AttentionHalfSaturation = 12.0` → `3.0` (update the comment: re-tuned for the smaller filtered reach
    distribution after tier-weighting).
  - `Version => "radar-formula-v4"`; explanation prefix + empty-window explanation string become
    `radar-formula-v4:`.
- **Everything else identical to v3**: `MediaReachWeight = 0.25`, `OpportunityAttentionDivisor = 250.0`,
  Trajectory, EvidenceConfidence, Velocity, recency, clamps, contributions (one per current-window signal in
  input order; Neutral/Mixed weight 0; never from `PreviousSignals`), `ScoreComponents`/`ScoreComputation`
  shape, `ComponentJson`.
- Pure and deterministic (AD-3): no clock/IO/randomness; `_weights` is an immutable lookup.

### `ConfiguredAttentionSourceWeights` + `AttentionSourceTierOptions` (Infrastructure)

- `AttentionSourceTierOptions`: `double UnknownWeight` (default `0.5`) + a named-tier map
  (`{ Weight, string[] Publishers }`), bindable from `Radar:Attention`. A code-level default instance carries
  the curated mill/genuine lists so a no-config run still tiers.
- `ConfiguredAttentionSourceWeights`: builds an internal `Dictionary<string,double>` (publisher → weight,
  `OrdinalIgnoreCase`, whitespace-normalised keys); `WeightFor` normalises the input and returns the mapped
  weight or `UnknownWeight`. Clamps all weights to `[0,1]`; fails fast (throws in the ctor / at registration)
  on a negative or `>1` configured weight so a misconfiguration cannot silently distort scoring.

### `ScoringEngine` (Application)

- Bump `ScoringConfigVersion` v9 → v10 with an updated comment (as above). Do **NOT** touch `EngineVersion`;
  `ScoringVersion` advances via `_formula.Version`.

### DI (`InfrastructureServiceCollectionExtensions`)

- Register `IAttentionSourceWeights` → `ConfiguredAttentionSourceWeights` (singleton), bound from
  `Radar:Attention` with the curated default map as fallback. Repoint `IScoreFormula` → `RadarScoreFormulaV4`
  (which now resolves `IAttentionSourceWeights` from DI). Fail fast on invalid config.

### AD-6 amendment (ledger)

- Add the `radar-formula-v4` refinement subsection (**Proposed**) and update the Status line as under
  **Version, DI, and ledger**. This is the sanctioned formula-change mechanism, not drift.

---

## Tests

Port `RadarScoreFormulaV3Tests` → `RadarScoreFormulaV4Tests` (delete the V3 file), recompute value-pinned
expectations, and add the tiering + saturation pins. Match the existing test style (`BuildSignal`/`InputFrom`
helpers, `[Fact]`/`Assert`). Provide a small in-test `IAttentionSourceWeights` (a fake or the real
`ConfiguredAttentionSourceWeights` seeded with a known map) so tests control tier weights deterministically.

Ported/kept green (recompute expected numbers where the changed constants move them):
- **Version:** `Version == "radar-formula-v4"` and it appears in the explanation.
- **Trajectory / EvidenceConfidence / Velocity / Contributions / Neutral-zero-weight / empty-window /
  determinism** — **unchanged in shape** (none of these components moved). Recompute nothing except where an
  Attention value flows into Opportunity.
- **`Opportunity_FallsAsAttentionRises_NeverZeroes`** — still holds (higher Attention ⇒ `Opp ≤` lower, and
  `Opp > 0`).

New pins locking the v4 tiering + saturation:
1. **Mill-dominated Attention is materially LOWER than genuine-coverage Attention (the headline property).**
   Two inputs with the **same count** of distinct third-party publishers, same directions/strength/confidence:
   input A's publishers are all **mill** (weight `0.1`), input B's are all **genuine** (weight `1.0`). Assert
   `Attention(B) > Attention(A)` by a **material** margin (e.g. `B − A > 20`), proving breadth is now earned by
   genuine notice, not raw publisher count.
2. **A thinly-but-genuinely-covered name is NOT wrongly zeroed.** An input with just **1–2 genuine** publishers
   yields a **non-trivial, > 0** Attention (e.g. `Attention > 0` and comparable to a small-reach expectation
   under `100·r/(r+3)`), and is **not** lower than a name covered only by a *larger* pile of mills whose
   weighted reach is smaller — locks that a few real outlets beat many mills.
3. **Unknown publishers default to a sane non-zero weight (never silently zeroed).** An input whose publishers
   are **all unknown** (not in any tier list) yields `Attention > 0`, and its weighted reach reflects the
   `UnknownWeight` default (assert the weighted-reach-implied Attention matches `UnknownWeight` per distinct
   unknown publisher, e.g. N unknowns ⇒ reach ≈ `N·UnknownWeight`).
4. **The tier list is config-driven.** Construct `RadarScoreFormulaV4` with two different
   `IAttentionSourceWeights` maps (or two `ConfiguredAttentionSourceWeights` from two option sets) that classify
   the **same** publisher name into different tiers; assert the resulting Attention differs accordingly —
   proving tiers come from config, not hardcoded in the formula.
5. **Saturation re-tuned for the filtered scale.** A weighted reach ≈ 5 (e.g. genuine + unknown publishers
   summing to ~5) yields Attention in the **~60s** under `+3` (assert `100·r/(r+3)` in-range), and a weighted
   reach ≈ 0.6 (mill-only thin name) yields a **low** Attention (~15–20) — locks the spread on the new scale.
6. **Distinct-by-publisher preserved.** The same mill publisher appearing many times still contributes its
   weight **once** (repeated same `SourceName` does not multiply reach).
7. **`ConfiguredAttentionSourceWeights` (Infrastructure test, if the project exists):** config-driven lookup
   returns the tier weight for a listed publisher, `UnknownWeight` for an unlisted one, is case-insensitive and
   whitespace-tolerant, and **fails fast** on a negative or `>1` configured weight.

`ScoringEngineTests`:
8. **`ScoringConfigVersion` stamp** — update the assertion to the new value (expected
   `"radar-scoring-config-v10"`; order-robust current `+1`).
9. **Repoint `new RadarScoreFormulaV3(...)`** usages (e.g. `DirectionalGuidanceChange_…`) to
   `RadarScoreFormulaV4` (pass a weights instance); those directional assertions stay green (Trajectory
   unchanged).

Keep all other scoring / pipeline / report / extractor tests green. Search the tree for any remaining
`RadarScoreFormulaV3` / `radar-formula-v3` reference and update or remove it.

---

## Constraints

- Target `net10.0`, C# 14. Application-layer formula + a new Application abstraction; the curated media data +
  its config binding live in `Radar.Infrastructure` (AD-5: Application depends on the abstraction,
  Infrastructure provides the config data). No provider SDK, no AI, no HTTP, no DB (AD-8, files-first).
- **AD-6 change via the sanctioned mechanism:** bump the formula `Version` (`radar-formula-v4`), amend the
  ledger (**Proposed** until maintainer sign-off on the tier weights, publisher lists, and re-tuned constant),
  delete v3, port tests, preserve v1/v2/v3 snapshot provenance (recorded `ScoringVersion` strings unchanged).
  Change **only** the Attention component (reach breadth + saturation); keep Opportunity's discount *shape*,
  Trajectory, EC, Velocity, window, provenance byte-for-byte from v3.
- **`EvidenceQuality` is NOT overloaded** — the attention tier is a separate publisher-keyed concept; quality
  stays evidence *integrity* feeding `EvidenceConfidenceScore` only. No Domain change.
- **Determinism (AD-3):** the formula stays a pure function of `(input, immutable weights)`; no
  clock/IO/randomness; contributions and ordering unchanged.
- **Provenance is sacred and preserved:** one contribution per current-window signal, in input order, from
  current-window signals only; `ScoreEvidenceLink` construction in `ScoringEngine` untouched.
- **AD-9:** no advice language; all component scores stay clamped in `[0,100]`. The tiering only moves
  ranking/labels via legitimate Attention changes — no banned tokens.
- **Unknown publishers default to a non-zero weight** so real coverage is never silently zeroed; the mill
  list is small, curated, config-driven, and documented (maintenance burden noted below).
- **Bump `ScoringEngine.ScoringConfigVersion`** to current `+1` (AD-10); `EngineVersion` unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

### Maintenance / false-positive risk (documented honestly)

A curated mill list is inherently arbitrary and **needs upkeep**: new content mills appear, publisher name
strings vary ("Simply Wall St" vs "Simplywall.st"), and a legitimate niche outlet could be mis-tagged. The
design mitigates this by: (1) keeping the list **small and config-driven** (edit `appsettings`, no code
change); (2) an **unknown → `0.5` default** so an un-listed real outlet still counts (worst case: it is
*under*-counted, not zeroed); (3) documenting the lists inline. It does **not** attempt a comprehensive
reputation database — that would be a much larger, higher-maintenance effort and is explicitly not the goal.
The genuine/allowlist side is likewise small; growing either list is expected routine ops.

---

## Out of scope (note, do NOT implement this round)

- **News-volume within-source signal-multiplicity dedup.** One real event spawning many near-duplicate
  articles inflates `mediaCount` (the `MediaReachWeight·mediaCount` term); tier-weighting *partly* mitigates
  the breadth side but does **not** dedup the raw media term. Collapsing near-duplicate articles about one
  event into a single reach contribution is a **separate deferred slice** — this slice does not touch
  `MediaReachWeight` or the media term.
- **Fully retiring the raw media term** (dropping `MediaReachWeight` to 0) — deferred (spec 87 Out of scope,
  still deferred).
- **Overloading `EvidenceQuality`** to carry attention tier, or tagging tier onto evidence at the collector —
  evaluated and rejected (see Design); not this round.
- **A comprehensive source-reputation database / auto-classification of publishers** — explicitly out of scope;
  this is a small curated config map only.
- **Going-concern / skeptic-reviewer wiring.**
- **Any change to a non-Attention component** (Trajectory, EvidenceConfidence, Velocity) or Opportunity's
  discount *shape* / divisor — not touched; only the Attention reach-breadth + saturation move.

---

## Acceptance criteria

- [ ] **Implemented AFTER spec 87 merges** (this supersedes `RadarScoreFormulaV3`); not run in parallel with
      any scoring/formula/engine/extractor/attention slice.
- [ ] `IAttentionSourceWeights` (Application) + `ConfiguredAttentionSourceWeights` + `AttentionSourceTier
      Options` (Infrastructure, bound from `Radar:Attention`, with a curated code-default map) are added; the
      curated media list lives in Infrastructure config, not in the Application formula. Unknown publishers
      return a **non-zero** default weight; invalid configured weights **fail fast**.
- [ ] `RadarScoreFormulaV4` (`Version = "radar-formula-v4"`) is added and changes **only** the Attention
      component: reach breadth becomes a **tier-weighted distinct-publisher sum** via injected
      `IAttentionSourceWeights`, and `AttentionHalfSaturation` is re-tuned `12.0 → 3.0` for the filtered reach
      distribution. `MediaReachWeight (0.25)`, `OpportunityAttentionDivisor (250.0)`, Trajectory, Evidence
      Confidence, SignalVelocity, recency, clamps, contributions, the empty-window behaviour, and the
      `PreviousSignals`/window/provenance rules are byte-for-byte unchanged from v3. Explanation prefix is
      `radar-formula-v4`.
- [ ] `RadarScoreFormulaV3` is **deleted** (no dormant deprecated code); DI points `IScoreFormula` at
      `RadarScoreFormulaV4` and registers `IAttentionSourceWeights`; snapshots now record
      `mvp-engine-v1+radar-formula-v4` (via `_formula.Version`); `EngineVersion` unchanged; `EvidenceQuality`
      NOT overloaded (no Domain change).
- [ ] A mill-dominated company's Attention is **materially lower** than a genuine-coverage company's at equal
      publisher count (tested); a thinly-but-genuinely-covered name is **not wrongly zeroed** (tested); unknown
      publishers default to a sane non-zero weight (tested); the tier list is **config-driven** (tested);
      distinct-by-publisher is preserved (tested).
- [ ] `ScoringEngine.ScoringConfigVersion` bumped to the next integer (order-robust; expected
      `radar-scoring-config-v9 → v10`) with an updated comment; every test asserting the old stamp updated;
      `new RadarScoreFormulaV3(...)` test usages repointed to `RadarScoreFormulaV4`.
- [ ] `docs/architecture-decisions.md` AD-6 is amended with a `radar-formula-v4` refinement subsection (marked
      **Proposed** until maintainer sign-off on the tier weights, publisher lists, and re-tuned constant)
      recording the live finding, the tier-weighted reach + re-tuned saturation + rationale, and that only the
      Attention component changes; the Status line adds the v4 refinement date.
- [ ] `RadarScoreFormulaV3Tests` ported to `RadarScoreFormulaV4Tests` (V3 file deleted) with recomputed
      expectations and the new tiering/saturation pins; all scoring / pipeline / report / extractor tests green.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance, and AD-9 label/advice rules
      preserved; no Domain / collector / report / non-Attention-component change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
