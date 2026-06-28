# Task: Company Watch-Universe Seed — load a seed company table so resolution is meaningful

## Overview

Entity resolution (Stage 3) is conservative: it only maps a mention to a company that already exists in
`ICompanyRepository`. Today **nothing populates that universe**, so every mention resolves to
*unresolved*, no signal ever gets a `CompanyId`, scoring has nothing to surface, and the report is
always empty. The full-pipeline spec calls for exactly this: *"start with a seed company table / watch
universe."*

This slice adds a deterministic, offline seed mechanism mirroring the existing local-file evidence
collector:

- an Application **interface** `ICompanySeedSource` returning the seed companies + aliases (a data
  source, provider-independent),
- an Application **idempotent seeder** `CompanyUniverseSeeder` that upserts the seed into
  `ICompanyRepository`,
- an Infrastructure **implementation** `LocalFileCompanySeedSource` that reads the seed from a local
  JSON file, and
- a DI helper `AddLocalFileCompanySeed(filePath)`.

The seeder is invoked by the Worker (spec 24); this slice only builds and tests the seeding capability.
It does **not** change the resolver, the runner, or the Worker.

> **Conservative by construction.** Seeding only *adds known companies* to the universe; it never
> invents tickers and never affects how a mention is matched — that logic stays entirely in
> `CompanyResolver`. A company the seed file does not contain simply stays unresolved (correct MVP
> behaviour), preserving "do not hallucinate tickers."

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models (`Company`, `CompanyAlias`), 03-repository-abstractions-and-inmemory
(`ICompanyRepository`), 05-local-file-evidence-collector (mirrors its file-reading pattern)
Conflicts with: 22-pipeline-runner (both edit `InfrastructureServiceCollectionExtensions`);
**sequence 22 → 23, do not parallelize**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  EntityResolution/
    CompanySeedData.cs          # NEW: seed payload (companies + aliases)
    ICompanySeedSource.cs       # NEW: provider-independent seed data source
    ICompanyUniverseSeeder.cs   # NEW
    CompanyUniverseSeeder.cs    # NEW: idempotent upsert into ICompanyRepository

src/Radar.Infrastructure/
  Sources/
    LocalFileCompanySeedSource.cs        # NEW: reads seed JSON
    LocalFileCompanySeedOptions.cs       # NEW: { required string FilePath }
    LocalFileCompanySeedDocument.cs      # NEW: JSON DTOs (companies + aliases)
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: add AddLocalFileCompanySeed()

tests/Radar.Application.Tests/
  EntityResolution/
    CompanyUniverseSeederTests.cs        # NEW

tests/Radar.Infrastructure.Tests/
  Sources/
    LocalFileCompanySeedSourceTests.cs   # NEW
```

Namespaces: `Radar.Application.EntityResolution`, `Radar.Infrastructure.Sources`.

---

## Implementation details

### Application: seed payload + source interface

```csharp
namespace Radar.Application.EntityResolution;

using Radar.Domain.Companies;

/// <summary>The watch-universe to seed: companies and their aliases.</summary>
public sealed record CompanySeedData(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanyAlias> Aliases);

/// <summary>
/// Provider-independent source of the seed company watch-universe. Implementations read from a file,
/// embedded resource, or (later) a database. Returns an empty payload rather than throwing when the
/// source is missing or unreadable.
/// </summary>
public interface ICompanySeedSource
{
    Task<CompanySeedData> GetSeedAsync(CancellationToken ct);
}
```

### Application: idempotent seeder

```csharp
namespace Radar.Application.EntityResolution;

