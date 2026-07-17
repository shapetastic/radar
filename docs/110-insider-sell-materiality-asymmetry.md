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

By default `InsiderMaterialityWeights.BuyTiers == SellTiers` (spec 96 split the tables but defaulted both
to the same spec-93 values), so a `$1.6M` sell maps to the `$1,000,000 → 7` tier — the top "very material"
weight. There are actually **two** failures tangled here, and they need different fixes:

1. **Strength is overstated relative to materiality.** A ~$1.6M sale maps to strength `7` purely on dollar
   amount. For a company of AEHR's size, $1.6M is a modest sale; strength `7` overstates it. Sell strength
   should scale to **materiality** (dollar size relative to the company / the insider's holdings) so a
   small sale is a small signal and a large one is a large one. This is the fix this slice makes.
2. **One signal dominated a corroborated majority and flipped the *label*.** Even a *correctly* modest
   negative shouldn't, on its own, override five corroborating positives and set "Thesis deteriorating."
   That domination is a **formula** problem — it is **spec 111's** job (directional preponderance), **not**
   something to fix by muting the sale into non-existence.

So this slice recalibrates sell **materiality** (a mild buy≫sell prior — sells are noisier in aggregate —
*plus* size-scaling) while **deliberately preserving the signal in a discretionary sale.** The AEHR sale
is already classified `open-market` (discretionary), *not* `10b5-1`/RSU — that is precisely the
*informative* kind of insider sale, and it was recent. A recent discretionary sell is a **modest-but-real
negative, not noise.** The goal is "proportionate," not "muted": don't let a lone trim flip the thesis
(111 secures that), but don't blind Radar to genuine insider selling either.

> **Radar must NOT use the price reaction to judge the sale.** (AEHR fell ~84→81 the same day — which is
> also just profit-taking on a 260%-YTD runner, and 20k shares is tiny vs AEHR's daily volume, so the
> sale did not move the price.) Price is validation-only, never a scoring input (AD-14); wiring "price
> dropped ⇒ the sale was bad" would make Radar a momentum-chaser and break the core invariant. The sale
> is judged on its own attributes (type, size, cluster) — never on what the price did.

This is exactly the experiment spec 96's separate `SellTiers` seam was built to express. It is a
**config-magnitude change to the code defaults** (the baseline `default.json` omits `Radar:Scoring`, so
the new defaults ARE the baseline), **not** a rule-structure change — no `RuleSetVersion` bump.

> **Do not overfit to AEHR.** Recalibrate the general sell-materiality curve on the principled
> buy≫sell prior *and* materiality-scaling, not to hit a specific AEHR number. Keep BUY tiers unchanged
> (insider buys remain a strong signal). Keep the multi-insider cluster boost — a *cluster* of sales IS
> more informative than a lone trim. **Do NOT mute discretionary sells into noise:** scale to materiality
> so a small lone sale is a *small* negative (not zero, not dominant), while a large or clustered
> discretionary sale still registers strongly.

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

- **New default `SellTiers` (materiality-scaled, mild buy≫sell prior).** Propose a sell curve that scales
  with materiality rather than a blanket mute: a `$1.6M` lone discretionary sale should land at a
  **modest** strength — clearly below the top `7` "very material" tier, but **not** muted to noise (keep
  it a real, small negative, roughly low-mid range, at/above the reviewer's `MinMaterialStrength = 3`
  floor) — while a genuinely large sale (e.g. `≥ $25–50M`) still reaches a high strength and a cluster
  still gets the boost. The point is *proportionality*, not silence: discretionary sells retain signal.
  **The exact tier table is maintainer-approvable** — present a concrete proposal in the PR (respecting
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

- [ ] Default `SellTiers` scales to materiality with a mild buy≫sell prior; a routine ~$1.6M lone
      discretionary sale maps to a *modest* strength (well below the top `7` tier but not muted to noise —
      it stays a real, small negative), while large/clustered discretionary sales still register
      materially and BUY tiers are unchanged. Discretionary sells are **not** muted into noise.
- [ ] Change is config-magnitude only: `ScoringConfigVersion` re-stamps automatically via the hashed
      insider descriptor; **no** `RuleSetVersion` / `_formula.Version` bump. Pinned fingerprint test +
      `default.json` updated.
- [ ] `InsiderMaterialityWeights.Validate` passes for the new default table.
- [ ] AEHR acceptance fixture: a lone routine insider trim after record results no longer *by itself*
      sets "Thesis deteriorating" — but it remains a real, modest negative in the signal set (not muted to
      noise). (Spec 111 secures the non-flip durably at the formula level; this slice ensures the sell
      strength is *proportionate to materiality*, and that discretionary sells keep signal.)
- [ ] The `InsiderBuying`→(better name) rename is recorded as a deferred follow-up, not done here.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
