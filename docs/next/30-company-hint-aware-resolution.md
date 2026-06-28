# Task: Company-hint-aware resolution — use collector hints as a high-confidence path

## Overview

The master spec's resolution rules put collector hints **first**:

> 1. If a collector collected evidence from a company-specific feed, use that company as a
>    high-confidence hint.
> 2. If an alias appears in title or body, resolve to that company.

Today `CompanyResolver` matches only on the mention string (name/alias/ticker). The RSS collector
(slice 28) already attaches a `CompanyHint` (the bound feed's ticker) to each `CollectedEvidence`, and
slice 26 carries those hints alongside each new evidence in the runner. This slice lets the resolver
**use** those hints as a high-confidence, conservative path — without ever inventing a ticker.

---

## Assignment

Worktree: any
Dependencies: 26-collector-seam (hints carried in runner), 27-source-feeds, 28-rss-collector (hints set)
Conflicts with: 28, 29, 32 (all edit `RadarPipelineRunner`). Sequence after 28.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/EntityResolution/
  ICompanyResolver.cs     # MODIFIED: hint-aware overload
  CompanyResolver.cs      # MODIFIED: hint path (highest precedence)

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: pass evidence hints to resolver

tests/Radar.Application.Tests/EntityResolution/CompanyResolverTests.cs   # MODIFIED
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs       # MODIFIED
```

---

## Implementation details

### `ICompanyResolver` — hint-aware overload

Add an overload that accepts hints; keep the existing single-arg method (delegating with empty hints) so
current callers/tests are unaffected:

```csharp
Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct);   // existing

/// <summary>
/// Resolves a mention, preferring high-confidence collector hints (e.g. the ticker of a
/// company-specific feed). Hints are matched against known companies only — an unknown hint is
/// ignored, never fabricated into a company.
/// </summary>
Task<CompanyResolutionResult> ResolveAsync(
    string mentionText, IReadOnlyList<string> companyHints, CancellationToken ct);
```

### `CompanyResolver` — hint path (highest precedence, still conservative)

Before the existing name/alias/ticker logic, evaluate hints:
1. For each non-blank hint, match it against known companies by **exact** ticker (case-insensitive,
   as today) and by normalized name/alias (reuse the existing `Normalize`). Collect distinct matched
   company Ids.
2. If exactly **one** company matches across all hints → return
   `CompanyResolutionResult(companyId, 0.95m, "Company hint match", matchedHint)`. (High but below the
   1.0 exact-name confidence; a feed binding is strong but indirect.)
3. If hints match **more than one** distinct company → ambiguous: do **not** trust the hints; fall
   through to the existing mention-based logic (which may itself resolve or return unresolved). Log a
   Debug note.
4. If no hint matches a known company → fall through to existing logic unchanged.

Do not change any existing matching behaviour; hints are a new, additive, highest-precedence path. A
hint that names an unknown company is ignored (conservative — never hallucinate a ticker/company).

### `RadarPipelineRunner`

In the extraction loop, the runner already has each new evidence's `CompanyHints` (carried since slice
26). Replace the resolve call with the hint-aware overload:

```csharp
var resolution = await _resolver
    .ResolveAsync(signal.CompanyMention, hints, ct)   // hints from the evidence's CollectedEvidence
    .ConfigureAwait(false);
```

Everything downstream (only set `CompanyId` when matched; unresolved → human review) is unchanged.

---

## Tests

### `CompanyResolverTests` (MODIFIED)
- **Hint resolves**: a hint equal to a seeded company's ticker resolves to that company at confidence
  `0.95` with reason "Company hint match", even when the mention text alone would not match.
- **Hint precedence**: when the hint points to company A but the mention text matches company B, the
  single unambiguous hint wins (A) — document this as intended (feed binding is authoritative).
- **Ambiguous hints ignored**: hints matching two different companies fall through to mention-based
  logic (no guess).
- **Unknown hint ignored**: a hint naming no known company falls through; a mention that matches still
  resolves, otherwise unresolved.
- **Empty hints == old behaviour**: the overload with `[]` returns exactly the single-arg result.

### `RadarPipelineRunnerTests` (MODIFIED)
- Evidence carrying a company hint (from a fake collector) resolves its signal to the hinted company even
  when the mention text is generic — verifying the runner threads hints to the resolver.

---

## Constraints

- Target .NET 10. Resolution stays deterministic and conservative: exact/normalized matching only, no
  fuzzy/substring/AI matching; ambiguity and unknown hints never produce a guess (AD: "never hallucinate
  tickers").
- Hints are matched against **known** companies only (the seed universe via `ICompanyRepository`).
- Preserve provenance: `CompanyResolutionResult.Reason` records when a hint drove the match.
- Additive change — the existing single-arg `ResolveAsync` and its tests keep passing.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `ICompanyResolver` has a hint-aware overload; the single-arg overload still works (empty hints).
- [ ] A single unambiguous hint matching a known company resolves at `0.95` with reason
      "Company hint match", taking precedence over mention-only matching.
- [ ] Ambiguous or unknown hints fall through to existing mention-based logic — never a fabricated match.
- [ ] `RadarPipelineRunner` passes each evidence's collector hints to the resolver.
- [ ] Tests cover hint-resolves, precedence, ambiguous-ignored, unknown-ignored, empty-hints parity, and
      runner threading; `build`/`test` green.
