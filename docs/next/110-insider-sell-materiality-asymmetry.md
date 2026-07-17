# Task: Recalibrate default insider SELL materiality so a routine trim can't flip the label

> **CALIBRATION / DE-NOISING REWORK — slice 2 of 4 (failure #4).** Part of the directed
> signal→score de-noising rework diagnosed from the live 2026-07-17 run (AEHR fixture). The scoring
> freeze is deliberately **LIFTED** for this rework (maintainer decision 2026-07-17). Spec 108's
> continuity-aware efficacy segmentation (PR #111 merged) protects the efficacy line across the
> re-stamp this slice causes.
>
> **SEQUENCING.** Dispatch AFTER 109 (media collapse) and BEFORE 111 (formula-v6), so the formula
> change is measured on the recalibrated insider baseline. Re-stamps the default fingerprint and edits
> the pinned fingerprint test + `default.json` comment — **sequence, do not parallelize** with
> 109/111/112.

## Overview

On the live 2026-07-17 run, AEHR — which had just posted record $60.7M bookings, a Q4 beat, and FY2027
guidance "well above" estimates — was labelled **"Thesis deteriorating."** The sole fresh Negative was
`SCOTT GEOFFREY GATES sold 20,000 shares (~$1.6M)` → `InsiderBuying (Negative), strength 7`. That one
signal dragged Trajectory `79→68` and tripped the action policy's deterioration branch
(`delta <= -5`, evaluated *before* opportunity — `WeeklyReportActionPolicyV1`), setting "Thesis
deteriorating" on a company that just posted record results.

Root cause: insider **buys** and **sells** are treated with **symmetric** materiality. By default
`InsiderMaterialityWeights.BuyTiers == SellTiers` (spec 96 split the tables but defaulted both to the
same spec-93 values), so a `$1.6M` sell maps to the `$1,000,000 → 7` tier — the same conviction weight
a `$1.6M` open-market *buy* gets. This is wrong on the evidence: an insider **purchase** is a strong,
rare conviction signal (executives buy for essentially one reason); an insider **sale** is a weak signal
(taxes, diversification, 10b5-1 plans, lifestyle) — and a ~$1.6M exec trim after a ~260% YTD run is
routine, not thesis-deteriorating. The fix is a **buy-vs-sell asymmetry** in the *default* sell tiers so
a routine, uncorroborated sale contributes low strength and cannot by itself flip the thesis label.

This is exactly the experiment spec 96's separate `SellTiers` seam was built to express. It is a
**config-magnitude change to the code defaults** (the baseline `default.json` omits `Radar:Scoring`, so
the new defaults ARE the baseline), **not** a rule-structure change — no `RuleSetVersion` bump.

> **Do not overfit to AEHR.** Recalibrate the general sell-materiality curve on the principled
> buy≫sell-conviction asymmetry, not to hit a specific AEHR number. Keep BUY tiers unchanged (insider
> buys remain a strong signal). Keep the multi-insider cluster boost — a *cluster* of sales IS more
> informative than a lone trim.

---

## Assignment

Worktree: any (sequence after 109)
Dependencies: 109 merged (rebase onto its new fingerprint)
Conflicts with: 109, 111, 112 (shared fingerprint pin + `default.json`). Sequence, do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

Modify:
- `src/Radar.Application/SignalExtraction/InsiderMaterialityWeights.cs` — change the **default
  `SellTiers`** (only) to a weaker, asymmetric curve. `BuyTiers` default unchanged; `ClusterBoost`
  unchanged.
- `tests/Radar.Application.Tests/SignalExtraction/KeywordSignalExtractorTests.cs` (and insider-weights
  tests) — update the pinned default sell-strength expectations.
- `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — re-pin the default
  fingerprint (the insider descriptor is hashed by value, spec 96, so it moves).
- `scripts/run-profiles/default.json` — update the fingerprint note + record the new default sell tiers
  in the `_comment`.

No production-code *structure* changes: this is a default-magnitude edit through the existing spec-96
config seam.

---

## Implementation details

- **New default `SellTiers` (asymmetric).** Propose a materially weaker sell curve than the buy curve so
  a lone sub-cluster sale is low-strength — e.g. shift the sell thresholds up and the strengths down so a
  `$1.6M` sale lands around strength `2–3` (reviewer `MinMaterialStrength = 3` territory) rather than
  `7`, while a genuinely large sale (e.g. `≥ $25–50M`) can still reach a mid strength. **The exact tier
  table is maintainer-approvable** — present a concrete proposal in the PR (respecting
  `InsiderMaterialityWeights.Validate`: descending by `MinInclusive`, `decimal.MinValue` floor,
  strengths `1..10`, non-increasing walking descending). Keep the floor tier low (routine sales are
  noise). BuyTiers and ClusterBoost stay at their spec-93 defaults.
- **Why config, not code-version.** Per AD-10 as amended by spec 96, the insider buy/sell tiers are
  hashed into `ScoringConfigVersion` **by value**, so changing the default `SellTiers` re-stamps the
  fingerprint **automatically** — **no `RuleSetVersion` bump, no `_formula.Version` bump.** The rule
  *structure* (phrase → `InsiderBuying` direction) is unchanged; only the sell magnitude table moves.
- **Label-flip guard is a consequence, not a special case.** With a weaker lone-sell strength, the
  Negative contribution to the confidence/recency-weighted Trajectory mean shrinks, so a routine sale no
  longer produces a `≥5`-point Trajectory drop and the `WeeklyReportActionPolicyV1` deterioration branch
  no longer fires on it. (Spec 111 hardens this further at the formula level; this slice is the honest
  magnitude fix.) Do **not** edit the action policy in this slice.
- **Naming note (deferred, out of scope here).** `InsiderBuying (Negative)` for a *sale* is genuinely
  confusing. Renaming the `SignalType` (e.g. `InsiderTransaction`/`InsiderTrading`) is a domain +
  rule-structure change with on-disk-name migration implications (signals/snapshots persist the enum by
  name), so it is a **separate future cleanup slice**, not folded here. Record it as a follow-up; do not
  rename in this PR.

---

## Tests

- Insider-weights / extractor tests: a `$1.6M` (and representative sub-cluster) open-market **sale**
  maps to the new low sell strength; an open-market **buy** of the same value is unchanged (asymmetry
  proven); a multi-insider cluster sale still gets the cluster boost; a large sale still reaches a
  mid strength. Validation still passes for the new default table.
- `ScoringConfigFingerprintTests`: default fingerprint re-stamps to the new pinned value;
  recompute-from-`EffectiveScoringConfig` equals the stamp; `insiderDesc` reflects the new sell tiers.
- A scoring test: a company with a strong positive-directional majority + one lone routine insider sale
  no longer drops Trajectory by `≥5` from the sale alone (the deterioration label driver is removed).

---

## Constraints

- Target .NET 10 / `net10.0`, C# 14.
- Config-magnitude change only — no `RuleSetVersion` / `_formula.Version` bump; the fingerprint
  re-stamps by value (spec 96 / AD-10).
- Preserve provenance and the buy/cluster behaviour; change **only** the default `SellTiers`.
- General curve, not an AEHR fit. No advice language (AD-9).

---

## Acceptance criteria

- [ ] Default `SellTiers` is asymmetric to (weaker than) `BuyTiers`; a routine ~$1.6M lone insider sale
      maps to a low strength (~2–3), while insider buys and large/cluster sales retain material weight.
- [ ] Change is config-magnitude only: `ScoringConfigVersion` re-stamps automatically via the hashed
      insider descriptor; **no** `RuleSetVersion` / `_formula.Version` bump. Pinned fingerprint test +
      `default.json` updated.
- [ ] `InsiderMaterialityWeights.Validate` passes for the new default table.
- [ ] AEHR acceptance fixture: a lone routine insider trim after record results no longer drags
      Trajectory `≥5` and no longer sets "Thesis deteriorating."
- [ ] The `InsiderBuying`→(better name) rename is recorded as a deferred follow-up, not done here.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
