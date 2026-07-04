# Task: Give the extractor a NEGATIVE capital-structure / dilution vocabulary; make CapitalRaise direction-aware

> **SEQUENCING + VERSION (read first).** This slice MUST land **AFTER spec 84**
> (`docs/next/84-news-attention-breadth-by-publisher.md`), which also bumps
> `ScoringEngine.ScoringConfigVersion`. Both slices edit `ScoringEngine.cs`, and this slice edits the
> extractor rule table (`KeywordSignalExtractor.cs`) — it must **NOT** run in parallel with any
> extractor / scoring / engine / directional-filing slice. The version numbers below are written as
> `v7 → v8` **assuming spec 84 (v6→v7) has already merged**; if the tree differs, **read the current
> `ScoringConfigVersion` from `ScoringEngine.cs` and bump to the next integer** — treat every
> `radar-scoring-config-v7 → v8` reference as "current value + 1", never a hard-coded target.

## Overview

Radar can currently only represent capital-structure events as **growth**. The deterministic
`KeywordSignalExtractor` maps `CapitalRaise` phrases (`"convertible note"`, `"credit facility"`,
`"debt financing"`, `"raises $"`, `"funding round"`, `"series a/b/c/seed"`) to
`SignalDirection.Positive` (Strength 5), and its only two SEC-item cues (`"direct financial obligation"`
2.03, `"unregistered sales of equity"` 3.02) are `Neutral`
(`src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs:86-98`). The **only** Negative-direction
signals Radar can emit today are `GuidanceChange` on `"cuts/lowers guidance/outlook"`
(lines 108-111) plus the AI earnings-release `Deteriorating` read (specs 74/75, 8-K item 2.02 only).

**The gap.** Dilutive / distress capital-structure events — **rights offerings**, **registered direct
offerings** of common stock + warrants, **at-the-market (ATM) offerings**, **shelf takedowns**, **warrant
issuance**, **reverse stock splits** — are today either **invisible** (they match no rule) or, worse, read
as **Positive growth capital** (a "raises $" or "offering"-adjacent phrase). A company being diluted into
the ground can currently be scored neutral-to-positive.

**Motivating live case (EOSE — Eos Energy Enterprises, just added to the watch universe).** Its actual
press-release headlines are literally *"Commencement of Rights Offering"*, *"Announces Proposed Registered
Direct Offering of Common Stock and Warrants"*, and *"Pricing of Registered Direct Offering…"* — classic
serial dilution. Radar cannot represent any of these as negative today.

**The fix (smallest correct change; deterministic-before-AI).** Add tightly-scoped **Negative-direction
`CapitalRaise`** cues for dilutive/distress capital-structure phrases that appear in **press-release text**
(the tractable surface — RSS carries the headline always and the body when the feed supplies it), ordered
**before** the existing Positive `CapitalRaise` cues so distress wins first-match-per-type. Demote the two
raw-debt Positive cues whose valence the code genuinely cannot read (`"credit facility"`, `"debt
financing"`) to `Neutral`; keep the venture-style raises Positive. **No Domain enum change** (reuse
`SignalType.CapitalRaise` with `SignalDirection.Negative`), **no formula-math change** (a Negative signal
already lowers Trajectory under `radar-formula-v2`, AD-6). This is scoring-affecting (new directional
signals; two rule-direction demotions) so it **bumps `ScoringEngine.ScoringConfigVersion`** (AD-10).

**Going-concern is deliberately OUT OF SCOPE — verified undetectable today (see grounding facts).**

---

## Assignment

