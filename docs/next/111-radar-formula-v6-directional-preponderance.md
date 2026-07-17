# Task: radar-formula-v6 — reward corroborated directional consensus (a strong minority isn't washed out)

> **CALIBRATION / DE-NOISING REWORK — slice 3 of 4 (failure #1).** Part of the directed signal→score
> de-noising rework diagnosed from the live 2026-07-17 run (AEHR fixture). The scoring freeze is
> deliberately **LIFTED** for this rework (maintainer decision 2026-07-17). Spec 108's continuity-aware
> efficacy segmentation (PR #111 merged) protects the efficacy line across the formula re-version this
> slice causes.
>
> **SEQUENCING.** Dispatch AFTER 109 (media collapse) and 110 (insider sell asymmetry), so the formula
> change is measured on a de-noised, recalibrated baseline. This is a **formula STRUCTURE** change → it
> bumps `_formula.Version` (`radar-formula-v5 → v6`) and re-stamps the fingerprint. **Requires maintainer
> approval of the exact structure** (AD-6: the formula shape is maintainer-co-designed). Sequence, do not
> parallelize with 109/110/112.

## Overview

On the live 2026-07-17 run, AEHR had a strong, **corroborated** positive thesis — ~4 `CustomerWin`
signals (EV silicon-carbide + data-center optical production/follow-on orders) plus a
`StrategicPartnership` — yet a **single, uncorroborated** insider-sale Negative dragged its Trajectory
`79→68`. The current `TrajectoryScore` (AD-6, radar-formula-v5) is a confidence/recency-weighted **mean**
of `sign·strength` over directional signals. A mean gives a lone dissenting signal weight comparable to
each of the five corroborating signals, so **corroboration is not rewarded**: five agreeing customer wins
move Trajectory no more decisively than one would, and one countervailing signal can overturn the read.

Radar's philosophy is "signals before stories, **evidence before opinions**, corroboration matters." A
direction backed by **many independent, high-strength signals** should be more robust than a direction
asserted by **one** signal. This slice proposes `radar-formula-v6`: a **corroboration/consensus-aware
Trajectory** so a strong directional majority is not washed out by an isolated dissenter — while an
isolated dissenter is **still recorded** (never ignored; a corroborated negative cluster must still bite).

> **Do not overfit to AEHR, and do not silence negatives.** This is a general robustness property, not a
> "positive bias." A *corroborated* negative cluster (multiple deteriorating signals) must move Trajectory
> down decisively; only an *isolated, uncorroborated* dissenter should be damped relative to a strong
> agreeing majority. AEHR is the acceptance fixture, not a special case.

---

## Assignment

Worktree: any (sequence after 110)
Dependencies: 109 + 110 merged (rebase onto their fingerprints)
Conflicts with: 109, 110, 112 (shared fingerprint pin + `default.json`; edits the formula). Sequence,
  do not parallelize.
Estimated time: ~1-2 hours (plus a maintainer approval gate on the structure)

---

## Project structure changes

Add:
- `src/Radar.Application/Scoring/RadarScoreFormulaV6.cs` — the new formula (copy v5, change **only** the
  Trajectory component; delete `RadarScoreFormulaV5.cs` per the spec-implementation checklist —
  do not leave it dormant).

Modify:
- Any magnitude constants the new consensus term introduces go into
  `src/Radar.Application/Scoring/ScoringWeights.cs` (config, not code identity — AD-6 v5) with defaults
  and `Validate()` coverage; **only the structural shape** stays in the formula.
- DI registration to construct `RadarScoreFormulaV6`.
- `tests/Radar.Application.Tests/Scoring/RadarScoreFormulaV5Tests.cs` → ported to
  `RadarScoreFormulaV6Tests.cs` (all prior behaviour that is unchanged must stay green).
- `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — re-pin the default
  fingerprint (moves via `_formula.Version` v5→v6).
- `scripts/run-profiles/default.json` — update the `_comment` (formula v5→v6, new fingerprint).
- `docs/architecture-decisions.md` — add the `radar-formula-v6` AD-6 refinement entry (structure change,
  maintainer-approved, with the exact shape and the AEHR before/after).

---

## Implementation details

- **Change ONLY the Trajectory component.** Attention (incl. spec 109's collapsed media set),
  EvidenceConfidence, SignalVelocity, Opportunity, recency, the empty-window behaviour, the
  `PreviousSignals`/window/provenance/contribution rules, and the direction SIGNS (`Positive +1` /
  `Negative -1` / `Neutral`,`Mixed` = 0) stay **byte-for-byte** as v5. (Neutral/Mixed are already excluded
  from Trajectory in v5 — this slice does not re-open that; it addresses the *directional* mean's
  sensitivity to an isolated dissenter, which is the real "washed out" mechanic once spec 109 removes the
  duplicate-media noise.)
- **Proposed structure (maintainer-approvable).** Split the directional signals into a **positive mass**
  and a **negative mass** — each the confidence·recency·strength sum over that direction — and combine
  them with a **corroboration factor** so the *number of independent agreeing signals* strengthens a
  direction and a **lone** dissenter is damped relative to a corroborated majority. Concretely, one clean
  option: `T_raw = (M+ − M−) / (M+ + M− + k)` scaled into the existing `[-10,10]` band, where `M+`/`M−`
  are the directional masses and `k` (config, `ScoringWeights`) is a corroboration-smoothing constant so
  a *single* signal (small total mass) cannot swing `T_raw` to an extreme, but a *corroborated* majority
  (large mass) can — and an equally-corroborated negative majority swings it down symmetrically. **Present
  the exact chosen form + constants in the PR for maintainer sign-off** (AD-6). The essential invariants
  the reviewer must check:
  - Monotone: adding a positive signal never lowers Trajectory; adding a negative never raises it.
  - Symmetric in direction: a corroborated negative cluster moves Trajectory down as decisively as a
    corroborated positive cluster moves it up (no positive bias).
  - Robust: an isolated single dissenter against a strong agreeing majority moves Trajectory less than it
    does under the v5 mean (the exact "washed out" fix), but is **not zeroed** (the dissent is recorded).
  - Empty directional set → neutral `50` (unchanged).
  - Pure/deterministic (AD-3); every component still clamps to `[0,100]`.
- **Contributions unchanged.** Still one provenance-carrying `ScoreContribution` per current-window signal
  in input order; the contribution weight formula may keep the v5 per-signal `sign·strength·conf·recency`
  (provenance is per-signal; the consensus shaping is an aggregate over them).
- **Version obligation (AD-6 / AD-10).** This is a formula **structure** change → bump `_formula.Version`
  to `radar-formula-v6`; `ScoringVersion` advances automatically and `ScoringConfigVersion` re-stamps via
  the derived fingerprint. Put every new **magnitude** in `ScoringWeights` (config) so future tuning of the
  corroboration constant is a config edit, not another formula class. Delete `RadarScoreFormulaV5`; port
  its tests.

---

## Tests

`RadarScoreFormulaV6Tests` (ported + new):
- All unchanged-component tests from v5 stay green (Attention/EC/Velocity/Opportunity byte-identical for
  the same inputs).
- Corroboration: five agreeing positive signals produce a higher Trajectory than one positive signal of
  the same strength (consensus rewarded).
- Robustness: a strong positive majority + one lone negative yields a higher Trajectory than the v5 mean
  would for the same inputs, and higher than the majority-with-a-*corroborated*-negative-cluster case
  (isolated dissent damped, corroborated dissent not).
- Symmetry: the negative-majority mirror of each case moves Trajectory down by the same shape (no positive
  bias).
- Monotonicity + empty-window + clamp invariants hold.

`ScoringConfigFingerprintTests`: default fingerprint re-stamps to the new pinned value (v6);
recompute-from-`EffectiveScoringConfig` equals the stamp.

---

## Constraints

- Target .NET 10 / `net10.0`, C# 14.
- Structure change is maintainer-gated (AD-6): the exact Trajectory shape + constants need sign-off.
- Only the Trajectory component changes; everything else is byte-identical to v5.
- Magnitudes → `ScoringWeights` (config); only shape is versioned. Delete the old formula, port tests.
- Provenance and determinism preserved (AD-3). No advice language (AD-9).

---

## Acceptance criteria

- [ ] `RadarScoreFormulaV6` changes only Trajectory; all other components byte-identical to v5 (proven by
      ported tests).
- [ ] A corroborated directional majority is rewarded; an isolated dissenter is damped relative to it but
      not ignored; a corroborated dissenting cluster still bites; direction-symmetric; monotone; clamps
      hold.
- [ ] `_formula.Version` = `radar-formula-v6`; `ScoringConfigVersion` re-stamps automatically. Pinned
      fingerprint test + `default.json` + a new AD-6 `radar-formula-v6` ledger entry added. `v5` deleted,
      tests ported.
- [ ] AEHR acceptance fixture: with the de-noised (109) and insider-recalibrated (110) baseline, AEHR's
      corroborated positive thesis is no longer overturned by the lone insider-sale Negative — its
      genuine positives are no longer washed out below the Neutral/dissent mass.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
