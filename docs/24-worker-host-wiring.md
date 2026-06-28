# Task: Worker Host Wiring — run the pipeline end-to-end from a configured host

## Overview

`Radar.Worker` is still the stock `BackgroundService` template: it logs `"Worker running at: {time}"`
on a loop using an inline `DateTimeOffset.UtcNow` and wires **none** of the pipeline. Every stage and
the `RadarPipelineRunner` (spec 22) plus the company seeder (spec 23) now exist, so this slice makes
Radar actually run: the host composes the dependency graph from configuration, seeds the watch-universe,
and invokes the pipeline runner — once or on an interval — using the injected `TimeProvider`.

The Worker stays a **thin host**: composition in `Program.cs`, and a `Worker` whose only job is to call
two Application services (`ICompanyUniverseSeeder` then `IRadarPipeline`) on a schedule and manage
shutdown. It contains **no business logic** — no collecting, extracting, resolving, reviewing, scoring,
or report logic inline (all of that lives behind the interfaces it calls).

After this slice the MVP is runnable end-to-end: a configured seed file + evidence directory in →
persisted evidence/signals/scores + a weekly markdown report out.

> **No clock leakage, UTC everywhere.** The inline `DateTimeOffset.UtcNow` is removed. The Worker and
> everything it drives take time only from the injected `TimeProvider`; the pipeline's single run instant
> (spec 22) flows from there.

---

## Assignment

Worktree: pending
Dependencies: 22-pipeline-runner (`IRadarPipeline`, `AddRadarPipeline`, `PipelineOptions`),
23-company-watch-universe-seed (`ICompanyUniverseSeeder`, `AddLocalFileCompanySeed`),
05-local-file-evidence-collector (`AddLocalFileCollector`), 03-repository-abstractions-and-inmemory
(`AddInMemoryRadarPersistence`)
Conflicts with: None (edits only `Radar.Worker` files + the solution file; sequence after 22 and 23 as
they provide the services this host composes)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Worker/
  Program.cs                 # MODIFIED: compose the full pipeline DI graph from configuration
  Worker.cs                  # MODIFIED: seed once, then run the pipeline (once or on an interval); thin
  WorkerRunOptions.cs        # NEW: schedule knobs (run-once vs interval)
  RadarWorkerOptions.cs      # NEW: configuration POCO bound from the "Radar" section
  RadarWorkerServices.cs     # NEW: internal static composition helper (keeps Program a one-liner, testable)
  appsettings.json           # MODIFIED: add the "Radar" section with defaults
  Radar.Worker.csproj        # MODIFIED only if a config/options package is genuinely needed (see notes)

tests/Radar.Worker.Tests/    # NEW test project (added to Radar.sln)
  Radar.Worker.Tests.csproj
  WorkerTests.cs
  RadarWorkerServicesTests.cs
```

Namespace: `Radar.Worker`.

---

## Implementation details

### Configuration POCOs

`RadarWorkerOptions` — bound from configuration section `"Radar"`, with safe defaults so the worker runs
out-of-the-box:

```csharp
namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>Directory of local evidence JSON files (Stage 1 source).</summary>
    public string EvidenceSourceDirectory { get; init; } = "data/evidence";

    /// <summary>Path to the company watch-universe seed JSON file.</summary>
    public string CompanySeedFilePath { get; init; } = "data/companies.json";

    /// <summary>Recent-signal scoring window length, in days (maps to ScoringOptions.Window).</summary>
    public int ScoringWindowDays { get; init; } = 30;

    /// <summary>Report period length, in days (maps to WeeklyReportOptions.Period).</summary>
    public int ReportPeriodDays { get; init; } = 7;

    /// <summary>Max companies in the report (maps to WeeklyReportOptions.MaxItems).</summary>
    public int ReportMaxItems { get; init; } = 25;

    /// <summary>Whether the run ends by building the weekly report (maps to PipelineOptions.GenerateReport).</summary>
    public bool GenerateReport { get; init; } = true;

    /// <summary>Run once then exit (true, MVP default), or loop on an interval (false).</summary>
    public bool RunOnce { get; init; } = true;

    /// <summary>Interval between runs in minutes when RunOnce is false.</summary>
    public int IntervalMinutes { get; init; } = 60;
}
```

`WorkerRunOptions` — the small, already-resolved schedule shape the `Worker` consumes (keeps the Worker
free of raw config parsing):

```csharp
namespace Radar.Worker;

