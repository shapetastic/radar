# Task: Shared Test Data Builders (EvidenceItem / Signal)

## Overview

A cross-slice architecture audit found that `EvidenceItem` and `Signal` are hand-constructed via
per-class private factory helpers (`MakeEvidence` / `MakeItem` / `MakeItemAt` / `MakeSignal` /
`CreateValidSignal`) that are **duplicated across six test files in two test projects**. Every new
slice that touches signals or evidence adds another near-identical helper, and the duplicated literal
field lists drift apart over time (different default `SourceType`, `Quality`, timestamps, etc.). This
is the single highest-churn-compounding piece of drift in the test tree.

This slice converges those helpers onto **one shared test-support project** with two fluent builders
(`EvidenceBuilder`, `SignalBuilder`) that produce valid domain records from sensible defaults and
expose `With…` overrides. It is a **test-only** change: no production behaviour changes, with the one
exception of the bundled L2 guard hardening (see below). Two cheap, low-risk tidy-ups are bundled
because they are single-file edits in files this slice's author is already reasoning about; one
finding is explicitly deferred (see Notes).

---

## Assignment

Worktree: any
Dependencies: 09-signal-extraction-contract-and-validation, 10-deterministic-keyword-signal-extractor,
11-deterministic-signal-review (these introduced the test files being migrated); also assumes the
persistence tests from 03/07 exist.
Conflicts with: None on shared production records. It **adds a new project to `Radar.sln`** and adds a
`ProjectReference` to all three test `.csproj` files, so it must not run in parallel with any other
slice that edits `Radar.sln` or those test project files. Sequence it standalone.
Estimated time: ~1-2 hours

---

## Project structure changes

Add:

```text
tests/Radar.TestSupport/
  Radar.TestSupport.csproj          # NEW: net10.0 class library, references Radar.Domain only
  EvidenceBuilder.cs                # NEW: fluent builder producing valid EvidenceItem
  SignalBuilder.cs                  # NEW: fluent builder producing valid Signal

tests/Radar.Domain.Tests/
  TestSupport/BuildersTests.cs      # NEW: smoke tests that default Build() is valid
```

Modify (production — bundled tidy-ups only):

```text
src/Radar.Application/SignalReview/
  DeterministicSignalReviewer.cs    # L1: move using directives above the file-scoped namespace
  ISignalReviewer.cs                # L1: same
  SignalReviewOutcome.cs            # L1: same

src/Radar.Infrastructure/Sources/
  LocalFileEvidenceCollector.cs     # L2: add ArgumentNullException.ThrowIfNull ctor guards
```

Modify (test wiring + migration — no behaviour change):

```text
Radar.sln                                                   # add Radar.TestSupport (under the tests folder)
tests/Radar.Application.Tests/Radar.Application.Tests.csproj # + ProjectReference to Radar.TestSupport
tests/Radar.Infrastructure.Tests/Radar.Infrastructure.Tests.csproj # + ProjectReference
tests/Radar.Domain.Tests/Radar.Domain.Tests.csproj          # + ProjectReference

tests/Radar.Application.Tests/SignalReview/DeterministicSignalReviewerTests.cs   # M1: drop MakeEvidence/MakeSignal
tests/Radar.Application.Tests/SignalExtraction/KeywordSignalExtractorTests.cs    # M1: drop MakeEvidence
tests/Radar.Application.Tests/SignalExtraction/ExtractedSignalMapperTests.cs     # M1: drop MakeEvidence (keep MakeExtracted)
tests/Radar.Domain.Tests/SignalValidationTests.cs                                # M1: drop CreateValidSignal
tests/Radar.Infrastructure.Tests/Persistence/InMemoryEvidenceRepositoryTests.cs  # M1: drop MakeItem/MakeItemAt
tests/Radar.Infrastructure.Tests/Persistence/InMemorySignalRepositoryTests.cs    # M1: drop MakeSignal
```