/// <summary>Loads the seed universe into the company repository. Safe to run on every startup.</summary>
public interface ICompanyUniverseSeeder
{
    /// <returns>The number of companies seeded.</returns>
    Task<int> SeedAsync(CancellationToken ct);
}
```

`CompanyUniverseSeeder : ICompanyUniverseSeeder` — constructor deps (`ThrowIfNull`):
`ICompanySeedSource`, `ICompanyRepository`, `ILogger<CompanyUniverseSeeder>`.

`SeedAsync` logic:
1. `seed = await _source.GetSeedAsync(ct)`.
2. For each company in `seed.Companies` (in source order), `await _companyRepository.AddAsync(company, ct)`.
3. For each alias in `seed.Aliases` (in source order), `await _companyRepository.AddAliasAsync(alias, ct)`.
4. `LogInformation` a one-line summary (company + alias counts); return `seed.Companies.Count`.

**Idempotency.** AD-1 makes `ICompanyRepository` upsert-by-`Id`, so re-running with the same stable Ids
overwrites the same rows rather than duplicating them. The seeder therefore requires the **source** to
return stable Ids across runs (see the file source below). The seeder adds no dedupe logic of its own —
it relies on the documented upsert semantics. Thread `ct`; do not check it on the in-memory path (AD-2),
but pass it through.

### Infrastructure: local-file seed source

`LocalFileCompanySeedOptions { public required string FilePath { get; init; } }`.

`LocalFileCompanySeedDocument` — JSON DTOs mirroring the evidence-collector pattern
(`PropertyNameCaseInsensitive = true`). Suggested shape (one file, an array of companies, each with an
optional aliases array):

```jsonc
{
  "companies": [
    {
      "id": "11111111-1111-1111-1111-111111111111",   // required, stable
      "name": "Acme Corp",
      "legalName": "Acme Corporation Inc.",
      "ticker": "ACME",
      "exchange": "NASDAQ",
      "countryCode": "US",
      "sector": "Technology",
      "industry": "Software",
      "aliases": [ "Acme", "Acme Inc" ]
    }
  ]
}
```

`LocalFileCompanySeedSource : ICompanySeedSource` — constructor deps (`ThrowIfNull`):
`LocalFileCompanySeedOptions`, `ILogger<LocalFileCompanySeedSource>`, `TimeProvider`. `GetSeedAsync`:

1. If `File.Exists(FilePath)` is false, `LogWarning` and return `new CompanySeedData([], [])` (mirrors
   the collector's missing-directory behaviour — never throw).
2. Read + `JsonSerializer.Deserialize` inside try/catch for `IOException`/`UnauthorizedAccessException`/
   `JsonException`; on failure `LogWarning` and return an empty payload.
3. For each company entry, skip (with a `LogWarning`) any entry missing a parseable `id` or a non-empty
   `name`; **do not hallucinate**. Build a `Company` with `Status = CompanyStatus.Active` and
   `CreatedAtUtc = UpdatedAtUtc = _timeProvider.GetUtcNow()`.
4. For each alias string under a company, build a `CompanyAlias` with `AliasType = "seed"`,
   `CreatedAtUtc = _timeProvider.GetUtcNow()`, `CompanyId = <that company's id>`, and a **deterministic**
   `Id` so re-seeding upserts the same alias row rather than creating a new one. Derive it from the
   stable tuple, e.g. a documented `DeterministicGuid(companyId, "seed", aliasText)` helper that hashes
   `$"{companyId}|seed|{normalizedAliasText}"` (SHA-1/MD5 → 16 bytes → `new Guid(bytes)`). Document the
   derivation in a comment. (The company's own `name` need not be added as an alias — the resolver
   already matches on `Company.Name`.)
5. Return `new CompanySeedData(companies, aliases)` preserving file order (deterministic).

> Note: the `TimeProvider` only stamps `CreatedAtUtc`/`UpdatedAtUtc`; it does **not** affect identity.
> Identity (company `Id`, derived alias `Id`) is stable across runs so seeding is idempotent regardless
> of clock — re-running upserts the same rows.

### DI

```csharp
/// <summary>
/// Registers the local-file company watch-universe seed source and the idempotent seeder. The seed file
/// at <paramref name="filePath"/> defines the companies/aliases that entity resolution can match
/// against. Safe to invoke the seeder on every startup (upsert-by-Id, AD-1).
/// </summary>
public static IServiceCollection AddLocalFileCompanySeed(
    this IServiceCollection services, string filePath)
{
    services.AddSingleton(new LocalFileCompanySeedOptions { FilePath = filePath });
    services.TryAddSingleton(TimeProvider.System);
    services.AddSingleton<ICompanySeedSource, LocalFileCompanySeedSource>();
    services.AddSingleton<ICompanyUniverseSeeder, CompanyUniverseSeeder>();
    return services;
}
```

