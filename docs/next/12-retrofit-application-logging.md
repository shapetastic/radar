# Task: Retrofit ILogger into Application Services

## Overview

AD-5 now permits `Radar.Application` to reference `Microsoft.Extensions.*` abstractions. Retrofit
structured logging (`ILogger<T>`) into the three stateful, decision-making Application services that
were built without it (because the old, now-reversed "package-free Application" rule blocked it):
`CompanyResolver`, `KeywordSignalExtractor`, and `DeterministicSignalReviewer`.

This adds observability into the pipeline's decision points (what resolved, what was extracted, what
was reviewed and why) without changing any behaviour. Pure/stateless helpers are intentionally
excluded.

---

## Assignment

Worktree: any
Dependencies: 06-company-alias-resolver, 09-signal-extraction-contract-and-validation,
10-deterministic-keyword-signal-extractor, 11-deterministic-signal-review
Conflicts with: None — modifies existing Application service files, `Radar.Application.csproj`, and
their tests. Does not touch the DI registration logic (services are already registered).
Estimated time: ~1 hour

---

## Scope

**In scope** (add `ILogger<T>`):
- `src/Radar.Application/EntityResolution/CompanyResolver.cs`
- `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs`
- `src/Radar.Application/SignalReview/DeterministicSignalReviewer.cs`

**Out of scope** (leave unchanged — pure/stateless, nothing meaningful to log):
- `EvidenceNormalizer` (pure function), `ExtractedSignalMapper` (static), `SignalValidation` (static,
  Domain). Do not add logging to Domain.
- The Infrastructure collector already has `ILogger` — do not touch it.

---

## Implementation details

### Package (per AD-5)

Add a `PackageReference` to `Microsoft.Extensions.Logging.Abstractions` (version `10.0.x`, matching the
other `Microsoft.Extensions.*` packages already used in the solution) to
`src/Radar.Application/Radar.Application.csproj`. Domain stays package-free.

### Constructor injection

For each in-scope service, add an `ILogger<TService>` parameter to the constructor, store it in a
`private readonly` field, and guard it with `ArgumentNullException.ThrowIfNull` (matching the existing
guard convention). Keep existing parameters; append the logger (parameter order otherwise unchanged):
- `CompanyResolver(ICompanyRepository companyRepository, ILogger<CompanyResolver> logger)`
- `KeywordSignalExtractor(ILogger<KeywordSignalExtractor> logger)`
- `DeterministicSignalReviewer(TimeProvider timeProvider, ILogger<DeterministicSignalReviewer> logger)`

### Log statements (structured, behaviour-neutral)

Use structured logging with named placeholders (no string interpolation, no string concatenation).
Keep volume sensible — these run per evidence item / per signal:
- **`CompanyResolver`** — `Debug` on each resolution outcome: resolved (mention → `CompanyId`,
  confidence) vs unresolved (mention → reason). Optionally a single `Debug` when the seed universe is
  empty.
- **`KeywordSignalExtractor`** — `Debug` with the count of signals extracted and the evidence id/title.
- **`DeterministicSignalReviewer`** — `Debug` per signal with the resulting `SignalReviewDecision` and
  whether confidence was reduced.

Levels: prefer `Debug` for per-item detail; use `Information` only for genuinely notable, low-volume
events. Do not log at `Warning`/`Error` for normal outcomes. Logging must have **no** effect on return
values or control flow.

---

## Tests

- **Direct-construction unit tests** that `new` these services must now pass a logger. Use
  `NullLogger<TService>.Instance` (from `Microsoft.Extensions.Logging.Abstractions`). Known call sites
  to update: `CompanyResolverTests.cs:49`, `KeywordSignalExtractorTests.cs` (lines ~38/148/159), and
  the `DeterministicSignalReviewer` test construction. Search for all `new CompanyResolver(`,
  `new KeywordSignalExtractor(`, `new DeterministicSignalReviewer(` and update each.
- **DI/container test** (`InfrastructureServiceCollectionExtensionsTests`) — it resolves these services
  from a built `ServiceProvider`. Now that they depend on `ILogger<T>`, register logging before
  `BuildServiceProvider()` (e.g. `services.AddLogging()`), adding the `Microsoft.Extensions.Logging`
  package to `Radar.Application.Tests` if `AddLogging` isn't already available. The existing assertions
  (resolver is singleton, resolves from root, repositories-only count) must still hold.
- No new behavioural assertions are required — this is observability only. Existing tests must stay
  green; do not weaken any assertion.

---

## Constraints

- Target .NET 10. Domain stays pure (no packages, no logging).
- Behaviour-neutral: identical return values and control flow; logging is side-effect only.
- Application may reference `Microsoft.Extensions.Logging.Abstractions` only (an abstraction, per
  AD-5); do not add concrete logging providers to Application.
- Keep scope to the three listed services; do not refactor unrelated code.
- Solution must build warnings-as-error clean and all tests pass.

---

## Acceptance criteria

- [ ] `Radar.Application` references `Microsoft.Extensions.Logging.Abstractions`; Domain unchanged.
- [ ] `CompanyResolver`, `KeywordSignalExtractor`, and `DeterministicSignalReviewer` take and store an
      `ILogger<T>` (null-guarded) and emit structured, behaviour-neutral logs at sensible levels.
- [ ] Pure helpers (normalizer/mapper/validation) and Domain are untouched.
- [ ] All direct-construction tests pass `NullLogger<T>.Instance`; the DI test registers logging and
      still passes.
- [ ] `dotnet build Radar.sln -c Release` (warnings-as-errors) and `dotnet test Radar.sln -c Release`
      are green.
