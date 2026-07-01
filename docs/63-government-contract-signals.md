# Task: Turn USASpending government-contract evidence into reliable directional signals

## Overview

Spec 62 landed the USASpending.gov contract collector: each watch-universe company's recent **federal
contract awards** are now collected as `GovernmentContract`-type evidence with full provenance
(award landing-page `SourceUrl`, `awardAmount`/`awardingAgency`/`startDate`/`awardId` metadata,
`High` declared quality). But — exactly as spec 56's SEC filings produced no signals until spec 57 —
that evidence produces **no reliable signal today**, so it never enters any company's signal set and
cannot lift the score. This slice is the deliberate signal-extraction follow-up to spec 62, mirroring
how spec 57 followed spec 56.

**The gap (this is the motivation — state it in the PR).** A USASpending `GovernmentContract` evidence
item only yields a `GovernmentContract` signal *incidentally* today. The shared `KeywordSignalExtractor`
already has `GovernmentContract` rules, but they fire only when the awarding-agency text contains
`"department of defense"`, `"nasa"`, `"dod "`, `"defence contract"`, etc. (lines ~101-110). The
USASpending collector, however, synthesizes evidence whose text is agency-agnostic:

- Title: `"Federal contract award {AwardId} — {AwardingAgency} → {RecipientName} (${amount}, {StartDate})"`
- RawText: `"Federal contract award {AwardId} (generated_internal_id …, recipient_id …): {AwardingAgency} awarded {RecipientName} ${amount} starting {StartDate}. Description: …"`

So a **DoD/NASA** award happens to match (`"department of defense"`/`"nasa"` appear as the agency), but
a **GSA / HHS / VA / DOE** award matches nothing — the seeded **AGYS** (~$6,775 HHS) and **CYRX**
(~$4,809 HHS) awards from spec 62 produce **zero** signals. Every real federal award is a legitimate
positive business-trajectory event and should reliably yield exactly one `GovernmentContract` Positive
signal, regardless of awarding agency.

**Downstream payoff.** A `GovernmentContract` Positive signal is a directional `Positive` signal, so it
contributes to `TrajectoryScore` under `radar-formula-v2` (AD-6) and — as an official-record source
type (`GovernmentContract`, `IsThirdPartyAttentionSource == false`, like `Filing`) — adds source-type
**diversity** to `EvidenceConfidenceScore`. This is the same corroboration/diversity unlock spec 57
delivered for filings, now for federal awards.

**Mechanism (no new capability).** Reuse the single deterministic `KeywordSignalExtractor` and extend
its shared rule table with the canonical phrase the collector actually emits: `"federal contract award"`
(the literal Title/RawText prefix on **every** such evidence item). This is a legitimate general
business phrase, **not** a source-type coupling — the extractor still does not read `evidence.SourceType`
or `evidence.Metadata`, preserving spec 57's deliberate source-agnostic design. Because
`"federal contract award"` is a real phrase, adding it to the shared table is harmless-to-helpful for
any other source (an RSS press release announcing a "federal contract award" is a real government-contract
signal too).

---

## Materiality — design decision (RECOMMENDED: option (a), fixed Strength now)

A ~$5k HHS order and a ~$50M DoD award currently map to the **same** fixed Strength, and a truly tiny
award is arguably noise. The clean numeric amount already sits in `Metadata["awardAmount"]` (invariant
decimal string), so amount-tiering is *feasible*. Two options:

- **(a) Keep a fixed Strength for this slice; defer materiality to an explicit follow-up spec.**
- **(b) Make the extractor amount-aware for `GovernmentContract` evidence** — tiered Strength by award
  amount and/or a materiality floor below which no signal is emitted.

**Recommendation: option (a).** Justification:

1. **Keeps the extractor source-agnostic (the whole point of the mechanism above).** Option (b) requires
   `KeywordSignalExtractor` to read `evidence.Metadata["awardAmount"]` and/or `evidence.SourceType` —
   a *deliberate new capability* that couples the shared, source-independent extractor to one collector's
   metadata schema. That is a real design change (does the amount live in metadata for all sources? what
   if metadata is absent? how do RSS "federal contract award" mentions get an amount?) and deserves its
   own slice, not a smuggled-in change to a keyword-table task.
