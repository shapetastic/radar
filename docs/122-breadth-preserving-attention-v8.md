# Task: Breadth-preserving Attention (`radar-formula-v8`) — credit distinct publishers across a collapsed event

> **DIRECTION CHOSEN: Option D (breadth-preserving collapse). AD-6 SIGN-OFF REQUIRED AT PR.** This closes OPEN
> DECISION (b) — the Attention/notedness metric UNDERSTATES how noticed a company is. The spec-124 diagnosis
> (now on `main`) proved the cause is **structural, not a bug**: spec-109's same-event collapse discards genuine
> **distinct-publisher breadth**, not just duplicate volume. This is a formula-STRUCTURE change
> (`radar-formula-v7 → v8`), so per AD-6 it needs the maintainer's in-session sign-off on the **shape + the
> default `CollapsedBreadthCredit`**. The direction (D) is chosen; run this via `run next` — proceed unattended,
> record the exact shape in the PR + the AD-6 ledger, and **request the shape/constant sign-off prominently in
> the PR** (the established v6/v7 pattern). Do NOT self-approve the AD-6 constant — the maintainer approves it at
> PR review.

## Overview / problem

`AttentionScore` (`radar-formula-v7`) measures **tier-weighted distinct third-party publisher breadth**, computed
over the signal set **after** the spec-109 `MediaAttentionCollapse` has run:

```
weightedBreadth = Σ over distinct third-party SourceName of tierWeight(name)     // post-collapse
reach           = weightedBreadth + MediaReachWeight · mediaSignalCount          // post-collapse
Attention       = 100 · reach / (reach + AttentionHalfSaturation)                // AttentionHalfSaturation = 3
```

The spec-124 characterization test proved the gap precisely (numbers below are from that test, on the default
tier map — 15 distinct outlets, 7 on the `1.0` genuine tier, 8 absent → `UnknownWeight` 0.25):

| Scenario | reach | Attention |
|---|---|---|
| 15 outlets, **one event** (burst) | `0.25 + 0.10·1 = 0.35` | **10** |
| Same 15, **spread** across distinct events | `9.0 + 0.10·15 = 10.5` | **78** |
| Burst set handed **directly to the formula**, bypassing only the collapse | `9.0 + …` | **78** |

The third row scoring identically to spread pins the **entire** 10→78 gap on the collapse: it keeps ONE
representative per event, so the formula's post-collapse breadth count sees one `SourceName` and
`mediaSignalCount` 1. Fifteen *different* genuine outlets choosing to cover one story is **genuine notedness**
(breadth) that the collapse is throwing away as collateral damage while doing its legitimate job (killing
duplicate *volume*).

