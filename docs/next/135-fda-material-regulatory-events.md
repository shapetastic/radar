# Task: Filter the openFDA collector to materially meaningful regulatory events

> **URGENT-ish / CORRECTNESS FIX to spec 129, surfaced by spec 133 going live.** Spec 133 (PR #139, merged
> `ad9ebd9`) promoted the `fda` collector into the live baseline. Live verification of the seeded applicants
> immediately afterwards showed the collector, as written, emits a **Positive `RegulatoryApproval` signal for
> routine post-market paperwork** — manufacturing-process supplements, 30-day notices and labeling changes on
> devices approved years ago. The next `RadarBaselineDaily` run will stamp that bullish artefact onto TMDX.
>
> **Reader parse change only.** Same emitted phrase, same extractor rule, same enabled-collector set ⇒
> **no `RuleSetVersion` bump, no `_formula.Version` bump, NO fingerprint move.** The post-spec-133
> fingerprints (AI-OFF `radar-scoring-fp-6b2f468041b9`, AI-ON `radar-scoring-fp-57356123e09b`) must be
> **byte-identical** after this slice.

## The evidence (live, openFDA, 2026-07-25 — keyless, re-runnable)

TransMedics (`applicant=TransMedics`) all-time PMA: **41 records, only 3 ORIGINAL** (2018-03-22, 2021-09-03,
2021-09-28); the other 38 are supplements. The **9** records inside the collector's 365-day window — the ones
that would drive a Positive signal today:

| `supplement_type` | count | representative `supplement_reason` |
|---|---|---|
| 30-Day Notice | 3 | Process Change - Manufacturer/Sterilizer/Packager/Supplier |
| Special (Immediate Track) | 3 | Process Change / Labeling Change |
| Real-Time Process | 2 | Change Design/Components/Specifications/Material |
| Normal 180 Day Track No User Fee | 1 | Labeling Change - PAS |

**Zero originals. Zero Panel Track.** So the signal fires because TransMedics changed a sterilizer or a
packaging supplier — administrative churn on already-approved devices, not a business-trajectory event. And
it never switches off: a company with three marketed PMA devices generates this paperwork continuously, so
the company carries a **permanent standing Positive**.

This contradicts spec 129's own stated rationale — *"a discrete, market-relevant regulatory gate"* — and is
precisely the failure mode the philosophy doc guards against: something that looks like hard evidence and is
actually routine maintenance. AXGN, the only other seeded applicant, has **no PMA record at all** and no
510(k) in-window, so today's live FDA coverage is one bullish artefact and one silence.

## Design — keep the gates, drop the maintenance

Filter inside the reader, after fetch, before the count that drives the evidence.

**510(k): keep every record.** A 510(k) *is* a device clearance — the marketing authorisation itself. No
sub-classification needed.

**PMA: keep only two kinds.**