2. **Philosophy: don't overclaim, but don't discard a real signal either.** A federal award — even a small
   one — is a genuine, official-record positive business-trajectory event. Emitting a fixed-Strength
   `Positive` signal states exactly that and no more; it is never advice. The existing
   `DeterministicSignalReviewer` (spec 11) already applies a materiality guardrail: a signal whose
   `Strength < MinMaterialStrength (3)` is flagged `NeedsMoreEvidence`. The current `GovernmentContract`
   Strength is `6`, so a tiny award is *not* silently dropped — the correct place to decide "is a $5k order
   material?" is the reviewer/scoring layer, on typed data, not the keyword table.
3. **Smallest correct, deterministic, reproducible change** — matches the current extractor and this slice's
   1-2h budget. A tiny-award materiality floor and amount-tiered Strength are a clean, well-scoped
   **follow-up spec** (call it out in the PR); this slice makes every award *reliably visible* first.

Whichever a reviewer prefers, the result stays deterministic and reproducible. This spec implements (a).

---

## Assignment

Worktree: any
Dependencies: 62 (USASpending contract collector) merged. 57 (SEC filing-item signals) is the direct
precedent for the pattern (shared rule table, no source-coupling) — read it, but not a code dependency.
Conflicts with: touches the **shared** `KeywordSignalExtractor` rule table and its tests — must NOT run
in parallel with any other extractor-editing slice. No conflict otherwise (no collector/DI/schema change).
Estimated time: ~1-2 h

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs           # MODIFIED: add "federal contract award" (+ optional "contract award")
                                      #   GovernmentContract Positive rule(s), placed to win first-match

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs      # MODIFIED: non-DoD (HHS/GSA) USASpending evidence -> one
                                      #   GovernmentContract Positive signal; DoD award -> exactly one (not two)
