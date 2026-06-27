# Task: Deterministic Keyword Signal Extractor

## Overview

Add the first concrete `ISignalExtractor`: a deterministic, keyword-based "fake" extractor so the
whole pipeline (collect -> extract -> validate -> store) runs offline, with no AI and no provider
SDK. This is the Stage 4 fake extractor the master spec calls for ("Signal extraction interface and
fake extractor"), letting tests assert real, reproducible behaviour before any LLM exists.

The extractor scans an evidence item's title and normalized body for a small, fixed table of
phrases mapped to the MVP `SignalType`s and emits typed `ExtractedSignal`s with a real excerpt taken
from the matched text (provenance). It performs **no entity resolution** — that is deliberately the
real AI extractor's job (a later, human-owned slice). To stay deterministic and honest, it uses the
evidence `SourceName` as the `CompanyMention` placeholder and never guesses a company or ticker.

This task is mechanical and deterministic. It does **not** introduce the real `Microsoft.Extensions.AI`
extractor or any prompt — that is explicitly reserved for a later, human-driven slice.

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models, 04-evidence-normalization-and-hashing,
09-signal-extraction-contract-and-validation
Conflicts with: 11-deterministic-signal-review (both edit
`InfrastructureServiceCollectionExtensions.cs` -> `AddRadarApplicationServices`) — sequence after
this task
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  SignalExtraction/
    KeywordSignalExtractor.cs
    KeywordSignalRule.cs            # internal phrase -> signal rule

src/Radar.Infrastructure/
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: register ISignalExtractor

tests/Radar.Application.Tests/
  SignalExtraction/
    KeywordSignalExtractorTests.cs
```

Namespace: `Radar.Application.SignalExtraction`. The extractor is pure/deterministic (no I/O, no
provider SDK), so it lives in **Application** alongside `CompanyResolver`, not in Infrastructure.
`Radar.Application` keeps **zero package references**.

---

## Implementation details

### Rule record (internal)

```csharp
namespace Radar.Application.SignalExtraction;

using Radar.Domain.Signals;

internal sealed record KeywordSignalRule(
    string Phrase,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence);
```

### Rule table

Define a small, ordered, documented `static readonly KeywordSignalRule[]` covering the MVP signal
types. Phrases are matched case-insensitively as substrings of the search text. Suggested starter
set (the coder may refine wording but keep it small and conservative):

| Phrase (examples)                                   | Type                 | Direction | Str | Nov | Conf |
|-----------------------------------------------------|----------------------|-----------|-----|-----|------|
| `"multi-year deal"`, `"selected by"`, `"deployment"`| CustomerWin          | Positive  | 6   | 5   | 0.6  |
| `"partnership"`, `"partners with"`, `"teams up"`    | StrategicPartnership | Positive  | 5   | 5   | 0.6  |
| `"appoints"`, `"names new"`, `"hires"`              | ExecutiveHire        | Positive  | 4   | 5   | 0.5  |
| `"launches"`, `"unveils"`, `"introduces"`           | ProductLaunch        | Positive  | 5   | 6   | 0.6  |
| `"raises $"`, `"funding round"`, `"series "`        | CapitalRaise         | Positive  | 5   | 5   | 0.6  |
| `"raises guidance"`, `"raises outlook"`             | GuidanceChange       | Positive  | 6   | 6   | 0.65 |
| `"cuts guidance"`, `"lowers guidance"`              | GuidanceChange       | Negative  | 6   | 6   | 0.65 |
| `"government contract"`, `"awarded contract"`       | GovernmentContract   | Positive  | 6   | 5   | 0.6  |

All values are within domain ranges (Strength/Novelty 1-10, Confidence 0-1) so mapped signals pass
`SignalValidation`. Keep the numbers as plain, visible constants — these are placeholder heuristics
for offline testing, **not** a tuned scoring model.

### Extractor (`KeywordSignalExtractor : ISignalExtractor`)

- Constructor takes nothing AI-related; it is stateless. (No DI dependencies required.)
- `ExtractAsync(EvidenceItem evidence, CancellationToken ct)`:
  1. `ArgumentNullException.ThrowIfNull(evidence)`.
  2. Build a single search string: `evidence.Title + "\n" + evidence.RawText`, and a lowercased copy
     for matching (culture-invariant).
  3. Iterate the rule table in order. For each rule whose `Phrase` (lowercased) is found in the
     search text, record a match **once per `SignalType`** — the first matching rule for a given
     `SignalType` wins; later rules for an already-emitted type are skipped (deterministic dedupe).
  4. For each emitted type, build an `ExtractedSignal`:
     - `CompanyMention = evidence.SourceName` (placeholder; this extractor does not extract company
       names — documented limitation).
     - `SignalType = rule.Type.ToString()`, `Direction = rule.Direction.ToString()` (round-trip
       through the string contract on purpose, so the mapper in task 09 is exercised).
     - `Strength`/`Novelty`/`Confidence` from the rule.
     - `SupportingExcerpt =` a deterministic window of the **original-cased** search text around the
       first match of the phrase (e.g. up to ~80 chars before and after, trimmed to word-ish
       boundaries is optional; a fixed character window is fine). The excerpt MUST be a verbatim
       slice of the search text so it survives task 09's excerpt-in-evidence provenance check.
     - `Reason =` a short fixed string naming the matched phrase, e.g. `$"Matched phrase '{phrase}'"`.
  5. Order the emitted signals deterministically by `SignalType` (enum order) then by the match
     index, so output ordering is stable (consistent with AD-3's determinism convention).
  6. Return `new ExtractSignalsOutput(signals, overallSummary)` where `overallSummary` is a short
     deterministic string such as `$"{signals.Count} signal(s) extracted by keyword rules."`.
  7. Honour `ct` via `ct.ThrowIfCancellationRequested()` at the top; the method is synchronous work
     wrapped in a completed task (`return Task.FromResult(...)`).

Because the excerpt is sliced from `Title + "\n" + RawText` and the body is already the normalizer's
output, task 09's whitespace/case-insensitive provenance check will pass for excerpts drawn from the
body; excerpts drawn from the title are also part of the canonical hashed content and acceptable as
provenance for the signal. (If the coder prefers to guarantee body-only provenance, restrict the
search/excerpt to `evidence.RawText`; either is acceptable as long as the mapper round-trip passes.)

### DI

Extend `InfrastructureServiceCollectionExtensions.AddRadarApplicationServices` to also register:

```csharp
services.AddSingleton<ISignalExtractor, KeywordSignalExtractor>();
```

The extractor is stateless and depends on nothing, so a singleton is correct. Do not add a new
public method unless cleaner; keep the existing `AddRadarApplicationServices` as the single
app-services entry point. Leave `AddInMemoryRadarPersistence` and `AddLocalFileCollector` untouched.

---

## Tests

`Radar.Application.Tests/SignalExtraction/KeywordSignalExtractorTests.cs` (xUnit). Construct
`EvidenceItem`s inline (no collector needed). Cases:

- Evidence whose body contains `"multi-year deal"` yields exactly one `CustomerWin` `ExtractedSignal`
  with `Direction == "Positive"` and a `SupportingExcerpt` that is a verbatim slice of the body.
- Evidence mentioning both a partnership phrase and a product-launch phrase yields two signals, one
  per type, in stable (enum) order.
- Evidence with no known phrase yields an empty `Signals` list and a non-null summary.
- Two phrases that map to the **same** `SignalType` (e.g. `"raises guidance"` plus another guidance
  phrase) yield a single deduped signal.
- A negative-direction phrase (`"cuts guidance"`) yields `Direction == "Negative"`.
- **Round-trip / integration:** for each emitted `ExtractedSignal`, calling
  `ExtractedSignalMapper.ToSignal(extracted, evidence, fixedClock)` from task 09 returns a valid
  `Signal` (`IsValid == true`) whose `EvidenceId == evidence.Id` and `ReviewStatus == Pending`. This
  proves the extractor's output satisfies the validation contract.
- Determinism: calling `ExtractAsync` twice on the same evidence yields equal type/direction/excerpt
  sequences.

Optionally add one end-to-end style test (still Application-only): build an `EvidenceItem`, extract,
map each signal, and store the valid ones in an `InMemorySignalRepository` (permitted via AD-4),
asserting they are retrievable by `GetByCompanyAsync`/`GetObservedBetweenAsync` semantics where
applicable. Keep it lightweight.

---

## Constraints

- Target .NET 10.
- Deterministic, pure extractor in **Application**; zero package references; BCL only.
- No AI, no provider SDK, no prompt, no `Microsoft.Extensions.AI` — those belong to a later,
  human-owned slice.
- No entity resolution / ticker inference — `CompanyMention` is the source name placeholder; do not
  guess company identity.
- Preserve provenance: every `ExtractedSignal` carries a verbatim excerpt from the evidence text and
  a reason naming the matched phrase.
- Stable output ordering (AD-3). No `DateTime.Now`, no randomness in emitted fields.
- Keep the rule table small and visibly constant — this is a placeholder heuristic, not a tuned
  model.

---

## Acceptance criteria

- [ ] `KeywordSignalExtractor` implements `ISignalExtractor` in `Radar.Application.SignalExtraction`
      and is registered via `AddRadarApplicationServices`.
- [ ] A fixed, documented keyword rule table covers the MVP signal types and stays within domain
      ranges.
- [ ] Output is deterministic, deduped per `SignalType`, stably ordered, with verbatim excerpts and
      `SourceName` as the placeholder `CompanyMention`.
- [ ] Every emitted `ExtractedSignal` maps to a valid `Signal` via task 09's
      `ExtractedSignalMapper` (round-trip test).
- [ ] `Radar.Application` has no package references; `AddInMemoryRadarPersistence` and
      `AddLocalFileCollector` are unchanged.
- [ ] Tests cover single/multiple/none/duped matches, negative direction, the mapper round-trip, and
      determinism.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
