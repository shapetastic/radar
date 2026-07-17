# Task: Make a confident raised-guidance earnings read materially move the thesis

> **CALIBRATION / DE-NOISING REWORK — slice 4 of 4 (failure #3).** Part of the directed signal→score
> de-noising rework diagnosed from the live 2026-07-17 run (AEHR fixture). The scoring freeze is
> deliberately **LIFTED** for this rework (maintainer decision 2026-07-17). Spec 108's continuity-aware
> efficacy segmentation (PR #111 merged) protects the efficacy line across the re-stamp this slice causes.
>
> **SEQUENCING.** Dispatch LAST in the rework (after 109/110/111), so the directional-read materiality is
> measured on the de-noised, recalibrated, robust-trajectory baseline. Re-stamps the default fingerprint
> and edits the pinned fingerprint test + `default.json` — sequence, do not parallelize.
>
> **The blocking CAUSE is already fixed.** The SEC `www.sec.gov` self-block that starved the AI
> earnings-read path is resolved by the global `*.sec.gov` throttle (PR #110, merged dcebe52, confirmed
> live 2026-07-17: 324 SEC requests/run, all 200, `www.sec.gov` reachable before *and* after a run). So
> this slice is **not** a throttle fix — it is the remaining **materiality** half: a confident directional
> earnings read must *count*.

## Overview

AEHR's most important document — the record-guidance earnings 8-K (FY2027 revenue guided +150%,
guidance "well above" estimates) — scored `GuidanceChange (Neutral), strength 3`. The deterministic
keyword extractor can only see the 8-K item-title metadata (`"results of operations"`, item 2.02) and
**cannot read** that guidance went *up* — the guidance detail lives in the EX-99.1 exhibit body, which
only the AI earnings-reader path (`ISecEarningsReleaseReader` → `IFilingAnalyzer` →
`IDirectionalFilingSignalSource`, specs 73/74/75) fetches. That AI path exists, supersedes the
deterministic Neutral when it fires (spec 78), and is now un-blocked (PR #110). Two gaps remain:

1. **Materiality.** When the AI read *does* fire, it emits `GuidanceChange` at
   `DirectionalFilingSignalOptions.Strength = 6` — **equal to the keyword maximum**. A confident,
   full-text "guidance raised well above estimates" read is a *stronger* signal than a generic keyword
   match, but it carries no more weight, so it cannot lift Opportunity past the `Investigate` gate (60).
   (Recorded as the deferred recalibration in the `radar-sec-www-block` note: "AI signal Strength=6 =
   keyword max, so Opportunity still won't clear the Investigate gate until the deferred Strength
   recalibration — spec 106 made Strength a config edit that re-fingerprints.")
2. **Fallback honesty.** When the AI path is unavailable (provider down), `GuidanceChange` stays the
   deterministic `Neutral, strength 3` — no positive credit for a record-guidance filing. This slice does
   **not** try to make the keyword extractor read the exhibit (that is the AI path's job); it ensures the
   AI read, when present, is materially decisive, and confirms the deterministic Neutral is not *harmful*
   (it is trajectory-excluded, so it is inert, not negative — verify and document, don't change).

Spec 106 already made the AI directional-filing magnitudes (`Strength`, `Novelty`, `MinConfidence`)
**config-bound and folded into the scoring fingerprint**. So this is a **config-magnitude recalibration**
of those defaults — no code-version bump; the fingerprint re-stamps by value.

> **Do not overfit to AEHR, and do not weaken the confidence gate.** Raise materiality only for
> *high-confidence* directional reads (the `MinConfidence` gate stays or tightens — a low-confidence read
> must still produce no directional signal, CLAUDE.md). A stronger `Strength` must apply symmetrically to
> a confident *deteriorating* read too (a real guidance cut should bite as hard as a raise lifts).

---

## Assignment

Worktree: any (sequence after 111)
Dependencies: 109 + 110 + 111 merged (rebase onto the current fingerprint). PR #110 (SEC throttle) —
  already merged.
Conflicts with: 109, 110, 111 (shared fingerprint pin + `default.json`). Sequence, do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

Modify:
- `src/Radar.Infrastructure/Filings/DirectionalFilingSignalOptions.cs` — recalibrate the default
  `Strength` (and, if the maintainer approves, `Novelty` / `MinConfidence`) so a confident directional
  earnings read outweighs a generic keyword `GuidanceChange`. Keep values in domain range (1..10) and
  above the reviewer floors.
- `tests/Radar.Infrastructure.Tests/Filings/DirectionalFilingSignalSourceTests.cs` — update pinned
  Strength/Novelty expectations and the `ScoringDescriptor()` string.
- `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` (and/or the Infrastructure
  fingerprint test that folds the directional descriptor, spec 106) — re-pin the default fingerprint.
- `scripts/run-profiles/default.json` — update the `_comment` (new directional Strength + fingerprint).
- Add a short note/verification (test or comment) that the deterministic `"results of operations"` →
  `GuidanceChange (Neutral)` rule is inert (trajectory-excluded), not harmful — no change to that rule.

---

## Implementation details

- **Recalibrate the AI directional Strength (config default).** Propose a concrete new default `Strength`
  (e.g. `8`) so a confident, full-text guidance read is materially stronger than the keyword max (6) and
  can, combined with a corroborated positive trajectory (spec 111) on a de-noised baseline (spec 109),
  lift Opportunity toward/over the `Investigate` gate — **present the exact number in the PR for maintainer
  sign-off.** It applies to both directions (Improving→Positive, Deteriorating→Negative), so a confident
  guidance *cut* bites as hard as a raise lifts (symmetry). Keep `MinConfidence` at least as strict as
  today (0.6) — materiality rises only for reads that clear the confidence gate.
- **Why config, not code-version (AD-10 / spec 106).** The directional-filing magnitudes are hashed into
  `ScoringConfigVersion` via the source's `ScoringDescriptor()` (spec 106), so changing the default
  `Strength` re-stamps the fingerprint **automatically** — **no `_formula.Version` / `RuleSetVersion`
  bump.** Cost/operational caps (`MaxFilingsPerRun`, `MaxConsecutiveRateLimited`) are deliberately **not**
  fingerprinted (spec 105/107) — do not touch them here.
- **Supersede behaviour unchanged (spec 78).** A directional AI `GuidanceChange` still replaces the
  deterministic Neutral for the same filing; this slice only changes the strength it carries. The
  deterministic Neutral remains the honest fallback when the AI path does not fire — leave that rule as-is.
- **No new fetch/AI behaviour.** This is purely a magnitude recalibration on an existing, un-blocked path.
  Do not add collectors, readers, or a deterministic exhibit-body reader (out of scope / footprint risk).

---

## Tests

- `DirectionalFilingSignalSourceTests`: a confident Improving read emits `GuidanceChange (Positive)` at
  the new Strength; a confident Deteriorating read emits `(Negative)` at the same new Strength (symmetry);
  a below-`MinConfidence` read still emits nothing; `ScoringDescriptor()` reflects the new magnitudes.
- Fingerprint test: default re-stamps to the new pinned value; recompute-from-stored equals the stamp.
- A scoring-level test (or documented reasoning): a company with a corroborated positive trajectory + a
  confident raised-guidance AI signal at the new Strength clears the `Investigate` opportunity gate,
  whereas at Strength 6 it did not (the materiality gap is closed) — this may be an
  Application-level test using a stubbed directional signal, not a live AI call.

---

## Constraints

- Target .NET 10 / `net10.0`, C# 14.
- Config-magnitude change only (spec 106 seam) — no `_formula.Version` / `RuleSetVersion` bump; the
  fingerprint re-stamps by value. Do not touch the operational caps.
- Keep AI provider SDKs behind Infrastructure (AD-5/AD-11); no new AI/HTTP behaviour.
- Confidence gate preserved; materiality symmetric across Improving/Deteriorating. No advice language
  (AD-9).

---

## Acceptance criteria

- [ ] Default AI directional `Strength` recalibrated so a confident raised-guidance earnings read is
      materially stronger than a generic keyword `GuidanceChange`, applied symmetrically to
      Improving/Deteriorating; `MinConfidence` not weakened.
- [ ] Config-magnitude only: `ScoringConfigVersion` re-stamps automatically via the spec-106 directional
      descriptor; **no** `_formula.Version` / `RuleSetVersion` bump. Pinned fingerprint test +
      `default.json` updated.
- [ ] The deterministic `"results of operations"` Neutral rule is confirmed inert (trajectory-excluded)
      and left unchanged; the AI signal still supersedes it (spec 78).
- [ ] AEHR acceptance fixture: on the de-noised (109), insider-recalibrated (110), robust-trajectory
      (111) baseline, a confident raised-guidance read on AEHR's earnings 8-K scores **Positive** and
      materially lifts Opportunity — AEHR no longer looks like every `Ignore` name.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
