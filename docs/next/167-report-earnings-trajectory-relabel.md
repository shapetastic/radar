# Task: Weekly report — stop presenting the stored `GuidanceChange` token as a literal guidance event

> **Small display-only slice.** Motivated 2026-08-01 (MSEX skeptic review): the AI filing reader is asked to
> classify the business trajectory **as reported** (`ChatFilingAnalyzer.SystemInstruction` — it is never asked
> whether guidance changed), but `DirectionalFilingSignalSource` hardcodes `SignalType: "GuidanceChange"` onto
> every passing directional read (spec-75 lineage). The label is therefore a taxonomy misnomer, not a model
> failure: MSEX's 0.90-confidence read had a correct EPS/net-income/revenue rationale, yet the report printed
> "GuidanceChange (Positive)" for a company that issues no guidance at all. Systemic, not isolated: all 145
> spec-162 calibration directional reads carry the token; 49 of their rationales mention neither guidance nor
> outlook. The real fix is the deferred taxonomy spec 168; THIS slice only stops the report **misleading a
> reader today**, and moves nothing.

## Overview

Relabel the `GuidanceChange` signal type in the weekly report's **own** compositions as
`EarningsTrajectory`, and add one legend line explaining the token where it appears inside stored
provenance text. Display-only: no stored byte changes, no scoring change, no fingerprint input, no pin move.

## Assignment

Worktree: any
Dependencies: current main.
Estimated time: ~1 hour.

## Context the implementer must verify first (do not assume)

1. **Two different sources print the token, and only ONE may be rewritten.**
   - The renderer composes signal-type text itself in the "Why noticed" list
     (`MarkdownWeeklyReportRenderer` — the `signal.Type.ToString()` site) and possibly other sites; find
     ALL renderer-owned sites that stringify `SignalType`.
   - The evidence-block lines ("… GuidanceChange (Positive), strength 8, confidence 0.90") come from the
     snapshot's **stored evidence-link reason**, authored at scoring time by `RadarScoreFormulaV8` (and
     `ScoringChannelComposition` for the channel formulas). That text is persisted provenance. **Do NOT
     rewrite, string-replace, or re-derive it at display time** — provenance is rendered verbatim.
2. **The token is NOT exclusively the AI read.** The deterministic spec-57 earnings-8-K signal also carries
   `GuidanceChange` (Neutral). Any display label implying an AI producer (e.g. "(AI read)") would mislabel
   those rows. The display name must be producer-neutral.

## Changes

### 1. Renderer-owned relabel

- At every site where `MarkdownWeeklyReportRenderer` itself stringifies a `SignalType`, map the
  `GuidanceChange` member to the display token **`EarningsTrajectory`**. Every other member renders
  unchanged (`ToString()` as today).
- One mapping function (private static, single definition), not per-site string literals.

### 2. Legend line for stored provenance

- Under the report's existing header caveats (beside the notedness line), add ONE line, e.g.:
  `> "GuidanceChange" in evidence lines denotes the earnings-trajectory-as-reported read (deterministic or
  AI); the type name is historical and does not imply the company issued or changed guidance.`
- Exact wording at the implementer's discretion, but it must (a) be advice-free, (b) state that the token
  does not imply a guidance event, and (c) not claim the read is AI-only.

### 3. Nothing else

- `SignalType` (Domain) is untouched. Stored snapshots, link reasons, signal files, the action policy
  (which compares enum values, not display strings), and the corroboration floor are untouched.
- The strategy sections (spec 150) render scores only and print no signal types — verify rather than
  assume; if any type token is found there, apply the same mapping function.

## Tests

- Renderer: a fixture entry with a `GuidanceChange` signal renders `EarningsTrajectory` in "Why noticed";
  every other signal type renders its enum name; the legend line is present exactly once.
- Provenance intact: a stored evidence-link reason containing the literal `GuidanceChange (Positive)` string
  renders **byte-verbatim** inside the evidence block (the mapping must not reach it).
- Golden guard: apart from the mapped token and the one legend line, the report for the shared golden model
  is byte-identical to pre-167 (mirror the spec-150 `PreSpec150Golden` approach: capture the pin from the
  unmodified renderer first).
- Action policy control: `WeeklyReportActionPolicyV1` decisions are unchanged for a fixture containing
  `GuidanceChange` signals (the policy consumes the enum, never the display string).

## Constraints

- **No fingerprint input, no pin move, no scoring change, no stored-byte change.** Nothing under
  `Scoring/`, `Domain/`, `SignalExtraction/`, or `Filings/` is touched.
- Output-language rules hold: the legend is not advice and introduces no new vocabulary beyond the six labels.

## Out of scope, recorded not built

- The taxonomy change itself (rename + structured basis + reserving literal `GuidanceChange` for explicit
  guidance actions) — **spec 168, deferred** in `docs/next/deferred/` until the current accrual/comparison
  window concludes.
- Any change to `Strength` (constant 8) — spec 162 established materiality varies; separate work.
- Rewriting or supersede-marking accrued signals.

## Acceptance criteria

- [ ] `GuidanceChange` renders as `EarningsTrajectory` at every renderer-owned type site; all other types
      unchanged.
- [ ] Legend line present once, advice-free, producer-neutral.
- [ ] Stored evidence-link reason text renders byte-verbatim (asserted).
- [ ] Golden before/after pin: only the mapped token + legend line differ.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass;
      no fingerprint pin moves (`ScoringConfigFingerprintTests` untouched).
