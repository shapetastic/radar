# Task: Expand the deterministic keyword rule table (recall + GovernmentContract breadth)

## Overview

The deterministic extractor's rule table is deliberately small, but it is now thin enough to miss
common, master-spec-named events on real feeds. Most notably **GovernmentContract** only matches
"government contract" / "awarded contract", yet the master spec explicitly calls out
`Government, defence, NASA, MOD, DoD, public procurement, grant` as the cues for that type. Similar
gaps exist for everyday phrasings of customer wins, product launches, and guidance changes.

This slice widens the fixed, visible rule table so the title-aware extractor (slice 34) recognises more
real events, while keeping the extractor fully deterministic and explainable. It adds **no** new signal
types, no AI, and no scoring changes — only more phrase→type rules, each with a clear reason string.

---

## Assignment

Worktree: any
Dependencies: 34 (title-aware extraction — same file, must land first)
Conflicts with: 34 (both edit `KeywordSignalExtractor.cs`). Sequence after 34; do not parallelize.
Estimated time: ~1 hour

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs   # MODIFIED: extend the rule table

tests/Radar.Application.Tests/SignalExtraction/KeywordSignalExtractorTests.cs   # MODIFIED/extended
```

No new files, interfaces, signal types, or domain changes.

---

## Implementation details

Extend the existing `Rules` array only. Keep every constraint already in force:

- Fixed, ordered, visibly-constant table; phrases matched case-insensitively as substrings.
- The first matching rule for a given `SignalType` still wins (per-type dedupe). Order new rules so the
  more specific/strongest phrasing for a type appears first.
- All `Strength`/`Novelty` in `1..10`, `Confidence` in `0..1` (so mapped signals pass
  `SignalValidation`).
- Each rule keeps a human-readable reason via the existing `$"Matched phrase '{phrase}'"` mechanism.
- Placeholder heuristics, not a tuned model — keep the existing comment to that effect.

Add (at least) the following, grouped with the existing rules for their type:

- **GovernmentContract** (master-spec cues): `"nasa"`, `"department of defense"`, `"dod "`,
  `"ministry of defence"`, `"defence contract"`, `"defense contract"`, `"public procurement"`,
  `"government grant"`, `"awarded"`. Direction `Positive`. Keep strength/novelty in line with the
  existing GovernmentContract rows (e.g. 6 / 5 / 0.6m).
  - Note the existing `"selected by"` row maps to `CustomerWin`; that stays. Because per-type dedupe is
    independent across types, a "selected by NASA …" headline can legitimately produce both a
    `CustomerWin` and a `GovernmentContract` signal — that is acceptable and should be asserted in a
    test, not suppressed.
- **CustomerWin**: `"contract win"`, `"wins contract"`, `"renews"`, `"expands agreement"`.
- **ProductLaunch**: `"rolls out"`, `"new platform"`, `"general availability"`.
- **GuidanceChange** (negative breadth): `"cuts outlook"`, `"lowers outlook"` (Direction `Negative`,
  in line with existing guidance rows).
- **CapitalRaise**: `"credit facility"`, `"debt financing"`, `"convertible note"`.

Choose short, lowercase, low-false-positive phrases. Avoid bare single words that collide across types
(e.g. do not add a lone `"contract"`). If two new phrases for the **same** type could both match, order
them by specificity; cross-type matches are independent and expected.

Keep the rule comment block accurate (it documents the table's invariants).

---

## Tests

### `KeywordSignalExtractorTests` (extended)
- A "NASA"/"awarded"/"defence contract" headline produces a `GovernmentContract` signal.
- A "selected by NASA" headline produces **both** a `CustomerWin` and a `GovernmentContract` signal
  (independent per-type dedupe), in the established stable order (by `SignalType` enum, then index).
- New CustomerWin / ProductLaunch / CapitalRaise phrases each produce the expected single typed signal.
- A negative guidance phrase ("cuts outlook" / "lowers outlook") produces a `GuidanceChange` signal
  with `Direction = Negative`.
- Regression: existing phrases still map to the same types/directions; per-type dedupe still yields one
  signal per type when multiple phrases of that type match; all outputs are valid through
  `ExtractedSignalMapper.ToSignal`.

---

## Constraints

- Target .NET 10; C# 14.
- Deterministic, explainable extraction only — no AI, no new signal types, no scoring change.
- All rule values within domain ranges; per-type dedupe and stable ordering preserved.
- Keep phrases conservative to limit false positives; no bare ambiguous single words.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] GovernmentContract recognises the master-spec cues (NASA, DoD, defence/defense contract, public
      procurement, government grant, awarded).
- [ ] CustomerWin, ProductLaunch, GuidanceChange (negative), and CapitalRaise gain the listed phrasings.
- [ ] A "selected by NASA" headline yields both CustomerWin and GovernmentContract signals.
- [ ] Per-type dedupe, stable ordering, and domain-range validity are preserved; existing tests pass.
- [ ] New tests cover each added rule group; build/test green.
