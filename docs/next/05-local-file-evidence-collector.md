# Task: Local File Evidence Collector

## Overview

Add the first deterministic collector (Stage 1 - Source Collection). It reads evidence definitions
from a local directory of JSON files and produces immutable `EvidenceItem` records, computing
normalized text and content hash via `IEvidenceNormalizer` from task 04. This lets the whole
pipeline run end-to-end in tests with no internet and no real source feeds, satisfying the master
spec's requirement for "at least one deterministic local/test collector".

The collector only builds `EvidenceItem` records; it does not persist them. A caller (a later
worker job) will pass each item to `IEvidenceRepository.AddIfNewAsync`, which already dedupes on
`ContentHash`. Keeping collection and persistence separate preserves provenance and keeps this
slice small.

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models, 03-repository-abstractions-and-inmemory, 04-evidence-normalization-and-hashing
Conflicts with: 06-company-alias-resolver (both edit `InfrastructureServiceCollectionExtensions.cs`) — sequence after this task
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Collectors/
    IEvidenceCollector.cs

src/Radar.Infrastructure/
  Sources/
    LocalFileEvidenceCollector.cs
    LocalFileEvidenceCollectorOptions.cs
    LocalFileEvidenceDocument.cs        # internal JSON DTO
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: add AddLocalFileCollector

tests/Radar.Infrastructure.Tests/
  Sources/
    LocalFileEvidenceCollectorTests.cs
```

Namespaces: `Radar.Application.Collectors`, `Radar.Infrastructure.Sources`.

The collector touches the filesystem, so its implementation lives in Infrastructure (the master
spec's `Sources/` area); only the interface lives in Application.

---

## Implementation details

### Interface (Application)

```csharp
namespace Radar.Application.Collectors;

using Radar.Domain.Evidence;

public interface IEvidenceCollector
{
    Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct);
}
```

### JSON document format (Infrastructure, internal DTO)

Each file in the source directory is a single evidence document, e.g. `acme-q3.json`:

```json
{
  "sourceName": "Acme Newsroom",
  "sourceUrl": "https://acme.example/press/q3",
  "title": "Acme signs multi-year deal with Globex",
  "summary": "Optional short summary",
  "publishedAtUtc": "2026-06-01T13:00:00Z",
  "rawText": "Full press release body..."
}
```

Define an `internal sealed record LocalFileEvidenceDocument(...)` with nullable string/`DateTimeOffset?`
properties matching these fields. Deserialize with `System.Text.Json` using
`JsonSerializerOptions { PropertyNameCaseInsensitive = true }`. `System.Text.Json` is in the
framework — **do not add a package reference**.

### Options

```csharp
namespace Radar.Infrastructure.Sources;

public sealed class LocalFileEvidenceCollectorOptions
{
    public required string SourceDirectory { get; init; }
}
```

### Collector (Infrastructure)

`LocalFileEvidenceCollector` constructor takes `IEvidenceNormalizer`,
`LocalFileEvidenceCollectorOptions`, and `ILogger<LocalFileEvidenceCollector>`.

`CollectAsync`:

- Enumerate `*.json` files in `options.SourceDirectory`, **ordered by filename** (deterministic
  ordering). If the directory does not exist, return an empty list (log a warning) — do not throw.
- For each file: read text, deserialize to `LocalFileEvidenceDocument`. If deserialization fails
  or `title`/`rawText` is missing/blank, **skip** the file and log a warning (one bad file must
  not abort the run).
- Call `IEvidenceNormalizer.Normalize(title, rawText)` to get `NormalizedText` + `ContentHash`.
- Build an immutable `EvidenceItem`:
  - `Id = Guid.NewGuid()`
  - `SourceType = EvidenceSourceType.LocalFile`
  - `SourceName` from the document (fallback to the file name without extension if absent)
  - `SourceUrl` from the document (nullable)
  - `Title` = document title
  - `Summary` = document summary (nullable)
  - `RawText` = normalized text from the normalizer (the cleaned body)
  - `ContentHash` from the normalizer
  - `PublishedAtUtc` from the document (nullable), converted to UTC if present
  - `CollectedAtUtc` from an injected `TimeProvider` (`timeProvider.GetUtcNow()`) — inject
    `TimeProvider` too so tests are deterministic
  - `Quality = EvidenceQuality.Unknown`
  - `MetadataJson` = a small JSON object recording the originating file name (provenance)
- Return the items as `IReadOnlyList<EvidenceItem>`.

Use async file reads (`File.ReadAllTextAsync`) and honour the `CancellationToken`.

### DI

Extend `InfrastructureServiceCollectionExtensions` with:

```csharp
public static IServiceCollection AddLocalFileCollector(
    this IServiceCollection services, string sourceDirectory)
```

It should register `IEvidenceNormalizer -> EvidenceNormalizer` (singleton), the options instance,
and `IEvidenceCollector -> LocalFileEvidenceCollector`. Keep the existing
`AddInMemoryRadarPersistence` method untouched. Do not register a real source feed.

---

## Tests

`Radar.Infrastructure.Tests/Sources/LocalFileEvidenceCollectorTests.cs` (xUnit). Write JSON
fixtures to a unique temp directory per test (under the test's own temp path) and clean up.
Use a fixed `TimeProvider` (e.g. `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if already
available, otherwise a tiny inline `TimeProvider` returning a constant) and
`NullLogger<LocalFileEvidenceCollector>`.

- Two valid JSON files produce two `EvidenceItem`s with `SourceType == LocalFile`,
  populated `Title`/`RawText`/`ContentHash`, and `CollectedAtUtc` equal to the fake clock.
- Files are processed in deterministic filename order.
- A malformed JSON file and a file missing `title`/`rawText` are skipped, not fatal; the valid
  files still come through.
- A non-existent `SourceDirectory` yields an empty list (no throw).
- The produced `ContentHash` equals what `EvidenceNormalizer` computes for the same title+body
  (round-trip with the dedupe rule), and feeding the item into `InMemoryEvidenceRepository`
  twice returns `true` then `false`.

---

## Constraints

- Target .NET 10.
- Interface in Application, filesystem implementation in Infrastructure (`Sources/`).
- No package references added; `System.Text.Json` and `TimeProvider` are in-framework.
- Preserve provenance: every `EvidenceItem` keeps source name, URL, published date, and the
  originating file name in `MetadataJson`. Evidence stays immutable — the collector never writes.
- Deterministic: ordering by filename, `CollectedAtUtc` from injected `TimeProvider`, no
  `DateTime.Now`. (`Id` via `Guid.NewGuid()` is acceptable and not asserted on.)
- Do not implement RSS/HTTP collectors here.

---

## Acceptance criteria

- [ ] `IEvidenceCollector` exists in `Radar.Application.Collectors`.
- [ ] `LocalFileEvidenceCollector` reads `*.json` from a directory and emits immutable
      `EvidenceItem`s using `IEvidenceNormalizer`.
- [ ] Malformed/incomplete files are skipped; missing directory returns empty; one bad file does
      not abort collection.
- [ ] `AddLocalFileCollector` registers normalizer, options, and collector without disturbing
      `AddInMemoryRadarPersistence`.
- [ ] Tests cover happy path, ordering, skip/fault tolerance, missing directory, and the
      hash/dedupe round-trip, all with a deterministic clock.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
