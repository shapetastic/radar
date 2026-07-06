# Task: `InstitutionalOwnership` SignalType + 13D/13G extractor rule group (scoring-trunk half)

> **DIRECTED FEATURE (read first).** This is the **first of two** sequenced slices adding a new
> deterministic *institutional / activist ownership* axis to Radar, independent from the existing insider
> (`secform4`), filing (`sec`), gov-demand (`usaspending`), and news axes. This slice lands the **Domain +
> scoring-trunk** changes only: a new `SignalType.InstitutionalOwnership`, the deterministic extractor rule
> group that maps three fixed ownership phrases to it, the `RuleSetVersion` bump those new rules require, and
> the fingerprint repin. The **collector** that produces the evidence carrying those phrases is spec **100**
> (`docs/next/100-sec-13d-13g-institutional-ownership-collector.md`), which depends on this one. Splitting
> here is deliberate: the enum + extractor + fingerprint touch the **scoring trunk** (Domain shared record,
> `KeywordSignalExtractor`, the fingerprint tests) and must be sequenced away from any other scoring/extractor
> slice, whereas the collector is pure Infrastructure. The **fixed phrase strings defined here are the
> contract** spec 100 emits — get them exactly right.

## Overview

Radar has no signal type for **institutional / activist beneficial ownership** — the "smart money" axis. A
Schedule **13D** filing marks an activist taking a **> 5 % stake with intent** (a forward catalyst); a **13G**
marks **passive** > 5 % accumulation. Today the only ownership-adjacent type is `SignalType.InsiderBuying`,
which is **insider-specific** (company officers/directors filing Form 4) — a 13D/13G filer is an **external**
beneficial owner, so overloading `InsiderBuying` would be semantically wrong (verified: `InsiderBuying`'s
collector, extractor phrases, and materiality tiers are all framed around *insider* transactions — spec 93/96).

This slice adds a new, honest category — **`SignalType.InstitutionalOwnership`** — and teaches the
deterministic `KeywordSignalExtractor` to map three fixed ownership phrases onto it, mirroring exactly how the
GovernmentContract and InsiderBuying rule groups already turn a collector-synthesized phrase into a typed,
directional signal. The **valence (13D activist vs 13G passive vs amendment) rides on `SignalDirection`**, the
settled precedent for `InsiderBuying` (buy/sell), `CapitalRaise` (raise/dilution), and `GuidanceChange`
(raise/cut) — one category on the enum, direction expresses the read. No new formula, no black-box score.

After this slice the type is **defined and rule-mapped but unfed** — precisely the state `InsiderBuying` was in
before spec 93 fed it. Spec 100 feeds it from the SEC 13D/13G collector.

---

## Assignment

Worktree: any — but it edits the **scoring trunk** (`Radar.Domain` shared enum, `KeywordSignalExtractor.cs`,
and repins the fingerprint tests), so it **must NOT run in parallel** with any scoring / extractor / fingerprint
slice (it bumps `KeywordSignalExtractor.RuleSetVersion`, which re-stamps `ScoringConfigVersion`).
Dependencies: 93 (InsiderBuying rule-group precedent — merged), 95 (`SignalSourceDescriptor` /
`RuleSetVersion` folded into the fingerprint — merged), 96 (config insider tiers — merged).
Conflicts with: spec 100 (the collector — sequence AFTER this; it emits the phrases defined here); any slice
touching `KeywordSignalExtractor` / `ScoringConfigFingerprint` / `SignalType`.
Estimated time: ~1–1.5 h

---

## Domain: add `SignalType.InstitutionalOwnership` — decision + justification

**Decision: add a new `SignalType.InstitutionalOwnership` enum value** (NOT reuse `InsiderBuying` or `Other`).
Smallest change that is semantically honest:

- **`InsiderBuying` would be wrong.** It is insider-specific by construction (Form 4 officers/directors,
  `insiderNetValue`/`insiderCluster` metadata, buy/sell tiers). A 13D/13G reporting person is an **external**
  institution/activist. Overloading `InsiderBuying` would conflate two distinct axes and corrupt its
  materiality semantics — the task and CLAUDE.md's provenance ethos both forbid the silent overload.