Do **not** touch `InMemoryCompanyRepositoryTests.cs` or `InMemoryScoreRepositoryTests.cs`: they build
`Company`/`CompanyAlias`/`CompanyScoreSnapshot`/`ScoreEvidenceLink`, which are out of scope for the
`EvidenceBuilder`/`SignalBuilder` pair. (Builders for those aggregates can follow in a later sweep if
the same duplication appears there.)

---

## Implementation details

### New project: `tests/Radar.TestSupport`

A plain class library (NOT a test project — no xunit, no test SDK). `Directory.Build.props` already
supplies `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, and `TreatWarningsAsErrors=true`, so
the csproj only needs the project reference:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Radar.Domain\Radar.Domain.csproj" />
  </ItemGroup>

</Project>
```

It references **only `Radar.Domain`** (the builders construct domain records). It must not reference
Application, Infrastructure, or xunit.

Add it to `Radar.sln` (prefer `dotnet sln Radar.sln add tests/Radar.TestSupport/Radar.TestSupport.csproj
--solution-folder tests` so it nests under the existing `tests` solution folder), then add a
`ProjectReference` to it from all three test projects.

### `EvidenceBuilder` (namespace `Radar.TestSupport`)

A `sealed` fluent builder with a private field per `EvidenceItem` constructor parameter, a `With…`
method per field returning `this`, and a `Build()` returning a valid `EvidenceItem`. Mirror the
`EvidenceItem` record shape:

```csharp
using Radar.Domain.Evidence;

namespace Radar.TestSupport;

public sealed class EvidenceBuilder
{
    private Guid _id = Guid.NewGuid();
    private EvidenceSourceType _sourceType = EvidenceSourceType.PressRelease;
    private string _sourceName = "Acme Newsroom";
    private string? _sourceUrl = "https://example.com/acme";
    private string _title = "Untitled";
    private string? _summary = "A summary.";
    private string _rawText = "Acme made an announcement today.";
    private string _contentHash = "hash-1";
    private DateTimeOffset? _publishedAtUtc;
    private DateTimeOffset _collectedAtUtc = new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);
    private EvidenceQuality _quality = EvidenceQuality.High;
    private string? _metadataJson;

    public EvidenceBuilder WithId(Guid id) { _id = id; return this; }
    public EvidenceBuilder WithSourceType(EvidenceSourceType v) { _sourceType = v; return this; }
    public EvidenceBuilder WithSourceName(string v) { _sourceName = v; return this; }
    public EvidenceBuilder WithSourceUrl(string? v) { _sourceUrl = v; return this; }
    public EvidenceBuilder WithTitle(string v) { _title = v; return this; }
    public EvidenceBuilder WithSummary(string? v) { _summary = v; return this; }
    public EvidenceBuilder WithRawText(string v) { _rawText = v; return this; }
    public EvidenceBuilder WithContentHash(string v) { _contentHash = v; return this; }
    public EvidenceBuilder WithPublishedAtUtc(DateTimeOffset? v) { _publishedAtUtc = v; return this; }
    public EvidenceBuilder WithCollectedAtUtc(DateTimeOffset v) { _collectedAtUtc = v; return this; }
    public EvidenceBuilder WithQuality(EvidenceQuality v) { _quality = v; return this; }
    public EvidenceBuilder WithMetadataJson(string? v) { _metadataJson = v; return this; }

    public EvidenceItem Build() => new(
        Id: _id,
        SourceType: _sourceType,
        SourceName: _sourceName,
        SourceUrl: _sourceUrl,
        Title: _title,
        Summary: _summary,
        RawText: _rawText,
        ContentHash: _contentHash,
        PublishedAtUtc: _publishedAtUtc,
        CollectedAtUtc: _collectedAtUtc,
        Quality: _quality,
        MetadataJson: _metadataJson);
}
```

The exact default literals above are a starting point; the **binding requirement** is in the Migration
rule below: every migrated call site must end up producing the same field values it asserts on or
otherwise depends on today.

### `SignalBuilder` (namespace `Radar.TestSupport`)

