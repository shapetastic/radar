# Task: Scale GovernmentContract signal Strength by award dollar amount (materiality)

## Overview

Spec 63 made **every** federal award reliably yield a `GovernmentContract` Positive signal, but it
**deliberately deferred materiality** and named this slice as the follow-up. Quoting spec 63's deferral
note (§Materiality, options a/b):

> "A tiny-award materiality floor and amount-tiered Strength are a clean, well-scoped **follow-up spec**
> (call it out in the PR); this slice makes every award *reliably visible* first."
> … "the correct place to decide 'is a $5k order material?' is the reviewer/scoring layer, on typed data,
> not the keyword table."

This is that follow-up. It fixes a **real distortion observed on live data**: every `GovernmentContract`
rule emits a **flat Strength 6**, so a routine ~$500k DoD order and a ~$50M award produce **identical
Strength-6 Positive signals**. Because Strength feeds `radar-formula-v2` `TrajectoryScore` as the
confidence/recency-weighted mean of directional strength (AD-6), identical Strengths move the thesis
identically — which inflated **Mercury Systems to the #1 opportunity on immaterial awards**. A bigger
award is a materially better business event and should move the thesis more; a tiny routine procurement
order should not.

The lever is exactly the one AD-6 endorses: **award amount → Strength**. The numeric amount already sits
on the evidence as `Metadata["awardAmount"]` (an invariant-culture decimal string written by the
USASpending collector, e.g. `"508575.00"`), so this is a **deterministic, offline** refinement — **no
external API, no AI, no DB.**

---

## Assignment

Worktree: any
Dependencies: 62 (USASpending contract collector) and 63 (GovernmentContract signals) merged.
Conflicts with: touches the **shared** `KeywordSignalExtractor` and its tests — must **NOT** run in
parallel with any other extractor-editing slice. No collector/DI/schema/scoring/report change.
Estimated time: ~1.5–2 h

---

## Design decision — where materiality lives, and why it is intentional (not drift)

Spec 63 established a hard invariant: the extractor is **source-agnostic** — it never reads
`evidence.SourceType` and never reads `evidence.Metadata`; it only matches text. This slice makes a
**single, deliberate, tightly-scoped exception**: a **metadata-aware Strength refinement that applies
only to `SignalType.GovernmentContract` Positive signals**. Justification the coder/reviewer must accept
as intentional:

1. **It is amount-driven, not source-type-driven.** The extractor still never branches on
   `evidence.SourceType`. It reads one generic, provider-neutral metadata key (`awardAmount`) that *any*
   source could legitimately carry for a contract award. The phrase-match layer that decides *whether* a
   `GovernmentContract` signal fires stays 100% source-agnostic (spec 63 preserved); only the *magnitude*
   of an already-fired `GovernmentContract` signal is refined by the amount in hand.
2. **Every NON-`GovernmentContract` signal is completely unchanged** — still source-agnostic, still emits
   the rule's fixed Strength. This is not a general "make the extractor metadata-aware" change; it is one
   documented, commented, `SignalType.GovernmentContract`-scoped adjustment.
3. **It reuses evidence already in hand.** The extractor already receives the full `EvidenceItem`; the
   `awardAmount` is a field on its `MetadataJson`. No new inputs, no new collector coupling.

**Location: a contained post-match adjustment inside `KeywordSignalExtractor`, scoped to
`SignalType.GovernmentContract`** — documented in code as *the first metadata-aware rule*.