- **`Other` would be wrong.** It is the untyped catch-all; routing ownership through it makes an effectively
  black-box signal the report/formula cannot distinguish (violates "typed records," "no black-box scores").
- **One new umbrella type, valence on direction.** `InstitutionalOwnership` covers both the activist 13D and
  the passive 13G; direction/strength expresses which. This is the settled "category on enum, valence on
  `SignalDirection`" pattern (`InsiderBuying`, `CapitalRaise`, `GuidanceChange`). The name
  `InstitutionalOwnership` is the neutral umbrella (a 13G filer is an institution; a 13D activist is a subset);
  the alternative `ActivistStake` is too narrow (excludes passive 13G). **Revisit** (record in
  `architecture-decisions.md` if chosen later): a future split into distinct activist/passive types is
  possible, but not warranted now.

**Ripple surface (all verified minimal):**
- `ExtractedSignalMapper` parses `SignalType` **by name** via `Enum.TryParse` (no exhaustive switch) — a new
  value is accepted automatically once the extractor emits its name.
- `RadarScoreFormulaV5` special-cases **only** `SignalType.MediaAttention` (attention/reach, line ~153);
  every other type contributes to **TrajectoryScore via `SignalDirection`**. So `InstitutionalOwnership`
  Positive/Neutral/Negative feeds Trajectory as a **directional corroborating signal, exactly like
  `InsiderBuying`** — **no `_formula.Version` bump, no scoring-math change**.
- `MarkdownWeeklyReportRenderer` prints `signal.Type.ToString()` (no exhaustive switch); a new value renders
  as its name with **no advice language** (AD-9 unaffected — the type name is factual, not a recommendation).

---

## The fixed extractor phrases (the contract spec 100 must emit)

The collector (spec 100) decides direction from the SEC **form type** and synthesizes exactly one of these
fixed phrases into the evidence Title/RawText; the extractor only maps **phrase → fixed
type+direction+strength** (never re-derives valence — mirrors the GovernmentContract/InsiderBuying precedent).
Three phrases:

| Phrase (case-insensitive substring) | Source form | Type | Direction | Strength | Novelty | Confidence |
|---|---|---|---|---:|---:|---:|
| `activist beneficial-ownership stake (13d)` | `SC 13D` (original) | `InstitutionalOwnership` | Positive | 6 | 5 | 0.6 |
| `passive beneficial-ownership stake (13g)` | `SC 13G` (original) | `InstitutionalOwnership` | Neutral | 3 | 5 | 0.5 |
| `beneficial-ownership amendment (routine)` | `SC 13D/A`, `SC 13G/A` | `InstitutionalOwnership` | Neutral | 3 | 4 | 0.45 |

Rationale:
- **13D = activist with declared intent** → Positive, higher strength (a forward catalyst; strength 6 mirrors
  the InsiderBuying directional baseline). This rare, intentful filing is the ONLY bullish ownership read in v1.
- **13G = passive > 5 % accumulation → Neutral in v1 (maintainer decision 2026-07-06).** 13G is dominated by
  passive index/institutional filers (Vanguard/BlackRock/State Street cross 5 % mechanically — confirmed live:
  13G/13G-A vastly outnumber 13D for the watch universe), and v1 **cannot distinguish a conviction 13G from an
  index-fund one** (filer name / % of class are deferred — see spec 100). Reading every 13G as bullish would
  inject the same "mechanical flow ≠ business signal" noise Radar saw with index-inclusion price moves, so 13G
  is **Neutral** — it still lifts source-type diversity/provenance but contributes 0 to Trajectory, so it never
  misfires as bullish. Promoting a *conviction* 13G to Positive needs the filer-identity / % -of-class parse —
  deferred (noted in spec 100).
- **`/A` amendments → Neutral in v1.** Submissions metadata alone (all the collector has in v1 — see spec 100)
  **cannot distinguish** an amendment that *increases* a stake from one that *reduces* it or reports an
  **exit** to 0. Neutral is the conservative, deterministic read; it still lifts source-type diversity
  (Neutral contributes 0 to Trajectory, so it never misfires as bullish/bearish). Making amendments
  directional (increase → Positive, exit/reduction → Negative) requires parsing the % of class from the
  unstructured 13D/G filing body — **deferred** to a spec 100 follow-up (noted there).

