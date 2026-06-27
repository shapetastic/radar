# Task: Company Alias Resolver (Conservative Entity Resolution)

## Overview

Add Stage 3 (Entity Resolution): a deterministic service that maps a company mention string to a
known company in the seed universe via exact, normalized alias/name/ticker matching. The master
spec is emphatic that resolution must be **conservative** — never hallucinate a ticker, and when
uncertain produce an unresolved mention rather than guessing.

This task implements deterministic matching only (no AI). It reads the seed universe through the
existing `ICompanyRepository` (companies + aliases) and returns a resolution result the pipeline
can use to build an `EvidenceMention`. Keeping it deterministic and conservative protects
provenance: an evidence-to-company link is only asserted when it is unambiguous.

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models, 03-repository-abstractions-and-inmemory
Conflicts with: 05-local-file-evidence-collector (both edit `InfrastructureServiceCollectionExtensions.cs`) — sequence after 05
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  EntityResolution/
    ICompanyResolver.cs
    CompanyResolver.cs
    CompanyResolutionResult.cs

src/Radar.Infrastructure/
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: register ICompanyResolver

tests/Radar.Application.Tests/
  EntityResolution/
    CompanyResolverTests.cs
```

Namespace: `Radar.Application.EntityResolution`.

`Radar.Application` keeps zero package references; BCL only. The resolver depends on
`ICompanyRepository` (already in `Radar.Application.Abstractions.Persistence`).

---

## Implementation details

### Result record

```csharp
namespace Radar.Application.EntityResolution;

public sealed record CompanyResolutionResult(
    Guid? CompanyId,
    decimal Confidence,
    string Reason,
    string? MatchedAlias);
```

`CompanyId == null` means unresolved. `Confidence` is `0m` when unresolved.

### Interface

```csharp
namespace Radar.Application.EntityResolution;

public interface ICompanyResolver
{
    Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct);
}
```

### Implementation (`CompanyResolver`)

Constructor takes `ICompanyRepository`.

Matching rules (conservative, deterministic):

- Normalize a candidate string with a private helper: trim, lower-case using
  `ToLowerInvariant`, collapse internal whitespace to single spaces, and strip a small set of
  common company suffixes/punctuation (e.g. trailing `inc`, `inc.`, `corp`, `corporation`,
  `ltd`, `plc`, `llc`, `co`, and `,`/`.`). Keep the suffix list small and documented in code.
- Build a lookup over the seed universe loaded from `ICompanyRepository.GetAllAsync` and
  `GetAliasesAsync`:
  - company `Name` (normalized) -> companyId
  - each `CompanyAlias.Alias` (normalized) -> companyId
  - company `Ticker` is matched **only** as an exact, case-insensitive whole-string match of the
    raw mention (tickers are short and ambiguous; do not apply suffix-stripping to them).
- Resolution outcome for `ResolveAsync(mentionText)`:
  - If the normalized mention exactly matches exactly **one** company via name/alias -> resolved:
    `Confidence = 1.0m`, `Reason` describing the match (e.g. `"Exact alias match"`),
    `MatchedAlias` = the matched key.
  - If it matches an exact ticker (raw, case-insensitive) and nothing else -> resolved with
    `Confidence = 0.9m`, `Reason = "Exact ticker match"`.
  - If the normalized mention maps to **more than one distinct company** (ambiguous) -> unresolved:
    `CompanyId = null`, `Confidence = 0m`, `Reason = "Ambiguous mention"`.
  - If no match -> unresolved: `CompanyId = null`, `Confidence = 0m`, `Reason = "No match"`.
- Do **not** perform fuzzy/substring/Levenshtein matching in this task. Exact normalized matches
  only. (Fuzzy matching, if ever added, is a separate, later, clearly-flagged task.)
- Blank/whitespace `mentionText` returns unresolved with `Reason = "Empty mention"` (no throw on
  empty; `ArgumentNullException` only on a null argument).

Note for callers: a `CompanyResolutionResult` maps cleanly onto the domain `EvidenceMention`
record (`ResolvedCompanyId`, `ResolutionConfidence`, `ResolutionReason`) when persisting — but
this task does not build or persist `EvidenceMention`s; it only resolves.

### DI

Register `ICompanyResolver -> CompanyResolver` (scoped or transient) in
`InfrastructureServiceCollectionExtensions.AddInMemoryRadarPersistence` (it already wires the
in-memory `ICompanyRepository` the resolver needs), or in a small dedicated method if cleaner.
Do not change the existing repository registrations.

---

## Tests

`Radar.Application.Tests/EntityResolution/CompanyResolverTests.cs` (xUnit). Seed an
`InMemoryCompanyRepository` directly with deterministic companies and aliases.

- Exact name match resolves to the right company with `Confidence == 1.0m`.
- Exact alias match resolves with `Confidence == 1.0m` and reports `MatchedAlias`.
- Suffix/case/whitespace variations (e.g. `"Acme, Inc."` vs alias `"acme"`) still resolve.
- Exact ticker match resolves with `Confidence == 0.9m`.
- An unknown mention is unresolved (`CompanyId == null`, `Confidence == 0m`, reason "No match").
- An alias shared by two different companies yields an ambiguous, unresolved result (no guess).
- Empty/whitespace mention returns unresolved with reason "Empty mention"; null throws
  `ArgumentNullException`.
- No partial/substring match: a mention that merely contains a company name as a substring does
  **not** resolve.

All deterministic — no AI, no clock, no network.

---

## Constraints

- Target .NET 10.
- Interface + logic in Application; depends only on `ICompanyRepository`. `Radar.Application`
  keeps zero package references.
- Conservative resolution: exact normalized matches only; ambiguity and uncertainty resolve to
  unresolved. Never invent or infer a ticker.
- Preserve provenance: outputs carry confidence and a human-readable reason so a downstream
  `EvidenceMention` records *why* a company was (or was not) matched.
- Do not implement fuzzy matching, AI resolution, or `EvidenceMention` persistence here.

---

## Acceptance criteria

- [ ] `ICompanyResolver`, `CompanyResolver`, and `CompanyResolutionResult` exist under
      `Radar.Application.EntityResolution`.
- [ ] Exact name/alias matches resolve at confidence 1.0; exact ticker at 0.9; everything else
      (no match, ambiguous, empty) is unresolved with confidence 0 and a descriptive reason.
- [ ] Normalization handles case, whitespace, and a small common-suffix set; no substring or
      fuzzy matching occurs.
- [ ] Resolver is registered in Infrastructure DI without disturbing existing registrations.
- [ ] Tests cover resolved, ticker, unresolved, ambiguous, empty, and substring-rejection cases.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