public sealed class WorkerRunOptions
{
    public bool RunOnce { get; init; } = true;
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}
```

### Composition helper (`RadarWorkerServices`)

Put the DI wiring in an `internal static` helper so `Program.cs` stays a few lines and the graph is
unit-testable without launching a host:

```csharp
namespace Radar.Worker;

internal static class RadarWorkerServices
{
    public static IServiceCollection AddRadarWorker(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Radar").Get<RadarWorkerOptions>() ?? new RadarWorkerOptions();

        // Register the configured option instances FIRST so the libraries' TryAddSingleton defaults
        // (ScoringOptions / WeeklyReportOptions / PipelineOptions) do not override them.
        services.AddSingleton(new ScoringOptions { Window = TimeSpan.FromDays(options.ScoringWindowDays) });
        services.AddSingleton(new WeeklyReportOptions
        {
            Period = TimeSpan.FromDays(options.ReportPeriodDays),
            MaxItems = options.ReportMaxItems,
        });
        services.AddSingleton(new PipelineOptions { GenerateReport = options.GenerateReport });
        services.AddSingleton(new WorkerRunOptions
        {
            RunOnce = options.RunOnce,
            Interval = TimeSpan.FromMinutes(options.IntervalMinutes),
        });

        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCollector(options.EvidenceSourceDirectory);
        services.AddLocalFileCompanySeed(options.CompanySeedFilePath);
        services.AddRadarPipeline();

        services.AddHostedService<Worker>();
        return services;
    }
}
```

> **Ordering matters.** `AddRadarApplicationServices` / `AddRadarPipeline` register their options with
> `TryAddSingleton`; registering the configured instances *before* those calls is what lets configuration
> win. Add a comment to that effect so it is not "tidied" into the wrong order.

### `Program.cs`

Reduce to host construction + the helper:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Radar.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRadarWorker(builder.Configuration);

var host = builder.Build();
host.Run();
```

(`Host.CreateApplicationBuilder` already loads `appsettings.json` + environment + command line.)

### `Worker.cs` (thin)

Constructor injection: `ICompanyUniverseSeeder`, `IRadarPipeline`, `IHostApplicationLifetime`,
`WorkerRunOptions`, `TimeProvider`, `ILogger<Worker>` (all `ThrowIfNull`). `ExecuteAsync`:

1. Wrap the body in `try { … } catch (OperationCanceledException) { }` so shutdown is graceful.
2. `await _seeder.SeedAsync(stoppingToken)` — seed the watch-universe once at startup (idempotent, AD-1).
3. If `_options.RunOnce`: `await RunPipelineAsync(stoppingToken)`, then
   `_lifetime.StopApplication()` and return.
4. Otherwise loop with `using var timer = new PeriodicTimer(_options.Interval, _timeProvider);` —
   run, then `while (await timer.WaitForNextTickAsync(stoppingToken))` run again. (The `PeriodicTimer`
   `TimeProvider` overload keeps the schedule test-controllable.)
5. `RunPipelineAsync`: `var result = await _pipeline.RunAsync(ct);` then `LogInformation` a one-line
   summary including `_timeProvider.GetUtcNow()` and `result` counts (evidence new, signals approved,
   companies scored, report id). **No `DateTimeOffset.UtcNow` anywhere.**

The Worker performs **only** these two service calls plus scheduling/lifetime — no pipeline logic
inline.

### `appsettings.json`

Add a `"Radar"` section with the defaults (so the repo runs without extra config):

```jsonc
"Radar": {
  "EvidenceSourceDirectory": "data/evidence",
  "CompanySeedFilePath": "data/companies.json",
  "ScoringWindowDays": 30,
  "ReportPeriodDays": 7,
  "ReportMaxItems": 25,
  "GenerateReport": true,
  "RunOnce": true,
  "IntervalMinutes": 60
}
```

### csproj / packages

`Microsoft.Extensions.Hosting` (already referenced) transitively provides Configuration binding, Options,
and DI — confirm `configuration.GetSection(...).Get<T>()` compiles; only add
`Microsoft.Extensions.Configuration.Binder` if the build genuinely needs it (do not add packages
speculatively). No `Microsoft.AspNetCore.*` is needed (this is a Worker, not a web host). The new
`Radar.Worker.Tests` project references `Radar.Worker` and the xUnit stack used by the other test
projects; add `Microsoft.Extensions.TimeProvider.Testing` for `FakeTimeProvider` **only if** the other
test projects don't already supply an equivalent — otherwise reuse a small in-test `TimeProvider` stub.

