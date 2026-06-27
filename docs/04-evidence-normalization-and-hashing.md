# Task: Evidence Normalization and Content Hashing

## Overview

Add a deterministic, dependency-free service that normalizes raw source text and computes a
stable content hash for an evidence item. This is Stage 2 (Evidence Normalization) of the master
pipeline. It turns messy raw input into clean, comparable text and produces the `ContentHash`
that the evidence store already uses to reject duplicates (`IEvidenceRepository.AddIfNewAsync`).

This is a pure function with no I/O, no AI, and no persistence. It is the prerequisite for the
local file collector (next task), which must call it to build `EvidenceItem` records. Putting it
in its own slice keeps the collector small and makes the hashing rules independently testable.

---

## Assignment

Worktree: any
Dependencies: 02-domain-models
Conflicts with: None (adds files under `src/Radar.Application` and `tests/Radar.Application.Tests`)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Evidence/
    IEvidenceNormalizer.cs
    EvidenceNormalizer.cs
    NormalizedEvidence.cs

tests/Radar.Application.Tests/
  Evidence/
    EvidenceNormalizerTests.cs
```

Namespace: `Radar.Application.Evidence`.

`Radar.Application` keeps **zero package references** (Domain reference only). Use only the BCL
(`System.Security.Cryptography`, `System.Text`). Do **not** add a DI registration in this task —
the service is constructed directly in tests and will be wired up by the collector task.

---

## Implementation details

### Result record

```csharp
namespace Radar.Application.Evidence;

public sealed record NormalizedEvidence(string NormalizedText, string ContentHash);
```

### Interface

```csharp
namespace Radar.Application.Evidence;

public interface IEvidenceNormalizer
{
    NormalizedEvidence Normalize(string title, string rawText);
}
```

### Implementation (`EvidenceNormalizer`)

Deterministic, culture-invariant, allocation-light:

- Guard: `ArgumentNullException.ThrowIfNull` on `rawText`; treat a null/empty `title` as an empty
  string rather than throwing.
- Normalization steps on the body text, in order:
  1. Normalize line endings (`\r\n` and `\r` to `\n`).
  2. Trim trailing whitespace from each line.
  3. Collapse runs of three or more blank lines down to a single blank line.
  4. Collapse runs of spaces/tabs within a line to a single space.
  5. Trim the overall result (leading/trailing whitespace).
- `NormalizedText` is the cleaned **body** only.
- `ContentHash` is computed over the canonical string `normalizedTitle + "\n" + normalizedText`,
  where `normalizedTitle` is the title trimmed and inner-whitespace-collapsed the same way. Use
  `SHA256` over the UTF-8 bytes and return **lowercase hex** (64 chars). Use
  `Convert.ToHexStringLower` if available, otherwise lowercase `Convert.ToHexString`.
- The method must be a pure function: same inputs always produce the same `ContentHash`.

Rationale for hashing title+body: two evidence items that differ only in title are distinct, but
re-collecting the identical article produces the same hash so the repository dedupes it.

---

## Tests

Add `Radar.Application.Tests/Evidence/EvidenceNormalizerTests.cs` (xUnit). Remove the
`PlaceholderTests.cs` from `Radar.Application.Tests`.

- Identical inputs produce identical `ContentHash` (determinism).
- Inputs that differ only in trailing whitespace / line endings / extra blank lines produce the
  **same** `ContentHash` (normalization makes them equal).
- Different body text produces a different `ContentHash`.
- Different title with identical body produces a different `ContentHash`.
- `ContentHash` is 64 lowercase hex characters.
- `NormalizedText` collapses `"a\r\n\r\n\r\n\r\nb"` style gaps and multi-space runs as specified.
- `Normalize` with a null title does not throw and still hashes deterministically.
- `Normalize` with null `rawText` throws `ArgumentNullException`.

Tests construct `EvidenceNormalizer` directly. No persistence, no AI, no clock.

---

## Constraints

- Target .NET 10.
- `Radar.Application` must keep zero package references; BCL only.
- Pure and deterministic: no I/O, no `DateTime.Now`, no randomness.
- Preserve provenance: this task only computes normalized text + hash; it must not strip the
  title or source information that callers will attach to `EvidenceItem`.
- Keep scope tight — do not build the collector or touch persistence here.

---

## Acceptance criteria

- [ ] `IEvidenceNormalizer`, `EvidenceNormalizer`, and `NormalizedEvidence` exist under
      `Radar.Application.Evidence`.
- [ ] `ContentHash` is a deterministic 64-char lowercase SHA-256 hex of canonical title+body.
- [ ] Whitespace/line-ending/blank-line differences hash equal; body or title differences hash
      differently.
- [ ] `Radar.Application` has no package references.
- [ ] Placeholder test removed; new normalizer tests cover the cases above.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
