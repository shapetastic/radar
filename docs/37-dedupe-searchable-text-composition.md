# Task: De-duplicate `ComposeSearchableText` into one shared helper

## Overview

Slice 34 ("title-aware keyword extraction") made signal extraction search the evidence
**title joined to body** rather than the body alone. To do that it introduced a private
helper that composes the searchable text:

```
(title ?? "") + "\n" + (rawText ?? "")
```

That helper now exists **byte-for-byte twice**, in two classes in the same namespace/assembly:

- `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` (`ComposeSearchableText`, ~line 162)
  — the extractor *builds* excerpts as verbatim slices of this composition.
- `src/Radar.Application/SignalExtraction/ExtractedSignalMapper.cs` (`ComposeSearchableText`, ~line 105)
  — the mapper *validates* each excerpt against the same composition (`ExcerptIsInEvidence`,
  via the `searchableText` it composes at line 34).

The two copies are bound by a correctness invariant the compiler cannot enforce. Each copy
even carries a comment that it "must agree byte-for-byte" with the other. If a future slice
changes the composition in one place and misses the other (a different separator, prepending
`SourceName`, trimming, etc.), title-drawn excerpts silently fail the mapper's
`ExcerptIsInEvidence` check and otherwise-valid signals get **dropped** — a
provenance-affecting regression that no test on a single class would catch.

This task removes the duplication so the composition rule lives in exactly one place. It is
**pure de-duplication**: the composed string must remain byte-for-byte identical to today, so
there is no behaviour change — extraction output, excerpt slices, validation results,
determinism, ordering, and dedupe all stay exactly as they are.

---

## Assignment

Worktree: any
Dependencies: None (slices 34–36 are merged and promoted to `docs/`)
Conflicts with: None pending (`docs/next/` is otherwise empty)
Estimated time: ~1 hour or less

---

## Project structure changes

Add:

- `src/Radar.Application/SignalExtraction/EvidenceSearchableText.cs` — a tiny `internal static`
  helper class holding the single canonical `ComposeSearchableText` method.

Modify:

- `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — delete its private
  `ComposeSearchableText`; call the shared helper instead.
- `src/Radar.Application/SignalExtraction/ExtractedSignalMapper.cs` — delete its private
  `ComposeSearchableText`; call the shared helper instead.

Add tests (see Tests section):

- `tests/Radar.Application.Tests/SignalExtraction/EvidenceSearchableTextTests.cs`

Do **not** touch the rule table, scoring, collectors, the report renderer/builder, domain
types, DI registration, or the solution file. The **only** permitted project-file change is
adding a single `InternalsVisibleTo("Radar.Application.Tests")` to `Radar.Application` to
enable the focused unit test (see Tests) — no other project edits.

---

## Implementation details

### New shared helper

Create `EvidenceSearchableText` in the existing `Radar.Application.SignalExtraction`
namespace. Both consumers already live in that namespace and assembly, so the helper can be
`internal` — no public surface change.

```csharp
namespace Radar.Application.SignalExtraction;

