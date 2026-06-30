# Task: Persist signals + signal reviews to disk — `data/signals/`

## Overview

Files-first persistence (AD-8) is only half done. Raw evidence (`FileRawEvidenceStore` →
`data/evidence/raw/`) and weekly reports (`FileReportWriter` → `data/reports/weekly/`) land on disk,
but **signals and their review records live only in the singleton in-memory repositories** and vanish
when the worker process exits. The master spec's project structure lists `data/signals/`, and its Data
Persistence Roadmap names the five conceptual objects to persist files-first: *Evidence, Signals,
Scores, Reports, Reviews*. Two of those (signals, reviews) are not yet on disk.

This slice adds a signal file store that mirrors each newly-stored signal — together with the
immutable review record produced for it — to `data/signals/`, alongside the existing in-memory
`ISignalRepository`/`ISignalReviewRepository`. It is a write-mirror in the exact shape of
`FileRawEvidenceStore` (slice 29): the queryable store stays in-memory (AD-8); the on-disk copy makes
signals durable and inspectable and preserves provenance (each file carries `evidenceId`, the resolved
`companyId`, and the embedded review whose `signalId` traces back to the signal).

It deliberately does **not** add load-on-startup / cross-run querying — that is a later slice once the
files exist. No database (AD-8). No AI (still Level 1).

---

## Assignment

Worktree: any
Dependencies: None (builds on merged slices 26–45).
Conflicts with: 47 (both edit `RadarPipelineRunner`, `InfrastructureServiceCollectionExtensions`,
`RadarWorkerServices`/`RadarWorkerOptions`, and `RadarPipelineRunnerTests`). Sequence 46 then 47; do
not parallelize. Also conflicts with any future runner-editing slice.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Signals/
  ISignalFileStore.cs                # NEW (Application abstraction; NEW folder/namespace Radar.Application.Signals)

src/Radar.Infrastructure/FileSystem/
  FileSignalStore.cs                 # NEW: writes data/signals/...
  FileSignalStoreOptions.cs          # NEW: { required string RootDirectory }

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: mirror each stored signal+review
src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs  # MODIFIED: AddFileSignalStore
src/Radar.Worker/RadarWorkerOptions.cs                  # MODIFIED: SignalsDirectory
src/Radar.Worker/RadarWorkerServices.cs                 # MODIFIED: wire AddFileSignalStore

tests/Radar.Infrastructure.Tests/FileSystem/FileSignalStoreTests.cs   # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs    # MODIFIED (fake store)
```

> Namespace note: place the new interface in a fresh `Radar.Application.Signals` namespace/folder, NOT
> in `Radar.Application.SignalReview`. The latter collides with the domain type
> `Radar.Domain.Signals.SignalReview` and would force fully-qualified names (the existing L-10
> annoyance). The new namespace sidesteps it.

---

## Implementation details

### Application abstraction

```csharp
namespace Radar.Application.Signals;

using Radar.Domain.Signals;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record. Writes one JSON file per signal under
/// the signals root, capturing provenance (evidence id, resolved company id) and the embedded review.
/// A signal is upsert-by-Id (AD-1): an existing file for the same signal id is overwritten
/// (last-write-wins). Returns the written path.
/// </summary>
public interface ISignalFileStore
{
    Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct);
}
```

`SignalReview` here is `Radar.Domain.Signals.SignalReview` — usable unqualified from the
`Radar.Application.Signals` namespace.

### `FileSignalStore` (Infrastructure)

Model it on `FileRawEvidenceStore`/`FileReportWriter`:

- Ctor deps (`ArgumentNullException.ThrowIfNull`): `FileSignalStoreOptions`,
  `ILogger<FileSignalStore>`.
- `static readonly JsonSerializerOptions` with `WriteIndented = true` and
  `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (match `FileRawEvidenceStore`).
- Path: `{RootDirectory}/{ObservedAtUtc:yyyy}/{ObservedAtUtc:MM}/{signal.Id}.json`, year/month from
  `signal.ObservedAtUtc.ToUniversalTime()`, `CultureInfo.InvariantCulture`.
- **Overwrite-allowed (upsert-by-Id, AD-1 — signals are NOT insert-only; only evidence is.)** Do not
  guard on `File.Exists`. Use `File.WriteAllTextAsync` (UTF-8) or a `FileStream` with
  `FileMode.Create`. Call out in the `<summary>` that this differs from the insert-only evidence store
  and why (AD-1: immutability is evidence-only; signals upsert-by-Id last-write-wins).
- `Directory.CreateDirectory(Path.GetDirectoryName(path)!)` before writing.
- Serialize a private record matching the persisted shape (camelCase via the serializer):
  `signalId`, `evidenceId`, `companyId` (nullable), `companyMention`, `type`, `direction`, `strength`,
  `novelty`, `confidence`, `supportingExcerpt`, `reason`, `reviewStatus`, `observedAt`, `createdAt`,
  and a nested `review` object: `reviewId`, `signalId`, `reviewerName`, `decision`, `summary`,
  `issuesJson`, `reviewedAt`. Serialize enums as their string names (add `JsonStringEnumConverter` to
  the serializer options, or map enum values to strings in the record — match whatever the existing
  file stores do; `FileRawEvidenceStore` snake/kebab-cases its enum, so for signals prefer the plain
  `JsonStringEnumConverter` string name to keep it simple and lossless).
