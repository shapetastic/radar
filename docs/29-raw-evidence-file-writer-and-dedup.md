# Task: Raw evidence file writer + dedup — persist evidence to `data/evidence/raw/`

## Overview

Files-first persistence (AD-8) means raw evidence must land on disk, not only in the in-memory
repository. The master spec is specific:

```text
data/evidence/raw/{sourceType}/{yyyy}/{MM}/{contentHash}.json
```

> - Do not overwrite raw evidence.
> - If the same URL/content appears again, skip it.
> - Every downstream object references the evidence ID.

This slice adds an **insert-only** raw-evidence file store and writes each newly-stored `EvidenceItem`
to disk during the pipeline run, alongside the existing in-memory `IEvidenceRepository`. Provenance is
preserved exactly (AD-1: evidence is immutable; existing files are never overwritten).

---

## Assignment

Worktree: any
Dependencies: 26-collector-seam
Conflicts with: 28, 30, 32 (all edit `RadarPipelineRunner`). Sequence after 26; do not parallelize with
other runner-editing slices.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Evidence/
  IRawEvidenceStore.cs               # NEW (Application abstraction)

src/Radar.Infrastructure/FileSystem/
  FileRawEvidenceStore.cs            # NEW: writes data/evidence/raw/...
  FileRawEvidenceStoreOptions.cs     # NEW: { required string RootDirectory }

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: write each new EvidenceItem
src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs  # MODIFIED

tests/Radar.Infrastructure.Tests/FileSystem/FileRawEvidenceStoreTests.cs   # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs         # MODIFIED (fake store)
```

---

## Implementation details

### Application abstraction

```csharp
namespace Radar.Application.Evidence;

using Radar.Domain.Evidence;

/// <summary>
/// Insert-only raw-evidence file store. Writes immutable evidence to local JSON, never overwriting an
/// existing file (provenance, AD-1). Returns true if a new file was written, false if it already
/// existed (skip).
/// </summary>
public interface IRawEvidenceStore
{
    Task<bool> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct);
}
```

### `FileRawEvidenceStore`

Ctor deps (`ThrowIfNull`): `FileRawEvidenceStoreOptions`, `ILogger<FileRawEvidenceStore>`.
- Path: `{RootDirectory}/{sourceTypeFolder}/{PublishedOrCollected:yyyy}/{:MM}/{ContentHash}.json`.
  - `sourceTypeFolder`: a documented, stable `EvidenceSourceType → folder` map matching the master
    (`PressRelease → "press-releases"`, `LocalFile → "local-file"`, `RssFeed → "rss"`,
    `NewsArticle → "news"`, … default `kebab-case-of-enum`).
  - Year/month from `PublishedAtUtc ?? CollectedAtUtc` (UTC).
- **Insert-only**: if the target file already exists, `LogDebug` "skip" and return `false`. Otherwise
  create directories, serialize the evidence (a JSON shape matching the master "Raw Evidence Schema":
  `evidenceId`, `sourceType`, `sourceName`, `sourceUrl`, `title`, `rawText`, `publishedAt`,
  `collectedAt`, `contentHash`, `companyHints` (from `MetadataJson`), `metadata`), write it, return
  `true`. Write to a temp file + `File.Move`/atomic replace is **not** required for MVP, but never
  overwrite an existing final path.
- `System.Text.Json` with indented output (human-readable, the report is the UI but raw files are for
  debugging). Use invariant/UTC formatting. Catch `IOException`/`UnauthorizedAccessException` →
  `LogWarning` and return `false` (a disk hiccup must not crash the run; the in-memory copy still works).

`FileRawEvidenceStoreOptions { public required string RootDirectory { get; init; } }` (e.g.
`data/evidence/raw`).

### `RadarPipelineRunner`

Inject `IRawEvidenceStore` (new ctor dep, `ThrowIfNull`). In the collect→store loop, when
`AddIfNewAsync` returns true (newly stored), also `await _rawEvidenceStore.WriteIfNewAsync(item, ct)`.
The file store is the on-disk mirror of the immutable repository; a `false` from either path is just a
dedupe skip and must not abort the run. Keep counters as-is (the file write does not change
`evidenceNew`).

### DI

```csharp
public static IServiceCollection AddFileRawEvidenceStore(
    this IServiceCollection services, string rootDirectory)
{
    services.AddSingleton(new FileRawEvidenceStoreOptions { RootDirectory = rootDirectory });
    services.AddSingleton<IRawEvidenceStore, FileRawEvidenceStore>();
    return services;
}
```

The host (slice 32) registers this with a configured root. Provide a **no-op** default only if needed
so existing tests/runs that don't register a store still work — preferred approach: the runner requires
`IRawEvidenceStore`, and tests inject a fake; the Worker registers the file store. (Do not make the
dependency optional/nullable — keep the runner's deps explicit.)

---

## Tests

### `FileRawEvidenceStoreTests` (temp dir, clean up)
- Writes a new file at the expected `…/{sourceTypeFolder}/{yyyy}/{MM}/{contentHash}.json` path; content
  deserializes back to the same evidence fields incl. `companyHints`.
- **Insert-only**: calling `WriteIfNewAsync` twice for the same evidence returns `true` then `false`,
  and the file is not modified (assert unchanged bytes/timestamp).
- Year/month derived from `PublishedAtUtc` when present, else `CollectedAtUtc`.
- An IO failure (e.g. root pointed at an invalid path) returns `false` without throwing.

### `RadarPipelineRunnerTests` (MODIFIED)
- Inject a fake `IRawEvidenceStore` recording writes; assert exactly the newly-stored evidence is
  written once, and re-collected duplicates are not re-written.

---

## Constraints

- Target .NET 10. All file I/O lives in `FileRawEvidenceStore` (Infrastructure); the Application sees
  only `IRawEvidenceStore`.
- Insert-only / never overwrite raw evidence (AD-1). The on-disk file is keyed by `contentHash`, matching
  the repository's content-hash dedupe.
- Disk failures degrade gracefully (warn + skip), never crash the run.
- UTC timestamps; deterministic, documented `SourceType → folder` mapping.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `IRawEvidenceStore` (Application) + `FileRawEvidenceStore` (Infrastructure) write evidence to
      `data/evidence/raw/{sourceType}/{yyyy}/{MM}/{contentHash}.json` insert-only, never overwriting.
- [ ] The on-disk JSON matches the master "Raw Evidence Schema" (incl. `companyHints` + `metadata`).
- [ ] `RadarPipelineRunner` mirrors each newly-stored evidence to disk; duplicates and disk errors are
      skipped without aborting the run.
- [ ] `AddFileRawEvidenceStore(rootDirectory)` registers the store (central DI).
- [ ] Tests cover new-write, insert-only skip, path derivation, IO-failure tolerance, and runner wiring;
      `build`/`test` green.