**Option D** separates those two: keep the volume collapse (one representative, `mediaSignalCount` stays
collapsed — no loudness/velocity re-admission), but let the **breadth** term count the distinct publishers of a
collapsed event. Tier-weighting is unchanged, so it remains the anti-mill guard — 15 mill re-posts still sum to
≈1.5, 15 genuine outlets to 15. This is a truer reading of "breadth" than v7 and does **not** re-open the hype
loop (spec 109's real target — syndicated duplicate volume — is still collapsed and still tier-down-weighted).

> **Why not Option B (velocity term)?** B re-admits loudness/velocity — the transient-burst shape spec 109
> deliberately closed. D fixes the actual complaint (distinct genuine outlets not counted) without admitting raw
> volume or a time-derivative. B is recorded in the decision history below but is **not** the chosen direction.

**AD-14 (hard):** no price / market-cap / trading-volume / intraday-move term. D uses only Radar's own non-price
publisher-breadth evidence. Preserved.

---

## Assignment

Worktree: any. Edits the scoring formula (`radar-formula-v7 → v8`), `ScoringWeights`, the collapse→formula
plumbing, and ports tests.
Dependencies: none outstanding (spec 121/123/124 all merged). Conflicts with any slice touching
`RadarScoreFormulaV7`, `ScoringEngine`, `ScoringInput`, `MediaAttentionCollapse`, `ScoringWeights`, or
`ScoringConfigFingerprint`.
Estimated time: ~2.5–3 hours.

---

## Grounding facts (verified against the code, 2026-07-21)

- **Attention breadth is counted post-collapse** in `RadarScoreFormulaV7`
  (`src/Radar.Application/Scoring/RadarScoreFormulaV7.cs`, ~194–202): `weightedBreadth` sums
  `_sourceWeights.WeightFor(SourceName)` over distinct third-party `SourceName`s of the signal set it is handed;
  `reach = weightedBreadth + _weights.MediaReachWeight * mediaCount`; `mediaCount` = surviving
  `SignalType.MediaAttention` signals; `AttentionHalfSaturation` default 3, `MediaReachWeight` default 0.10.
- **The collapse runs in `ScoringEngine`** (`src/Radar.Application/Scoring/ScoringEngine.cs`) via
  `MediaAttentionCollapse.Collapse(...)` (`src/Radar.Application/Scoring/MediaAttentionCollapse.cs`, version
  `media-collapse-v1`) BEFORE building `ScoringInput` — so the engine still holds the **pre-collapse** signal
  list at that point. The collapse groups same-event `MediaAttention` signals and keeps the earliest as the
  single representative; it already computes each bucket's membership.
- **Tier weights are config** (`ConfiguredAttentionSourceWeights` over `AttentionSourceTierOptions`, bound from
  `Radar:Attention`), and are folded into `ScoringConfigVersion` via `CanonicalDescriptor()`. Unchanged by this
  spec — mills ≈0.1, unknown default 0.25, genuine 1.0 stay as-is.
- **`ScoringWeights`** (`src/Radar.Application/Scoring/ScoringWeights.cs`) is the home for tunable magnitudes,
  hashed into the fingerprint and `Validate()`-checked; **`ScoringConfigFingerprint`** folds `_formula.Version`
  and the weights. Adding a weight + bumping the formula version re-stamps both AI-OFF and AI-ON pins.
- **`AttentionBreadthCharacterizationTests`** (`tests/Radar.Application.Tests/Scoring/…`, spec 124) pins the
  10 / 78 / 78 numbers above — it becomes the acceptance proof for this slice (burst must rise to match spread).

---

## The v8 shape (for AD-6 sign-off)

Only the **Attention reach** changes; Trajectory, EvidenceConfidence, SignalVelocity, the Opportunity discount,
recency, contributions, direction-signs, and the empty-window path stay **byte-identical to v7**.

```
breadthSurvivors      = Σ over distinct third-party publishers in the POST-collapse set   of tierWeight(p)
breadthCollapsedExtra = Σ over distinct third-party publishers present ONLY in collapsed-away
                        members (i.e. not already in the survivor set)                     of tierWeight(p)
reach = breadthSurvivors + CollapsedBreadthCredit · breadthCollapsedExtra
                         + MediaReachWeight · mediaSignalCount        // mediaSignalCount stays POST-collapse
Attention = 100 · reach / (reach + AttentionHalfSaturation)
```

- **New config magnitude** `ScoringWeights.CollapsedBreadthCredit ∈ [0,1]`, `Validate()` enforces the range.
  **Recommended default `1.0`** (a distinct genuine outlet is breadth regardless of whether coverage clustered
  on one event — the exact complaint). It is the AD-6 constant the maintainer signs off (or defers to a live
  sweep like spec-94's `MediaReachWeight`).
- **`CollapsedBreadthCredit = 0.0` reproduces `radar-formula-v7` byte-for-byte** (breadthCollapsedExtra drops
  out; reach == v7). **Pin this** — it is the safety proof that v8 is a pure superset.
- **`mediaSignalCount` is unchanged** (post-collapse) — volume/loudness is still collapsed; no velocity term, no
  raw-article-count dominance (spec 94), AD-14 clean.
- Anti-mill guard intact: `breadthCollapsedExtra` is tier-weighted, so mill/unknown re-posts of one event add
  ≈0.1/0.25 each, never 1.0.

**Predicted effect (default 1.0), from the spec-124 fixture:** burst reach `9.0 + 0.10·1 = 9.1` → Attention
**≈75** (up from 10), essentially matching spread's 78; the small residual is the legitimately-different volume
term (burst has 1 surviving media event vs. spread's 15). Re-pin the exact number by running.

---

## Implementation details

1. **Add `CollapsedBreadthCredit`** to `ScoringWeights` (default 1.0; `Validate()` range `[0,1]`); fold it into
   `ScoringConfigFingerprint` in the established fixed-order position (after the last existing Attention/discount
   weight). Bind it under the existing `Radar:Scoring` profile surface.
2. **Expose the collapsed-away publishers.** In `ScoringEngine`, the pre-collapse third-party attention signal
   list is in hand where `MediaAttentionCollapse.Collapse(...)` is called. Thread the information the formula
   needs into `ScoringInput` — **recommended:** pass the **pre-collapse** third-party attention publisher set (or
   the pre-collapse signal list) alongside the existing collapsed `Signals`, and let v8 derive
   `breadthCollapsedExtra` = pre-collapse distinct publishers minus post-collapse distinct publishers. Keep the
   formula the **single owner** of the breadth math (do not split Attention across engine + formula). Do **not**
   change `MediaAttentionCollapse`'s transform (which signals survive) or its `media-collapse-v1` version /
   descriptor — it is behaviour-unchanged; you are only reading the pre-collapse set the engine already has.
3. **`RadarScoreFormulaV8`**: copy v7, change ONLY the reach block per the shape above, bump `Version` to
   `radar-formula-v8`. **Delete `RadarScoreFormulaV7.cs`** (AD-6: no dormant formula) and update DI/registration
   to construct v8.
4. **Port tests** `RadarScoreFormulaV7Tests → RadarScoreFormulaV8Tests`; recompute any Attention-dependent pins.
   Add: (a) the `CollapsedBreadthCredit = 0` byte-identical-to-v7 pin on the burst fixture (Attention 10); (b) a
   default-credit test where the single-event-many-publishers burst rises to ≈ the spread value; (c) a mill-tier
   test showing collapsed-away mill outlets add ≈0.1 each (anti-hype guard holds); (d) monotonicity in
   `CollapsedBreadthCredit`.
5. **Update `AttentionBreadthCharacterizationTests`** (spec 124) to the v8 numbers — the burst row is now the
   *fixed* behaviour; keep a `credit=0` case asserting the old 10 as the regression anchor.
6. **Re-pin the fingerprint by RUNNING the test** (never by hand): both AI-OFF `8f4b59efd288` and AI-ON
   `2ef5ef96cce2` re-stamp (formula version + new weight). Record old → new in the PR body and the
   default-profile `_comment`.
7. **AD-6 ledger:** add `### Refinement — radar-formula-v8 (spec 122)` to `docs/architecture-decisions.md`
   documenting the shape, the `CollapsedBreadthCredit` default, and the byte-identical-at-0 property; update the
   status footer. **Request the maintainer's shape + default sign-off prominently in the PR.**

---

## Constraints

- Target `net10.0`, C# 14. **Only** the Attention reach term changes; every other v7 component stays
  byte-identical (reviewer should diff v7↔v8 line-by-line, as for v6/v7).
- **AD-6:** structural change → `_formula.Version` v7→v8, delete v7, port tests, re-pin by running, ledger entry,
  in-session maintainer sign-off on shape + default `CollapsedBreadthCredit` (requested at PR).
- **AD-14 absolute:** no price / market-cap / trading-volume / intraday-move term. `mediaSignalCount` stays
  post-collapse — no velocity/loudness term is introduced.
- **Anti-hype posture preserved:** breadth is tier-weighted distinct publishers only; mills stay ≈0.1; raw
  article volume never dominates (spec 94). Philosophy: signals before stories, avoid hype loops.
- Reuse over copy (CLAUDE.md): route through existing homes; the collapse transform and tier map are unchanged.

---

## Acceptance criteria

- [ ] `radar-formula-v8` credits distinct third-party publishers of a spec-109-collapsed event to the Attention
      **breadth** term (`breadthCollapsedExtra`, tier-weighted), while `mediaSignalCount` stays post-collapse.
- [ ] `CollapsedBreadthCredit ∈ [0,1]` added to `ScoringWeights` (default 1.0, `Validate()`-ranged, fingerprinted);
      at `0.0` v8 reproduces v7 byte-for-byte (pinned by test).
- [ ] The spec-124 single-event-many-publishers burst rises from Attention 10 to ≈ the spread value at the
      default credit; the `credit=0` regression anchor still asserts 10.
- [ ] Only the Attention reach term changed; all other v7 components byte-identical; `RadarScoreFormulaV7`
      deleted, tests ported to v8; `media-collapse-v1` transform/descriptor unchanged.
- [ ] Fingerprints re-pinned by RUNNING the test (AI-OFF + AI-ON both re-stamp), old → new recorded; AD-6 ledger
      entry added; maintainer shape/default sign-off requested prominently in the PR.
- [ ] No price/market-cap/volume term (AD-14). `dotnet build Radar.sln -c Release` and
      `dotnet test Radar.sln -c Release --no-build` pass.

---

## Decision history (superseded — kept for provenance)

- **Option A** (audit collector/tiering for a measurement bug) — done as **spec 124**; found **no bug** (no live
  evidence corpus; the gap is structural). Its characterization test is the acceptance proof for this slice.
- **Option B** (bounded recency-gated velocity term) — considered and **not chosen**: re-admits loudness/velocity,
  the transient-burst shape spec 109 deliberately closed. Available as a fallback if D under-delivers on live data.
- **Option C** (report-only caveat) — already shipped via **spec 123** (Notedness line surfaces Attention +
  FollowingTier). Complementary; does not fix ranking.
- **Option D** (this spec) — breadth-preserving collapse. Chosen: fixes the actual complaint (distinct genuine
  outlets uncounted) without admitting volume/velocity; anti-mill guard intact via tier-weighting.