1. **Originals** — a new device approval.
2. **`Panel Track` supplements** — the supplement type that carries a **new indication** (live-verified:
   TMDX's two Panel Tracks both read `Labeling Change - Indications/instructions/shelf life/tradename`).

**Exclude every other supplement type**, explicitly: `30-Day Notice`, `Real-Time Process`,
`Special (Immediate Track)`, `Normal 180 Day Track`, `Normal 180 Day Track No User Fee`. Treat an
**unrecognised** `supplement_type` as **excluded** (fail closed): a new FDA category should not silently
become bullish. Log unrecognised values at debug so a genuine new material type can be spotted and added.

### Pinned field facts (live-verified 2026-07-25 — do not re-derive)

- An **original** PMA has `supplement_number` **present but an EMPTY STRING** (`""`), *not* absent and not
  null. `supplement_type` is likewise `""`. Test emptiness, not presence.
- A **Panel Track** row has a real `supplement_number` (e.g. `"S002"`) and `supplement_type == "Panel Track"`.
- Compare `supplement_type` **Ordinal, case-insensitive**, trimmed.
- Unchanged and still correct: `decision_date` is `YYYY-MM-DD`; the far-future ceiling `9999-12-31` works;
  404 is openFDA's empty-result response and already maps to `Success` 0.

## Metadata must reflect the filter

The evidence metadata currently records `clearanceCount` / `reportedTotal510k` / `reportedTotalPma`. The
**emitted count must be the post-filter count**, and the raw API totals must be labelled as pre-filter
provenance — the same discipline spec 134 applied to the patents root `count` vs the post-normalisation
count. Add the excluded-supplement count to metadata so a reader can see what was filtered and why.

## Expected effect — state it plainly in the PR

With this filter, **TMDX yields 0 material events in the 365-day window** (verified: originals 2018/2021,
Panel Tracks 2019/2022 — all outside it), and AXGN already yields 0. So **live FDA coverage becomes 0 of 43**,
and the collector contributes nothing until a seeded company has a real approval or a new indication.

That is the correct outcome: an honest zero beats a standing false positive, and the collector will fire
properly when a genuine gate is cleared. It does, however, mean the value of having `fda` enabled is
currently latent — see the follow-up below.

## Assignment

Worktree: any. Files: `Radar.Infrastructure/Fda/HttpFdaClearanceReader.cs` (+ options if the excluded-type set
is made configurable — **prefer a pinned code constant**, this is rule structure, not a tunable magnitude),
`FdaClearanceCollector.cs` metadata, and the reader's unit tests/fixtures.
Dependencies: **129 (merged)** and **133 (merged, PR #139 / `ad9ebd9`)**. Independent of 132.
Estimated time: ~1–1.5 h.

## Tests

- A fixture containing one original PMA (`supplement_number: ""`), one `Panel Track`, one `30-Day Notice`,
  one `Real-Time Process`, one `Special (Immediate Track)` asserts **exactly 2** material events are counted.
- **An unrecognised `supplement_type` is EXCLUDED** (fail-closed), with the debug log asserted.
- A 510(k) fixture asserts **all** rows count regardless of any supplement-ish field.
- The emitted `clearanceCount` is the **post-filter** count; raw API totals remain in metadata labelled
  pre-filter; the excluded count is recorded.
- **Regression-lock the live case:** a fixture built from TMDX's real 9-record window asserts the collector
  emits **no evidence at all** (0 material events), pinning the exact defect this slice fixes.
- Unchanged behaviour re-asserted: 404 ⇒ `Success` 0; non-404 non-2xx ⇒ `HttpError`; `decision_date`
  `YYYY-MM-DD`; far-future ceiling.
- **Fingerprint guard:** `ScoringConfigFingerprintTests` green **unmodified, no pin edit** — AI-OFF
  `radar-scoring-fp-6b2f468041b9` / AI-ON `radar-scoring-fp-57356123e09b` must hold.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **No `RuleSetVersion` / `_formula.Version` / weight / tier / enum / collector-set change.** If any
  fingerprint pin needs editing, the change has leaked scope and is wrong.
- **Do not disable the `fda` collector** to dodge the problem — `default.json` stays 7 collectors. The fix is
  in what counts as an event, not in whether the collector runs.
- Provenance intact: metadata records the applicant, window, post-filter count, excluded count, and the
  material events' identifiers.

## Out of scope / follow-up (record, do not build)

- **Widening the FDA seed set.** At 2 of 43 companies — and 0 material events today — the collector cannot
  demonstrate value. Adding seeded applicants is an evidence-led task, and per the spec-134 seed lesson
  (`listed entity ≠ filing entity`; EOSE's committed token matched zero rows) **every new token must be
  live-verified against `api.fda.gov` before it is committed**.
- **Treating excluded supplements as a Neutral signal.** Defensible — they are evidence of continued
  commercial activity — but it changes the emitted phrase and therefore bumps `RuleSetVersion`. Separate
  slice if wanted; v1 simply drops them.

## Acceptance criteria

- [ ] PMA records count **only** when `supplement_number` is empty (original) **or** `supplement_type` is
      `Panel Track` (Ordinal, case-insensitive, trimmed); every other type is excluded, and an unrecognised
      type is excluded **fail-closed** with a debug log.
- [ ] 510(k) records all count, unchanged.
- [ ] The emitted count is **post-filter**; raw API totals are retained as pre-filter provenance; the
      excluded count is recorded in metadata.
- [ ] A fixture reproducing TMDX's real 9-record window emits **no evidence**, regression-locking the defect.
- [ ] **Fingerprints byte-identical** — `ScoringConfigFingerprintTests` green with no pin edit.
- [ ] `fda` remains enabled in `scripts/run-profiles/default.json` (7 collectors, unchanged by this slice).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