**Rejected alternative: a separate `IGovernmentContractMaterialityEnricher` component.** It would need
the same `EvidenceItem` + the same `awardAmount` parse, add a new interface + DI registration + wiring in
the pipeline runner, and split the "how strong is this award" logic away from the rule table that sets the
baseline Strength — more surface area and more drift risk for a ~10-line deterministic tier lookup. Reject
it for this slice; keep the change contained and obvious. (If a *second* metadata-aware refinement ever
appears, extracting a shared helper becomes worthwhile — note that, don't pre-build it.)

---

## Materiality input — the metadata shape (read this exactly)

`EvidenceItem.MetadataJson` is a JSON **string** produced by `CollectedEvidenceMapper.ToEvidenceItem`,
shaped as a nested object (NOT a flat dictionary):

```json
{ "metadata": { "quality": "High", "awardAmount": "508575.00", "awardingAgency": "…", "startDate": "…" },
  "companyHints": [ … ] }
```

So `awardAmount` lives at `root.metadata.awardAmount` as a string. Parse it defensively:

- `JsonDocument.Parse(evidence.MetadataJson)` inside a `try/catch (JsonException)`; treat any failure,
  or a null/blank `MetadataJson`, as "no amount".
- Guard every hop with `TryGetProperty`: root is an object → `"metadata"` is an object → `"awardAmount"`
  is a string.
- `decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)` — invariant
  culture (matches how the collector wrote it). Blank/unparseable ⇒ "no amount".

**"No amount" ⇒ fall back to the rule's current fixed Strength (6). No throw, no regression.** This
covers: absent/blank/unparseable `awardAmount`, malformed JSON, and a `GovernmentContract` signal that
fired from a **non-USASpending** source (e.g. an RSS press release saying "federal contract award") that
carries no `awardAmount` at all.

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs            # MODIFIED: add a documented, GovernmentContract-scoped
                                       #   award-amount → Strength tier adjustment applied AFTER the
                                       #   rule match, before building the ExtractedSignal. First
                                       #   metadata-aware rule; every other SignalType untouched.

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs       # MODIFIED: large-award → high Strength; tiny-award → floor
                                       #   Strength; absent/unparseable → fixed fallback; non-gov signal
                                       #   unaffected; determinism.
```

Optionally also (recommended, small):

```text
tests/Radar.Application.Tests/SignalReview/
  DeterministicSignalReviewerTests.cs  # MODIFIED (optional): a floor-Strength gov-contract signal is
                                       #   flagged NeedsMoreEvidence by the existing MinMaterialStrength
                                       #   guardrail (proves the floor reuses the guardrail, not a new drop path).
```

No production code outside `Radar.Application` changes. Collector, DI, seed, scoring formula, signal
review rules, policy and report are untouched.

---

## Implementation details

### The amount tier table (documented, deterministic, monotonic non-decreasing)

Add a **visibly-constant, ordered** tier table next to `Rules`, e.g. an array of
`(decimal MinInclusive, int Strength)` sorted descending by threshold, or a small ordered `switch`
expression. It must be monotonic non-decreasing in amount and every Strength in **1–10** (so mapped
signals still pass `SignalValidation`, which enforces Strength 1–10 / Novelty 1–10 / Confidence 0–1).

**Proposed starting tiers** (present these as the STARTING proposal the coder/reviewer/maintainer may
tune — state the reasoning in the PR: small awards are routine procurement noise; large awards are
materially better business):

| Award amount (USD)        | Strength | Rationale |
|---------------------------|:-------:|-----------|
| `< $100k`                 | **2**   | Sub-material routine order. **Deliberately below `MinMaterialStrength` (3)** so the existing `DeterministicSignalReviewer` flags it `NeedsMoreEvidence` (see below) — reuse the guardrail, don't invent a drop path. |
| `$100k – < $1M`           | **4**   | Small but real; above the materiality floor, modest thesis contribution. |
| `$1M – < $10M`            | **6**   | Baseline material award. Equals the current fixed Strength ⇒ **no regression** for mid-size awards. |
| `$10M – < $100M`          | **8**   | Large, clearly material award. |
| `≥ $100M`                 | **9**   | Very large, thesis-moving award. |

Notes / reasoning to record:

- **Floor vs the reviewer guardrail is a strict-inequality interaction — get it right.**
  `DeterministicSignalReviewer` flags materiality via `signal.Strength < MinMaterialStrength` with
  `MinMaterialStrength = 3` (spec 11). The check is **strict `< 3`**, so a Strength of **3 would PASS**
  (not flagged) while **2 is flagged** `NeedsMoreEvidence`. To make a tiny award "treated as immaterial"
  by the *existing* reviewer, the sub-material tier must be **≤ 2**. The proposal uses **2**. (This is why
  the floor is 2, not 3 — call it out; it is the crux of "reuse the guardrail".)
- **Do not add a new "drop"/suppression path.** The extractor still emits exactly one `GovernmentContract`
  Positive signal for every award (spec 63 invariant). A tiny award is emitted at Strength 2 and the
  downstream reviewer decides immateriality on typed data — no signal is silently discarded in the
  extractor.
- Thresholds are inclusive-lower / exclusive-upper as tabled; document that explicitly so boundary awards
  (exactly $1,000,000 → Strength 6) are unambiguous and reproducible.

### The post-match adjustment (GovernmentContract only)

In `ExtractAsync`, when building each `ExtractedSignal` from a matched rule, compute the effective
Strength:

- If `rule.Type == SignalType.GovernmentContract` **and** `rule.Direction == SignalDirection.Positive`
  **and** the `awardAmount` parses (per the parsing rules above) ⇒ **effective Strength = tier lookup of
  the amount**.
- Otherwise ⇒ **effective Strength = `rule.Strength`** (unchanged, including every non-gov signal, and gov
  signals with no parseable amount).

Keep everything else identical to spec 63: the phrase table (still add nothing / remove nothing there),
`EvidenceSearchableText.Compose(Title, RawText)`, first-match-per-`SignalType` dedupe, stable ordering,
verbatim excerpt provenance, `CompanyMention = evidence.SourceName`, and `Reason`. **Novelty and
Confidence stay exactly as the rule sets them** (5 / 0.6 for `GovernmentContract`) — this slice calibrates
only Strength; changing Novelty/Confidence too would be unjustified scope creep. Parse the `MetadataJson`
**once per evidence** (not per rule) for efficiency and determinism.

Add a short code comment marking this as *the first metadata-aware rule*, scoped to
`GovernmentContract`, and stating that all other signals remain source/metadata-agnostic (spec 63
invariant).

### Provenance

Unchanged. The Strength number changes; the excerpt is still the verbatim searchable-text slice, the
evidence→signal link is unchanged, and the signal still round-trips valid via `ExtractedSignalMapper`.

---

## Tests

Extend `KeywordSignalExtractorTests` (xUnit; reuse the existing `MakeEvidence` helper /
`EvidenceBuilder`, which already supports `.WithMetadataJson(...)`). Construct evidence whose text mirrors
the real USASpending collector output **and** whose `MetadataJson` carries the nested
`{ "metadata": { "awardAmount": "…" }, … }` shape (build it via `EvidenceBuilder().WithMetadataJson(...)`
or a small JSON literal, so the tests prove the actual production parse path). Cases:

1. **Large award → high Strength.** USASpending-shaped `GovernmentContract` evidence with
   `awardAmount = "52000000.00"` (~$52M) ⇒ single `GovernmentContract` Positive signal with
   `Strength == 8`. (This is the ~$50M award from the distortion example.)
2. **Very large award → top tier.** `awardAmount = "250000000"` (≥$100M) ⇒ `Strength == 9`.
3. **Mid award → baseline, no regression.** `awardAmount = "3500000"` ($3.5M) ⇒ `Strength == 6` (equals
   the old fixed value).
4. **Small routine order → sub-material floor.** `awardAmount = "508575.00"` (the routine ~$500k DoD
   order) ⇒ `Strength == 4`. Add a sibling case for a truly tiny award (`awardAmount = "6775"`, the seeded
   AGYS/CYRX HHS order) ⇒ `Strength == 2`, and assert `2 < 3` (below `MinMaterialStrength`).
5. **Floor Strength is treated as immaterial by the EXISTING reviewer (guardrail reuse).** Feed the tiny
   award's extracted+mapped `Signal` (with a resolved `CompanyId`) through `DeterministicSignalReviewer`
   and assert the decision is `NeedsMoreEvidence` / `ReviewStatus == NeedsHumanReview`. (Prefer this in
   `DeterministicSignalReviewerTests`; it proves the floor reuses the guardrail rather than inventing a
   drop path. If wiring the reviewer here is awkward, at minimum assert the extractor Strength is `< 3`.)
6. **Absent amount → fixed fallback.** USASpending-shaped gov evidence with **no** `awardAmount` in
   metadata (or `MetadataJson == null`) ⇒ `Strength == 6` (the rule's fixed value). No throw.
7. **Unparseable amount → fixed fallback.** `awardAmount = "not-a-number"` (and a blank-string variant)
   ⇒ `Strength == 6`. No throw.
8. **Malformed MetadataJson → fixed fallback.** `MetadataJson = "{ this is not json"` ⇒ `Strength == 6`,
   no throw.
9. **NON-gov signal is completely unaffected by amount metadata.** Evidence that fires a `CustomerWin`
   (e.g. "multi-year deal") **with** an `awardAmount` present in metadata ⇒ the `CustomerWin` signal still
   has its fixed `Strength == 6`; amount metadata must never touch a non-`GovernmentContract` signal.
   (Optionally: a huge `awardAmount` does not bump a `ProductLaunch`/`CapitalRaise` Strength.)
10. **Determinism / reproducibility.** Two extractions over the same amount-bearing evidence yield equal
    signal sequences **including Strength** (extend the existing determinism assertion to include
    `Strength` in the compared key).

Keep **all** existing extractor tests green — especially the spec-63 cases
(`NonDodUsaSpendingAward_…`, `DodUsaSpendingAward_YieldsExactlyOneGovernmentContractSignal_NotTwo`,
`BenefitsAwardHeadline_DoesNotYieldGovernmentContractSignal`,
`SelectedByNasa_YieldsBothCustomerWinAndGovernmentContract_InEnumOrder`). Note that the existing
spec-63 tests build evidence **without** `awardAmount` metadata, so they now exercise the fixed-Strength
fallback path and must remain unchanged/green.

No advice language is introduced; all Strengths stay in 1–10.

---

## Spec-implementation checklist

1. **Code paths touched:** only `KeywordSignalExtractor.ExtractAsync` signal-construction (Strength
   selection) + a new constant tier table + a private `awardAmount` parse helper. The phrase `Rules`
   table is **not** modified (spec 63 owns it). No code removed — the fixed Strength remains the fallback.
2. **Old paths replaced:** the flat Strength-6 for `GovernmentContract` is now the *fallback*, not the
   only outcome. Update the spec-63 tests only if any asserts an exact gov Strength value (they assert
   type/direction, not Strength, so they stay green untouched).
3. **Tests:** add cases 1–10 above; keep all existing extractor (and reviewer) tests green.
4. **Delete nothing still used:** all existing `GovernmentContract` rules and non-gov rules remain valid.
5. **CLAUDE.md:** no architecture change (deterministic, in `Radar.Application`, no new interface/DI). The
   one nuance worth a one-line PR note (not a CLAUDE.md edit): the extractor gains its **first
   metadata-aware, `GovernmentContract`-scoped** refinement while remaining source-type-agnostic. **No
   CLAUDE.md update needed** — state this in the PR.

---

## Constraints

- Target `net10.0`, C# 14. Extraction stays **deterministic** and in `Radar.Application`, **before** any
  AI (prefer deterministic code first). Reuse the single `KeywordSignalExtractor` — no new extractor, no
  new interface/DI for this slice.
- **Scoped metadata read only:** the amount-aware refinement applies **only** to
  `SignalType.GovernmentContract` Positive signals and reads **only** `Metadata["awardAmount"]`. The
  extractor still never branches on `evidence.SourceType`; every non-gov signal stays exactly
  source/metadata-agnostic (spec 63 invariant preserved).
- No provider SDK; **no DB** (AD-8, files-first); no AI. Provenance preserved (verbatim excerpt;
  evidence → signal via the mapper unchanged).
- **Never emit advice language.** Materiality is evidence-strength calibration (a bigger award is stronger
  evidence of an improving trajectory), **NOT** a buy/sell view. `GovernmentContract`/`Positive` states a
  real award and its size, nothing more.
- All Strengths within domain range (1–10) so `SignalValidation` accepts them; parse amounts
  invariant-culture; fall back to the fixed rule Strength on any absent/blank/unparseable/malformed input
  without throwing.
- Scope: `KeywordSignalExtractor` + its tests (+ optional reviewer test). Do NOT touch the USASpending
  collector, DI, seed, scoring formula, signal-review rules, policy, or the report.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] A `GovernmentContract` **Positive** signal whose evidence carries a parseable
      `Metadata["awardAmount"]` has its **Strength set from a documented, deterministic, monotonic
      non-decreasing amount-tier table** (all Strengths in 1–10), instead of the flat Strength 6.
- [ ] A **large** award (~$50M) yields a **high** Strength and a **tiny** award (< $100k) yields the
      **sub-material floor Strength** — asserted by tests — so the ~$500k order and the ~$50M award no
      longer produce identical signals (fixes the Mercury Systems distortion at the Strength lever, AD-6).
- [ ] The tiny/floor tier is **below `MinMaterialStrength` (3)** so the **existing**
      `DeterministicSignalReviewer` flags it `NeedsMoreEvidence` — the floor **reuses that guardrail**
      rather than adding a new drop/suppression path (asserted).
- [ ] **Absent, blank, unparseable, or malformed** `awardAmount` (incl. `MetadataJson == null`, and
      `GovernmentContract` signals from non-USASpending sources) ⇒ the **fixed rule Strength (6)** with **no
      throw** — no regression versus spec 63.
- [ ] **No non-`GovernmentContract` signal is affected** by amount metadata; the extractor still never
      reads `evidence.SourceType`; all spec-63 extractor tests stay green (they now exercise the
      fixed-Strength fallback).
- [ ] Provenance preserved (verbatim excerpt + evidence→signal link unchanged); each emitted signal
      round-trips valid via `ExtractedSignalMapper.ToSignal`; extraction is deterministic (equal
      Strengths on repeat).
- [ ] No collector/DI/seed/scoring/policy/report change; no advice language. `dotnet build` /
      `dotnet test` on `Radar.sln -c Release` green.