Phrase-string requirements: distinctive enough to never collide with other rules (the parenthetical
`(13d)`/`(13g)`/`(routine)` tokens guarantee this); matched case-insensitively as substrings, exactly like
every existing rule.

---

## Project structure changes

```text
src/Radar.Domain/Signals/
  SignalType.cs                    # MODIFIED: add InstitutionalOwnership enum value

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs        # MODIFIED: add the InstitutionalOwnership rule group (3 fixed phrases);
                                   #   bump RuleSetVersion "radar-keyword-rules-v1" -> "radar-keyword-rules-v2";
                                   #   extend the class XML-doc rule-group inventory

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs   # MODIFIED: 3 phrase->type+direction+strength cases; existing groups green
tests/Radar.Application.Tests/Scoring/
  ScoringConfigFingerprintTests.cs # MODIFIED: repin default fingerprint + RuleSetVersion literals
  SignalSourceDescriptorTests.cs   # MODIFIED: repin "rules=radar-keyword-rules-v1;..." literals -> v2
  ScoringEngineTests.cs            # MODIFIED: repin its "rules=radar-keyword-rules-v1;..." literal -> v2
tests/Radar.Infrastructure.Tests/FileSystem/
  FileScoringConfigStoreTests.cs   # MODIFIED: repin its "rules=radar-keyword-rules-v1;..." literal -> v2
```

No formula, mapper, report, scoring-weights, DI, or Worker file changes.

---

## Implementation details

### Domain

- Add `InstitutionalOwnership` to `SignalType` (place it near `InsiderBuying`, the sibling ownership-adjacent
  type). Domain references nothing (AD layering) — a pure additive enum value.

### Extractor rule group (`KeywordSignalExtractor.Rules`)

- Add a new **ordered block** after the InsiderBuying group, following the group convention (any future
  Negative rule first, then Positive, then Neutral — v1 has no Negative phrase yet). Each rule is a
  `new(phrase, SignalType.InstitutionalOwnership, direction, strength, novelty, confidence)` per the table
  above. **No materiality metadata read in v1** — the collector does not parse % of class (deferred), so
  strength is the fixed rule strength (unlike InsiderBuying's `insiderNetValue` tiering). Do **not** add an
  `InstitutionalOwnership` branch to the metadata materiality read.
- Update the class XML-doc "rule table" inventory to list the new group and note it carries **no** materiality
  metadata read (keeping the "exactly ONE `EvidenceSourceType` branch + two metadata reads" invariant intact —
  this slice adds neither a source-type branch nor a metadata read, only phrase rules).

### `RuleSetVersion` bump (required — this is a rule-STRUCTURE change)

- Bump `public const string RuleSetVersion` from `"radar-keyword-rules-v1"` to `"radar-keyword-rules-v2"`.
  Adding a new `SignalType` rule group that produces new directional signals is a scoring-affecting
  **structural** rule change — exactly the case the const's own comment says a human must bump (parallel to a
  `_formula.Version` bump for a formula-shape change, AD-6). This automatically re-stamps `ScoringConfigVersion`
  via `SignalSourceDescriptor` (`rules={RuleSetVersion};collectors=...`), so the fingerprint stays honest even
  before the collector is enabled.

### Fingerprint repin (mechanical, multi-file)

Bumping `RuleSetVersion` changes the `srcDesc` field for **every** run, so the default fingerprint value and
every pinned `rules=radar-keyword-rules-v1;...` literal change. Update:

- `ScoringConfigFingerprintTests.cs`: the pinned default `Assert.Equal("radar-scoring-fp-7e56a8007342", fp)`
  (line ~94) and the two `rules=radar-keyword-rules-v1;collectors=...` descriptor literals (lines ~22, ~137)
  → recompute the new `radar-scoring-fp-<12hex>` from the code and repin; add a comment noting the v2
  RuleSetVersion bump supersedes the spec-96 value. **Do not guess the hex — let the recomputed value from a
  green test run drive the pin.**