Worktree: any
Dependencies: **57/66/70** (the `KeywordSignalExtractor` rule table + its two source/metadata-aware
branches — all merged), **58** (`radar-formula-v2` Trajectory: Positive/Negative drive the read;
Neutral/Mixed weigh 0), **69/70** (`ScoringConfigVersion` stamp + AD-10). **Spec 84** (news-attention
breadth) MUST land first — it also bumps `ScoringConfigVersion` and edits `ScoringEngine.cs`. This is a
directed extractor-vocabulary slice driven by the EOSE live finding, not the generic planner loop; it is
**not** architecture-gated.
Conflicts with: touches `KeywordSignalExtractor.cs` (the `Rules` table) + its tests, and
`ScoringEngine.cs` (`ScoringConfigVersion` constant + comment) + its assertion. Must **NOT** run in
parallel with any extractor / scoring / engine / directional-filing slice — sequence it. No Domain,
DI-shape, collector, report-renderer, or formula-math change.
Estimated time: ~1.5–2 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **The rule table is the only place to add cues.** `KeywordSignalExtractor.Rules`
  (`KeywordSignalExtractor.cs:46-135`) is a fixed, ordered `KeywordSignalRule[]` matched
  case-insensitively as substrings of the composed searchable text `Title + "\n" + RawText`
  (`EvidenceSearchableText.Compose`, used at line 206). **First matching rule per `SignalType` wins**
  (the `emittedTypes` HashSet, lines 212-229) — so **within `CapitalRaise`, whichever cue appears earliest
  in the `Rules` array claims the type**. Ordering the Negative dilution cues **before** the Positive/Neutral
  `CapitalRaise` cues makes distress win when a headline mixes both (e.g. a registered direct offering that
  also says "raises $30 million").
- **Press-release text carries the dilution phrases; SEC filing text does NOT.**
  - `RssPressReleaseCollector.MapToEvidence` sets `RawText: item.Content ?? item.Summary ?? item.Title`
    and `Title: item.Title` (`src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs:108-109`). So the
    **headline is always searchable** and the body is searchable when the feed supplies `content:encoded`/
    `summary`. EOSE's cues (*"Rights Offering"*, *"Registered Direct Offering of Common Stock and
    Warrants"*) live in the **title**, which is always present → these cues are reliably detectable.
  - `SecEdgarFilingCollector.MapToEvidence` synthesizes filing `Title`/`RawText` from **metadata only** —
    `form + human description + raw 8-K item codes + the official item titles resolved by
    SecFormItemTitles.ResolveTitles` (`src/Radar.Infrastructure/Sec/SecEdgarFilingCollector.cs:130-156`).
    **No filing body text is fetched or fabricated.** `SecFormItemTitles` maps only 8-K items
    **1.01 / 2.01 / 2.02 / 2.03 / 3.02 / 5.02** (`SecFormItemTitles.cs:14-23`). A shelf/offering shows up in
    SEC filings mostly via **item 8.01 (Other Events)** and dedicated forms (S-1/S-3/424B5/424B), **none of
    which are in the title map**, and going-concern / "substantial doubt" language lives **only** in
    **10-K/10-Q body text**, which Radar does not ingest at all.
- **GOING-CONCERN DETECTABILITY VERDICT: undetectable today → scoped OUT.** Because SEC evidence carries
  only 8-K item-title metadata (no full-body text) and there is no 10-K/10-Q body ingestion, a
  `"going concern"` / `"substantial doubt"` keyword rule **would never fire** on the evidence Radar
  currently holds. Adding it would be a dead rule against text that is not ingested — a violation of
  "do not plan rules against text that isn't ingested". It is therefore **deferred to a future AI/full-text
  read** (see Out of scope). Only the **press-release-detectable dilution cues** are in scope. (A rare PR
  that says "going concern" is possible, but scoping the whole class to press-release keyword matching would
  be misleading — record it as the deferred slice instead of half-implementing it.)