```

No production code outside `Radar.Application` changes. The USASpending collector, DI, seed, scoring,
policy, and report are untouched.

---

## Implementation details

### Keyword extractor — add the canonical federal-award cue

Add to the existing `GovernmentContract` group in the `Rules` table (`KeywordSignalExtractor.cs`,
~lines 101-110):

- `new("federal contract award", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m)`
  — **required.** This is the literal Title/RawText prefix the USASpending collector emits on *every*
  `GovernmentContract` evidence item, so it guarantees a signal regardless of awarding agency.
- `new("contract award", SignalType.GovernmentContract, SignalDirection.Positive, 6, 5, 0.6m)`
  — **recommended** (a broader general phrase catching other sources' "contract award" language). Include
  it unless a test surfaces a false positive; it does not match the existing negative-case fixtures
  ("Top Benefits Award", "Series 3 …").

Placement / ordering:

- Add `"federal contract award"` **first in the `GovernmentContract` group** (before
  `"government contract"`/`"department of defense"`). First-match-per-`SignalType` means this rule then
  claims the `GovernmentContract` type for every USASpending award, so DoD and non-DoD awards get the
  **same, uniform** excerpt (drawn from the Title prefix, index 0) and Strength — cleaner and more
  reproducible. All `GovernmentContract` rules already share Strength 6 / Novelty 5 / Confidence 0.6, so
  which one wins does not change the emitted magnitude — only the excerpt/`Reason`.
- If `"contract award"` is included, place it after `"federal contract award"` so the more specific phrase
  wins first-match on USASpending evidence.

Keep everything else unchanged: the matching algorithm, `EvidenceSearchableText.Compose(Title, RawText)`
composition, `CompanyMention = evidence.SourceName`, first-match-per-`SignalType` dedupe, stable
ordering, and the verbatim-excerpt provenance. All numbers stay in domain range (Strength/Novelty 1-10,
Confidence 0-1) so mapped signals pass `SignalValidation`. Do **not** make the extractor read
`evidence.Metadata` or `evidence.SourceType` (that is the deferred materiality slice).

### Why this is not source-coupling

`"federal contract award"` and `"contract award"` are ordinary business phrases, exactly like the SEC
8-K item titles spec 57 added to the same table. The extractor remains blind to *where* the evidence came
from; it only matches text. A press release that says "federal contract award" legitimately produces a
`GovernmentContract` signal too. This preserves the single-extractor / no-source-type-coupling invariant.

---

## Tests

Extend `KeywordSignalExtractorTests` (xUnit; use the existing `MakeEvidence` helper and
`EvidenceBuilder`). Construct evidence text that mirrors the **real** USASpending collector output
(Title + RawText), so the tests prove the actual production path:

1. **Non-DoD (HHS) award now yields a signal — the gap this slice closes.** Evidence whose Title is
   `"Federal contract award 75D30122P12345 — Department of Health and Human Services → AGILYSYS INC ($6,775, 2026-02-10)"`
   and matching RawText yields a single `GovernmentContract` **Positive** signal, with the excerpt a
   verbatim slice of the composed Title+RawText. (Assert `Single`, `SignalType == GovernmentContract`,
   `Direction == "Positive"`, excerpt `Contains` in composed text.) Add a sibling GSA/VA/DOE case (Theory)
   to prove agency-independence.
2. **DoD award still yields exactly ONE `GovernmentContract` signal, not two.** Evidence whose text
   contains BOTH `"Federal contract award …"` and `"Department of Defense"` produces exactly one
   `GovernmentContract` signal (first-match-per-type dedupe), Positive. (Assert `Single` among
   `GovernmentContract`-typed signals; no duplicate.)
3. **Round-trips to a valid `Signal`.** A USASpending-shaped evidence item's extracted signal passes
   `ExtractedSignalMapper.ToSignal(...).IsValid` (excerpt provenance survives the mapper round-trip).
4. **Regressions stay green.** Existing cases unchanged, especially:
   `BenefitsAwardHeadline_DoesNotYieldGovernmentContractSignal` ("Top Benefits Award" must still NOT
   produce a `GovernmentContract` signal — confirms `"contract award"`, if added, does not over-match),
   and `SelectedByNasa_YieldsBothCustomerWinAndGovernmentContract_InEnumOrder`.
5. **Determinism.** Two extractions over the same USASpending-shaped evidence yield equal signal
   sequences (extend or rely on the existing determinism test).

No advice language is introduced; all values in domain range.

---

## Spec-implementation checklist

1. **Code paths replaced:** none removed — this is purely additive to the `Rules` table. The incidental
   DoD/NASA path still works (now subsumed by the earlier-winning `"federal contract award"` rule for
   USASpending evidence, identical magnitude).
2. **Tests:** add the non-DoD/agency-independent cases and the DoD "exactly one" case above; keep all
   existing extractor tests green.
3. **Delete nothing still used:** the existing `GovernmentContract` rules remain valid for RSS/other text
   ("wins a DoD contract", "public procurement", etc.) — do not remove them.
4. **CLAUDE.md:** no architecture change; **no update needed** (note this in the PR).

---

## Constraints

- Target `net10.0`, C# 14. Extraction stays deterministic and in `Radar.Application`, **before** any AI
  (prefer deterministic code first). Reuse the single `KeywordSignalExtractor` — no new extractor.
- **No source-type coupling:** do not read `evidence.SourceType` or `evidence.Metadata` in the extractor
  (materiality/amount-awareness is a deferred follow-up slice).
- No provider SDK; no DB (AD-8, files-first); no AI. Provenance preserved (verbatim excerpt; evidence →
  signal via the mapper). Never emit advice language; `GovernmentContract`/`Positive` states a real award,
  not a recommendation.
- Scope: the keyword rule table + its tests only. Do NOT touch the USASpending collector, DI, seed,
  scoring formula, signal review, policy, or the report.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `KeywordSignalExtractor` emits exactly one `GovernmentContract` **Positive** signal for a USASpending
      `GovernmentContract` evidence item **regardless of awarding agency**, via a `"federal contract award"`
      rule (and optionally `"contract award"`) added to the shared rule table.
- [ ] A **non-DoD** (e.g. HHS/GSA — the seeded AGYS/CYRX HHS awards) USASpending-shaped evidence item, which
      previously yielded no signal, now yields a `GovernmentContract` Positive signal (asserted by test).
- [ ] A **DoD** USASpending-shaped evidence item yields **exactly one** `GovernmentContract` signal (not two)
      — first-match-per-`SignalType` dedupe preserved (asserted by test).
- [ ] Verbatim-excerpt provenance is preserved and each emitted signal round-trips valid via
      `ExtractedSignalMapper.ToSignal`; `CompanyMention = evidence.SourceName` unchanged.
- [ ] The extractor still does NOT read `evidence.SourceType`/`evidence.Metadata` (source-agnostic; no new
      capability); existing extractor tests (incl. the "Top Benefits Award" negative case and the NASA
      dual-signal case) stay green.
- [ ] No collector/DI/seed/scoring/policy/report change. Materiality (amount-tiered Strength / floor) is
      explicitly noted as a deferred follow-up, not implemented here. `build`/`test` green.
