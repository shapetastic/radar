# Task: Diagnose the Attention/notedness understatement — characterize the input, apply only narrow safe fixes

> **DIAGNOSIS SLICE (Option A of spec 122 — non-gated, runnable now).** Spec 122 asks whether Radar's Attention
> metric UNDERSTATES notedness (AEHR: intensely covered around one event, yet `Attention` ≈ 21). Before any
> formula-shape change (`radar-formula-v8`, AD-6-gated), this slice does Option A: **verify the input isn't the
> problem** with a deterministic characterization test, attribute the lost distinct-publisher breadth to a
> specific pipeline stage, and apply ONLY narrow, unambiguous, non-formula fixes. It **must NOT** change the
> Attention formula shape, the spec-109 collapse algorithm, or introduce a velocity/loudness term — those remain
> the spec-122 decision. Its findings are the key input to that decision.

## Overview / problem

`AttentionScore` (RadarScoreFormulaV7) measures **tier-weighted DISTINCT third-party publisher breadth**:

```
weightedBreadth = Σ over distinct third-party SourceName of sourceWeights.WeightFor(name)
reach           = weightedBreadth + MediaReachWeight · mediaSignalCount
Attention       = 100 · reach / (reach + AttentionHalfSaturation)
```

The maintainer's observation (spec 122, open decision b) is that a name loudly noticed around a **single** event
scores a low Attention — indistinguishable from a genuinely under-followed name. There are two very different
root causes, and this slice exists to tell them apart **before** anyone reshapes the formula:

1. **Measurement failure (fixable here, cheaply):** the pipeline is genuinely under-counting distinct real
   publishers — e.g. the `newssearch` title-relevance filter over-drops real articles, or genuine outlets are
   absent from the `Radar:Attention` tier map and fall to the unknown default, or the spec-109 same-event
   collapse reduces many distinct-publisher signals to ONE representative so `weightedBreadth` sees 1 outlet.
2. **True low breadth (NOT fixable here):** the name really was covered by few distinct genuine outlets (one
   loud event amplified by duplication), in which case the breadth metric is arguably working as designed and the
   real question — whether to re-admit loudness/velocity, or gate the discount on event recency — is the
   AD-6-gated spec-122 decision, not a bug.

**Prime suspect (state it, then prove/disprove it with a test):** the spec-109 `MediaAttentionCollapse` runs in
`ScoringEngine` **before** the breadth count, and it keeps ONE representative signal per same-event bucket. If
many distinct genuine publishers of one event collapse to a single surviving signal, `weightedBreadth` counts one
`SourceName` and `mediaSignalCount` drops to 1 — a direct mechanical explanation for AEHR ≈ 21. The
`NewsAttentionCollector` deliberately sets `SourceName` to the real outlet (not the per-company feed name) to
preserve distinct-outlet breadth (NewsAttentionCollector.cs:248–256), so the collector side is likely sound and
the collapse is the leading candidate. **Prove which stage it is; do not assume.**

---

## Assignment

Worktree: any. Primarily adds a characterization test + records findings; touches production code ONLY for a
narrow, unambiguous fix if the diagnosis finds one (see "Safe fixes" — bounded).
Dependencies: none (independent of the still-BLOCKED spec 122 decision; this informs it). No sequencing
dependency on 121/123 (already merged).
Conflicts with: any slice changing `RadarScoreFormulaV7`, `MediaAttentionCollapse`, `ConfiguredAttentionSourceWeights`,
`AttentionSourceTierOptions`, or `NewsAttentionCollector`.
Estimated time: ~1.5–2 hours

---

## Grounding facts (verified against the code, 2026-07-21)

- **Breadth is counted post-collapse.** `RadarScoreFormulaV7` (`src/Radar.Application/Scoring/RadarScoreFormulaV7.cs`,
  ~lines 194–202) computes `weightedBreadth` over `signals` — the signal set AFTER `ScoringEngine` applies the
  spec-109 collapse — restricted by `EvidenceSourceTypes.IsThirdPartyAttentionSource`, de-duplicated by
  `SourceName` (OrdinalIgnoreCase). `mediaSignalCount` counts surviving `SignalType.MediaAttention` signals.
- **Spec-109 collapse** (`src/Radar.Application/Scoring/MediaAttentionCollapse.cs`) buckets same-event
  `MediaAttention` signals within `EventWindow` and keeps the earliest as the single representative. Verify in
  `ScoringEngine` (`src/Radar.Application/Scoring/ScoringEngine.cs`) that this runs before the formula sees the
  signal list, and confirm whether the *distinct-publisher* information of the collapsed members is discarded.
- **Publisher tiering is CONFIG** (`ConfiguredAttentionSourceWeights` over `AttentionSourceTierOptions`, bound
  from `Radar:Attention`). A genuine outlet absent from every tier resolves to `UnknownWeight`. **The tier map is
  folded into `ScoringConfigVersion` via `CanonicalDescriptor()`** — so correcting the map is a *config edit* that
  **re-stamps the fingerprint** (like a `ScoringWeights` profile), NOT a formula-structure change and NOT AD-6.
- **newssearch relevance filter** (`NewsAttentionCollector.IsRelevant`, ~lines 189–206): keeps an article only if
  its whitespace-normalised, publisher-suffix-stripped title contains the company phrase OR the ticker token
  (substring, case-insensitive). This is Infrastructure collection behaviour — **collection breadth is NOT hashed
  into the fingerprint**, so a relevance-filter fix is fingerprint-neutral. (Note the known single/short-ticker
  substring-collision exposure already recorded for `V`/`TR` in prior specs — do not regress it.)
- Formula/rules unchanged by this slice → **no `_formula.Version` (`radar-formula-v7`) or `RuleSetVersion` bump.**

