# Task: Collector seam — `CollectionContext`, `CollectedEvidence`, and the evidence mapper

## Overview

The collector-driven master spec (AD-8) introduces a richer collector seam so Radar can host
*multiple* collectors (RSS, SEC, etc.) behind one interface. Today `IEvidenceCollector` is a single
method returning fully-built `EvidenceItem`s, and `LocalFileEvidenceCollector` does its own
normalization, content-hashing and quality parsing inline. That couples "find evidence" to
"normalize evidence" and gives every future collector a different way to build domain records.

This slice **refactors the seam only** (no new collector, no new behaviour):

- Evolve `IEvidenceCollector` to the master shape: `CollectorName`, `SourceType`, and
  `CollectAsync(CollectionContext, ct)` returning a **pre-persistence** DTO
  `IReadOnlyCollection<CollectedEvidence>`.
- Add `CollectionContext` (the watch universe Radar hands collectors) and `CollectedEvidence`
  (raw, un-normalized collection result).
- Add **one** Application mapper, `CollectedEvidenceMapper`, that turns a `CollectedEvidence` into a
  domain `EvidenceItem` — centralising normalization (`IEvidenceNormalizer`), content-hashing,
  quality parsing (AD-7), `SourceType` resolution, and `CollectedAt`/`Id` stamping in **one** place.
- Adapt `LocalFileEvidenceCollector` to the new seam (it becomes the deterministic **test/debug**
  collector — AD-8) and update `RadarPipelineRunner` + DI.

After this slice the pipeline behaves exactly as before; the seam is just ready for the RSS collector.

---

## Assignment