Leave every existing registration untouched.

---

## Tests

### `LocalFileCompanySeedSourceTests` (Radar.Infrastructure.Tests, xUnit)

Write a temp JSON file (use the scratch/temp dir; clean up). Use `NullLogger<T>` and a fixed
`TimeProvider`.
- **Reads companies + aliases.** A two-company file with aliases yields the expected `Company` records
  (correct `Id`/`Name`/`Ticker`, `Status = Active`) and `CompanyAlias` records (`AliasType = "seed"`,
  correct `CompanyId`).
- **Deterministic alias Ids.** Reading the same file twice yields aliases with **equal** `Id`s for the
  same `(companyId, aliasText)`.
- **Missing file → empty payload, no throw.** A non-existent path returns `CompanySeedData([], [])` and
  logs a warning.
- **Malformed/partial entries skipped.** Invalid JSON returns an empty payload; an entry missing `id` or
  `name` is skipped while valid siblings are still returned.

### `CompanyUniverseSeederTests` (Radar.Application.Tests, xUnit)

Per AD-4, reference `Radar.Infrastructure` and seed a real `InMemoryCompanyRepository`. Use an in-test
`ICompanySeedSource` returning fixed `CompanySeedData` (built with `CompanyBuilder` + hand-built
`CompanyAlias`es with fixed Ids) and `NullLogger<T>`.
- **Seeds companies + aliases.** After `SeedAsync`, `ICompanyRepository.GetAllAsync` and
  `GetAliasesAsync` return the seeded records; the return value equals the company count.
- **Idempotent.** Running `SeedAsync` twice leaves `GetAllAsync` and `GetAliasesAsync` counts unchanged
  (upsert-by-Id), with no duplicate companies or aliases.
- **Enables resolution (integration).** After seeding, a real `CompanyResolver` resolves a mention equal
  to a seeded company's name (or alias) to that company's `Id` — demonstrating the seed makes resolution
  meaningful.

Optionally one DI wiring test: `AddInMemoryRadarPersistence()` + `AddLocalFileCompanySeed(tempFile)`,
resolve `ICompanyUniverseSeeder`, seed, and assert the repository is populated.

---

## Constraints

- Target .NET 10. The seeder/interfaces live in Application and depend only on Domain records +
  `ICompanyRepository` + `ILogger<T>` (AD-5). All file/JSON I/O lives in
  `LocalFileCompanySeedSource` inside `Radar.Infrastructure` — **no file access in Application**.
- **Do not modify `CompanyResolver` or change matching logic.** Seeding only populates the universe.
- Preserve "do not hallucinate tickers": skip entries lacking a stable `id` or a `name`; never fabricate
  data for malformed entries.
- Respect AD-1 (company/alias upsert-by-Id; rely on stable Ids for idempotency), AD-2 (thread `ct`),
  AD-3 (preserve deterministic source order). Store all timestamps in UTC via the injected
  `TimeProvider`.
- Central DI convention: register via `InfrastructureServiceCollectionExtensions`. Add no new domain
  types and no new repository methods.

---

## Acceptance criteria

- [ ] `CompanySeedData`, `ICompanySeedSource`, `ICompanyUniverseSeeder`, and `CompanyUniverseSeeder`
      exist under `Radar.Application.EntityResolution`; `LocalFileCompanySeedSource` (+ options + JSON
      DTOs) exists under `Radar.Infrastructure.Sources`.
- [ ] `AddLocalFileCompanySeed(filePath)` registers the source + seeder (central DI).
- [ ] The file source reads companies + aliases from JSON, returns an empty payload (no throw) when the
      file is missing/unreadable, skips entries lacking `id`/`name`, and derives **stable** alias Ids so
      re-seeding upserts the same rows.
- [ ] `CompanyUniverseSeeder.SeedAsync` upserts companies + aliases into `ICompanyRepository` and is
      idempotent across repeated runs; after seeding a real `CompanyResolver` can resolve a seeded
      name/alias.
- [ ] Tests cover file reading, deterministic alias Ids, missing/malformed files, idempotent seeding,
      and seed-enables-resolution.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
