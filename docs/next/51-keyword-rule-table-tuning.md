# Task: Tune the keyword rule table against real headlines — close recall gaps and tighten loose cues

## Overview

The first live run surfaced concrete extraction problems in `KeywordSignalExtractor`'s fixed rule table when
run against real IR press releases:

**Recall gaps — material, in-window headlines that produced NO signal:**
- Energy Recovery — *"New Wastewater Project **Wins** Across India"* (a customer win). CustomerWin cues only
  match `"wins contract"`/`"contract win"`; bare `"wins"`/`"project win"` is missed.
- Helios — *"Q1 Results that **Exceeded Outlook** with Sales Growth of 17%"* (a guidance/results beat).
  GuidanceChange only matches `"raises/cuts/lowers outlook|guidance"`; `"exceeded outlook"`, beat-and-raise
  language, and earnings-results events have no cue.
- Mercury — *"Receives Largest Production **Order** …"* — `"order"` / `"production order"` is not a cue. (This
  item also fell outside the scoring window; window length is handled separately via config, not here.)

**Precision risks — loose cues that fire (or will fire) on unrelated text:**
- `"deployment"` → CustomerWin fired on Aehr (a burn-in *order*) and Sapiens; it is a generic technical word.
- `"series "` → CapitalRaise will match `"a series of"`, `"Series 3 sensors"`, `"webinar series"`.
- bare `"awarded"` → GovernmentContract matches `"awarded best workplace"`, `"award-winning"`.
- `"renews"` → CustomerWin matches `"renews lease"`, etc.

This slice tunes the **single fixed `Rules` table** in `KeywordSignalExtractor.cs` to catch the real events it
is missing and to tighten the cues most likely to mis-fire, using the live headlines above as test fixtures.
It remains a deterministic placeholder table (the real AI extractor is a later, human-owned slice) — the goal is
better precision/recall on real press-release text, not a model.

---

## Assignment

Worktree: any
Dependencies: existing trunk. Independent of spec 50 (that touches the RSS collector; this touches the
extractor) — either order. Note the interaction: once spec 50 raises evidence confidence, false-positive matches
carry more weight, so tightening precision here is more valuable after 50 lands.
Conflicts with: None.
Estimated time: ~1–1.5 h

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs        # MODIFIED: edit the Rules table (add recall cues, tighten loose ones)

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs   # MODIFIED: real-headline recall cases + false-positive negative cases
```

---

## Implementation details

Edit only the static `Rules` table (and, if helpful, the per-rule strength/novelty/confidence already present).
Keep the table fixed, ordered, visibly-constant; keep first-match-per-`SignalType` dedupe; keep all numbers in
domain range (Strength/Novelty 1–10, Confidence 0–1). Do not change the matching algorithm, excerpt logic, the
shared `EvidenceSearchableText` composition, or `CompanyMention = SourceName`.

**Recall — add cues (CustomerWin):** phrasings for win/order announcements, e.g. `"project win"`, `"wins"`-style
order/contract wins, `"production order"`, `"receives order"`, `"largest order"`, `"selected to"`. Be
deliberate: prefer specific bigrams (`"project win"`, `"production order"`) over a bare `"win"`/`"order"` token
that would mis-fire. Where a bare token is the only way to catch a real pattern, weigh it against the
false-positive cases below and document the choice.

**Recall — add cues (GuidanceChange / results beat, Positive):** `"exceeded outlook"`, `"above the high end"`,
`"raises full-year"`, `"record revenue"`, `"beat"`/`"beats expectations"`-style. Keep the existing Negative
guidance cues. (If a results-beat fits `GuidanceChange` Positive rather than a new type, use that — do **not**
add a new `SignalType`; stay within the existing enum.)

**Precision — tighten the loose cues:**
- `"deployment"`: qualify it (e.g. require `"customer deployment"`/`"production deployment"`) or remove it; bare
  `"deployment"` is too generic for CustomerWin.
- `"series "`: replace with funding-specific forms (`"series a"`, `"series b"`, `"series c"`, `"series seed"`,
  `"funding round"`) so it stops matching `"Series 3"` / `"a series of"`.
- bare `"awarded"`: drop it (the table already has the specific `"awarded contract"`); keep the specific
  government cues.
- `"renews"`: qualify to `"renews contract"`/`"renews agreement"`.

Use judgement balancing recall vs precision; the acceptance is "the real misses now hit and the named
false-positive strings don't", not an exhaustive taxonomy.

---

## Tests (drive these from the live-run headlines)

Add focused cases to `KeywordSignalExtractorTests`:
- **Recall (now hit):**
  - `"New Wastewater Project Wins Across India"` → a CustomerWin signal.
  - `"Reports First Quarter Results that Exceeded Outlook with Sales Growth of 17%"` → a Positive GuidanceChange
    signal.
  - `"Receives Largest Production Order for its … Servers"` → a CustomerWin signal.
- **Precision (must NOT fire):**
  - `"Introduces the Series 3 pressure sensor"` → no CapitalRaise.
  - `"Recognized with Top Benefits Award from Mployer"` → no GovernmentContract.
  - A generic `"deployment"` sentence with no customer context → no CustomerWin (if `"deployment"` is qualified
    rather than removed, assert the unqualified form does not match).
- Existing positive cases (`"selected by"`, `"appoints"`, `"unveils"`, government cues, etc.) still pass; the
  per-`SignalType` dedupe and stable ordering are unchanged.

---

## Constraints

- Target `net10.0`. Deterministic; table stays fixed/visible. No new `SignalType`, no algorithm change, no AI.
- Scope strictly to `KeywordSignalExtractor.cs` + its tests.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] The three real missed headlines (Energy Recovery win, Helios outlook beat, Mercury production order) now
      produce the correct signal types, asserted by tests using those exact strings.
- [ ] The named false-positive strings (`"Series 3"`, a benefits `"Award"`, an unqualified `"deployment"`) no
      longer produce spurious signals, asserted by tests.
- [ ] Existing extractor tests still pass; dedupe/ordering/excerpt behaviour unchanged; `build`/`test` green.
