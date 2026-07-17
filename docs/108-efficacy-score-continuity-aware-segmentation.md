# Task: Efficacy visual slice-2 — score-continuity-aware segmentation

> **READ-SIDE, FREEZE-SAFE, NO SCORING IMPACT.** This edits only the efficacy SVG renderer
> (a research-statistic artifact, the READ side of AD-14). It changes **no** scoring math, no
> formula, no weights, no fingerprint, no evidence/signal/score path. It is the deferred efficacy
> **slice-2** referenced repeatedly in the ledger (spec 101 deferred slice-2; AD-10 spec-103
> lineage note; the spec-103 fingerprint section). It is the durable fix for the fragmentation the
> maintainer is currently working around by freezing scoring and accruing a cadence run — so it
> directly serves, and ultimately relaxes, that freeze.

## Overview

The per-company efficacy overlay (`EfficacySvgRenderer`, `data/efficacy/{ticker}.svg`) partitions the
sparse score points into contiguous segments of equal `ScoringConfigVersion` and draws a connecting
line **only within a segment**, marking every fingerprint boundary with a dashed vertical rule + the
short fingerprint suffix. That rule exists for a real correctness reason (AD-10): you must never draw a
trend line across a formula/weight change, because those scores came from different scoring generations.

But the codebase churned the fingerprint ~10 times in one week (config-v1..v10 → `c1e71b26` →
`7e56a8007342` → `eee8ed0665f2` → `8d638b90d4aa` → `c9e609ed53e9` …), and **many of those re-stamps
were byte-identical in score** — e.g. the spec-103 `RuleSetVersion` v2→v3 bump re-stamped the default
fingerprint while every company scored *exactly the same* (the new rule matched no existing evidence and
the collector was opt-in-off). The current renderer still draws a hard segment break at such a boundary,
so a genuinely **continuous** score line is shredded into disconnected length-1 dots. The accruing
efficacy artifact is illegible not because the score moved but because its *input hash* moved.

This slice makes segmentation **score-continuity-aware**: when two adjacent points straddle a fingerprint
boundary **but the plotted score component value is identical on both sides**, the connecting line is
drawn across the boundary (a cosmetic re-stamp, not a measurement break). The dashed config-change tick
is **still** drawn at every fingerprint change (provenance is preserved — the reader can still see a
config change happened). When the plotted value **differs** across a boundary, the line still breaks, so
the AD-10 correctness property — never draw a misleading trend across a real score change — is fully
preserved.

---

## Assignment

Worktree: any — a single self-contained Application file (`EfficacySvgRenderer.cs`) plus its test file.
It touches **no** scoring, DI, Domain, Infrastructure, Worker, seed, or config surface, so it does not
conflict with any scoring/collector/fingerprint slice and can run in parallel with anything that does not
touch `Radar.Application/Efficacy/`.
Dependencies: 101 (the efficacy visual — merged), AD-14 (read side — Accepted), AD-10 (fingerprint
segmentation rationale — read it). None pending.
Conflicts with: only another slice editing `EfficacySvgRenderer.cs` / its tests.
Estimated time: ~1–2 hours

---

## Grounding facts (verified against the code, 2026-07-17)

- **`EfficacySvgRenderer`** (`src/Radar.Application/Efficacy/EfficacySvgRenderer.cs`) is a pure,
  deterministic renderer (AD-3: `CultureInfo.InvariantCulture`, fixed precision, no wall-clock, stable
  element order, byte-identical output for identical input). It renders a left score axis (0–100), a
  right price axis, a dense price polyline, then the segmented score line via the private
  `RenderScoreSegments(sb, points, X, YScore)`.
- **Current segmentation logic** (`RenderScoreSegments`, ~lines 163–218): it walks contiguous runs where
  `SameSegment(prev, next)` holds, where `SameSegment(string? a, string? b) => string.Equals(a, b,
  StringComparison.Ordinal)`. A run of length ≥ 2 emits a `<polyline>`; a run of length 1 emits no line.
  At every non-final boundary it emits a dashed vertical `<line>` + a `<text>` with
  `ShortFingerprint(points[i].ScoringConfigVersion)`. After the segments it emits a `<circle>` dot for
  every point.