Worktree: any
Dependencies: 22-pipeline-runner, 25-end-to-end-integration-tests (existing trunk)
Conflicts with: 27, 28, 29, 30, 32 (all build on this seam / the runner's collection loop).
**Sequence 26 first; do not parallelize the collector chain.**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Collectors/
    IEvidenceCollector.cs            # MODIFIED: CollectorName/SourceType/CollectAsync(CollectionContext, ct)
    CollectionContext.cs             # NEW
    CollectedEvidence.cs             # NEW
    CollectedEvidenceMapper.cs       # NEW: CollectedEvidence -> EvidenceItem (+ company hints)
  Pipeline/
    RadarPipelineRunner.cs           # MODIFIED: build context, map collected -> evidence

src/Radar.Infrastructure/
  Sources/
    LocalFileEvidenceCollector.cs    # MODIFIED: implement new seam, return CollectedEvidence

tests/Radar.Infrastructure.Tests/Sources/LocalFileEvidenceCollectorTests.cs   # MODIFIED
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs            # MODIFIED
tests/Radar.Application.Tests/Collectors/CollectedEvidenceMapperTests.cs      # NEW
```

Namespace: `Radar.Application.Collectors`.

---

## Implementation details

### `CollectedEvidence` (pre-persistence DTO)

Matches the master spec shape. `Metadata` is a free-form provenance bag (the local collector puts its
`sourceFile` and declared `quality` here; the RSS collector will add the feed url/item id and company
hint in slice 28).

```csharp
namespace Radar.Application.Collectors;

public sealed record CollectedEvidence(
    string SourceType,                              // e.g. "local_file", "press_release"
    string SourceName,
    string? SourceUrl,
    string Title,
    string RawText,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CollectedAt,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Ticker/name hints supplied by a company-specific collector (e.g. an RSS feed bound to one
    /// company). Empty for generic sources. Carried through to resolution in slice 30; preserved on
    /// the evidence's MetadataJson for provenance.
    /// </summary>
    public IReadOnlyList<string> CompanyHints { get; init; } = [];
}
```

### `CollectionContext`

The watch universe Radar hands every collector. Minimal for now (collectors may ignore it); the RSS
collector uses `Companies` for company-hint resolution in slice 28. Keep it a record so slice 28 can
extend it (e.g. add source feeds) without breaking callers.

```csharp
namespace Radar.Application.Collectors;

using Radar.Domain.Companies;

public sealed record CollectionContext(IReadOnlyList<Company> Companies);
```

### `IEvidenceCollector` (new seam)

```csharp
namespace Radar.Application.Collectors;

public interface IEvidenceCollector
{
    /// <summary>Stable identifier of the concrete collector (provenance / logging).</summary>
    string CollectorName { get; }

    /// <summary>Canonical source-type token this collector emits (e.g. "local_file").</summary>
    string SourceType { get; }

    Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context, CancellationToken cancellationToken);
}
```

### `CollectedEvidenceMapper`

A pure, deterministic Application service (instance class, DI-registered singleton) — the single place
raw collection becomes a domain `EvidenceItem`. Constructor deps (`ThrowIfNull`): `IEvidenceNormalizer`.
(`Id` uses `Guid.NewGuid()`; `CollectedAt` comes from the `CollectedEvidence`, so no `TimeProvider`
is needed here — the collector already stamped the instant.)

```csharp
public EvidenceItem ToEvidenceItem(CollectedEvidence collected);
```

Logic:
1. `var normalized = _normalizer.Normalize(collected.Title, collected.RawText);`
2. `SourceType` string → `EvidenceSourceType` via a documented, case-insensitive table:
   `"local_file"|"localfile" → LocalFile`, `"press_release"|"pressrelease" → PressRelease`,
   `"rss"|"rss_feed" → RssFeed`, `"news"|"news_article" → NewsArticle`, else `Manual` (log Debug).
3. Quality: read `collected.Metadata["quality"]` (if present) and map with the **existing**
   `LocalFileEvidenceCollector.ParseQuality` rules (case-insensitive, defined-enum-only, digit-only
   rejected, default `Unknown`). Move that helper here so AD-7 ("quality is a declared input") holds
   for every collector. Missing key → `Unknown`.
4. `MetadataJson`: serialize the full provenance bag — at minimum the `Metadata` dictionary plus
   `companyHints` (from `collected.CompanyHints`) — as a JSON object (`System.Text.Json`). This keeps
   company hints and source metadata on the immutable evidence record (provenance, AD-1).
5. Build and return `EvidenceItem` (`Id` = new Guid, `RawText` = normalized text,
   `ContentHash` = normalized hash, `CollectedAtUtc` = `collected.CollectedAt`,
   `PublishedAtUtc` = `collected.PublishedAt?.ToUniversalTime()`).

### `LocalFileEvidenceCollector` (adapt to seam)

- Add `CollectorName => "LocalFileEvidenceCollector"`, `SourceType => "local_file"`.
- `CollectAsync(CollectionContext context, ct)` keeps the directory enumeration/JSON parsing, but each
  document now yields a **`CollectedEvidence`**, NOT an `EvidenceItem`:
  - `RawText = doc.RawText` (raw — the mapper normalizes now; **remove** the inline `_normalizer`
    call and the `ParseQuality` method from this class).
  - `Metadata = { ["sourceFile"] = fileName, ["quality"] = doc.Quality ?? "" }` (omit empty quality).
  - `CollectedAt = _timeProvider.GetUtcNow()` (unchanged stamping behaviour, AD-7).
  - `SourceType = "local_file"`, `SourceName` = `doc.SourceName` or the file name as today,
    `CompanyHints = []`.
- The collector no longer depends on `IEvidenceNormalizer` (drop that ctor dep). It keeps
  `LocalFileEvidenceCollectorOptions`, `ILogger`, `TimeProvider`. `context` is ignored (document why).

### `RadarPipelineRunner` (collection loop)

- Build the context and call the new signature:
  ```csharp
  var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
  var context = new CollectionContext(companies);
  var collected = await _collector.CollectAsync(context, ct).ConfigureAwait(false);
  ```
  (The runner already loads companies for scoring later; loading them once up front is fine. Keep the
  existing post-collection `asOfUtc` capture per AD-7 #2 — capture it **after** `CollectAsync` returns.)
- Inject `CollectedEvidenceMapper` (new ctor dep, `ThrowIfNull`). For each `CollectedEvidence`, map to
  `EvidenceItem`, `AddIfNewAsync`, and — so slice 30 has hints without re-parsing JSON — keep the new
  evidence as a small private pair `(EvidenceItem Evidence, IReadOnlyList<string> CompanyHints)` in the
  `newEvidence` list. The extraction loop reads `.Evidence`; `.CompanyHints` is unused this slice
  (slice 30 consumes it). Iterate in the collector's returned order (AD-3 spirit).
- All counters and the rest of the pipeline are unchanged.

### DI

In `AddRadarApplicationServices` (or `AddLocalFileCollector`), register
`services.AddSingleton<CollectedEvidenceMapper>()` so the runner can resolve it. `AddLocalFileCollector`
keeps registering `IEvidenceNormalizer` (the mapper now needs it) and the local collector; drop nothing
that other services rely on.

---

## Tests

### `CollectedEvidenceMapperTests` (Radar.Application.Tests, NEW)
- Maps title/rawText to normalized `RawText` + `ContentHash` (same hash as `EvidenceNormalizer` over
  the same input — provenance preserved).
- `SourceType` table: `"local_file" → LocalFile`, `"press_release" → PressRelease`, unknown → `Manual`.
- Quality: `Metadata["quality"]="High" → EvidenceQuality.High`; missing/blank/digit-only → `Unknown`
  (AD-7 parity with the old `LocalFileEvidenceCollector` behaviour).
- `CompanyHints` and `Metadata` are serialized into `MetadataJson` (assert round-trip of `companyHints`).
- `CollectedAtUtc`/`PublishedAtUtc` carried through (published converted to UTC).

### `LocalFileEvidenceCollectorTests` (MODIFIED)
- Update to the new seam: call `CollectAsync(new CollectionContext([]), ct)` and assert returned
  `CollectedEvidence` (raw text, `SourceType="local_file"`, `Metadata["sourceFile"]`,
  `Metadata["quality"]` when present, `CollectedAt` from the test `TimeProvider`). Move any
  normalization/hash/quality assertions to the mapper tests.

### `RadarPipelineRunnerTests` (MODIFIED)
- Update the test collector/fakes to the new interface; assert end-to-end counts are unchanged
  (collect → map → AddIfNew → extract → … → report) and that a re-collected duplicate (same content
  hash) is still skipped.

---

## Constraints

- Target .NET 10. No provider SDKs introduced. `CollectedEvidence`/`CollectionContext`/mapper live in
  `Radar.Application.Collectors`; file I/O stays in `LocalFileEvidenceCollector` (Infrastructure).
- **Refactor only** — no behaviour change to the running pipeline; all existing tests stay green.
- Preserve provenance (AD-1): the mapper records company hints + source metadata on `MetadataJson`;
  evidence stays immutable/dedup-by-hash.
- Preserve AD-7: quality remains a *declared input* (now parsed in the mapper) and the run-instant is
  still captured after collection in the runner.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.

---

## Acceptance criteria

- [ ] `IEvidenceCollector` exposes `CollectorName`, `SourceType`, and
      `CollectAsync(CollectionContext, ct) → IReadOnlyCollection<CollectedEvidence>`.
- [ ] `CollectedEvidence` (with `CompanyHints`) and `CollectionContext` exist in
      `Radar.Application.Collectors`.
- [ ] `CollectedEvidenceMapper` centralises normalization, hashing, quality parsing (AD-7),
      `SourceType` resolution, and hint/metadata serialization; it is the only place a collection
      result becomes an `EvidenceItem`.
- [ ] `LocalFileEvidenceCollector` implements the new seam, returns `CollectedEvidence`, and no longer
      normalizes/hashes/parses quality itself.
- [ ] `RadarPipelineRunner` builds a `CollectionContext`, maps collected → evidence, dedup-stores, and
      carries company hints alongside each new evidence; pipeline output is unchanged.
- [ ] Mapper/collector/runner tests updated and green; `build` + `test` green.