---

## Tests

### `WorkerTests` (Radar.Worker.Tests, xUnit)

Use in-test fakes for `ICompanyUniverseSeeder` and `IRadarPipeline` that record call order (and counts),
a real `Microsoft.Extensions.Hosting.ApplicationLifetime` (or a minimal `IHostApplicationLifetime`
double), `NullLogger<Worker>`, and a controllable `TimeProvider`.
- **Seed precedes pipeline (run-once).** With `RunOnce = true`, start the worker via `StartAsync`/await
  `ExecuteAsync`; assert the seeder was called exactly once **before** the pipeline, the pipeline ran
  exactly once, and `IHostApplicationLifetime.StopApplication()` was triggered (e.g. its
  `ApplicationStopping` token fires).
- **Graceful cancellation.** Cancelling the stopping token does not throw out of `ExecuteAsync`.
- **Interval mode (optional).** With `RunOnce = false` and a `FakeTimeProvider`, advancing time past one
  interval causes a second `RunAsync` before cancellation; assert ≥ 2 runs.

### `RadarWorkerServicesTests` (Radar.Worker.Tests, xUnit)

- **Graph resolves.** Build a `ServiceCollection`, call `AddRadarWorker` with an in-memory
  `IConfiguration` (`ConfigurationBuilder().AddInMemoryCollection(...)`), build the provider, and resolve
  `IRadarPipeline`, `ICompanyUniverseSeeder`, and the hosted `Worker` without throwing.
- **Configuration wins over library defaults.** Setting `Radar:ScoringWindowDays = 14` and
  `Radar:ReportMaxItems = 5` yields a resolved `ScoringOptions.Window == TimeSpan.FromDays(14)` and
  `WeeklyReportOptions.MaxItems == 5` (proves the register-before-`TryAddSingleton` ordering).
- **GenerateReport flows through.** `Radar:GenerateReport = false` yields
  `PipelineOptions.GenerateReport == false`.

Add `Radar.Worker.Tests` to `Radar.sln`. Keep the worker's own behaviour tests fast and host-free
(drive `Worker` directly; no real files, no network).

---

## Constraints

- Target .NET 10. The Worker is the **only** project that may use full hosting packages
  (`Microsoft.Extensions.Hosting`) — per AD-5 these belong in the host layer, not in Application. Do not
  move any pipeline logic into the Worker.
- **Thin host:** `Worker` calls `ICompanyUniverseSeeder` then `IRadarPipeline` and manages
  schedule/lifetime only. All stage behaviour stays behind the interfaces.
- **Remove the inline clock.** No `DateTimeOffset.UtcNow`/`DateTime.UtcNow` in the Worker — use the
  injected `TimeProvider`. Store/emit UTC only.
- Reuse the established central DI helpers (`AddInMemoryRadarPersistence`, `AddRadarApplicationServices`,
  `AddLocalFileCollector`, `AddLocalFileCompanySeed`, `AddRadarPipeline`) — do not re-register their
  services individually. Preserve the configured-options-before-`TryAddSingleton` ordering.
- Add no new domain types, no new repository methods, and no changes to Application/Infrastructure
  services (this slice is host wiring only).

---

## Acceptance criteria

- [ ] `Program.cs` composes the full graph via `AddRadarWorker(builder.Configuration)`; the stock
      template loop and its inline `DateTimeOffset.UtcNow` are gone.
- [ ] `Worker` injects `TimeProvider`, seeds the universe once, then runs `IRadarPipeline` — once
      (`RunOnce`) then calls `StopApplication()`, or on a `PeriodicTimer(Interval, TimeProvider)` loop —
      and contains no pipeline logic.
- [ ] `RadarWorkerOptions` binds from the `"Radar"` config section; configuration overrides the library
      `ScoringOptions`/`WeeklyReportOptions`/`PipelineOptions` defaults (registered before their
      `TryAddSingleton`).
- [ ] `appsettings.json` contains a `"Radar"` section with working defaults; the worker runs end-to-end
      against a seed file + evidence directory and produces a persisted weekly report.
- [ ] `Radar.Worker.Tests` is added to `Radar.sln` and covers seed-before-pipeline + run-once shutdown,
      graceful cancellation, graph resolution, and configuration-overrides-defaults.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
