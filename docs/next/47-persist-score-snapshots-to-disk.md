# Task: Persist score snapshots + score-evidence links to disk — `data/scores/`

## Overview

This completes the files-first persistence picture (AD-8). After slice 46, evidence, reports, signals,
and reviews are on disk; the only remaining conceptual object from the master spec's Data Persistence
Roadmap (*Evidence, Signals, Scores, Reports, Reviews*) that is **not** persisted is the score. The
master spec's project structure lists `data/scores/`, but `CompanyScoreSnapshot`s and their
`ScoreEvidenceLink`s currently live only in the singleton in-memory `IScoreRepository` and are lost
when the process exits.

This slice adds a score file store that mirrors each `CompanyScoreSnapshot` together with its
`ScoreEvidenceLink`s to `data/scores/`, in the same write-mirror shape as `FileRawEvidenceStore`/
`FileReportWriter`/`FileSignalStore`. The queryable store stays in-memory (AD-8); the on-disk copy
makes scores durable, inspectable, and fully traceable: each file records the snapshot's component
breakdown and explanation plus the links that trace the score back to contributing `signalId`s and
`evidenceId`s. This preserves the sacred provenance chain end-to-end on disk
(evidence → signal → score → report).

It deliberately does **not** add load-on-startup / cross-run score querying or run-history analytics —
those are a later slice now that the score files exist. No database (AD-8). No AI (still Level 1).

---

## Assignment

Worktree: any
Dependencies: 46 (sequence after — both edit `RadarPipelineRunner`,
`InfrastructureServiceCollectionExtensions`, `RadarWorkerServices`/`RadarWorkerOptions`, and
`RadarPipelineRunnerTests`). Reset to `origin/main` after 46 merges before starting.
Conflicts with: 46 and any future runner-editing slice. Do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  IScoreSnapshotFileStore.cs         # NEW (Application abstraction)

src/Radar.Infrastructure/FileSystem/
  FileScoreSnapshotStore.cs          # NEW: writes data/scores/...
  FileScoreSnapshotStoreOptions.cs   # NEW: { required string RootDirectory }

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: capture result + mirror to disk
src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs  # MODIFIED: AddFileScoreStore
src/Radar.Worker/RadarWorkerOptions.cs                  # MODIFIED: ScoresDirectory
src/Radar.Worker/RadarWorkerServices.cs                 # MODIFIED: wire AddFileScoreStore

tests/Radar.Infrastructure.Tests/FileSystem/FileScoreSnapshotStoreTests.cs   # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs           # MODIFIED (fake store)
```

---

## Implementation details

### Application abstraction

```csharp
namespace Radar.Application.Scoring;

using Radar.Domain.Scoring;

/// <summary>
/// On-disk mirror of a company score snapshot and the evidence links that trace it back to the
/// contributing signals/evidence. Writes one JSON file per snapshot, grouped by company. A snapshot is
/// upsert-by-Id (AD-1): an existing file for the same snapshot id is overwritten (last-write-wins).
/// Returns the written path.
/// </summary>
public interface IScoreSnapshotFileStore
{
    Task<string> WriteAsync(
        CompanyScoreSnapshot snapshot,
        IReadOnlyList<ScoreEvidenceLink> links,
        CancellationToken ct);
}
```

Note: the existing `CompanyScoreResult` record (returned by `IScoringEngine.ScoreCompanyAsync`) already
pairs a `Snapshot` with its `Links`, so the runner has both to hand.

### `FileScoreSnapshotStore` (Infrastructure)

Model it on `FileRawEvidenceStore`/`FileReportWriter`/`FileSignalStore`:

- Ctor deps (`ArgumentNullException.ThrowIfNull`): `FileScoreSnapshotStoreOptions`,
  `ILogger<FileScoreSnapshotStore>`.
- `static readonly JsonSerializerOptions` with `WriteIndented = true`,
  `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, and a `JsonStringEnumConverter` (no enums on the
  score records today, but keep it consistent/lossless).
- Path: `{RootDirectory}/{snapshot.CompanyId}/{snapshot.Id}.json`. Grouping by company id mirrors the
  schema-spec index `company_score_snapshots(company_id, created_at_utc)` and makes browsing a single
  company's history trivial once multiple runs accumulate.
- **Overwrite-allowed (upsert-by-Id, AD-1).** Do not guard on `File.Exists`; use
  `File.WriteAllTextAsync(path, json, ct)` (UTF-8) or `FileMode.Create`. Document in the `<summary>`
  that this differs from the insert-only evidence store and why (AD-1: immutability is evidence-only).
- `Directory.CreateDirectory(Path.GetDirectoryName(path)!)` before writing.
- Serialize a private record matching the persisted shape (camelCase): `snapshotId`, `companyId`,
  `scoringVersion`, `trajectoryScore`, `opportunityScore`, `attentionScore`,
  `evidenceConfidenceScore`, `signalVelocityScore`, `explanation`, `componentJson` (persist as-is — it
  is already a JSON string; acceptable to emit it as a string field for MVP, matching the snapshot
  record), `windowStartUtc`, `windowEndUtc`, `createdAtUtc`, and a `links` array of
  `{ linkId, scoreSnapshotId, signalId, evidenceId, contributionReason, contributionWeight }`.
