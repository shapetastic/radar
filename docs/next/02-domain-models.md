# Task: Domain Models and Enums

## Overview

Add the core domain records and enums to `Radar.Domain`, transcribed faithfully from
`docs/radar-schema-spec.md`. These are the immutable, provider-agnostic types that the whole
pipeline (evidence → signal → score → report) is built on. This task adds types and
lightweight validation only; no persistence, AI, or pipeline logic.

This is the foundation for repositories, collectors, extraction, scoring, and reporting, so it
comes immediately after the skeleton.

---

## Assignment

Worktree: any
Dependencies: 01-solution-skeleton
Conflicts with: None (only adds files under `src/Radar.Domain`)
Estimated time: ~1-2 hours

---

## Project structure changes

Add under `src/Radar.Domain/`, grouped by area to mirror the spec:

```text
Companies/
  Company.cs
  CompanyStatus.cs
  CompanyAlias.cs
Evidence/
  EvidenceItem.cs
  EvidenceSourceType.cs
  EvidenceQuality.cs
  EvidenceMention.cs
Signals/
  Signal.cs
  SignalType.cs
  SignalDirection.cs
  SignalReviewStatus.cs
  SignalReview.cs
  SignalReviewDecision.cs
Scoring/
  CompanyScoreSnapshot.cs
  ScoreEvidenceLink.cs
Reports/
  RadarReport.cs
  RadarReportItem.cs
  RadarReportAction.cs
Validation/
  SignalValidation.cs
```

Use a single root namespace convention: `Radar.Domain.Companies`, `Radar.Domain.Evidence`,
`Radar.Domain.Signals`, `Radar.Domain.Scoring`, `Radar.Domain.Reports`,
`Radar.Domain.Validation`.

---

## Implementation details

Transcribe the records and enums **exactly** as defined in `docs/radar-schema-spec.md`:

- `Company`, `CompanyStatus`, `CompanyAlias`
- `EvidenceItem`, `EvidenceSourceType`, `EvidenceQuality`, `EvidenceMention`
- `Signal`, `SignalType`, `SignalDirection`, `SignalReviewStatus`
- `SignalReview`, `SignalReviewDecision`
- `CompanyScoreSnapshot`, `ScoreEvidenceLink`
- `RadarReport`, `RadarReportItem`, `RadarReportAction`

Rules:

- All records are `public sealed record` with positional parameters as written in the spec.
- Use `Guid` for IDs, `DateTimeOffset` for timestamps, `decimal` for confidence/0-1 values,
  `int` for 1-10 and 0-100 ranges — match the spec types precisely.
- Do **not** add the AI structured-output records (`ExtractSignalsOutput`,
  `ReviewSignalsOutput`, etc.) here — those belong in the Application/AI layer in a later task,
  not in Domain.
- Keep each enum in its own file with members in the exact order from the spec (order matters
  if these are ever persisted as ints).

### Validation helper

Add a small, dependency-free validator so callers can enforce the spec's signal rules before
persistence. Domain stays free of exceptions-as-control-flow by returning a result rather than
throwing in normal flow, but a guard method is acceptable.

```csharp
namespace Radar.Domain.Validation;

public static class SignalValidation
{
    public static IReadOnlyList<string> Validate(Signal signal)
    {
        var errors = new List<string>();
        if (signal.Strength is < 1 or > 10)
            errors.Add("Strength must be between 1 and 10.");
        if (signal.Novelty is < 1 or > 10)
            errors.Add("Novelty must be between 1 and 10.");
        if (signal.Confidence is < 0m or > 1m)
            errors.Add("Confidence must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(signal.SupportingExcerpt))
            errors.Add("Supporting excerpt must not be empty.");
        if (signal.EvidenceId == Guid.Empty)
            errors.Add("Every signal must reference evidence.");
        return errors;
    }

    public static bool IsValid(Signal signal) => Validate(signal).Count == 0;
}
```

---

## Tests

Add to `Radar.Domain.Tests` (`SignalValidationTests.cs` and a small `DomainRecordsTests.cs`):

- A valid signal returns no validation errors / `IsValid == true`.
- Strength 0 and 11 each produce an error.
- Novelty 0 and 11 each produce an error.
- Confidence -0.1 and 1.1 each produce an error.
- Empty/whitespace `SupportingExcerpt` produces an error.
- `EvidenceId == Guid.Empty` produces the "must reference evidence" error.
- Record value equality: two `Company` records with identical fields are equal (sanity check
  that records were declared, not classes).

Remove the placeholder test added in task 01 from `Radar.Domain.Tests`.

---

## Constraints

- Target .NET 10.
- `Radar.Domain` must reference no other solution project and no third-party packages.
- Preserve provenance: every signal carries `EvidenceId`; do not add a parameterless path that
  lets a signal exist without evidence.
- Transcribe from the schema spec; do not invent extra fields or rename existing ones.

---

## Acceptance criteria

- [ ] All records/enums from the schema spec compile under `net10.0` in `Radar.Domain`.
- [ ] `Radar.Domain` has zero project/package references.
- [ ] `SignalValidation` enforces strength, novelty, confidence, excerpt, and evidence rules.
- [ ] Domain tests cover each validation rule plus record value-equality, and pass.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