- `SignalSourceDescriptorTests.cs` (lines ~87, ~94, ~102), `ScoringEngineTests.cs` (line ~673),
  `FileScoringConfigStoreTests.cs` (line ~15): replace `radar-keyword-rules-v1` → `radar-keyword-rules-v2` in
  every pinned descriptor literal. Grep the whole test tree for `radar-keyword-rules-v1` and
  `radar-scoring-fp-7e56a8007342` to catch all occurrences.

The `KeywordSignalExtractor.RuleSetVersion` const already carries `radar-keyword-rules-v1` as its only
production occurrence — the const bump is the single production edit; the rest is test repins.

---

## Tests

- `KeywordSignalExtractorTests` (MODIFIED): three new cases feed a synthetic `EvidenceItem` whose Title/RawText
  contains each phrase and assert the emitted signal is `InstitutionalOwnership` with the correct
  Direction/Strength/Novelty/Confidence:
  - `activist beneficial-ownership stake (13d)` → Positive, Strength 6.
  - `passive beneficial-ownership stake (13g)` → Neutral, Strength 3.
  - `beneficial-ownership amendment (routine)` → Neutral, Strength 3.
  - A NewsArticle-source evidence containing a 13D phrase still yields only the Neutral MediaAttention signal
    (the spec 70 source-type branch is unchanged — the ownership rules are suppressed for news like all
    directional rules).
  - Existing GovernmentContract / InsiderBuying / CapitalRaise / other cases stay green (the new group is
    additive; first-match-per-type preserved).
- Fingerprint/descriptor tests (MODIFIED): repinned as above; `SignalSourceDescriptorTests` still asserts the
  descriptor contains `KeywordSignalExtractor.RuleSetVersion` (now v2) and is culture-invariant/deterministic;
  `Compute_ChangedSignalSourceDescriptor_ChangesFingerprint` still holds.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic; NO AI.
- `Radar.Domain` references nothing (layering); the enum value is purely additive.
- **No scoring-math change:** `_formula.Version` / `ScoringVersion` / windowing / contributions / provenance
  links are byte-for-byte unchanged. Only the fingerprint *input* widens via the `RuleSetVersion` bump (default
  fingerprint value re-stamps — expected and repinned).
- No advice language (AD-9): the new type name and the factual phrases carry no recommendation.
- The three phrase strings are a **contract** — spec 100 must emit them verbatim.

## Out of scope (note explicitly)

- **The SEC 13D/13G collector** (reader, form classifier, DI, Worker wiring, seed) — that is spec **100**,
  which feeds this type.
- **Materiality by % of class** (a tiered strength read like InsiderBuying's `insiderNetValue`) — v1 uses
  fixed rule strengths because the collector does not parse the unstructured 13D/G body; deferred.
- **Directional amendments** (increase → Positive, exit/reduction → Negative) — needs the % -of-class body
  parse; v1 amendments are Neutral. Deferred (flagged in spec 100).
- **Splitting `InstitutionalOwnership` into distinct activist/passive types** — a future Domain nicety, not now.
- **13F institutional holdings** — filed BY the institution, needs a reverse-index; deferred entirely (spec 100
  documents why).

## Acceptance criteria

- [ ] `SignalType.InstitutionalOwnership` added (additive Domain enum value); mapper accepts it by name;
      formula treats it as a directional TrajectoryScore contributor (no `_formula.Version` bump); report
      renders it with no advice language.
- [ ] `KeywordSignalExtractor` maps the three fixed phrases to `InstitutionalOwnership`
      Positive(6)/Neutral(3)/Neutral(3) exactly (only 13D is Positive in v1); no materiality metadata read
      added; existing rule groups unchanged and green.
- [ ] `RuleSetVersion` bumped `radar-keyword-rules-v1` → `radar-keyword-rules-v2`; `ScoringConfigVersion`
      re-stamps automatically via `SignalSourceDescriptor`.
- [ ] All pinned fingerprint (`radar-scoring-fp-7e56a8007342`) and `radar-keyword-rules-v1` literals across the
      four test files repinned/recomputed; `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