/// <summary>
/// Single source of truth for composing the evidence "searchable text" that signal extraction
/// scans and the mapper validates excerpts against. Title first (events lead the headline),
/// then a single '\n', then the body; null/empty fields are treated as the empty string.
/// </summary>
/// <remarks>
/// The extractor builds verbatim excerpt slices from this composition and the mapper checks
/// each excerpt against it (provenance). Both MUST use this one method so the round-trip
/// invariant cannot silently drift. Changing the composition here changes it for both
/// consumers at once.
/// </remarks>
internal static class EvidenceSearchableText
{
    public static string Compose(string? title, string? rawText) =>
        (title ?? string.Empty) + "\n" + (rawText ?? string.Empty);
}
```

The method body must be the exact composition used today: `(title ?? string.Empty) + "\n" +
(rawText ?? string.Empty)`. Do not add trimming, normalization, or a different separator.

### `KeywordSignalExtractor`

- Delete the private `ComposeSearchableText` method (and its "must agree byte-for-byte" comment).
- At the existing call site (currently `var searchableText = ComposeSearchableText(evidence.Title,
  evidence.RawText);`) call `EvidenceSearchableText.Compose(evidence.Title, evidence.RawText)`.
- Keep the surrounding provenance comment, updated to note the composition now lives in the
  shared helper that the mapper also uses.
- `BuildExcerpt` and all other logic are unchanged.

### `ExtractedSignalMapper`

- Delete the private `ComposeSearchableText` method (and its "must agree byte-for-byte" comment).
- At the existing call site (currently `var searchableText = ComposeSearchableText(evidence.Title,
  evidence.RawText);`) call `EvidenceSearchableText.Compose(evidence.Title, evidence.RawText)`.
- `ExcerptIsInEvidence`, `Normalize`, enum parsing, and all other logic are unchanged.

---

## Tests

### New: `EvidenceSearchableTextTests`

Lock the canonical composition so the rule is asserted in exactly one place:

- `Compose_TitleAndBody_JoinsWithSingleNewline` — `Compose("Headline", "Body")` equals
  `"Headline\nBody"`.
- `Compose_NullTitle_TreatedAsEmpty` — `Compose(null, "Body")` equals `"\nBody"`.
- `Compose_NullRawText_TreatedAsEmpty` — `Compose("Headline", null)` equals `"Headline\n"`.
- `Compose_BothNull_YieldsSingleNewline` — `Compose(null, null)` equals `"\n"`.

**Visibility note (confirmed):** `Radar.Application` does **not** currently expose internals to
`Radar.Application.Tests` (no `InternalsVisibleTo`), so an `internal` helper is not directly
referenceable from the test project as-is. Resolve this with the smallest converging choice —
add a single `InternalsVisibleTo("Radar.Application.Tests")` for `Radar.Application` (an
`AssemblyAttribute`/`InternalsVisibleTo` item in the existing `Radar.Application.csproj`, or a
small `AssemblyInfo.cs`). This is the **only** permitted project-adjacent change in this task
and exists solely to enable the focused unit test; it keeps the helper `internal` (no public
surface). Do not make the helper `public` to dodge this, and do not change any other project
settings.

If, and only if, the team objects to adding `InternalsVisibleTo`, fall back to asserting the
composition indirectly via the existing extractor/mapper round-trip tests below and drop the
direct `EvidenceSearchableTextTests` file. The direct unit test with `InternalsVisibleTo` is
preferred because it pins the rule in one focused place.

### Existing tests — must stay green unchanged

The extractor and mapper round-trip suites already pin the behaviour this task must preserve;
do not weaken them:

- `KeywordSignalExtractorTests.TitleSourcedExcerpts_RoundTripToValidSignals`
- `KeywordSignalExtractorTests.SamePhraseInTitleAndBody_YieldsSingleSignal_WithExcerptFromComposedText`
- `KeywordSignalExtractorTests.EventOnlyInTitle_YieldsExpectedSignal` /
  `EventOnlyInBody_StillYieldsSignal_Regression`
- `KeywordSignalExtractorTests.EachEmittedSignal_RoundTripsToValidSignal`
- `KeywordSignalExtractorTests.Determinism_TwoCalls_YieldEqualSequences`
- the full `ExtractedSignalMapperTests` suite.

These collectively prove the extractor→mapper excerpt round-trip still passes after the
de-duplication. Optionally, the inline `(evidence.Title ?? string.Empty) + "\n" + …`
expressions in `KeywordSignalExtractorTests` may be switched to call the shared helper for
consistency, but this is not required and must not change any assertion's meaning.

---

## Constraints

- Target .NET 10 / C# 14.
- Pure de-duplication: the composed searchable text must be **byte-for-byte identical** to
  today. No behaviour change to extraction output, excerpt slices, validation, determinism,
  ordering, or dedupe.
- Preserve provenance: excerpts remain verbatim slices of the composition; the extractor→mapper
  round-trip stays green.
- Scope strictly to `KeywordSignalExtractor.cs`, `ExtractedSignalMapper.cs`, the new
  `EvidenceSearchableText` helper, and their tests. Do not touch the rule table, scoring,
  collectors, the report, domain types, DI, or the solution file. The single permitted
  project change is the `InternalsVisibleTo` addition described in Tests.
- Honour the layering rules (this is all within `Radar.Application`); no new package references.

---

## Acceptance criteria

- [ ] A single canonical composition method exists (`EvidenceSearchableText.Compose`), in the
      `Radar.Application.SignalExtraction` namespace, `internal static`.
- [ ] The private `ComposeSearchableText` is deleted from both `KeywordSignalExtractor` and
      `ExtractedSignalMapper`; both call the shared helper.
- [ ] No "must agree byte-for-byte with the identical helper" comments remain (the invariant
      now lives in one place).
- [ ] The composed string is byte-for-byte identical to before: `(title ?? "") + "\n" +
      (rawText ?? "")`.
- [ ] New `EvidenceSearchableTextTests` lock the composition (title+body, null title, null
      rawText, both null).
- [ ] All existing extractor/mapper tests pass unchanged, including the title-sourced excerpt
      round-trip and determinism tests.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build`
      are green.
