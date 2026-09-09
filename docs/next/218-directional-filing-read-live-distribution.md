# Task: The directional filing read has never reported what it produces — measure its live distribution, its groundedness, and its disagreement with the rest of the record

## Overview

`DirectionalFilingSignalSource` (spec 119, `ai=` in the scoring fingerprint) is the highest-strength signal
Radar mints: `GuidanceChange` at `Strength: 8` against `MediaAttention` 4 and `InsiderActivity` 3–5. It
gates trajectory more than any other single input. It has never had its live distribution reported.

**Measured on 2026-09-09 over `data/filings-cache/` (500 accrued read records, cacheVersion 2):**

- **242 directional reads: 226 `Positive`, 16 `Negative` — 93.4% Positive.** 258 further reads produced
  `NoDirectionalSignal`. The one genuinely model-produced directional field is near-constant.
- `Confidence` varies (median 0.90; 0.95 ×77, 0.65 ×64, 0.85 ×51, 0.90 ×33, remainder ≤10 each).
- `Strength: 8` and `Novelty: 6` are constant in 242/242 — **by design, not a defect**: both are
  `_options.Strength` / `_options.Novelty` from `DirectionalFilingSignalOptions`, are hashed into the
  descriptor, and are not model output. This spec does not treat them as a finding.
- **`SupportingExcerpt` is the evidence title in 242/242.** `DirectionalFilingSignalSource.cs` assigns
  `SupportingExcerpt: evidence.Title` and its own comment states the title is "a stable, guaranteed-present
  excerpt"; `ExtractedSignalMapper` then verifies excerpt-in-evidence, which that assignment cannot fail.
  The model's actual claim rides `Reason`, which the same comment records as "not provenance-checked".
  A filing evidence record's `rawText` is ~113 chars (accession, form type, item codes), so the filing body
  the model read is not on disk and the `Reason`'s figures cannot be checked against it. **This is a
  documented design choice being measured, not a newly discovered bug.**

**The worked example.** On 2026-08-04 Radar labelled Powell Industries (POWL) **Thesis improving**, rationale
"Trajectory rose 38→60 (+22)". The driver was one signal:

> `8-K (2026-08-03) [items: 2.02,8.01] Results of Operations` — GuidanceChange (**Positive**), strength 8,
> confidence 0.95

Fourteen news signals sat beside it, **every one `MediaAttention (Neutral)`**, including *"POWL Is Down 9.0%
After Record Data Center Deal And Earnings Miss – Has The Bull Case Changed?"*, *"Drops 6.3%"*, *"Dipped More
Than Broader Market"*, and *"slides as valuation pressure and recent insider selling weigh on sentiment"*.
The 8-K carried item 2.02 **and** 8.01 — results plus the data-centre award. POWL then fell 19.3% over the
next 21 sessions. Radar's own record contained the disagreement and nothing surfaced it.

This spec **measures**. It changes no score, no prompt, no weight and no formula, and it moves no fingerprint
pin. Phase B (below) is where enforcement would live, and it is deferred behind a stated entry condition.

## Assignment

Worktree: any. Dependencies: main at `2964324` (spec 216) or later. No overlap with spec 217 — 217 touches
item-1.01 recognition and `CompanyStatus`; this touches item-2.02 read reporting only.

Use `run-next.ps1 -Spec 218`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. The live distribution artifact (`directional-filing-read-distribution-v1`)

A new Application reporter over the accrued read corpus, written each `full` run alongside the other
efficacy artifacts, to `data/efficacy/directional-filing-reads.{csv,md,json}`:

- Direction counts and share: `Positive` / `Negative` / no-directional, split by `FilingNoSignalCause`
  (`BelowConfidence`, `NoDirectionalRead`, and every other cause the enum carries — **each cause is named
  and counted, never collapsed into an "other" bucket**).
- `Confidence` distribution: min / p25 / median / p75 / max, plus a value histogram, and the count capped by
  `ComparabilityConfidenceCap` (`cappedConfidence` non-null) reported separately from the uncapped.
- Per-company read counts, so a company contributing many reads is visible rather than pooled away.
- **Counted exclusions:** cache records that fail to parse, records with `cacheVersion` below current,
  reads whose company no longer resolves, and reads with no matching evidence record. A zero here is a
  measured zero; a field that was never recorded renders as `null`, never `0`.

