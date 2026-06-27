# Task: Repository Abstractions and In-Memory Implementations

## Overview

Define persistence-agnostic repository interfaces in `Radar.Application` and provide simple
thread-safe in-memory implementations (in `Radar.Infrastructure`) for deterministic testing
and local pipeline runs. This lets later tasks (collector, scoring, reporting) be built and
tested end-to-end without PostgreSQL. The real Dapper/Postgres repositories come in a separate,
later task behind the same interfaces.

This sits directly on top of the domain models and unblocks every pipeline stage.

---

## Assignment

Worktree: any
Dependencies: 01-solution-skeleton, 02-domain-models
Conflicts with: None (adds files under `src/Radar.Application` and `src/Radar.Infrastructure`)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Abstractions/
    Persistence/
      ICompanyRepository.cs
      IEvidenceRepository.cs
      ISignalRepository.cs
      IScoreRepository.cs
      IReportRepository.cs

src/Radar.Infrastructure/
  Persistence/
    InMemory/
      InMemoryCompanyRepository.cs
      InMemoryEvidenceRepository.cs
      InMemorySignalRepository.cs
      InMemoryScoreRepository.cs
      InMemoryReportRepository.cs
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs

tests/Radar.Infrastructure.Tests/
  Persistence/
    InMemoryEvidenceRepositoryTests.cs
    InMemorySignalRepositoryTests.cs
```

Namespaces: `Radar.Application.Abstractions.Persistence`,
`Radar.Infrastructure.Persistence.InMemory`,
`Radar.Infrastructure.DependencyInjection`.

---

## Implementation details

### Interfaces (Application)

Keep them minimal — only what the MVP pipeline needs. All methods are async and take a
`CancellationToken`. Use `IReadOnlyList<T>` for returns.

```csharp
public interface IEvidenceRepository
{
    // Returns false if an item with the same ContentHash already exists (dedupe),
    // true if newly added. Preserves immutability: never overwrites existing evidence.
    Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct);
    Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct);
    Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct);
}

public interface ICompanyRepository
{
    Task AddAsync(Company company, CancellationToken ct);
    Task<Company?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct);
    Task AddAliasAsync(CompanyAlias alias, CancellationToken ct);
    Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct);
}

public interface ISignalRepository
{
    Task AddAsync(Signal signal, CancellationToken ct);
    Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
}

public interface IScoreRepository
{
    Task AddSnapshotAsync(CompanyScoreSnapshot snapshot, CancellationToken ct);
    Task AddEvidenceLinkAsync(ScoreEvidenceLink link, CancellationToken ct);
    Task<IReadOnlyList<CompanyScoreSnapshot>> GetSnapshotsForCompanyAsync(
        Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ScoreEvidenceLink>> GetLinksForSnapshotAsync(
        Guid snapshotId, CancellationToken ct);
}

public interface IReportRepository
{
    Task AddAsync(RadarReport report, IReadOnlyList<RadarReportItem> items, CancellationToken ct);
    Task<RadarReport?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RadarReportItem>> GetItemsAsync(Guid reportId, CancellationToken ct);
}
```

### In-memory implementations (Infrastructure)

- Back each with a thread-safe collection (`ConcurrentDictionary` keyed by Id, plus a hash
  index for evidence). All methods complete synchronously but return `Task` /
  `Task.FromResult`.
- `AddIfNewAsync` must honour the unique-content-hash rule: if the hash already exists, return
  `false` and do not mutate the existing record (evidence is immutable).
- Register each as a **singleton** in `InfrastructureServiceCollectionExtensions.AddInMemoryRadarPersistence(this IServiceCollection)`
  (singleton so the in-memory store persists across the run). Return `IServiceCollection` for
  chaining. Do **not** register Postgres here — that arrives in a later task.

### DI

`InfrastructureServiceCollectionExtensions` needs `Microsoft.Extensions.DependencyInjection.Abstractions`.
Add that package reference to `Radar.Infrastructure` if not already present.

---

## Tests

`Radar.Infrastructure.Tests` (xUnit). Remove the task-01 placeholder test from this project.

`InMemoryEvidenceRepositoryTests`:
- `AddIfNewAsync` returns `true` for a new item and the item is retrievable by Id and by hash.
- Adding a second item with the **same** `ContentHash` returns `false` and leaves the original
  unchanged (assert the stored RawText/Title is still the first item's).
- `GetByIdAsync` / `GetByContentHashAsync` return `null` when absent.

`InMemorySignalRepositoryTests`:
- `GetByCompanyAsync` returns only signals for the requested `CompanyId`.
- `GetObservedBetweenAsync` returns only signals whose `ObservedAtUtc` falls within the window
  (inclusive bounds), excluding ones outside it.

Use deterministic, hand-built domain records — no AI, no clock dependence beyond explicit
timestamps passed into the records.

---

## Constraints

- Target .NET 10.
- Interfaces live in Application; implementations live in Infrastructure. Application must not
  reference Infrastructure.
- Preserve provenance and immutability: evidence is never overwritten; signals retain their
  `EvidenceId`.
- Keep interfaces minimal — do not speculatively add methods the MVP pipeline won't call.
- No PostgreSQL/Dapper code in this task.

---

## Acceptance criteria

- [ ] Five repository interfaces exist in `Radar.Application.Abstractions.Persistence`.
- [ ] In-memory implementations exist in Infrastructure and are registered via
      `AddInMemoryRadarPersistence`.
- [ ] Duplicate-content-hash evidence is rejected without overwriting the original.
- [ ] Company/time-window/by-company signal queries behave per the tests.
- [ ] Application has no reference to Infrastructure; Infrastructure references Application +
      Domain only.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
