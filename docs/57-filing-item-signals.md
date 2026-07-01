# Task: Turn SEC 8-K item codes into signals (realise the source-diversity confidence lift)

## Overview

Spec 56 landed the SEC EDGAR collector: a live `["rss","sec"]` run collected **150 `Filing`-type evidence
records** across 6 companies, merged cleanly with the 84 RSS items. But the report was unchanged — still 33
signals (RSS-only), every company on "Ignore" — because **filings produce no signals yet**, so they never enter
any company's signal set and can't lift `EvidenceConfidenceScore` (whose diversity term is computed over the
evidence *backing signals*).

This slice makes 8-K filings produce signals, so a company whose evidence spans **two source types**
(`PressRelease` + `Filing`) gets the diversity boost — the measured unlock to move corroborated companies from
"Ignore" into `Watch`.

**Design honesty — read before implementing.** An 8-K *item code* encodes the event **type** but not its
**direction**: `2.02` = "Results of Operations" (beat or miss? unknown from the code), `1.01` = "Material
Definitive Agreement" (customer deal, or a debt facility?), `5.02` = officer change (hire or departure?). So
filing-derived signals must be **conservative**: map the code to a sensible `SignalType`, but use
`SignalDirection.Neutral` unless the event is inherently growth-leaning. A `Neutral` signal contributes 0 to
Trajectory (AD-6) yet still creates a `Filing`-source `ScoreEvidenceLink`, which is exactly what raises the
evidence-confidence **diversity** factor. The value here is diversity + context, not trajectory precision;
directional precision (parsing the filing body) is a deliberate future slice, not this one.

Mechanism: reuse the single deterministic `KeywordSignalExtractor` (no new extractor, no source-coupling). The
SEC collector already writes evidence text; extend it to include the **official 8-K item titles** (real SEC
semantics, not fabricated), and add matching cues to the shared rule table. Because those titles are legitimate
business phrases, adding them to the shared table is harmless-to-helpful for RSS too.

---

## Assignment

Worktree: any
Dependencies: 56 (SEC collector) merged.
Conflicts with: None. Touches the SEC collector's evidence-text synthesis and the keyword rule table (+ tests).
Estimated time: ~1.5 h

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  SecEdgarFilingCollector.cs   # MODIFIED: expand 8-K item codes → official item titles in Title/RawText
  SecFormItemTitles.cs         # NEW (optional): static map of 8-K item code → official SEC item title

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs    # MODIFIED: add rule cues for material 8-K item titles → SignalType/direction

tests/Radar.Infrastructure.Tests/Sec/
  SecEdgarFilingCollectorTests.cs   # MODIFIED: item codes expand to titles in the evidence text
tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs    # MODIFIED: filing-title text → expected signal type/direction
```

---

## Implementation details

### SEC collector — expand item codes to official titles
- Add a static, fixed map of the material 8-K item codes → their **official SEC item titles** (real semantics;
  see EDGAR 8-K item list). At minimum:
  - `1.01` → "Entry into a Material Definitive Agreement"
  - `2.01` → "Completion of Acquisition or Disposition of Assets"
  - `2.02` → "Results of Operations and Financial Condition"
  - `2.03` → "Creation of a Direct Financial Obligation"
  - `3.02` → "Unregistered Sales of Equity Securities"
  - `5.02` → "Departure of Directors or Certain Officers; Election of Directors; Appointment of Certain Officers"
  - (unmapped/other codes: leave as the bare code — do not fabricate a title)
- In `MapToEvidence`, when `filing.Items` is present, append the resolved titles to the `RawText` (and optionally
  the `Title`) alongside the existing raw codes — e.g. `... 8-K item codes: 2.02,9.01. Items: Results of
  Operations and Financial Condition.` Keep the raw codes (provenance) AND the titles (matchable text).
- Only 8-K carries `items`; `10-Q`/`10-K`/`Form 4` have none — leave those as evidence/context only (no forced
  signal). Do not synthesise titles for forms that don't have item codes.
- No change to quality (`High`), provenance (index URL), hints, dedupe, or the reader.

### Keyword extractor — add filing cues
- Add rule-table entries for the material item titles, conservative direction:
  - "material definitive agreement" → `StrategicPartnership`, `Positive` (growth-leaning)
  - "completion of acquisition" → `StrategicPartnership`, `Positive`
  - "results of operations" → `GuidanceChange`, `Neutral` (valence unknown from the code)
  - "appointment of certain officers" (and/or "election of directors") → `ExecutiveHire`, `Neutral`
  - "direct financial obligation" → `CapitalRaise`, `Neutral`
  - "unregistered sales of equity" → `CapitalRaise`, `Neutral`
  - Keep strengths/novelty/confidence within domain ranges; pick modest values consistent with the existing
    table. First-match-per-`SignalType` dedupe and stable ordering are unchanged.
- These are generic business phrases; they legitimately also apply to RSS text (a press release announcing a
  "material definitive agreement" is a real partnership signal), so no source-coupling is introduced.
- Do NOT change the matching algorithm, the shared `EvidenceSearchableText` composition, `CompanyMention`, the
  scoring formula, the policy, or the report.

---

## Tests

- `SecEdgarFilingCollectorTests`: a filing with `items = "2.02,9.01"` yields evidence whose text contains
  "Results of Operations and Financial Condition"; `1.01` → "Entry into a Material Definitive Agreement";
  an unmapped code (e.g. `9.01` alone) leaves the bare code and fabricates no title. Raw codes still present.
- `KeywordSignalExtractorTests`: evidence text containing each added item title produces the expected
  `SignalType` and `SignalDirection` (esp. `results of operations` → GuidanceChange/**Neutral**); a `2.02`
  filing does not emit a spurious Positive trajectory signal. Existing RSS cases and dedupe/ordering unchanged.
- Confirm no advice language is introduced; all values in domain range.

---

## Constraints

- Target `net10.0`. Deterministic; reuse the single `KeywordSignalExtractor` (no new extractor, no source-type
  coupling in Application). SEC item-code knowledge stays in `Radar.Infrastructure/Sec` (AD-5).
- Conservative directions: `Neutral` unless the event is inherently growth-leaning. Never infer a beat/miss from
  a bare code. No DB, no AI, no advice language, provenance preserved.
- Scope: SEC collector text synthesis + keyword rule table + their tests. Do not touch scoring/policy/report.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] The SEC collector expands recognised 8-K item codes into their official item titles in the evidence text
      (keeping the raw codes); unmapped codes are left bare (no fabricated titles); non-8-K forms are unchanged.
- [ ] The keyword rule table maps those item titles to sensible `SignalType`s with conservative directions
      (`Neutral` where the code doesn't reveal valence; `Positive` only for inherently growth-leaning events),
      asserted by tests using the real item-title strings.
- [ ] A live-style extraction over an 8-K produces at least one `Filing`-source signal, so a company with both
      an RSS signal and a recent 8-K gains a second contributing source type. No scoring/policy/report code
      changed. `build`/`test` green.