- **The plotted value** is chosen by `SelectScore(EfficacyPoint p)` from `Component`
  (`EfficacyScoreComponent`, default `Opportunity`): `Trajectory`/`Opportunity`/`Attention`/
  `EvidenceConfidence`/`SignalVelocity` → the matching `int` field on `EfficacyPoint`.
- **`EfficacyPoint`** (`src/Radar.Application/Efficacy/EfficacyPoint.cs`) carries the five `int`
  component scores, `DateOnly ScoreDate`, and `string? ScoringConfigVersion` (null = pre-stamp/unknown).
- Points arrive **already ordered by `ScoreDate`** (the dataset builder joins score-snapshot history
  ascending). This slice does not re-sort.
- Tests live in `tests/Radar.Application.Tests/Efficacy/EfficacySvgRendererTests.cs`; shared fakes in
  `EfficacyTestFakes.cs`. `EfficacyReadOnlyGuardrailTests.cs` structurally pins that the efficacy
  subsystem has no scoring/evidence/signal write dependency — keep it green (this slice adds nothing that
  would trip it).

---

## The segmentation rule (settled — encode exactly)

Two adjacent score points `a` (earlier) and `b` (later) are **connected by the score line** iff:

```
SelectScore(a) == SelectScore(b)                       // same plotted value, OR
    OR string.Equals(a.ScoringConfigVersion,
                     b.ScoringConfigVersion, Ordinal)   // same fingerprint (today's behaviour)
```

Equivalently: the line breaks between `a` and `b` **only** when the fingerprint differs **and** the
plotted value differs. This strictly widens today's connect-set (which requires equal fingerprint) with
the extra "…or the value is unchanged" clause.

Rationale to encode in code comments and the PR:

- **Correctness (AD-10) is preserved.** A line is still never drawn across a *real* score change:
  differing fingerprint **and** differing value ⇒ break. Only a cosmetic re-stamp (identical plotted
  value) is bridged.
- **Provenance is preserved.** The dashed config-change tick + fingerprint label is **still drawn at
  every fingerprint boundary**, whether or not the value is continuous — the reader always sees that the
  scoring config changed at that x. The change is *only* whether the connecting line crosses it.
- **Determinism (AD-3) is preserved.** The rule is a pure function of the (already-deterministic) point
  values; no wall-clock, no culture-sensitive comparison (integer equality + `Ordinal` string equality).
- **Component-scoped, honestly.** "Continuous" means *the plotted component* is unchanged across the
  boundary. Bridging when only the plotted component happens to match while another component moved is an
  **accepted, documented approximation** — the artifact plots one component at a time, so a bridge is
  correct *for what is drawn*. A richer "connect iff the full scoring inputs are unchanged" rule is noted
  as a deferred option below; do not build it here.

### Implementation shape

- Replace the single `SameSegment` gate in `RenderScoreSegments` with a `Connected(a, b)` predicate
  implementing the rule above (keep it a small `private static bool Connected(EfficacyPoint a,
  EfficacyPoint b)` on the renderer, or inline). Note `Connected` needs the plotted values, so it either
  takes the two points and calls `SelectScore` (instance method) or takes the two already-selected ints
  plus the two fingerprints.
- **Boundary markers stay keyed on fingerprint change**, not on `Connected`. Iterate two distinct
  notions:
  1. **Line runs** — maximal runs where every adjacent pair is `Connected`; emit one `<polyline>` per run
     of length ≥ 2 (a length-1 run emits no line, as today).
  2. **Config-change ticks** — at every index `i` (1..count-1) where
     `!Ordinal.Equals(points[i-1].ScoringConfigVersion, points[i].ScoringConfigVersion)`, emit the dashed
     vertical rule + `ShortFingerprint` label at `points[i]` (exactly today's marker logic, now decoupled
     from where the line breaks).
- Keep the per-point `<circle>` dots exactly as they are (drawn last, on top).
- Do not change the canvas, axes, price polyline, legend, caption, escaping, or number formatting. Output
  must stay byte-identical for any input where the fingerprint never changes across a value change (i.e.
  the only observable diffs are the newly-bridged cosmetic boundaries).