- Disk failures degrade gracefully: catch `IOException`/`UnauthorizedAccessException`, `LogWarning`,
  and return the attempted path (do not throw — the in-memory copy still exists). Mirror
  `FileReportWriter`'s return-path-on-failure behaviour.
- Honour `ct` on the async write (`WriteAllTextAsync(path, json, ct)`).

`FileSignalStoreOptions { public required string RootDirectory { get; init; } }` (e.g.
`data/signals`).

### `RadarPipelineRunner` (MODIFIED)

Inject `ISignalFileStore` (new ctor dep, `ThrowIfNull`, store in a field). In the
extract→resolve→review→store loop, immediately after the existing two writes:

```csharp
await _signalRepository.AddAsync(outcome.ReviewedSignal, ct).ConfigureAwait(false);
await _signalReviewRepository.AddAsync(outcome.Review, ct).ConfigureAwait(false);
await _signalFileStore.WriteAsync(outcome.ReviewedSignal, outcome.Review, ct).ConfigureAwait(false);
```

`outcome.Review.SignalId == outcome.ReviewedSignal.Id` (the reviewer builds the review from the
signal), so the persisted file's embedded review traces to the signal — provenance holds. The file
write must not change any counters and must not abort the run (the store already swallows disk
errors). Update the orchestration comment to note the on-disk mirror, consistent with the existing
evidence-store comment.

### DI

```csharp
public static IServiceCollection AddFileSignalStore(
    this IServiceCollection services, string rootDirectory)
{
    services.AddSingleton(new FileSignalStoreOptions { RootDirectory = rootDirectory });
    services.AddSingleton<ISignalFileStore, FileSignalStore>();
    return services;
}
```

Place it next to `AddFileRawEvidenceStore` with an XML-doc summary in the same style. The runner
requires `ISignalFileStore` (not optional); tests inject a fake.

### Worker wiring

- `RadarWorkerOptions`: add `public string SignalsDirectory { get; init; } = "data/signals";` with a
  `<summary>` matching the others.
- `RadarWorkerServices.AddRadarWorker`: add `services.AddFileSignalStore(options.SignalsDirectory);`
  next to `AddFileRawEvidenceStore`.

---

## Tests

### `FileSignalStoreTests` (temp dir; clean up in a `finally`/`IDisposable`)
- Writes a new file at `{root}/{yyyy}/{MM}/{signalId}.json` (year/month from `ObservedAtUtc`); the JSON
  deserializes back to the same signal fields and the embedded review fields (decision, summary,
  reviewer name, `signalId` equals the signal id).
- **Overwrite-allowed**: writing the same signal id twice (e.g. with a changed `reviewStatus`) leaves
  exactly one file whose contents reflect the second write (last-write-wins) — proves it is NOT
  insert-only.
- Enum fields persist as readable string names (e.g. `"CustomerWin"`, `"Approved"`,
  `"Positive"`), not integers.
- An IO failure (root pointed at an invalid/illegal path) returns the attempted path without throwing.
- Use the shared test-data builders (slice 13) for `Signal`/`SignalReview` where available.

### `RadarPipelineRunnerTests` (MODIFIED)
- Inject a fake `ISignalFileStore` recording `(signal, review)` writes; assert one write per
  stored signal, that each recorded `review.SignalId == signal.Id`, and that re-collected duplicate
  evidence (which produces no new signals) yields no extra signal writes.

---

## Constraints

- Target .NET 10. All file I/O lives in `FileSignalStore` (Infrastructure); the Application sees only
  `ISignalFileStore`.
- Signals are upsert-by-Id last-write-wins (AD-1) — the signal file store is overwrite-allowed, unlike
  the insert-only evidence store. Call this out in code comments so the architecture reviewer does not
  re-flag it.
- Preserve provenance: every signal file carries its `evidenceId`, resolved `companyId` (nullable),
  and the embedded review traceable by `signalId`.
- Disk failures degrade gracefully (warn + return path), never crash the run.
- UTC timestamps, `InvariantCulture` formatting.
- No database, no AI. Keep changes scoped to persistence of signals/reviews.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `ISignalFileStore` (Application, namespace `Radar.Application.Signals`) + `FileSignalStore`
      (Infrastructure) write each reviewed signal to `data/signals/{yyyy}/{MM}/{signalId}.json` with
      its embedded review record.
- [ ] The store is overwrite-allowed by signal id (upsert-by-Id, AD-1), not insert-only; enums persist
      as string names.
- [ ] `RadarPipelineRunner` mirrors each stored signal+review to disk after the repository writes;
      disk errors do not abort the run or change counters.
- [ ] `AddFileSignalStore(rootDirectory)` registers the store; the Worker wires it from
      `RadarWorkerOptions.SignalsDirectory` (default `data/signals`).
- [ ] Tests cover new-write, overwrite/last-write-wins, enum-as-string, IO-failure tolerance, and
      runner wiring; `build`/`test` green.
