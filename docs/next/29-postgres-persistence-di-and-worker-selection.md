# Task: Postgres persistence DI registration and Worker store selection

## Overview

Wires the Postgres repositories (specs 27/28) into the application via a new
`AddPostgresRadarPersistence(connectionString)` extension that mirrors the existing
`AddInMemoryRadarPersistence`, and lets `Radar.Worker` **select** the store from configuration
(in-memory — the default — vs Postgres). It also makes both registrations supply an
`IPersistenceInitializer` (spec 26) so the host can "prepare the store" uniformly: a no-op for in-memory,
the schema migration runner for Postgres.

After this slice Radar can run end-to-end against a real PostgreSQL database by setting two config values,
while the default configuration (and every existing test) continues to use the in-memory store unchanged.

---

## Assignment

Worktree: pending
Dependencies: 26 (`IPersistenceInitializer`, `NpgsqlConnectionFactory`, `PostgresSchemaInitializer`,
`PostgresDapperConfiguration`), 27 (evidence/company Postgres repos), 28 (signal/score/report Postgres
repos)
Conflicts with: 31 (both edit `InfrastructureServiceCollectionExtensions.cs`) — sequence 29 before 31.
Touches the Worker composition files; sequence after 24-worker-host-wiring (already merged).
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: + AddPostgresRadarPersistence; in-memory no-op initializer
src/Radar.Infrastructure/Persistence/InMemory/
  NoOpPersistenceInitializer.cs                 # NEW: IPersistenceInitializer no-op for the in-memory store

src/Radar.Worker/
  RadarWorkerOptions.cs   # MODIFIED: + Persistence selector + PostgresConnectionString
  RadarWorkerServices.cs  # MODIFIED: choose in-memory vs Postgres from config
  Worker.cs               # MODIFIED: call IPersistenceInitializer.InitializeAsync before seeding
  appsettings.json        # MODIFIED: add Persistence (+ commented connection-string guidance)
```

---

## Implementation details

### `NoOpPersistenceInitializer` (in-memory)

```csharp
internal sealed class NoOpPersistenceInitializer : IPersistenceInitializer
{
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register it inside `AddInMemoryRadarPersistence` (one added line):
`services.AddSingleton<IPersistenceInitializer, NoOpPersistenceInitializer>();`

### `AddPostgresRadarPersistence(connectionString)`

Mirror the in-memory method's shape and XML-doc style:

```csharp
public static IServiceCollection AddPostgresRadarPersistence(
    this IServiceCollection services, string connectionString)
{
    PostgresDapperConfiguration.EnsureConfigured();

    var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    services.AddSingleton(dataSource);                       // singleton NpgsqlDataSource (pooled)
    services.AddSingleton<NpgsqlConnectionFactory>();

    services.AddSingleton<IEvidenceRepository, PostgresEvidenceRepository>();
    services.AddSingleton<ICompanyRepository, PostgresCompanyRepository>();
    services.AddSingleton<ISignalRepository, PostgresSignalRepository>();
    services.AddSingleton<IScoreRepository, PostgresScoreRepository>();
    services.AddSingleton<IReportRepository, PostgresReportRepository>();

    services.AddSingleton<IPersistenceInitializer, PostgresSchemaInitializer>();
    return services;
}
```

Throw `ArgumentException` on a null/empty `connectionString`. Singleton lifetimes match the in-memory
registration so singleton consumers (the pipeline runner, resolver) resolve them from the root provider.

### Worker configuration (`RadarWorkerOptions`)

Add:

```csharp
/// <summary>Backing store: "InMemory" (default) or "Postgres".</summary>
public string Persistence { get; init; } = "InMemory";

/// <summary>PostgreSQL connection string; required when Persistence is "Postgres".</summary>
public string? PostgresConnectionString { get; init; }
```

### `RadarWorkerServices` selection

Replace the unconditional `services.AddInMemoryRadarPersistence();` with a config-driven choice (keep the
configured-options-before-`TryAddSingleton` ordering note intact — persistence registration stays where
the in-memory call is today):

```csharp
if (string.Equals(options.Persistence, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    var cs = options.PostgresConnectionString
        ?? throw new InvalidOperationException(
            "Radar:PostgresConnectionString is required when Radar:Persistence is \"Postgres\".");
    services.AddPostgresRadarPersistence(cs);
}
else
{
    services.AddInMemoryRadarPersistence();
}
```

### `Worker.cs` — prepare the store before seeding

Inject `IPersistenceInitializer` (constructor, `ThrowIfNull`). In `ExecuteAsync`, **before**
`_seeder.SeedAsync(...)`, call `await _initializer.InitializeAsync(stoppingToken)`. For in-memory this is
a no-op; for Postgres it applies the schema migrations. This keeps the Worker provider-agnostic — it never
references Npgsql or a Postgres type (AD-5).

### `appsettings.json`

Add `"Persistence": "InMemory"` to the `"Radar"` section, and document (commented or via
`appsettings.Development.json`) the Postgres form, e.g.:

```jsonc
// "Persistence": "Postgres",
// "PostgresConnectionString": "Host=localhost;Port=5432;Database=radar;Username=radar;Password=radar"
```

The committed default stays `InMemory` so the repo runs (and CI tests pass) with no database.

---

## Tests

Add to `Radar.Worker.Tests` (host-free, no real database):

- **Default selects in-memory.** With no `Radar:Persistence` set, the resolved `IEvidenceRepository` is
  `InMemoryEvidenceRepository` and `IPersistenceInitializer` is `NoOpPersistenceInitializer`.
- **Postgres selection requires a connection string.** `Radar:Persistence = "Postgres"` with no
  connection string throws `InvalidOperationException` from `AddRadarWorker`.
- **Postgres selection builds the graph.** `Radar:Persistence = "Postgres"` + a syntactically valid
  connection string resolves `PostgresEvidenceRepository` and `PostgresSchemaInitializer` **without
  opening a connection** (building `NpgsqlDataSource` does not connect; do not call any repository method
  in this test).

Live-database behaviour is covered by spec 30. Keep `Radar.IntegrationTests` and all in-memory tests
unchanged and green.

---

## Constraints

- Target .NET 10. `AddPostgresRadarPersistence` and all Npgsql/Dapper usage stay in
  `Radar.Infrastructure` (AD-5). The Worker selects via configuration and the
  `IPersistenceInitializer`/repository interfaces only — no Postgres types leak into the Worker.
- Default configuration must remain in-memory so `dotnet test Radar.sln` stays green without a database.
- Preserve the existing register-configured-options-before-`TryAddSingleton` ordering in
  `RadarWorkerServices`.
- Keep the in-memory repositories and their tests intact; this slice only adds the no-op initializer and a
  selection branch.

---

## Acceptance criteria

- [ ] `AddPostgresRadarPersistence(connectionString)` registers the `NpgsqlDataSource`, connection
      factory, all five Postgres repositories, and `PostgresSchemaInitializer` as `IPersistenceInitializer`.
- [ ] `AddInMemoryRadarPersistence` additionally registers `NoOpPersistenceInitializer`.
- [ ] `RadarWorkerOptions` exposes `Persistence` + `PostgresConnectionString`; `RadarWorkerServices`
      selects the store from config and throws a clear error if Postgres is chosen with no connection string.
- [ ] `Worker` calls `IPersistenceInitializer.InitializeAsync` before seeding and references no Postgres
      type.
- [ ] `appsettings.json` default is `Persistence: "InMemory"`; Postgres form is documented.
- [ ] New Worker tests cover default-in-memory, Postgres-needs-connection-string, and Postgres-graph-builds.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
