# Task: Signal Extraction Contract and ExtractedSignal Validation

## Overview

Add the provider-agnostic Stage 4 (Signal Extraction) contract plus the deterministic mapping and
validation that turns an extractor's typed output into a domain `Signal`. This is the seam every
extractor (the deterministic fake in task 10, the real `Microsoft.Extensions.AI` extractor much
later) must implement, and the guard that ensures no AI output is ever trusted into the domain
without validation.

This slice is **pure**: interface + records + a static mapper/validator. There is no AI, no
provider SDK, no persistence, and no extractor implementation yet. Defining the contract and the
validation in their own slice keeps the fake extractor (task 10) small and makes the
ExtractedSignal-to-Signal rules independently testable. It directly satisfies the master rule that
"AI outputs must be typed records and validated before persistence" and preserves provenance by
anchoring every produced `Signal` to its source `EvidenceItem`.

This task does **not** decide the scoring formula or any AI prompt; those are deferred and are
explicitly reserved for human input in later slices.

---

## Assignment

Worktree: any
Dependencies: 02-domain-models
Conflicts with: None (adds files under `src/Radar.Application/SignalExtraction` and
`tests/Radar.Application.Tests`; no DI changes)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  SignalExtraction/
    ExtractedSignal.cs
    ExtractSignalsOutput.cs
    ISignalExtractor.cs
    SignalMappingResult.cs
    ExtractedSignalMapper.cs

tests/Radar.Application.Tests/
  SignalExtraction/
    ExtractedSignalMapperTests.cs
```

Namespace: `Radar.Application.SignalExtraction`.

`Radar.Application` keeps **zero package references** (Domain reference only). Use only the BCL.
No DI registration in this task — the mapper is `static` and the interface is implemented in task 10.

---

## Implementation details

### Output records (mirror the schema spec's AI structured-output schema)

```csharp
namespace Radar.Application.SignalExtraction;

public sealed record ExtractedSignal(
    string CompanyMention,
    string SignalType,
    string Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason);

public sealed record ExtractSignalsOutput(
    IReadOnlyList<ExtractedSignal> Signals,
    string OverallSummary);
```

`SignalType` and `Direction` are deliberately **strings** here — they are the raw, untrusted shape
an extractor (including a future LLM) returns. The mapper is responsible for parsing and rejecting
unknown values.

### Interface

```csharp
namespace Radar.Application.SignalExtraction;

using Radar.Domain.Evidence;

public interface ISignalExtractor
{
    Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct);
}
```

One evidence item in, a typed (possibly empty) set of candidate signals out. Returning empty
`Signals` is valid and expected when nothing meaningful is present.

### Mapping result

```csharp
namespace Radar.Application.SignalExtraction;

using Radar.Domain.Signals;

public sealed record SignalMappingResult(Signal? Signal, IReadOnlyList<string> Errors)
{
    public bool IsValid => Signal is not null;
}
```

When valid, `Signal` is non-null and `Errors` is empty; when invalid, `Signal` is null and `Errors`
explains why (so the caller can log/skip without throwing).

### Mapper / validator (`ExtractedSignalMapper`, static)

```csharp
public static SignalMappingResult ToSignal(
    ExtractedSignal extracted,
    EvidenceItem evidence,
    DateTimeOffset createdAtUtc);