---

## Implementation details

### 1. Characterize (the primary deliverable — always produced)

Add a deterministic characterization test that reproduces the AEHR-shaped situation through the **real** scoring
path (engine + spec-109 collapse + formula + configured/default source weights), with **no live network**: a
synthetic evidence/signal set for one company — **one event date, ~15 distinct genuine third-party publishers**
(realistic outlet names) all covering that single event — plus a small control (the same 15 publishers spread
across several distinct events). Assert the resulting `AttentionScore` for each and record the numbers.

This test **documents current behaviour** (it is a characterization, not a red test): it pins exactly how much
distinct-publisher breadth survives the collapse for a single-event burst vs. spread coverage, isolating the
stage that suppresses breadth. Keep it generic (no ticker/company special-casing — AEHR is the motivating
example, not a coded case).

### 2. Attribute + record findings (always produced)

In the PR body, state which stage accounts for the gap, with the characterization numbers as evidence:
- **collapse** (distinct publishers of one event reduced to one surviving signal), and/or
- **tiering** (genuine outlets resolving to `UnknownWeight` because absent from `Radar:Attention`), and/or
- **relevance filter** (real articles dropped), and/or
- **genuinely low breadth** (no bug — feeds the spec-122 structural decision).

Then append a dated one-paragraph **"## Diagnosis findings (spec 124)"** note under the decision block of
`docs/next/122-notedness-attention-measurement-decision.md` summarising the attribution and the recommended
spec-122 direction (measurement-fixed → likely no formula change needed; collapse-destroys-breadth or
genuinely-low → the maintainer's A/B/recency-gate call). This keeps the finding co-located with the decision it
informs. Do NOT tick spec 122's decision checkboxes or lift its BLOCKED banner.

### 3. Safe fixes ONLY (apply iff the diagnosis unambiguously finds one; otherwise ship steps 1–2)

Permitted, narrow, non-formula fixes:
- **Relevance over-drop:** if `IsRelevant` demonstrably drops real, on-topic articles (a genuine matching bug —
  not the intended single/short-ticker tightening), correct it with the smallest change. Infrastructure;
  fingerprint-neutral.
- **Outlet mis-tiering:** if specific genuine outlets fall to `UnknownWeight` purely because they are missing
  from the tier map, add them to the appropriate existing tier in the `Radar:Attention` configuration/defaults.
  This **re-stamps the fingerprint** — re-pin `ScoringConfigVersion` by RUNNING the pinned test (never by hand),
  and record old → new in the PR body and the default-profile `_comment`. Config edit, not AD-6.

**Explicitly OUT OF SCOPE (do NOT do — these are the spec-122 decision):** changing the Attention formula shape;
adding a velocity/loudness/volume term; making `weightedBreadth` count pre-collapse distinct publishers (this
changes scores and is the structural question); altering the spec-109 collapse algorithm's bucketing or
representative choice; any price/market-cap/intraday term (AD-14). If breadth-loss is attributed to the
collapse or to counting the post-collapse set, **STOP and record it as a finding** — do not "fix" it here.

---

## Tests

- **New characterization test** (in the scoring test project alongside `RadarScoreFormulaV7Tests` / the engine
  tests): single-event-many-publishers vs. spread-coverage Attention, through the real engine+collapse+formula,
  asserting the documented numbers. Deterministic, no network, AD-3 (repeatable).
- **If a relevance fix is applied:** a `NewsAttentionCollector` test proving the previously-dropped on-topic
  article is now kept and the intended single/short-ticker tightening still holds (no `V`/`TR` regression).
- **If a tier-map fix is applied:** the added outlets now resolve to the intended tier weight (not
  `UnknownWeight`); the fingerprint pin test is re-pinned to the new value.
- All existing scoring/collector/fingerprint tests stay green. Full gate: `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Diagnosis-first: the characterization test + findings are the deliverable; production
  changes are permitted ONLY as the narrow, unambiguous fixes above.
- **No formula-structure change** — stays `radar-formula-v7`; **no `_formula.Version` / `RuleSetVersion` bump.**
  A tier-map config correction MAY re-stamp `ScoringConfigVersion` (re-pin by running the test); a relevance-only
  fix or test-only outcome leaves fingerprints AI-OFF `8f4b59efd288` / AI-ON `2ef5ef96cce2` unmoved.
- **AD-14 absolute:** no price / market-cap / trading-volume / intraday-move term, ever.
- **AD-6 respected:** the formula shape and the spec-109 collapse are untouched; any structural finding is
  reported to spec 122, not implemented here.
- Reuse over copy (CLAUDE.md): route any shared publisher/normalisation helpers through their existing homes;
  do not paste a second copy.

---

## Acceptance criteria

- [ ] A deterministic characterization test reproduces the single-event-many-publishers Attention through the
      real engine + spec-109 collapse + formula, and records the numbers (documents current behaviour).
- [ ] The PR body attributes the breadth gap to a specific stage (collapse / tiering / relevance / genuinely-low),
      and a dated "Diagnosis findings (spec 124)" note is appended under spec 122's decision block.
- [ ] Any applied fix is narrow and non-formula: a relevance-filter correction (fingerprint-neutral, no `V`/`TR`
      regression) and/or a tier-map config correction (fingerprint re-pinned by running the test, old → new
      recorded). If no unambiguous bug is found, steps 1–2 alone are a valid completion.
- [ ] The Attention formula shape, the spec-109 collapse, and `_formula.Version` / `RuleSetVersion` are UNCHANGED;
      spec 122's BLOCKED banner and decision checkboxes are left as-is.
- [ ] No price/market-cap/volume term (AD-14). `dotnet build` and `dotnet test` (Release) pass.