Because a config-change tick can now sit *on top of* a continuous line (value unchanged, fingerprint
changed), that is the intended, correct visual: a connected line with a thin dashed "config re-stamped
here (score unchanged)" annotation. Consider tightening the tick label wording to make that legible
(e.g. keep the fingerprint suffix; optionally the caption already says "segmented by scoring-config
fingerprint" — leave the caption as-is unless a one-word clarification is trivially safe).

---

## Tests (extend `EfficacySvgRendererTests`)

- **Cosmetic re-stamp is bridged.** A series of ≥ 3 points where the middle point has a *different*
  `ScoringConfigVersion` but the **same** plotted `OpportunityScore` as its neighbours renders **one
  continuous score polyline covering all three** (assert a single connecting polyline spans the boundary
  x), AND still renders the dashed config-change tick + fingerprint label at the boundary.
- **Real score change still breaks.** Two adjacent points with different fingerprint **and** different
  plotted value render **no** connecting line across the boundary (two separate/short runs), plus the
  config-change tick — today's behaviour, unchanged.
- **Same-fingerprint run unchanged.** A run of equal-fingerprint points still connects (regression: the
  added clause must not drop existing connections); no spurious config tick within it.
- **Value equal but same fingerprint** — still one polyline, no config tick (the `OR` short-circuits
  correctly; no double-marking).
- **Non-default component.** With `Component = Trajectory`, the bridge/break decision follows
  `TrajectoryScore`, not `OpportunityScore` (prove the rule is component-scoped).
- **Null fingerprint segment.** A `null`→non-null fingerprint transition with equal plotted value bridges
  the line and still ticks the boundary (`ShortFingerprint(null)` → `"unknown"` label unchanged).
- **Determinism.** Rendering the same series twice yields byte-identical strings (existing determinism
  test still green; extend if needed).
- **Guardrail.** `EfficacyReadOnlyGuardrailTests` stays green (no new scoring/evidence dependency).
- Full gate: `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Pure/deterministic (AD-3) — no wall-clock, `InvariantCulture`, `Ordinal`
  string comparison, stable element order, byte-identical output.
- **No scoring/formula/weight/fingerprint/RuleSetVersion change.** No Domain/Infrastructure/Worker/DI/
  seed/config edit. This slice must **not** re-stamp any fingerprint or touch a scoring test pin.
- Read-side of AD-14: reads only score/price artifacts, writes only the SVG string. Provenance preserved
  — the config-change tick still marks every real fingerprint boundary. AD-9-clean: still a research
  statistic, no advice framing added.
- Scope is `EfficacySvgRenderer.cs` + `EfficacySvgRendererTests.cs` only. Do **not** alter the CSV
  renderer, dataset builder, report generator, or artifact store in this slice.

---

## Out of scope / future slices (record, do not build)

- **Full-input continuity** — "connect iff the entire effective scoring config produced identical scores
  across the boundary" (i.e. bridge only when *all five* components match, or when the persisted
  effective-config weights are unchanged). The component-scoped rule here is the pragmatic, honest
  approximation for a single-component chart; revisit only if a real case shows a misleading bridge.
- **Efficacy gallery / index artifact** and **forward-return benchmark-relative stats** (efficacy slice-3)
  — the maintainer explicitly deferred these pending accrued cadence data.
- **CSV per-point "cosmetic re-stamp vs real change" flag** — a possible small follow-up mirroring this
  rule for external analysis; not needed for the visual.

---

## Acceptance criteria

- [ ] `EfficacySvgRenderer` connects the score line across a fingerprint boundary **iff** the plotted
      component value is equal across it (else the line still breaks), while **still** drawing the dashed
      config-change tick + fingerprint label at **every** fingerprint change.
- [ ] The bridge/break decision is scoped to the selected `Component` (proven for a non-default
      component).
- [ ] Byte-identical, deterministic output; no wall-clock/culture leakage.
- [ ] **No** scoring/formula/weight/fingerprint/RuleSetVersion/Domain/DI/config/seed change; no scoring
      test pin touched; `EfficacyReadOnlyGuardrailTests` green.
- [ ] `EfficacySvgRendererTests` extended with the cosmetic-bridge, real-break, same-fingerprint,
      component-scoped, and null-fingerprint cases; all green.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