```

Deterministic, culture-invariant, pure (clock is passed in, not read). Steps:

1. `ArgumentNullException.ThrowIfNull` on `extracted` and `evidence`.
2. Parse `extracted.SignalType` with `Enum.TryParse<SignalType>(value, ignoreCase: true, out var type)`.
   An unparseable/unknown value is an **error** ("Unknown signal type '…'") — never silently map to
   `SignalType.Other` (do not invent meaning).
3. Parse `extracted.Direction` the same way into `SignalDirection`; unknown value is an error.
4. Trim `CompanyMention`; empty is an error ("Company mention must not be empty"). Do **not**
   resolve a company here — `CompanyId` is always `null` at extraction time (entity resolution is a
   separate, already-existing concern). Carry the trimmed mention onto the `Signal`.
5. Trim `SupportingExcerpt`; empty is an error. Enforce provenance: the trimmed excerpt must appear
   in `evidence.RawText` when both are compared **whitespace-insensitively and case-insensitively**
   (collapse runs of whitespace to a single space, `ToLowerInvariant`, then `Contains`). If absent,
   add error "Supporting excerpt not found in evidence" — this prevents fabricated quotes from
   entering the domain.
6. Compute `ObservedAtUtc = evidence.PublishedAtUtc ?? evidence.CollectedAtUtc`.
7. Build a candidate `Signal`:
   - `Id = Guid.NewGuid()`
   - `EvidenceId = evidence.Id`
   - `CompanyId = null`
   - `CompanyMention =` trimmed mention
   - `Type`, `Direction =` parsed values
   - `Strength`, `Novelty`, `Confidence =` raw values (range-checked next)
   - `SupportingExcerpt =` trimmed excerpt
   - `Reason = extracted.Reason ?? string.Empty` (trimmed; may be empty)
   - `ReviewStatus = SignalReviewStatus.Pending`
   - `ObservedAtUtc =` computed above
   - `CreatedAtUtc = createdAtUtc`
8. Run domain `Radar.Domain.Validation.SignalValidation.Validate(candidate)` and **merge** its
   errors (this enforces Strength 1-10, Novelty 1-10, Confidence 0-1, non-empty excerpt, evidence
   reference). Do not duplicate those range checks in the mapper — reuse the domain validator as the
   single source of truth.
9. If there are any errors, return `new SignalMappingResult(null, errors)`; otherwise
   `new SignalMappingResult(candidate, [])`.

Keep all error strings stable and human-readable; tests assert on a non-empty `Errors` collection
rather than exact text where practical.

---

## Tests

`Radar.Application.Tests/SignalExtraction/ExtractedSignalMapperTests.cs` (xUnit). Construct an
`EvidenceItem` inline (with a known `RawText`, `Id`, `PublishedAtUtc`, `CollectedAtUtc`) and call the
static mapper with a fixed `createdAtUtc`. Cases:

- Valid `ExtractedSignal` whose excerpt is a substring of the evidence body maps to a `Signal` with:
  `EvidenceId == evidence.Id`, `CompanyId == null`, parsed `Type`/`Direction`,
  `ReviewStatus == Pending`, `CreatedAtUtc ==` the supplied clock, and
  `ObservedAtUtc == evidence.PublishedAtUtc`.
- When `PublishedAtUtc` is null, `ObservedAtUtc == evidence.CollectedAtUtc`.
- Case-insensitive enum parsing: `"customerwin"` / `"POSITIVE"` parse successfully.
- Unknown `SignalType` string → invalid, `Signal` null, `Errors` non-empty.
- Unknown `Direction` string → invalid.
- Excerpt not present in evidence body → invalid ("not found in evidence").
- Excerpt present but differing only in whitespace/casing vs the body → **valid** (provenance check
  is whitespace/case-insensitive).
- Empty/whitespace `CompanyMention` → invalid.
- Out-of-range `Strength` (0 or 11), `Novelty`, or `Confidence` (e.g. 1.5m) → invalid (errors come
  from `SignalValidation`).
- Determinism: same inputs + same `createdAtUtc` produce equal field values (ignoring the random
  `Id`).

No persistence, no AI, no real clock.

---

## Constraints

- Target .NET 10.
- `Radar.Application` must keep zero package references; BCL only.
- Pure and deterministic: the clock is a parameter; no `DateTime.Now`, no randomness beyond the
  unobserved `Guid.NewGuid()` for `Id`.
- Preserve provenance: every produced `Signal` references its evidence and carries an excerpt proven
  to exist in that evidence. Never invent a company, ticker, or signal type.
- Reuse `SignalValidation` for range/required-field checks — do not reimplement them.
- Keep scope tight — no extractor implementation, no DI, no review/scoring logic here.

---

## Acceptance criteria

- [ ] `ISignalExtractor`, `ExtractedSignal`, `ExtractSignalsOutput`, `SignalMappingResult`, and
      `ExtractedSignalMapper` exist under `Radar.Application.SignalExtraction`.
- [ ] `ExtractedSignalMapper.ToSignal` parses string type/direction case-insensitively, rejects
      unknown values, enforces the excerpt-in-evidence provenance check, and merges
      `SignalValidation` errors.
- [ ] Valid output maps to an immutable `Signal` with `CompanyId == null`,
      `ReviewStatus == Pending`, evidence reference set, and `CreatedAtUtc` from the passed clock.
- [ ] `Radar.Application` has no package references.
- [ ] Tests cover valid mapping, both `ObservedAtUtc` paths, case-insensitive parsing, unknown
      type/direction, the excerpt provenance check (positive and negative), empty mention, and
      out-of-range numeric fields.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
