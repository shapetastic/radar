# Task: Company Resolver DI Lifetime and Convention Cleanup

## Overview

A cross-slice architecture audit found drift that the per-PR reviews missed: `ICompanyResolver` is
the only `AddScoped` registration in the tree, and it is registered *inside*
`AddInMemoryRadarPersistence` — the method whose doc says it registers in-memory repositories only.

Both points are real problems, not cosmetics:

- **Latent runtime bug.** `CompanyResolver` is stateless and depends only on the singleton
  `ICompanyRepository`. Registering it scoped means a singleton consumer (the hosted
  `BackgroundService` / Worker, once it resolves the pipeline) that resolves it from the root
  provider will throw `InvalidOperationException: Cannot resolve scoped service ... from root
  provider`. The dependency graph is entirely singleton, so the resolver should be a singleton too.
- **Convention drift.** It violates the one-`AddXxx`-per-area convention: persistence registration
  should register repositories only, and application services belong in their own extension.

This slice converges those conventions before more code lands on top. It is pure cleanup: no new
features, no behaviour change beyond the resolver's DI lifetime, and one redundant comment removed.

Two genuinely-cheap LOW tidy-ups are bundled in because they sit in files this slice already touches
or are single-line, single-file edits (see L2/L5 below). Two further cosmetic findings are
**deferred** (see Notes).

---

## Assignment

Worktree: any
Dependencies: 06-company-alias-resolver, 07-persistence-determinism-and-convention-cleanup
Conflicts with: None — touches `InfrastructureServiceCollectionExtensions.cs`,
`IEvidenceCollector.cs`, `InMemoryEvidenceRepository.cs`, and adds one Infrastructure test. Sequence
it after 06/07.
Estimated time: ~1 hour

---

## Project structure changes

Modify only (one new test file; no new production files beyond the DI extension method):

```text
src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # M1: move resolver out into its own AddXxx; AddSingleton

src/Radar.Application/Collectors/
  IEvidenceCollector.cs                           # L2: move using above the file-scoped namespace

src/Radar.Infrastructure/Persistence/InMemory/
  InMemoryEvidenceRepository.cs                   # L5: drop the lone AD-2 comment (AD-2 ledger carries it)

tests/Radar.Infrastructure.Tests/DependencyInjection/
  InfrastructureServiceCollectionExtensionsTests.cs   # NEW: assert registrations + lifetimes
```

---

## Implementation details

### M1 — Move `ICompanyResolver` into its own extension and register it as a singleton

In `src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`:

1. **Remove** the line from `AddInMemoryRadarPersistence`:

   ```csharp
   services.AddScoped<ICompanyResolver, CompanyResolver>();
   ```

   After this, `AddInMemoryRadarPersistence` registers exactly the five repositories and nothing
   else, matching its `<summary>`.

2. **Add** a new extension method that registers the stateless application services. Use the name
   `AddRadarApplicationServices` (groups future stateless app services; `AddCompanyResolution` is an
   acceptable narrower alternative if the coder prefers — pick one and keep it consistent):

   ```csharp
   /// <summary>
   /// Registers the stateless application services (currently the deterministic
   /// <see cref="Radar.Application.EntityResolution.ICompanyResolver"/>) as singletons. The
   /// resolver only depends on the singleton repositories, so a singleton lifetime is correct
   /// and lets singleton consumers (e.g. a hosted service) resolve it from the root provider.
   /// Requires <see cref="AddInMemoryRadarPersistence"/> (or another registration of the
   /// repositories) to have been called for the resolver's dependencies.
   /// </summary>
   public static IServiceCollection AddRadarApplicationServices(this IServiceCollection services)
   {
       services.AddSingleton<ICompanyResolver, CompanyResolver>();
       return services;
   }
   ```

   Keep the existing `using Radar.Application.EntityResolution;` (it is still needed) and the
   existing `using` ordering/style in the file.

There is no production call site to update: `Program.cs` does not currently call these extension
methods, and `CompanyResolverTests` constructs `CompanyResolver` directly. Do **not** add a
`Program.cs` wiring change in this slice — keep the Worker a placeholder. (If the coder finds any
caller that previously relied on `AddInMemoryRadarPersistence` to also register the resolver, update
it to additionally call `AddRadarApplicationServices`.)

### L2 — `using` placement in `IEvidenceCollector.cs`

