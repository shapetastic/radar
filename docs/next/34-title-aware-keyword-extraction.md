# Task: Title-aware keyword signal extraction (provenance-preserving)

## Overview

The keyword extractor currently misses the single most signal-dense field in a real press release:
the **headline**. `KeywordSignalExtractor` scans only `evidence.RawText` (the body), and the RSS
press-release collector sets `RawText = item.Summary ?? item.Title`. So whenever an RSS item carries a
summary, the title — where events like "Announces Multi-Launch Agreement", "Awarded Contract",
"Launches …" actually appear — is never searched, and those signals are silently lost.

This slice makes extraction **title-aware**: the extractor scans the evidence title together with the
body, and the signal mapper's provenance check accepts an excerpt drawn from either. Both the title and
the body are first-class fields of the immutable `EvidenceItem`, so excerpting from the title preserves
provenance exactly — the excerpt remains a verbatim, reproducible slice of stored evidence.

This is the highest-value next step for the RSS-first MVP: it directly increases how many real signals
the deterministic loop produces from fetched feeds, with no change to scoring, resolution, or storage.

---

## Assignment

Worktree: any
Dependencies: None (builds on merged slices 26–33)
Conflicts with: 35 (also edits `KeywordSignalExtractor.cs`). Sequence 34 before 35; do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs   # MODIFIED: scan title + body
src/Radar.Application/SignalExtraction/ExtractedSignalMapper.cs    # MODIFIED: provenance over title + body

tests/Radar.Application.Tests/SignalExtraction/KeywordSignalExtractorTests.cs    # MODIFIED/extended
tests/Radar.Application.Tests/SignalExtraction/ExtractedSignalMapperTests.cs     # MODIFIED/extended
```

No new files, no new interfaces, no domain changes. The `EvidenceItem` already carries both `Title`
and `RawText`.

---

## Implementation details

### Single "searchable text" composition

Introduce one shared, documented notion of an evidence's **searchable text** so the extractor and the
mapper agree byte-for-byte on what counts as "in the evidence":

```text
searchableText = Title + "\n" + RawText
```

- Title first (events lead the headline), then a single `\n`, then the body.
- Treat a null/empty `Title` or `RawText` as the empty string; never throw.
- This is an internal composition detail used in two places — keep it as a small private helper in
  each class (or one tiny shared internal static helper in `Radar.Application.SignalExtraction`). Do
  **not** add it to the domain `EvidenceItem` or change persistence.

### `KeywordSignalExtractor`

- Build `body`/`searchableText` from `Title + "\n" + RawText` instead of `RawText` alone.
- Run the existing fixed, ordered rule table against the lowercased searchable text (same
  case-insensitive substring matching, same "first matching rule per `SignalType` wins" dedupe, same
  stable ordering by `SignalType` then match index).
- `BuildExcerpt` takes its verbatim window from the **same** composed `searchableText` (original
  casing), so an excerpt may now legitimately include headline text. The excerpt stays a contiguous
  slice — do not stitch title and body fragments together beyond the single `\n` join already present
  in the composed string.
- Keep `CompanyMention = evidence.SourceName` (unchanged — extraction still performs no resolution).
- Keep all strengths/novelty/confidence within domain ranges (unchanged rule table values).

### `ExtractedSignalMapper`

- `ExcerptIsInEvidence` must validate the supporting excerpt against the **composed searchable text**
  (`Title + "\n" + RawText`), not `RawText` alone, using the same existing whitespace-normalizing,
  case-insensitive containment check. This keeps the round-trip green for title-sourced excerpts while
  still rejecting an excerpt that appears in neither field.
- No other mapper behaviour changes (enum parsing, empty-mention rule, `SignalValidation`, timestamps
  all stay as-is).

### Determinism

Output for a given evidence must remain fully deterministic and reproducible: same composition, same
rule order, same excerpt window. No clock, no randomness.

---

## Tests

### `KeywordSignalExtractorTests` (extended)
- Evidence with the event **only in the title** (e.g. `Title = "Acme awarded contract by NASA"`,
  `RawText = "Boilerplate about the company."`) now yields the expected signal(s); previously zero.
- Evidence with the event **only in the body** still yields the same signals as before (regression).
- Evidence with the **same phrase in both** title and body yields exactly one signal for that type
  (per-type dedupe preserved); assert the chosen excerpt is a verbatim slice of `Title + "\n" + RawText`.
- The produced `SupportingExcerpt` round-trips: feed each extractor output through
  `ExtractedSignalMapper.ToSignal` and assert `IsValid` (no "excerpt not found" error).

### `ExtractedSignalMapperTests` (extended)
- An excerpt found **only in the title** validates (no provenance error) and maps to a valid `Signal`.
- An excerpt found **only in the body** still validates (regression).
- An excerpt found in **neither** title nor body still fails with "Supporting excerpt not found in
  evidence." (negative case preserved).

---

## Constraints

- Target .NET 10; C# 14.
- Preserve provenance: the excerpt must remain a verbatim, reproducible slice of stored evidence
  fields (title and/or body). No fabricated text.
- Deterministic output; no clock, no randomness, invariant casing rules unchanged.
- Keep changes scoped to extraction + the mapper's provenance check. Do not touch the RSS collector,
  scoring, resolution, or storage. Do not add AI.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `KeywordSignalExtractor` scans `Title + "\n" + RawText` and can emit signals whose match lies in
      the headline.
- [ ] Excerpts are verbatim slices of the composed searchable text and survive the mapper's provenance
      check.
- [ ] `ExtractedSignalMapper` validates supporting excerpts against title **and** body; an excerpt in
      neither still fails.
- [ ] Per-`SignalType` dedupe, stable ordering, and all domain ranges are preserved (body-only
      regression tests still pass).
- [ ] New tests cover title-only, body-only, both-fields, and excerpt-in-neither cases; build/test green.
