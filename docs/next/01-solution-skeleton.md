# Task: Solution Skeleton Targeting .NET 10

## Overview

Create the empty, buildable solution skeleton for Project Radar. This establishes the
project boundaries (Domain, Application, Infrastructure, Worker) and the test projects so
that every later task has a place to land code. No business logic is added here — the goal
is a clean `dotnet build` and `dotnet test` on an otherwise empty solution.

This task exists first because every subsequent spec assumes these projects and their
reference graph already exist. Getting the architecture boundaries right now prevents drift
later (e.g. Domain must not reference Infrastructure).

---

## Assignment

Worktree: any
Dependencies: None
Conflicts with: None (this task creates the solution/project files; do not run in parallel
with any other task, since everything else modifies these files)
Estimated time: ~1 hour

---

## Project structure changes

Create the following layout at the repo root:

```text
Radar.sln

src/
  Radar.Domain/Radar.Domain.csproj
  Radar.Application/Radar.Application.csproj
  Radar.Infrastructure/Radar.Infrastructure.csproj
  Radar.Worker/Radar.Worker.csproj

tests/
  Radar.Domain.Tests/Radar.Domain.Tests.csproj
  Radar.Application.Tests/Radar.Application.Tests.csproj
  Radar.Infrastructure.Tests/Radar.Infrastructure.Tests.csproj

Directory.Build.props        # shared MSBuild settings
```

Also add a root `global.json` pinning the SDK to the 10.0 feature band so the build is
reproducible.

---

## Implementation details

### `global.json`

Pin to the installed 10.0 SDK with `rollForward: latestFeature`:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

### `Directory.Build.props` (repo root)

Centralize common settings so individual csproj files stay minimal:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

### Projects

- `Radar.Domain` — `classlib`. No references. Pure domain records/enums (added in later task).
- `Radar.Application` — `classlib`. References `Radar.Domain`.
- `Radar.Infrastructure` — `classlib`. References `Radar.Application` and `Radar.Domain`.
- `Radar.Worker` — `worker` (Worker Service template). References `Radar.Application` and
  `Radar.Infrastructure`. Leave the default `Worker`/`Program.cs` from the template; it is a
  placeholder until the pipeline jobs exist.

### Test projects

- Use xUnit (`dotnet new xunit`) for all three test projects.
- `Radar.Domain.Tests` references `Radar.Domain`.
- `Radar.Application.Tests` references `Radar.Application` (+ `Radar.Domain`).
- `Radar.Infrastructure.Tests` references `Radar.Infrastructure`.
- Each test project may keep a single trivial passing test (e.g. `Assert.True(true)`) so the
  test run reports a green result. This placeholder is removed when real tests arrive.

### Reference rules (must hold)

- Domain references nothing in the solution.
- Application references Domain only.
- Infrastructure references Application + Domain.
- Worker references Application + Infrastructure.
- No project references Worker.

### Suggested commands

```bash
dotnet new sln -n Radar
# create projects with the templates above, then:
dotnet sln add (each csproj)
# wire references with: dotnet add <proj> reference <target>
```

---

## Tests

No production logic to test yet. Acceptance is the build + the placeholder tests passing.

---

## Constraints

- Target .NET 10 (`net10.0`) everywhere via `Directory.Build.props`.
- Keep `TreatWarningsAsErrors=true` from the start to prevent warning rot.
- Do not add NuGet packages beyond the test SDK/xUnit defaults.
- Do not implement any domain, pipeline, AI, or persistence code in this task.

---

## Acceptance criteria

- [ ] `Radar.sln` exists at repo root and contains all 7 projects.
- [ ] `dotnet build Radar.sln -c Release` succeeds with zero warnings.
- [ ] `dotnet test Radar.sln -c Release` runs and all (placeholder) tests pass.
- [ ] Project reference graph matches the rules above (Domain depends on nothing).
- [ ] All projects target `net10.0` with nullable + implicit usings enabled.