The markdown states the Positive share as a headline figure with its denominator and the window it covers.

## 2. Groundedness of the `Reason` (`filing-reason-groundedness-v1`)

For every directional read, report — descriptively, no gate:

- whether `SupportingExcerpt` is byte-equal to the evidence `Title` (expected: all of them; the count makes
  the guard's tautology visible in an artifact rather than only in a code comment);
- whether `Reason` contains at least one numeric token (`\d`), as a proxy for "the model asserted a figure";
- for reads where it does, whether that figure appears anywhere in the evidence record's stored text.
  **It cannot, because the body is not stored** — so this count is expected to be 0/N, and the artifact
  states that as the measured position rather than leaving it implied.

This section exists to convert a documented design choice into a standing number. It asserts no defect.

## 3. Disagreement with the rest of the record (`filing-read-disagreement-v1`)

For each directional read, join what Radar itself held in the same window and report the disagreement rate:

- **News:** typed facts (`data/news-typing/typings/`) for the same company whose observation published
  within ±3 days of the filing date. Count reads where the direction is `Positive` while ≥1 typed fact for
  that company/window carries a negative `eventType` or a `statement` matching a negative phrase set.
  Report the matched statements verbatim, capped at 5 per read with **the remainder counted**, never
  silently truncated.
- **Price (AD-14, validation-only, never a scoring input):** forward return over 21 sessions from the
  filing date, reported as a distribution split by read direction — `Positive` reads vs `Negative` reads vs
  no-directional. Descriptive. **No gate, no threshold, no promotion or demotion follows from it.**
- Reads with insufficient news coverage or an incomplete forward window are **excluded and counted**, with
  the reason named. Typing began 2026-08 (measured: no typing record has `createdAtUtc` before 2026-08-01),
  so pre-August reads have no news arm; the artifact says so per-read rather than scoring them as agreeing.

## 4. Worked example carried into the artifact

The POWL 2026-08-04 case above ships as a named row in the markdown, with its signal line, its fourteen
Neutral news lines, and its forward return — so the artifact is legible against a case a human can check by
hand. It is an illustration, not a test fixture.

## 5. Explicitly out of scope

- **No prompt change**, no schema change, no `Strength`/`Novelty`/`MinConfidence` change, no formula, no
  weight, no new strategy.
- **No fingerprint movement.** Nothing in `SignalSourceDescriptor` or the `ai=` segment is touched, so
  **spec 218 moves no pin** and needs no operator step. The reviewer must verify this against
  `ScoringConfigFingerprintTests` rather than trusting this sentence.
- **No filing-body persistence.** Storing bodies would change the evidence contract and the store's size
  profile; it is not proposed here.

## 6. Phase B — deferred, with its entry condition

If §1 confirms the Positive share stays above 85% across a second full run, **and** §3 shows a
disagreement rate materially above zero, then Phase B is warranted: make the read cite a verbatim span of
the fetched filing body, verified by substring match against the text actually read, failing closed to
`NoDirectionalSignal` with the failure counted. That is a scoring-input behaviour change: it moves the
`ai=` descriptor, moves the AI-ON pins, and owes its own operator step. It is **not** in this spec.

If §1 shows the Positive share is an artefact of which filings get read (e.g. only companies that pre-filter
to good news reach the reader), Phase B is the wrong fix and the spec that follows should target selection,
not citation. §1 is what decides.

## 7. Live verification (record in the PR body)

From the first full run after merge:

- total reads, `Positive` / `Negative` / no-directional counts and the Positive share, with denominator;
- confidence quantiles and the capped count;
- excerpt-equals-title count (expected N/N) and reason-contains-figure count;
- disagreement count and rate, with the excluded-for-no-coverage count stated separately;
- forward-return distribution by direction, labelled DESCRIPTIVE;
- confirmation that `ScoringConfigFingerprintTests` pins are unchanged.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. The three artifacts are written on a `full` run and are absent-but-not-fatal on `collect`.
3. Every count in §1–§3 has a named denominator; no code path drops a read without counting it.
4. No fingerprint pin moves; no `data/scoring-configs/strategies/*.json` is invalidated.
5. The §7 numbers are recorded in the PR body from a real run, not from a unit-test fixture.
