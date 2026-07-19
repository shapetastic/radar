# Task: Let a directional earnings read supersede an ALREADY-PERSISTED Neutral GuidanceChange

> **EARNINGS-READ UN-STICK — slice 1 of 3 (the score-unblocker).** Diagnosed live on 2026-07-18 from the
> reworked baseline (specs 109–112 merged). This slice makes the AI directional earnings read actually reach
> the score for filings whose first collection predated a successful read. Sequence with 114 (cache
> un-poisoning) — 114 makes re-analysis healthy; **113 makes the resulting directional signal count.**

## Overview

The reworked run scored AEHR at **Opportunity 39 · Ignore** — one point under Investigate — even though AEHR's
record-guidance 8-K is a textbook improving read. Diagnosis: the AI *does* read it correctly (llama3.1 returns
`GuidanceChange Positive, strength 8, confidence 0.90` once its cache entry is busted — see spec 114), but that
Positive signal **never reaches the score.** There are **zero** `Positive` GuidanceChange signals anywhere in
`data/signals/`; every stored GuidanceChange is the deterministic keyword `Neutral` (spec 57).

Root cause — the directional supersede has a **first-collection-only window**. `RadarPipelineRunner`
(`src/Radar.Application/Pipeline/RadarPipelineRunner.cs`) computes `supersededFilingEvidenceIds` from the
produced directional signals, then suppresses the deterministic Neutral GuidanceChange **in the extract loop**
(`IsSupersededGuidanceChange`, ~line 272/500). But the extract loop runs over **newly-stored evidence only**
(`newEvidence`) — re-collected duplicates are never re-extracted (AD-1 insert-only dedupe). So when an earnings
8-K is *first* collected during a spell where the directional read fails (e.g. the www.sec.gov self-block era),
the Neutral GuidanceChange is persisted, and on every later run:

1. the 8-K is old evidence → the extract loop never revisits it → the supersede can't suppress the stored Neutral;
2. the directional source *does* produce the Positive (line ~310 tries to store it), but the append-only signal
   store dedupes it against the already-stored GuidanceChange for that evidence → the Positive is dropped.

Net: a filing whose directional read failed on first collection is **permanently pinned to the stale Neutral**,
and no later correct read can ever replace it. This is exactly why AEHR is stuck at 39: the strength-8 Positive
exists (now in the analyzed-filing cache) but is structurally locked out of the scored signal set.

## Desired behaviour

A filing that has an available directional `GuidanceChange` (produced fresh **or** replayed from the
analyzed-filing cache this run) is **scored on the directional signal, never on the stale deterministic Neutral**,
regardless of whether the read succeeded on the 8-K's first collection. Never both for the same filing (no
double-count). Deterministic; provenance preserved; append-only stores respected (AD-8 — prefer a
**read/assembly-time supersede** over deleting a persisted signal).

## Assignment

Worktree: any
Dependencies: none blocking (pairs with 114; can land in either order, but the remediation re-measure wants both)
Conflicts with: 114/115 only lightly (different files); coordinate the earnings-path touch points.
Estimated time: ~1–2 hours

## Project structure changes

Modify (exact shape at implementer's discretion — two viable designs, pick the one that best fits AD-8):
- `src/Radar.Application/Pipeline/RadarPipelineRunner.cs` — extend the supersede so it also covers filings whose
  Neutral GuidanceChange is **already persisted** (not just the new-evidence extract-loop case). Because the
  signal store is append-only (AD-8), the cleanest design is likely a **supersede at the scoring signal-set
  assembly / read side**: when the set scored for a company contains both a deterministic Neutral GuidanceChange
  and a directional GuidanceChange over the **same filing evidence**, drop the Neutral from the scored set. The
  produced directional signal must be allowed to persist (see below) so it is present to supersede.
- The signal store / read path (`src/Radar.Infrastructure/FileSystem/FileSignalStore.cs` and/or the
  Application read used by scoring) — ensure a directional GuidanceChange for a filing that already has a stored
  Neutral GuidanceChange is **not dropped by dedupe**, so both are present and the assembly-time supersede can
  pick the directional one. Keep dedupe for genuine duplicates (same signal identity).

Prefer NOT to delete/rewrite persisted signals (respect append-only). If a read-time supersede is chosen, the
Neutral stays on disk for provenance but is excluded from scoring when a directional read exists for that filing.

## Implementation details

- **Supersede key** stays `(SignalType == GuidanceChange, same filing EvidenceId)` — identical to the existing
  `IsSupersededGuidanceChange` key, just applied at scoring-assembly time as well, so it covers old evidence.
- **No double-count, ever:** the scored set contains at most one GuidanceChange per filing evidence — the
  directional one when present, else the Neutral.
- **Determinism (AD-3):** the supersede is a pure function of the signal set; no wall-clock, stable ordering.
- **Provenance (AD-1/AD-8):** the directional signal carries its own evidence link; the superseded Neutral is not
  deleted (append-only) — it is simply excluded from scoring. The report's evidence listing must show the
  directional Positive/Negative for that filing, not the stale Neutral.
- **Version obligation:** this is a **pipeline/scoring-assembly correctness fix**, not a formula-structure change
  and not a config magnitude. Expect **no `_formula.Version` and no `RuleSetVersion` bump**; the default
  `ScoringConfigVersion` fingerprint should be **unchanged** (the scoring *config* is identical — only which
  already-available signal is scored changes). Confirm the pinned fingerprint test does not move; if it does,
  stop and flag it (it would mean the fix leaked into a fingerprint input, which it should not).

## Tests

- A company with a persisted deterministic `Neutral` GuidanceChange over a filing **and** an available directional
  `Positive` GuidanceChange over the SAME filing evidence is scored on the Positive (Trajectory reflects
  strength-8 Positive), and the Neutral is excluded — no double-count. Mirror for `Negative`.
- The new-evidence path (spec 78 suppress-before-store) still behaves byte-identically (regression).
- A filing with only the Neutral (no directional read available) is unchanged.
- Determinism: repeated assembly yields identical scored sets.

## Constraints

- Target .NET 10 / `net10.0`, C# 14. Layering intact; no provider SDK outside Infrastructure.
- Append-only stores respected (AD-8) — do not rewrite/delete persisted signals; supersede at read/assembly.
- No scoring-config/formula version change expected; fingerprint must not move (confirm).
- Provenance preserved (AD-1); no advice language (AD-9).

## Acceptance criteria

- [ ] A directional GuidanceChange supersedes an **already-persisted** Neutral over the same filing at scoring
      time, not only for new-evidence-in-this-run; at most one GuidanceChange per filing is scored.
- [ ] The produced directional signal is no longer dropped by dedupe against the stale Neutral.
- [ ] AEHR acceptance fixture: with its directional cache entry present (spec 114 / the busted entry), AEHR is
      scored on the strength-8 Positive GuidanceChange; Trajectory rises accordingly and Opportunity clears the
      Investigate 40 gate. **No ticker-specific logic.**
- [ ] Default `ScoringConfigVersion` fingerprint unchanged (this is a correctness fix, not a config change).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
