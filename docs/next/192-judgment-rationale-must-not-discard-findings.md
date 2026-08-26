# Task: An over-long rationale must not discard a judgment's findings

## Overview

`NewsJudgmentValidator.Validate` rejects the **whole** model response when the rationale exceeds
`MaxRationaleLength` (1,000), and it does so with a `return` placed **before** the findings loop. The findings
are therefore not judged invalid — they are **never examined at all**. Their citations are never checked,
the attribution-caveat rule never runs, the context-only event-type gate never runs. They are discarded
unread because the accompanying prose was long.

Measured on the live store:

| run | company | rationale | findings lost |
| --- | --- | ---: | ---: |
| 2026-08-25 | CVLT | 1,228 chars | **3** |
| 2026-08-25 | LBRT | 1,095 chars | **2** |
| 2026-08-25 | CASS | 1,115 chars | — |
| 2026-08-24 | GTY | 1,131 chars | — |

Four of 18 judgments failed validation on 2026-08-25 (**22%**), three of them for length alone. The model
clusters at **1,095–1,228** — consistently just over the bound. The affected companies render
`? unassessed (validation-failed)`, which a human reads as "Radar has nothing on this company" when in at
least two cases it had produced specific findings. The rationale is nulled rather than persisted, so the text
is unrecoverable; only `rawResponseHash` survives.

The rule's stated intent (spec 187 §1) is sound for a **missing** rationale: *"a judgment Radar cannot explain
is not a judgment"* — otherwise you render a clean-looking zero-finding read with nothing behind it. But
over-length is the opposite failure. Radar **has** the explanation; it is merely verbose. The validator
currently treats verbosity and absence identically, and only absence justifies discarding the response.

This is the omission-bias shape one seam past where spec 186 closed it: a **formatting** gate suppressing
**presence** claims. It also matters operationally — it is the single largest suppressor of the input that
spec 191 wires into scoring, which is why this slice goes first.

## Assignment

Worktree: any

Dependencies: specs 187–190 merged. Existing judgment records are immutable; nothing is rewritten or
re-judged by this slice.

Estimated time: ~half a day.

## 1. Separate the rationale verdict from the findings verdict

Restructure `Validate` so an over-long rationale can no longer short-circuit past finding validation.

- **Soft bound (`MaxRationaleLength`, 1,000) no longer fails the response.** An over-length rationale is
  **persisted in full** and flagged. It is deliberately **not truncated**: a shortened rationale is a
  fabricated explanation, and spec 187's "must be explainable" rule requires the real one.
- **Add a hard ceiling (`MaxRationaleHardLimit`, 4,000)** for genuine malformation — a runaway or non-prose
  response. Above it the response still fails, with its own distinct reason code
  (`rationale-exceeds-hard-limit`). Findings are still validated first and their count still reported.
- **Fix the ordering bug**: the advice-language scrub currently runs *after* the length check, so an
  over-long rationale was never scrubbed. Scrub first, then measure length.
- **The advice-language and missing-rationale rules are unchanged and still fail the response.** Advice
  language is a hard house rule, and a genuinely absent rationale is exactly the case spec 187 §1 was written
  for. Do not weaken either.

Findings are validated on their own merits in every case — citations, attribution caveats and the
context-only gate all run as they do today. An `accepted.Count == 0` after real validation still yields
`ValidationFailed`, per spec 185: all-invalid findings are never rendered as "no challenge found".

## 2. Record it, so the bound still means something

`NewsJudgmentRecord` gains two **trailing, nullable** fields — `RationaleLength` and `RationaleOverSoftLimit`.
Null means "not recorded" (a pre-192 record), never a fabricated `false`. Per the repo's trailing-nullable
precedent (spec 142's `EvidenceQuality`, spec 148's `EffectiveScoringConfig.Window`),
`CurrentSchemaVersion` does **not** bump: no field is removed or re-meant, and no vocabulary changes. Spec
189 bumped to `news-judgment-v3` because the completeness *vocabulary* changed; nothing comparable happens
here.

Add an aggregated per-cohort count of over-soft-limit rationales to the judgment pass summary, following the
spec-145 one-line-per-cohort precedent rather than one log line per judgment. The soft bound becomes a
**measured quality signal** instead of a silent destroyer: if the model consistently runs long, that is a
prompt-tuning fact worth seeing, not a reason to lose its work.

## 3. Consequences to expect, stated

- Previously-failed judgments **retry naturally**. `ValidationFailed` is not a completed status (spec 181), so
  those companies re-enter selection and are re-judged under the corrected validator, bounded by spec 187's
  `MaxJudgmentAttempts = 3`. CVLT, LBRT, CASS and GTY have spent 1–2 attempts each and will get another.
- **No cohort fork.** The stage-2 cohort key is `judge|prompt|schema|stage1|families`; validator rules are not
  part of it. The prompt, result schema and model request do not move, so no re-judge of the whole candidate
  set is triggered.
- Existing `ValidationFailed` records stay exactly as they are (insert-only, AD-8). Nothing is rewritten or
  backfilled.
- More judgments will reach `Judged`, so more leaders rows will carry a real marker instead of
  `? unassessed (validation-failed)`. That is the intended effect.

## 4. Tests

- A response with a 1,228-character rationale and three valid findings yields `Judged`, **three accepted
  findings**, the full rationale persisted, `RationaleOverSoftLimit = true` and `RationaleLength = 1228`.
  Build the fixture from the shape of the real CVLT failure.
- A response over the hard ceiling fails with `rationale-exceeds-hard-limit`, and the reason names the length.
- Advice language in a long rationale is **scrubbed first**, then the (now absent) rationale fails as
  `rationale-missing` — proving the ordering fix.
- A missing/blank rationale still fails exactly as today (byte-identical reason string).
- Valid findings beneath an over-long rationale are still individually subject to citation, attribution-caveat
  and context-only validation — an invalid finding is still dropped by name.
- All findings invalid on their own merits ⇒ `ValidationFailed`, never "no challenge found".
- A pre-192 record hydrates with both new fields null.
- Mutation proof: restoring the pre-192 validator fails the CVLT-shaped test.

## 5. Out of scope

- Changing the judge prompt, result schema, taxonomy, fact-family identity or any cohort key.
- Re-judging or rewriting historical records, or reviving the four lost rationales (unrecoverable — only the
  response hash was kept).
- Changing the marker vocabulary, marker policy, or any score, rank, label, strategy or fingerprint.
- Truncating or summarising a rationale.
- Spec 191's wiring of the judgment into scoring — this slice only stops suppressing its input.

## Acceptance criteria

- [ ] An over-soft-limit rationale never discards findings; findings are validated on their own merits in
      every path through `Validate`.
- [ ] The full rationale is persisted, never truncated; `RationaleLength` and `RationaleOverSoftLimit` are
      trailing, nullable, and null on pre-192 records.
- [ ] A hard ceiling still rejects genuinely malformed output, with its own named reason.
- [ ] Advice-language scrubbing runs before the length measurement; advice-language and missing-rationale
      failures are unchanged.
- [ ] `NewsJudgmentRecord.CurrentSchemaVersion` is unbumped, no cohort key moves, and no score, rank, label,
      strategy, marker policy or fingerprint changes.
- [ ] The pass summary reports an aggregated per-cohort over-soft-limit count.
- [ ] `dotnet build Radar.sln -c Release` and the full test suite pass; `git diff --check` clean.
