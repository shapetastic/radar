# Task: Make the AI earnings read weigh profitability, not just top-line "record" language

> **READ-QUALITY CALIBRATION.** Diagnosed live on 2026-07-20 from the remediation re-measure (specs 113–115
> merged), using the new spec-115 debug record. EOSE is the fixture. Behavioural change to the AI filing
> analyzer; validated by a live re-measure, not only unit tests.

## Overview

With the earnings read un-stuck (113–115), the 2026-07-20 baseline surfaced **EOSE (Eos Energy) as #3 "Thesis
improving," Opportunity 39** — while EOSE is down ~19% on the year and **~39% in the last month**, sitting just
above its 52-week low. The spec-115 debug record (`data/ai-debug/filings/0001628280-26-048253.json`) shows
exactly why: llama3.1 read EOSE's Q2 preliminary release as `Improving, confidence 0.90`, rationale:

> *"record quarterly revenue and backlog, driven by a more than three-fold increase in shipments."*

The release genuinely reports that. But the **very next bullet** of the same release reads **"Gross margin loss
between 69% and 73%"** — EOSE loses ~70¢ of gross profit on every revenue dollar, *before* opex. The model's
rationale does not mention margin at all. The correct classification of *"record revenue AND a −70% gross margin"*
is **Mixed** (the system prompt already offers `Mixed (materially both)`), not a confident `Improving`.

Root cause: `ChatFilingAnalyzer.SystemInstruction` (`src/Radar.Infrastructure/Filings/ChatFilingAnalyzer.cs`)
tells the model to classify the trajectory "as reported" and lists `Improving (record bookings, organic growth,
raised outlook)` — so the model latches onto "record revenue/backlog" and under-weights profitability, margin,
and cash burn. This is the mirror of the earlier failure: before, the read *missed* a real positive (AEHR); here
it *over-calls* a hype-y top-line. Both are read-quality gaps.

> This is NOT a request to make the model bearish or to judge beat-vs-consensus (there is no consensus feed —
> keep that prohibition). It is: weigh the **reported** profitability against the **reported** growth, and return
> `Mixed` when strong top-line coincides with heavy/negative gross margin, guidance cut, or large cash burn.

## Assignment

Worktree: any
Dependencies: 114 merged (uses its `CacheVersion` invalidation seam — a prompt change must re-read cached filings)
Conflicts with: none currently queued.
Estimated time: ~1–2 hours

## Project structure changes

Modify:
- `src/Radar.Infrastructure/Filings/ChatFilingAnalyzer.cs` — refine `SystemInstruction` to explicitly weigh
  profitability/margin/cash-burn against top-line growth, with a clear rule that **record/growing revenue paired
  with deeply negative or deteriorating gross margin (or a guidance cut, or heavy cash burn/dilution) is `Mixed`,
  not `Improving`.** Keep every existing guardrail: "as reported" (not beat-vs-consensus), no advice language,
  `Unknown` on boilerplate/ambiguous, single-sentence rationale. The rationale should be instructed to name the
  margin/profitability fact when it drove a Mixed call (so the debug record and Reason field show *why*).
- The analyzed-filing cache version — bump the spec-114 `CacheVersion` (or fold the prompt/analyzer identity into
  it) so every already-cached read is invalidated and re-analyzed under the new prompt. Without this, the poisoned-
  positive EOSE read (and the good AEHR/JNJ ones) would be replayed and the change would not take effect.

## Implementation details

- **Prompt is not a fingerprint input.** The directional-filing fingerprint hashes only `Strength`/`Novelty`/
  `MinConfidence` (spec 106); the system prompt text is not hashed. So this change **does not re-stamp
  `ScoringConfigVersion`** — the default fingerprint is unchanged. It changes *reads* (and therefore the scores of
  filings whose read changes), which is a read-quality correction, not a config re-stamp.
- **Cache invalidation is required** (see above) — a prompt change with no cache bump is a no-op on existing
  filings. Bump the `CacheVersion`.
- **Determinism caveat / how this is validated.** The read is an LLM output, so it is not deterministically
  unit-testable end-to-end. Unit tests cover the deterministic surface: `Mixed`/`Unknown` → no directional signal
  (mapping unchanged), the advice scrub, the confidence gate, and that the new `SystemInstruction` contains the
  profitability rule. **Behavioural acceptance is a LIVE re-measure** (run-radar.ps1 with `PersistReadDebug` on):
  EOSE's read becomes `Mixed`/no-signal (no strength-8 Positive), while AEHR (record bookings + a profitable
  trajectory) and JNJ stay `Improving`. Record the before/after debug records.
- No provider SDK leakage (AD-5); Microsoft.Extensions.AI abstractions only.

## Tests

- The refined `SystemInstruction` contains the profitability/margin rule (a string-contains guard is acceptable —
  the prompt is the behavioural contract).
- `Mixed` and `Unknown` verdicts still map to *no directional signal* (unchanged); advice-language scrub intact;
  below-`MinConfidence` still yields no signal.
- Cache: an entry stamped with the prior `CacheVersion` is a miss (re-analyzed) after the bump.
- No `ScoringConfigVersion` fingerprint change (confirm the pins do not move).

## Constraints

- Target .NET 10 / `net10.0`, C# 14. No provider SDK outside Infrastructure (AD-5); no advice language (AD-9).
- Keep "as reported, NOT beat-vs-consensus." Do not make the model systematically bearish — `Mixed` is the target
  for genuinely two-sided releases, not a blanket downgrade.
- Do not overfit to EOSE: the rule is general (profitability vs growth); EOSE is the acceptance fixture.

## Acceptance criteria

- [ ] `SystemInstruction` weighs profitability/margin/cash-burn; record top-line + heavy negative/deteriorating
      gross margin (or guidance cut / heavy cash burn) classifies as `Mixed`, not `Improving`.
- [ ] `CacheVersion` bumped so existing reads re-analyze under the new prompt.
- [ ] No `ScoringConfigVersion` / formula-version change (confirm pins unchanged).
- [ ] LIVE re-measure: EOSE reads `Mixed`/no-signal (no strength-8 Positive; drops off "Thesis improving");
      AEHR and JNJ still read `Improving`. Before/after debug records captured. **No ticker-specific logic.**
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