- Disk failures degrade gracefully: catch `IOException`/`UnauthorizedAccessException`, `LogWarning`,
  return the attempted path (do not throw — the in-memory copy still exists), mirroring
  `FileReportWriter`.
- Honour `ct` on the async write.

`FileScoreSnapshotStoreOptions { public required string RootDirectory { get; init; } }` (e.g.
`data/scores`).

### `RadarPipelineRunner` (MODIFIED)

Inject `IScoreSnapshotFileStore` (new ctor dep, `ThrowIfNull`, field). In the Stage 6 scoring loop the
runner currently discards the result of `ScoreCompanyAsync`. Capture it and mirror to disk:

```csharp
var result = await _scoringEngine.ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
await _scoreFileStore.WriteAsync(result.Snapshot, result.Links, ct).ConfigureAwait(false);
companiesScored++;
```

Provenance holds: `result.Links` already reference `snapshot.Id`, `signalId`, and `evidenceId` (the
engine builds them). The file write must not change any counters (every scored company still increments
`companiesScored`, including neutral zero-signal snapshots) and must not abort the run (the store
swallows disk errors). Update the Stage 6 orchestration comment to note the on-disk mirror, consistent
with the evidence-store comment.

### DI

```csharp
public static IServiceCollection AddFileScoreStore(
    this IServiceCollection services, string rootDirectory)
{
    services.AddSingleton(new FileScoreSnapshotStoreOptions { RootDirectory = rootDirectory });
    services.AddSingleton<IScoreSnapshotFileStore, FileScoreSnapshotStore>();
    return services;
}
```

Place it next to `AddFileRawEvidenceStore`/`AddFileSignalStore` with a matching XML-doc summary. The
runner requires `IScoreSnapshotFileStore` (not optional); tests inject a fake.

### Worker wiring

- `RadarWorkerOptions`: add `public string ScoresDirectory { get; init; } = "data/scores";` with a
  `<summary>` matching the others.
- `RadarWorkerServices.AddRadarWorker`: add `services.AddFileScoreStore(options.ScoresDirectory);`
  next to `AddFileRawEvidenceStore`/`AddFileSignalStore`.

---

## Tests

### `FileScoreSnapshotStoreTests` (temp dir; clean up)
- Writes a new file at `{root}/{companyId}/{snapshotId}.json`; the JSON deserializes back to the same
  snapshot fields (all five component scores, explanation, window bounds, scoring version) and the same
  link array (each link's `signalId`/`evidenceId`/`contributionWeight`).
- A snapshot with **zero** contributions (neutral company) writes a valid file with an empty `links`
  array.
- **Overwrite-allowed**: writing the same snapshot id twice (changed scores) leaves one file whose
  contents reflect the second write — proves it is NOT insert-only.
- An IO failure (invalid root path) returns the attempted path without throwing.
- Use shared test-data builders (slice 13) for `CompanyScoreSnapshot`/`ScoreEvidenceLink` where
  available.

### `RadarPipelineRunnerTests` (MODIFIED)
- Inject a fake `IScoreSnapshotFileStore` recording `(snapshot, links)` writes; assert one write per
  scored company (matching `CompaniesScored`), and that each recorded `link.ScoreSnapshotId` equals the
  written `snapshot.Id` (provenance preserved through the runner).

---

## Constraints

- Target .NET 10. All file I/O lives in `FileScoreSnapshotStore` (Infrastructure); the Application sees
  only `IScoreSnapshotFileStore`.
- Snapshots/links are upsert-by-Id last-write-wins (AD-1) — the score file store is overwrite-allowed,
  unlike the insert-only evidence store. Call this out in code comments.
- Preserve provenance: every score file carries its `companyId`, component breakdown, explanation, and
  the links tracing back to contributing `signalId`/`evidenceId`. A score without traceable evidence
  links is invalid.
- Disk failures degrade gracefully (warn + return path), never crash the run.
- UTC timestamps, `InvariantCulture` formatting.
- No database, no AI. Keep changes scoped to persistence of score snapshots/links.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `IScoreSnapshotFileStore` (Application) + `FileScoreSnapshotStore` (Infrastructure) write each
      snapshot to `data/scores/{companyId}/{snapshotId}.json` with its `ScoreEvidenceLink` array.
- [ ] The store is overwrite-allowed by snapshot id (upsert-by-Id, AD-1), not insert-only; zero-link
      neutral snapshots persist a valid empty `links` array.
- [ ] `RadarPipelineRunner` captures the `CompanyScoreResult` and mirrors snapshot+links to disk per
      scored company; disk errors do not abort the run or change `companiesScored`.
- [ ] `AddFileScoreStore(rootDirectory)` registers the store; the Worker wires it from
      `RadarWorkerOptions.ScoresDirectory` (default `data/scores`).
- [ ] Tests cover new-write, zero-link snapshot, overwrite/last-write-wins, IO-failure tolerance, and
      runner wiring; `build`/`test` green.