Same pattern for `Signal`. Defaults must yield a record that passes
`Radar.Domain.Validation.SignalValidation.IsValid` (non-empty `EvidenceId`, in-range
`Strength`/`Novelty`/`Confidence`, non-empty `SupportingExcerpt`/`CompanyMention`). Provide `With…` for
all 14 fields including `WithId`, `WithEvidenceId`, `WithCompanyId(Guid?)`, `WithStrength`,
`WithNovelty`, `WithConfidence`, `WithSupportingExcerpt`, `WithObservedAtUtc`, `WithCreatedAtUtc`,
`WithReviewStatus`, etc. Suggested defaults (align with the current `DeterministicSignalReviewerTests`
helper so that migration is behaviour-preserving): `Type = SignalType.CustomerWin`,
`Direction = SignalDirection.Positive`, `Strength = 6`, `Novelty = 6`, `Confidence = 0.8m`,
`SupportingExcerpt = "signed a multi-year deal"`, `ReviewStatus = SignalReviewStatus.Pending`,
non-empty `EvidenceId`/`CompanyId`.

### M1 — Migration rule (behaviour-identical)

For each of the six listed test files, **delete the private factory helper(s)** and replace each call
with a builder expression. The migration is correct only if it is **behaviour-identical**:

- Where a test **asserts on a field** (e.g. `KeywordSignalExtractorTests` asserts
  `evidence.SourceName == signal.CompanyMention`; `ExtractedSignalMapperTests` asserts on
  `evidence.Id`, `PublishedAtUtc`, `CollectedAtUtc`; the reviewer tests assert on `Quality`,
  `EvidenceId` mismatch, `Confidence`, `Strength`, `Novelty`), set that field **explicitly** on the
  builder with the **same value** the old helper produced.
- Where the old helper passed a value the test **depends on for behaviour but does not assert**
  (e.g. `DeterministicSignalReviewerTests` relies on `EvidenceId`/`CompanyId` matching, and on the
  default evidence quality / signal strength feeding the rules), preserve that value explicitly.
- Fields the test neither asserts on nor depends on may take the builder default.
- Preserve each file's existing fixed `DateTimeOffset` constants and `Guid` constants by passing them
  through `With…`; do not introduce `DateTimeOffset.UtcNow` where a fixed value was used. (Note
  `SignalValidationTests.CreateValidSignal` currently uses `DateTimeOffset.UtcNow` and
  `Guid.NewGuid()` for fields it does not assert on — those can become builder defaults; keep its
  `evidenceId`/`strength`/`novelty`/`confidence`/`supportingExcerpt` overrides explicit since those
  drive the validation assertions, including the `Guid.Empty` evidence-id case.)
- Keep `ExtractedSignalMapperTests.MakeExtracted` as-is — `ExtractedSignal` is an Application DTO, not
  a domain record, and is out of scope for this slice.

No test's assertions or `[Theory]` data change. The only edits to test bodies are swapping the
construction expression.

### L1 — `using` placement in the three `SignalReview` source files

`DeterministicSignalReviewer.cs`, `ISignalReviewer.cs`, and `SignalReviewOutcome.cs` currently place
their `using` directives **below** the file-scoped `namespace`. Every other file in the tree places
them above. Move the `using` directives above the `namespace` line in each of the three files (keep
the same usings; for `DeterministicSignalReviewer.cs` that is `System.Text.Json`,
`Microsoft.Extensions.Logging`, `Radar.Domain.Evidence`, `Radar.Domain.Signals`). No behaviour change.

### L2 — Constructor null-guards in `LocalFileEvidenceCollector`

`LocalFileEvidenceCollector`'s constructor (around lines 27-37) assigns its four injected dependencies
(`normalizer`, `options`, `logger`, `timeProvider`) without the `ArgumentNullException.ThrowIfNull`
guards that the other injected services in the tree use. Add a guard for each parameter before (or at)
assignment, e.g.:

```csharp
ArgumentNullException.ThrowIfNull(normalizer);
ArgumentNullException.ThrowIfNull(options);
ArgumentNullException.ThrowIfNull(logger);
ArgumentNullException.ThrowIfNull(timeProvider);
```

This is a minor production hardening (the only production behaviour change in the slice) that aligns
the collector with the established constructor convention.

---

## Tests

- **Builder smoke tests** — add `tests/Radar.Domain.Tests/TestSupport/BuildersTests.cs` (Domain.Tests
  references Domain and the new TestSupport):
  - `SignalBuilder().Build()` passes `SignalValidation.IsValid` and produces a non-empty `EvidenceId`.
  - `EvidenceBuilder().Build()` produces non-empty `Title`, `RawText`, `ContentHash`, and `SourceName`.
  - A `With…` override is reflected in `Build()` (e.g. `new SignalBuilder().WithStrength(3).Build()`
    has `Strength == 3`; `new EvidenceBuilder().WithQuality(EvidenceQuality.Low).Build()` has
    `Quality == Low`).
- **All migrated tests keep passing unchanged in intent.** The full existing suite
  (`DeterministicSignalReviewerTests`, `KeywordSignalExtractorTests`, `ExtractedSignalMapperTests`,
  `SignalValidationTests`, `InMemoryEvidenceRepositoryTests`, `InMemorySignalRepositoryTests`) must
  pass with identical assertions after the construction expressions are swapped to builders.

---

## Constraints

- Target .NET 10; respect the architecture decisions ledger (AD-1..AD-5) — nothing here re-litigates a
  recorded decision. In particular, AD-5 keeps `Radar.Domain` package-free: `Radar.TestSupport`
  references Domain only and adds no packages.
- Test-only change in spirit: no production behaviour changes **except** the L2 constructor guards
  (defensive hardening that throws on null injection, which never happens in the wired graph).
- Do not add builders for aggregates other than `EvidenceItem` and `Signal` in this slice.
- Do not expand or rewire the Worker (L3 is deferred — see Notes).
- Keep the solution buildable and green at every step; warnings-as-errors must stay clean.

---

## Acceptance criteria

- [ ] New `tests/Radar.TestSupport` class library exists, targets `net10.0`, references **only**
      `Radar.Domain`, and is added to `Radar.sln` under the `tests` solution folder.
- [ ] `EvidenceBuilder` and `SignalBuilder` exist in `Radar.TestSupport`, are `sealed`, expose a
      `With…` method for every record field, and `Build()` returns a valid record from defaults
      (`SignalBuilder` default passes `SignalValidation.IsValid`).
- [ ] All three test projects reference `Radar.TestSupport`.
- [ ] The duplicated per-class helpers are removed from all six listed files
      (`MakeEvidence`/`MakeItem`/`MakeItemAt`/`MakeSignal`/`CreateValidSignal`) and replaced with the
      shared builders; `ExtractedSignalMapperTests.MakeExtracted` is intentionally retained.
- [ ] Every migrated test keeps its original assertions and `[Theory]` data; fields previously asserted
      on or depended upon are set explicitly via `With…` so behaviour is identical.
- [ ] `InMemoryCompanyRepositoryTests` and `InMemoryScoreRepositoryTests` are unchanged.
- [ ] The three `SignalReview` source files place their `using` directives above the file-scoped
      namespace.
- [ ] `LocalFileEvidenceCollector`'s constructor guards all four dependencies with
      `ArgumentNullException.ThrowIfNull`.
- [ ] Builder smoke tests added and passing.
- [ ] `dotnet build Radar.sln -c Release` (warnings-as-errors) and `dotnet test Radar.sln -c Release`
      are green.

---

## Notes (deferred — do NOT do in this slice)

- **L3 — `Worker.cs` inline `DateTimeOffset.UtcNow`.** The Worker is still a heartbeat scaffold; the
  timestamp concern (injecting `TimeProvider`) should be addressed when the Worker is actually wired
  into the pipeline, not now. Intentionally excluded to keep this slice test-focused and small.