`src/Radar.Application/Collectors/IEvidenceCollector.cs` currently puts its `using` *below* the
file-scoped namespace. Every other file in the tree places `using` directives above the namespace.
Move the `using Radar.Domain.Evidence;` above `namespace Radar.Application.Collectors;` so the file
reads:

```csharp
using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

public interface IEvidenceCollector
{
    Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct);
}
```

No behaviour change.

### L5 — Remove the duplicated AD-2 comment

`src/Radar.Infrastructure/Persistence/InMemory/InMemoryEvidenceRepository.cs` is the only one of the
five in-memory repositories that carries the AD-2 "in-memory repos don't observe the
CancellationToken" comment (lines ~7–8). AD-2 is now recorded in `docs/architecture-decisions.md`,
which is the single source of truth for that convention. To remove the one-of-five inconsistency,
**delete** the two-line comment from `InMemoryEvidenceRepository.cs` and let AD-2 carry the
convention. Do not add the comment to the other four repositories (the ledger covers it). No
behaviour change — the `CancellationToken` is still accepted and not observed, per AD-2.

---

## Tests

Add `tests/Radar.Infrastructure.Tests/DependencyInjection/InfrastructureServiceCollectionExtensionsTests.cs`
covering the registration contract (this is the first DI-extension test in the project):

- **Persistence registers repositories only.** After `new ServiceCollection().AddInMemoryRadarPersistence()`,
  assert the collection contains a registration for each of the five repository interfaces
  (`IEvidenceRepository`, `ICompanyRepository`, `ISignalRepository`, `IScoreRepository`,
  `IReportRepository`) and contains **no** registration for `ICompanyResolver`.
- **Resolver is registered as a singleton.** After `AddRadarApplicationServices()`, assert the
  `ServiceDescriptor` for `ICompanyResolver` has `ServiceLifetime.Singleton`.
- **Resolver is resolvable from the root provider and is a singleton instance.** Build a provider
  from `services.AddInMemoryRadarPersistence().AddRadarApplicationServices()`, resolve
  `ICompanyResolver` twice directly from the root provider (no scope), and assert it succeeds and
  returns the **same** instance (`ReferenceEquals`). This is the regression test for the latent
  scoped-from-root bug.

Existing tests must still pass unchanged.

---

## Constraints

- Target .NET 10; keep `Radar.Application` package-free and the layering intact.
- Pure cleanup: the only behaviour change is the resolver's DI lifetime (scoped to singleton). No new
  domain types, no new packages, no provenance changes.
- Do not wire the resolver into `Program.cs` or expand the Worker.
- Respect the architecture decisions ledger — in particular AD-2 (in-memory repos do not observe the
  `CancellationToken`); L5 removes a redundant comment, it does not change that behaviour.
- Keep the solution buildable and green at every step.

---

## Acceptance criteria

- [ ] `ICompanyResolver` is registered `AddSingleton<ICompanyResolver, CompanyResolver>()` in a new
      dedicated extension (`AddRadarApplicationServices`), not inside `AddInMemoryRadarPersistence`.
- [ ] `AddInMemoryRadarPersistence` registers exactly the five repositories and no longer references
      `ICompanyResolver`; its `<summary>` matches what it registers.
- [ ] There are no `AddScoped` registrations left in the Infrastructure DI tree.
- [ ] `IEvidenceCollector.cs` places its `using` above the file-scoped namespace.
- [ ] The lone AD-2 comment is removed from `InMemoryEvidenceRepository.cs` (no comment added to the
      other four); AD-2 in the ledger carries the convention.
- [ ] New DI tests assert: persistence registers repositories only (no resolver), the resolver is a
      singleton, and the resolver resolves from the root provider returning the same instance twice.
- [ ] No production code outside the listed files changes; the Worker stays a placeholder.
- [ ] `dotnet build Radar.sln -c Release` (warnings-as-errors) and `dotnet test Radar.sln -c Release`
      are green.

---

## Notes (deferred — do NOT do in this slice)

- **L3 — test factory-method naming (`Make*` vs `New*`).** Inconsistent across the two test projects;
  pure cosmetic, spans many files. Defer.
- **L4 — test-class sealing inconsistency.** Some test classes are `sealed`, some are not; pure
  cosmetic, spans many files. Defer.

Both are tracked for a future cosmetic sweep and intentionally excluded here to keep this slice small
and low-risk.