- **A Negative signal genuinely lowers Trajectory (AD-6, `radar-formula-v2`).** Trajectory is the
  confidence/recency-weighted mean of directional strength over **only** Positive/Negative signals
  (`Positive +1`, `Negative −1`); Neutral/Mixed are excluded from numerator AND denominator
  (`RadarScoreFormulaV2.cs:21-22, 62-63, 117-137`). So a Negative `CapitalRaise` correctly drags Trajectory
  below 50 toward `Thesis deteriorating`, and a demoted-to-`Neutral` cue correctly contributes **0** to
  Trajectory (a capital event whose valence the code can't read). **No formula-math change is needed** — the
  math already handles a Negative signal.
- **`ScoringConfigVersion` is a code constant.** `ScoringEngine.cs:40` currently reads
  `private const string ScoringConfigVersion = "radar-scoring-config-v6";` and stamps every snapshot at
  line 161. Spec 84 bumps it to `v7`; this slice bumps it again to `v8` (order-robust: current + 1). The
  formula/engine identity `ScoringVersion` = `EngineVersion` = `"mvp-engine-v1"` and formula `Version` =
  `"radar-formula-v2"` are **unchanged**.
- **Domain/switch impact of reusing `CapitalRaise` Negative: none.** `SignalType`
  (`src/Radar.Domain/Signals/SignalType.cs`) already contains `CapitalRaise`; `SignalDirection` already
  contains `Negative`. Reusing them adds **no** enum member and touches **no** switch/exhaustiveness site.
  A new `SignalType` (e.g. `GoingConcern`) would be a Domain change with downstream blast radius — **not
  taken** (see the Type-mapping decision).

---

## Design decisions (make these explicit in the PR)

### 1. Type mapping — reuse `CapitalRaise` + `Negative` (smallest correct)

Dilutive raises are still capital-structure events, so they stay `SignalType.CapitalRaise`; the new
information is their **direction** (`Negative`). This needs **no Domain enum change**, **no new switch
arm anywhere**, and **no formula change** — it is the minimal correct representation and it slots straight
into the existing `CapitalRaise` first-match-per-type group. A new `SignalType` (`GoingConcern` /
`FinancialDistress`) is **rejected** for this slice: it is a Domain addition with a wider blast radius
(exhaustiveness, mappers, any type-keyed logic) and — given the going-concern detectability verdict above —
it would have **no detectable input** today. If a future AI/full-text distress read lands, it can introduce
a dedicated type deliberately in its own slice.

### 2. Direction-awareness of the EXISTING `CapitalRaise` cues (scoring-affecting — call out)

The existing Positive cues split into two groups by whether the code can actually read growth valence:

- **KEEP Positive — genuinely growth-leaning venture financing:** `"raises $"`, `"funding round"`,
  `"series a"`, `"series b"`, `"series c"`, `"series seed"`. A named funding round / "series" raise is a
  primary-market growth event; "raises $" is a company announcing new capital in. These stay `Positive 5`.
  *(Caveat noted below: "raises $" can co-occur with a distress offering; the Negative-first ordering makes
  the distress cue win the type in that case, so this stays correct.)*
- **DEMOTE to Neutral — a capital event the code cannot read the valence of:** `"credit facility"` and
  `"debt financing"` are **debt**, not equity dilution and not clearly growth — a revolver draw or a
  refinancing under distress reads identical to expansion financing at the keyword level. Demote both to
  `SignalDirection.Neutral` (Strength 4, Confidence 0.5m — matching the existing Neutral SEC-item cues at
  lines 97-98), so they represent "a capital event with unreadable valence" and contribute **0** to
  Trajectory rather than a spurious Positive. **`"convertible note"` — DEMOTE to Neutral too:** a
  convertible is a hybrid that is frequently dilutive (conversion into equity) and is not a clean growth
  signal; the code cannot tell an accretive convertible from a death-spiral one, so Neutral is the honest
  read.
  > This is a **scoring-affecting rule-direction change** (three cues stop contributing +1 to Trajectory).
  > It is a deliberate correctness fix (the code was over-claiming growth it cannot verify), and it is why
  > the `ScoringConfigVersion` bump is required. State each demotion and its justification in the PR. Do
  > **not** demote the venture-style `"raises $"`/`"funding round"`/`"series *"` cues — those are genuinely
  > growth-leaning and out of scope to touch.

### 3. Negative dilution cues — TIGHTLY scoped, ordered first (false-positive control)

Add these Negative `CapitalRaise` cues **at the top of the `CapitalRaise` group** (before the KEEP-Positive
and Neutral cues), so a headline mixing a dilutive offering with a "raises $" claim resolves to **Negative**
via first-match-per-type. Use **specific multi-word phrases** — never the bare word `"offering"` (far too
broad: "product offering", "service offering", "special offering") and never bare `"warrant"` alone in a way
that catches "warrants attention"/"warranty":

| Phrase (lowercase substring) | Direction | Strength | Novelty | Confidence | Rationale |
|---|---|---|---|---|---|
| `"rights offering"` | Negative | 6 | 5 | 0.6m | Classic dilutive pro-rata equity raise (EOSE). |
| `"registered direct offering"` | Negative | 6 | 5 | 0.6m | Discounted equity + warrants to institutions (EOSE). |
| `"at-the-market offering"` | Negative | 6 | 5 | 0.6m | ATM shelf dribble-out — ongoing dilution. |
| `"atm offering"` | Negative | 6 | 5 | 0.6m | Common shorthand for the above. |
| `"shelf registration"` | Negative | 5 | 5 | 0.55m | Capacity for future dilution (weaker/forward-looking → lower Strength/Confidence). |
| `"shelf offering"` | Negative | 5 | 5 | 0.55m | Takedown off a shelf. |
| `"reverse stock split"` | Negative | 6 | 5 | 0.6m | Near-universally a distress / listing-compliance move. |
| `"warrants to purchase"` | Negative | 5 | 5 | 0.55m | Warrant issuance = future dilution; the multi-word form avoids "warranty"/"warrants attention". |
| `"dilution"` | Negative | 5 | 4 | 0.5m | Explicit; lower Novelty/Confidence (may appear in risk-factor boilerplate). |
| `"dilutive"` | Negative | 5 | 4 | 0.5m | As above. |

Strength/Novelty/Confidence are chosen to be **material** (Strength ≥ 3, so the existing
`DeterministicSignalReviewer` `MinMaterialStrength = 3` guard does not flag them) but **not** to overwhelm a
genuine Positive event, and all values stay within domain ranges (Strength/Novelty 1-10, Confidence 0-1) so
mapped signals pass `SignalValidation`. Forward-looking / boilerplate-prone cues (`shelf *`, `warrants to
purchase`, `dilution`/`dilutive`) get slightly lower Strength/Confidence, matching how the table already
lowers the SEC-item Neutral cues.

**Do NOT add** bare `"offering"`, bare `"warrant"`/`"warrants"`, or `"equity offering"` (too broad — a
plain follow-on can be routine and "equity offering" overlaps healthy secondaries). Keep the phrases
narrow; the multi-word forms are the mitigation for the false-positive risk. Note this reasoning in a
code comment on the new rule group (mirroring the existing SEC-item comments).

### 4. Ordering within the `CapitalRaise` group (load-bearing)

The rule array section becomes, in order: **Negative dilution cues (new, first)** → **Positive venture
cues (`"raises $"`, `"funding round"`, `"series *"`)** → **Neutral cues (`"convertible note"` now Neutral,
`"credit facility"` now Neutral, `"debt financing"` now Neutral, plus the two SEC-item Neutral cues)**.
Because `SignalType` enum ordering + match index drive the final signal sort (lines 231-236) and
first-match-per-type drives dedupe, putting Negative first guarantees a mixed dilutive/growth headline
emits the **Negative** `CapitalRaise`. Add a short comment explaining the deliberate ordering.

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs        # MODIFIED: add Negative dilution cues at the TOP of the CapitalRaise
                                   #   group (ordered before Positive/Neutral); demote "convertible note",
                                   #   "credit facility", "debt financing" from Positive -> Neutral; keep
                                   #   "raises $"/"funding round"/"series *" Positive. Comment the ordering +
                                   #   false-positive scoping. No new metadata/source-type branch.

src/Radar.Application/Scoring/
  ScoringEngine.cs                 # MODIFIED: bump ScoringConfigVersion to the next integer (v7 -> v8 if 84
                                   #   landed; else current + 1) + update the comment to record this slice.
                                   #   No formula/engine identity change.

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs   # MODIFIED: dilutive-offering PRs -> Negative CapitalRaise; demoted debt
                                   #   cues -> Neutral; venture raises still Positive; mixed headline ->
                                   #   Negative; false-positive negatives (bare "offering"/"warranty"); the
                                   #   existing NewCapitalRaisePhrases_YieldSingleCapitalRaiseSignal test must
                                   #   be UPDATED for the demotions (see Tests).

tests/Radar.Application.Tests/Scoring/
  (existing scoring engine tests)  # MODIFIED: update the ScoringConfigVersion assertion to the new value.
```

No `Radar.Domain` change (no `SignalType`/`SignalDirection` member added). No collector, DI-shape,
report-renderer, or formula-math change. No new package reference. No provider SDK. No DB (AD-8).

---

## Implementation details

### `KeywordSignalExtractor` (Application)

- In the `Rules` array, **replace** the current `CapitalRaise` block (lines 86-98) with three ordered
  sub-groups:
  1. **Negative dilution cues (new, FIRST)** — the ten phrases in the table above, all
     `SignalType.CapitalRaise, SignalDirection.Negative` with the listed Strength/Novelty/Confidence.
  2. **Positive venture cues (KEEP)** — `"raises $"`, `"funding round"`, `"series a"`, `"series b"`,
     `"series c"`, `"series seed"` stay `Positive 5, 5, 0.6m`.
  3. **Neutral cues** — `"convertible note"`, `"credit facility"`, `"debt financing"` **demoted** to
     `Neutral 4, 5, 0.5m` (matching the existing SEC-item Neutral cues), followed by the unchanged
     `"direct financial obligation"` and `"unregistered sales of equity"` Neutral SEC-item cues.
- Add a comment block above the Negative sub-group explaining: (a) dilution/distress capital events are
  Negative and are ordered first so they win first-match-per-type over a co-occurring "raises $"; (b) the
  phrases are deliberately multi-word/specific to avoid false positives (no bare "offering"/"warrant"); and
  (c) going-concern/substantial-doubt is NOT here because it lives in 10-K/10-Q body text Radar does not
  ingest (deferred to a future AI/full-text read).
- **No** new source-type or metadata-aware branch — the `ExtractAsync` body (the NewsArticle branch,
  the `GovernmentContract` amount scaling) is **unchanged**. This is a pure rule-table edit, so the
  WATCH-ITEM about a third inline source-type special-case does not apply.
- Determinism preserved (AD-3): fixed, visibly-constant table; first-match-per-type dedupe; stable sort by
  `SignalType` then match index — all unchanged in mechanism.

### `ScoringEngine` (Application)

- Bump `ScoringConfigVersion` to the next integer (order-robust: read the current value and `+1`; expected
  `"radar-scoring-config-v7"` → `"radar-scoring-config-v8"` once spec 84 has merged). Update the constant's
  comment to record: "This generation adds Negative-direction dilutive `CapitalRaise` cues (rights /
  registered-direct / ATM / shelf / warrant / reverse-split offerings) and demotes `convertible note` /
  `credit facility` / `debt financing` from Positive to Neutral — extractor-rule change moves scoring output
  (new Negative signals lower Trajectory; demoted cues now weigh 0), so per AD-10 the stamp bumps."
- Do **NOT** touch `ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the formula math is
  byte-for-byte unchanged; only **which signals/directions are produced** changed, upstream in the extractor.
  This is the exact AD-10 case: a scoring-affecting change that is not a formula/engine identity change.

### No AD-6 formula change

`radar-formula-v2` already maps `Negative → −1` and excludes Neutral/Mixed from Trajectory. A Negative
`CapitalRaise` lowers Trajectory and a demoted-to-Neutral cue contributes 0 — both are the intended,
existing behaviour. No `radar-formula-v3`, no AD-6 edit.

---

## Version-bump obligation (state explicitly in the PR)

- **`ScoringEngine.ScoringConfigVersion` — BUMPED (AD-10).** Extractor-rule change (new directional cues +
  three demotions) can move Trajectory/Opportunity/ranking/action labels, so per AD-10 (and the CLAUDE.md
  spec-implementation checklist: "extractor-rule changes bump `ScoringConfigVersion`") the whole-generation
  stamp bumps in this same slice. Order-robust: current value `+1` (expected `v7 → v8` after spec 84).
- **`RadarScoreFormulaV2.Version` = `radar-formula-v2` — UNCHANGED.** No formula code/constant/shape edit;
  the math already handles Negative signals. Not `radar-formula-v3`, no AD-6 update.
- **`EngineVersion` / `ScoringVersion` = `mvp-engine-v1` — UNCHANGED.**

---

## Tests

### `KeywordSignalExtractorTests` (Application.Tests/SignalExtraction)

Use the existing `MakeEvidence(rawText, title, …)` and `ExtractAsync` helpers. Match the existing
`[Theory]`/`[InlineData]` + `Assert.Single` + `Assert.Equal(SignalType…, Direction)` style.

1. **Dilutive offerings → Negative `CapitalRaise` (the core fix).** `[Theory]` over EOSE-style headlines/
   bodies, each asserting a single `CapitalRaise` signal with `Direction == "Negative"`:
   - title `"Eos Energy Announces Commencement of Rights Offering"` (cue in title only — proves headline
     detection).
   - `"Announces Proposed Registered Direct Offering of Common Stock and Warrants"`.
   - `"Company enters into an at-the-market offering program"` / `"ATM offering of up to $50 million"`.
   - `"Files shelf registration statement"` / `"prices a shelf offering"`.
   - `"announces a 1-for-10 reverse stock split"`.
   - `"issues warrants to purchase up to 5,000,000 shares"`.
   - `"the transaction is dilutive to existing shareholders"`.
2. **Mixed dilutive + "raises $" headline → Negative (ordering guarantee).** e.g. title `"Announces
   Registered Direct Offering; raises $30 million"` → single `CapitalRaise`, `Direction == "Negative"`
   (Negative-first ordering wins first-match-per-type). Lock this — it is the false-positive-avoidance
   mechanism.
3. **Demoted debt cues → Neutral (UPDATE the existing test).** The existing
   `NewCapitalRaisePhrases_YieldSingleCapitalRaiseSignal` (`credit facility` / `debt financing` /
   `convertible note`, currently asserting `Positive`) MUST be updated to assert `Direction == "Neutral"`
   for these three cues (do not leave the stale `Positive` assertion). Keep it a single `CapitalRaise`
   signal. State in the PR that this test changed because the cues were demoted.
4. **Venture raises still Positive (regression lock).** `[Theory]` over `"raises $12 million in a funding
   round"`, `"closes its Series B"`, `"series seed round"` → single `CapitalRaise`, `Direction ==
   "Positive"`. (Guards that the demotion did not over-reach.)
5. **False-positive negatives (scope guard).** Assert **no** `CapitalRaise` signal for:
   - `"launches a new product offering for enterprises"` (bare "offering" — must NOT match).
   - `"extends the standard warranty to five years"` (must NOT match "warrants to purchase").
   Use `Assert.DoesNotContain(output.Signals, s => s.SignalType == SignalType.CapitalRaise.ToString())`
   (mirroring the existing `Series3Headline_DoesNotYieldCapitalRaiseSignal` test at ~line 549).
6. **Round-trip validity (provenance).** A dilutive-offering evidence's Negative `CapitalRaise` maps via
   `ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt)` to `IsValid == true` (Strength/Novelty/
   Confidence in range; excerpt present) — mirroring
   `MultiplePhrasesSameType_YieldSingleDedupedSignal_AndRoundTripValid`.
7. **Trajectory impact (recommended, optional).** A minimal scoring/formula assertion (or an
   `ExtractedSignalMapper` + `RadarScoreFormulaV2` unit) showing a lone Negative `CapitalRaise` yields
   `TrajectoryScore < 50` — demonstrating the Negative signal genuinely deteriorates the thesis (AD-6). If
   an equivalent Negative-direction Trajectory test already exists in the formula tests, reference/extend it
   rather than duplicate.

The existing `CapitalItemTitles_YieldNeutralCapitalRaise` (SEC-item 2.03/3.02 → Neutral) test stays green
(those cues are unchanged). All emitted `Reason`/excerpt text stays advice-free (AD-9) — the phrases are
event descriptions, never "buy/sell".

### Scoring (Application.Tests/Scoring)

8. **`ScoringConfigVersion` stamp.** Update any assertion of the stamp to the new value (expected
   `"radar-scoring-config-v8"`; if spec 84 has not merged, the current value `+1`).
   `ScoringVersion`/`EngineVersion`/formula `Version` assertions unchanged.

Existing extractor, scoring, engine, and report tests stay green.

---

## Spec-implementation checklist

1. **Code paths replaced:** the `CapitalRaise` rule block gains ten Negative dilution cues (ordered first)
   and demotes `convertible note` / `credit facility` / `debt financing` to Neutral. `ScoringConfigVersion`
   bumped. No extractor `ExtractAsync` body change; no formula/collector/report change.
2. **Tests:** UPDATE the stale `NewCapitalRaisePhrases_…` Positive assertion to Neutral; add the
   dilutive-offering, mixed-headline, venture-still-Positive, false-positive, round-trip, and (recommended)
   Trajectory cases; update the `ScoringConfigVersion` assertion. Keep all other tests green.
3. **Delete nothing still used** — the venture Positive cues and the SEC-item Neutral cues are retained.
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule; this follows AD-6 (formula
   unchanged — Negative already lowers Trajectory), AD-9 (signal direction is internal; the resulting
   `Thesis deteriorating`/`Ignore` label is the allowed, desired behaviour — no advice language),
   AD-10 (`ScoringConfigVersion` bump). No AD entry required.
5. **Bump `ScoringEngine.ScoringConfigVersion`** to current `+1` (AD-10) — done in this slice.

---

## Constraints

- Target `net10.0`, C# 14. Pure Application-layer rule-table + scoring-stamp edit; no provider SDK, no AI,
  no HTTP, no DB (AD-8, files-first). Layering (AD-5) unchanged.
- **Provenance is sacred and preserved:** each new Negative signal carries a verbatim excerpt from the
  matched searchable text (`BuildExcerpt`) and maps to a `Signal` referencing the evidence `Id` — the
  evidence→signal→score chain is intact. No new metadata/source-type coupling.
- **Determinism (AD-3):** fixed, ordered, visibly-constant rule table; first-match-per-type dedupe; stable
  sort — all unchanged in mechanism.
- **AD-6 formula UNCHANGED** (`radar-formula-v2` stays; no `radar-formula-v3`, no AD-6 edit). A Negative
  `CapitalRaise` lowers Trajectory and a demoted-Neutral cue weighs 0 — the intended existing math.
- **AD-9:** the signal `Direction` is internal; the extractor emits only event-description `Reason`/excerpt
  text (never "buy/sell/guaranteed/safe bet"). A Negative dilution signal correctly lowering Trajectory
  toward `Thesis deteriorating`/`Ignore` is the **desired, allowed** behaviour. No banned tokens.
- **Bump `ScoringEngine.ScoringConfigVersion`** to current `+1` (AD-10); do NOT touch
  `ScoringVersion`/`EngineVersion`/formula `Version`.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (note, do NOT implement this round)

- **AI / full-text going-concern / liquidity read.** `"going concern"` / `"substantial doubt"` and
  balance-sheet distress live in **10-K/10-Q body text** Radar does not ingest (SEC evidence carries only
  8-K item-title metadata — verified above), so a keyword rule for them would never fire. This is deferred
  to a future AI/full-text slice (a dedicated distress read, which could then justify a new `GoingConcern`/
  `FinancialDistress` `SignalType`). Do **not** add a dead going-concern keyword rule here.
- **Wiring the `radar-skeptic-reviewer` into the live pipeline** — a separate future slice; not touched here.
- **A new `SignalType`** (`GoingConcern`/`FinancialDistress`) — rejected for this slice (Domain blast radius,
  and no detectable input today). Reuse `CapitalRaise` + `Negative`.
- **Any formula-math retune** (Trajectory scale, Negative weighting) — the existing math already handles
  Negative; retuning would be a separate `radar-formula-v3` + AD-6 slice.
- Any collector, report-renderer, DI-shape, or Domain change.

---

## Acceptance criteria

- [ ] `KeywordSignalExtractor.Rules` gains tightly-scoped Negative-direction `CapitalRaise` cues
      (`rights offering`, `registered direct offering`, `at-the-market offering`/`atm offering`,
      `shelf registration`/`shelf offering`, `reverse stock split`, `warrants to purchase`,
      `dilution`/`dilutive`), ordered **before** the Positive/Neutral `CapitalRaise` cues so a dilutive
      headline wins first-match-per-type over a co-occurring `raises $`. A comment explains the ordering and
      the multi-word false-positive scoping.
- [ ] `convertible note`, `credit facility`, and `debt financing` are demoted from `Positive` to `Neutral`
      (Strength 4, Confidence 0.5m); `raises $` / `funding round` / `series a|b|c|seed` stay `Positive`; the
      two SEC-item Neutral cues are unchanged. No Domain enum member is added (reuse `CapitalRaise` +
      `Negative`); no `ExtractAsync` body / source-type branch change.
- [ ] Going-concern / substantial-doubt is NOT implemented as a keyword rule (verified undetectable: SEC
      evidence carries only 8-K item-title metadata, no 10-K/10-Q body ingestion); it is recorded as a
      deferred AI/full-text slice with the rationale in the code comment and the PR.
- [ ] `RadarScoreFormulaV2.Compute` is unchanged and `RadarScoreFormulaV2.Version` remains
      `"radar-formula-v2"` (no `radar-formula-v3`, no AD-6 edit). A lone Negative `CapitalRaise` yields
      `TrajectoryScore < 50` (Negative genuinely deteriorates the thesis, AD-6).
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped to the next integer (order-robust; expected
      `"radar-scoring-config-v7"` → `"radar-scoring-config-v8"` after spec 84) with an updated comment
      recording this slice; `ScoringVersion`/`EngineVersion`/formula `Version` unchanged; every test
      asserting the old stamp is updated.
- [ ] Tests: dilutive offerings → Negative `CapitalRaise`; mixed dilutive+`raises $` headline → Negative;
      demoted debt cues → Neutral (the existing `NewCapitalRaisePhrases_…` test updated, not left Positive);
      venture raises still Positive; false-positive negatives (bare `offering`, `warranty` do not match);
      round-trip validity; the `ScoringConfigVersion` assertion updated. All other extractor/scoring/engine/
      report tests green.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance, and AD-9 label/advice rules
      preserved; no Domain/collector/DI-shape/report/formula-math change. `dotnet build` / `dotnet test` on
      `Radar.sln -c Release` are green.
