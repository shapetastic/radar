# Task: Trunk cleanup — one shared reader for the evidence-metadata envelope (`{ "metadata": {...}, "companyHints": [...] }`)

## Overview

This is a **pure cleanup / convergence slice**. It converts the single MEDIUM finding (**M1**) from the
`radar-architecture-reviewer` checkpoint on the trunk after the AI arc (specs 72–75; verdict CLEANUP, no
HIGH, and this M1 is the only finding NOT already covered by the decisions ledger AD-1…AD-11).

**The drift.** The evidence-metadata envelope shape `{ "metadata": {...}, "companyHints": [...] }` is
**authored in exactly one place** —
`src/Radar.Application/Collectors/CollectedEvidenceMapper.cs` (lines 49–51, the
`JsonSerializer.Serialize(new { metadata = …, companyHints = … })`) — but it is **re-parsed
independently in three consumers**, each hand-rolling `JsonDocument.Parse` + `TryGetProperty("metadata")`
+ defensive `ValueKind` checks, each returning a *different* shape:

1. `src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs` — `ReadMetadata(string?)`
   (lines 280–311) → `Dictionary<string,string>` (string properties only). **NEW in the AI arc — the
   third copy, which is why this crossed from LOW to MEDIUM.**
2. `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — `TryGetAwardAmount(EvidenceItem,
   out decimal)` (lines 288–332) → probes `metadata.awardAmount` for an invariant-culture positive decimal.
3. `src/Radar.Infrastructure/FileSystem/FileRawEvidenceStore.cs` — `ParseMetadataJson(string?)`
   (lines 130–171) → `(string[] CompanyHints, JsonElement Metadata)`.

**Why it matters.** One writer, three drifting readers, with no compile-time link between the shape the
mapper emits and the shape each reader assumes. The pattern is **compounding** — the AI arc added the
third copy. Each future consumer re-implements the same brittle traversal, and a change to the envelope
(e.g. a new top-level key, or nested non-string metadata values) would have to be found and updated in N
scattered places by grep.

**The fix (recommended by the reviewer).** Extract **one shared reader in `Radar.Application`**, next to
the mapper that authors the envelope, so writer and readers move together. All three call sites consume
it; the mapper stays the sole **author** of the envelope. Every call site keeps its **existing external
behaviour byte-identical** — the string dictionary, the decimal probe, and the `(hints, metadata)` tuple
are all *derived from* the one shared parse result rather than each re-parsing the raw JSON.

**There is NO behaviour change here.** Extracted signals (including the `GovernmentContract` Strength that
depends on the award amount), on-disk `RawEvidenceFile` JSON (hints + metadata), and the directional
filing gate must be **byte-identical** before and after this slice. This slice does **not** change scoring
output, so per **AD-10** it does **NOT** bump `ScoringEngine.ScoringConfigVersion` (it stays
`radar-scoring-config-v3`) — this is stated explicitly so the implementer does not wonder.

---

## Assignment

Worktree: any
Dependencies: specs 72–75 (the AI arc, incl. spec 75 which introduced the third copy
`DirectionalFilingSignalSource.ReadMetadata`) — all merged.
Conflicts with: touches the metadata author (`CollectedEvidenceMapper` — no logic change, only optional
XML-doc cross-reference), a new shared Application reader, and its three consumers
(`DirectionalFilingSignalSource`, `KeywordSignalExtractor`, `FileRawEvidenceStore`), plus tests. **Must
NOT run in parallel with any extractor / collector / filing / persistence slice — sequence it.**
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Application/Collectors/
  EvidenceMetadata.cs                                          # NEW: single shared envelope reader (author-adjacent)
  CollectedEvidenceMapper.cs                                   # UNCHANGED logic; OPTIONAL: one XML-doc cross-ref line

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs                                    # MODIFIED: TryGetAwardAmount derives from the shared reader

src/Radar.Infrastructure/Filings/
  DirectionalFilingSignalSource.cs                             # MODIFIED: ReadMetadata delegates to / is replaced by the shared reader

src/Radar.Infrastructure/FileSystem/
  FileRawEvidenceStore.cs                                      # MODIFIED: ParseMetadataJson derives from the shared reader

tests/Radar.Application.Tests/Collectors/
  EvidenceMetadataTests.cs                                     # NEW: unit-test the shared reader directly
```

`Radar.Domain` is unchanged. No DB (AD-8). No new package references.

---

## Implementation details

### 1 — Add the shared reader `EvidenceMetadata` (Application, next to the author)

Add `src/Radar.Application/Collectors/EvidenceMetadata.cs` in namespace `Radar.Application.Collectors`
(the same namespace as `CollectedEvidenceMapper`, the author of the envelope, so writer and reader live
together). It is the **single** place that knows the envelope shape
`{ "metadata": { <string→string> ... }, "companyHints": [ <string> ... ] }`.

**Recommended surface** — one `static` type that parses the envelope once and hands back both projections
the current callers need, so no caller re-parses:

```csharp
using System.Text.Json;

namespace Radar.Application.Collectors;

/// <summary>
/// The single reader for the evidence-metadata envelope authored by <see cref="CollectedEvidenceMapper"/>
/// (<c>{ "metadata": { ... }, "companyHints": [ ... ] }</c>). Every consumer of <c>EvidenceItem.MetadataJson</c>
/// reads through this type so the envelope's author (the mapper) and its readers move together, instead of
/// each hand-rolling <see cref="JsonDocument"/> traversal. Defensive at every hop: null/blank/malformed
/// JSON, a missing/mistyped root, or a mistyped <c>metadata</c>/<c>companyHints</c> node all degrade to an
/// empty result — this reader never throws on bad input (skip-don't-throw, mirroring the mapper's tolerance).
/// </summary>
public static class EvidenceMetadata
{
    /// <summary>
    /// Parses the envelope. <paramref name="metadata"/> is the flat <c>metadata</c> object projected to
    /// its <b>string-valued</b> properties (ordinal keys); <paramref name="hints"/> is the
    /// <c>companyHints</c> string array. Returns <c>true</c> when a well-formed envelope object was parsed,
    /// <c>false</c> for null/blank/malformed input (with both out-params set to empty). Callers that only
    /// need one projection may ignore the other.
    /// </summary>
    public static bool TryRead(
        string? metadataJson,
        out IReadOnlyDictionary<string, string> metadata,
        out IReadOnlyList<string> hints)
    { /* JsonDocument.Parse once; guard RootElement.ValueKind == Object; project metadata + companyHints */ }
}
```

Implementation notes (fold in the union of the three existing readers' defensiveness so each caller's
behaviour is preserved exactly):

- Parse `metadataJson` **once** with `JsonDocument.Parse` inside a `try`/`catch (JsonException)`; on any
  failure (or null/blank input) return `false` with `metadata = empty`, `hints = []`.
- **`metadata` projection:** only include properties whose `ValueKind == String` (matching
  `DirectionalFilingSignalSource.ReadMetadata`: `result[prop.Name] = prop.Value.GetString() ?? ""`). Use an
  **ordinal** comparer for the dictionary (that is what `ReadMetadata` uses, and `KeywordSignalExtractor`
  only does a case-sensitive `awardAmount` lookup — do not change key casing behaviour).
- **`hints` projection:** the `companyHints` array's string elements only (matching
  `FileRawEvidenceStore.ParseMetadataJson`: `Where(ValueKind == String).Select(GetString()!)`).
- Because `JsonDocument` is disposed at method exit, materialise both projections into owned collections
  (a `Dictionary<string,string>` and a `string[]`/`List<string>`) **before** disposal — do **not** hand
  back a live `JsonElement`. See the `FileRawEvidenceStore` migration note below for how its
  `JsonElement`-based caller changes.

> **Layering (AD-5) check.** `EvidenceMetadata` is a pure Application type using only `System.Text.Json`
> (BCL) — no package reference, no provider SDK. `Radar.Application` may hold it (Domain stays pure). Both
> the Application consumer (`KeywordSignalExtractor`) and the two Infrastructure consumers
> (`DirectionalFilingSignalSource`, `FileRawEvidenceStore`) reference Application, so all three can call it.

### 2 — `DirectionalFilingSignalSource` (Infrastructure) consumes the shared reader

- Delete the private `ReadMetadata(string?)` (lines 280–311) and its now-unneeded `using System.Text.Json;`
  **only if** no other use of `JsonDocument` remains in the file (the regex/`TryResolveFiling` logic stays).
- In `TryResolveFiling`, replace `var metadata = ReadMetadata(evidence.MetadataJson);` with
  `EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);` (hints unused here). The
  subsequent `metadata.TryGetValue("form", …)`, `metadata.TryGetValue("items", …)`, and
  `metadata.TryGetValue("accessionNumber", …)` lookups are unchanged — the shared reader returns the same
  ordinal string dictionary the private method did, so the `form == "8-K"` + item-`2.02` gate and the
  accession cross-check behave identically.
- Add `using Radar.Application.Collectors;` if not already present.

### 3 — `KeywordSignalExtractor` (Application) consumes the shared reader

- In `TryGetAwardAmount(EvidenceItem, out decimal amount)` (lines 288–332), replace the inline
  `JsonDocument.Parse` + `root.TryGetProperty("metadata", …)` + `metadata.TryGetProperty("awardAmount", …)`
  traversal with:
  `EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);` then
  `if (!metadata.TryGetValue("awardAmount", out var value)) { amount = 0m; return false; }`.
- **Keep the exact downstream numeric rules unchanged** (this is behaviour-critical because it feeds
  `GovernmentContract` Strength → scoring): the invariant-culture `decimal.TryParse` with
  `NumberStyles.Number`, the **non-positive → treat as absent** rule (`amount <= 0m` → `amount = 0m; return
  false;`), and the blank-value → false rule. Only the *JSON traversal* moves to the shared reader; the
  decimal probe stays byte-identical.
- Remove the `using System.Text.Json;` **only if** no other `System.Text.Json` use remains in the file
  (verify — the class may use it elsewhere; if so, keep the using). Add `using Radar.Application.Collectors;`
  (the extractor is in `Radar.Application.SignalExtraction`, a sibling namespace).
- **No logic changes** to `Rules`, the `GovernmentContractAmountTiers` tier table, `StrengthForAmount`, or
  any signal-emitting branch.

### 4 — `FileRawEvidenceStore` (Infrastructure) consumes the shared reader

- `ParseMetadataJson(string?)` (lines 130–171) currently returns `(string[] CompanyHints, JsonElement
  Metadata)` and `Serialize` builds `RawEvidenceFile` with `Metadata = metadata` (a `JsonElement`). The
  shared reader deliberately does **not** hand back a live `JsonElement`. Two acceptable options — pick the
  one that keeps `RawEvidenceFile`'s on-disk JSON **byte-identical**:
  - **(a) Preferred if `RawEvidenceFile.Metadata` is only ever serialized:** have `EvidenceMetadata`
    additionally expose the metadata as a serializable projection (e.g. the same
    `IReadOnlyDictionary<string,string>` used elsewhere), and change `RawEvidenceFile.Metadata`'s
    serialized form to that dictionary — **only if** it round-trips to the same JSON the current
    `JsonElement` produces for the string-valued metadata the mapper writes. Verify against the existing
    `FileRawEvidenceStore` tests' golden JSON; if the output differs (e.g. property ordering, or the store
    currently preserves non-string metadata values), do **not** use this option.
  - **(b) Zero-risk fallback:** keep `ParseMetadataJson` returning `(string[], JsonElement)` but have it
    call `EvidenceMetadata.TryRead` for the **`companyHints`** projection (the part that is pure string
    array and already identical), and retain a **minimal** local `JsonElement` clone only for the
    `metadata` node. This shares the hints logic (removing one of the two duplicated traversals) while
    guaranteeing the serialized `Metadata` element is untouched.
- **The on-disk `RawEvidenceFile` JSON must be byte-identical before and after** — the existing
  `FileRawEvidenceStore` tests are the gate. State in the PR which option was taken and why (e.g. "option
  (b): the store preserves the raw metadata element shape, so only the hints traversal was consolidated").
- Add `using Radar.Application.Collectors;`.

### 5 — `CollectedEvidenceMapper` (author) — optional doc-only cross-reference

No logic change. Optionally add one XML-doc sentence to the class summary noting that the envelope it
authors is read back through `EvidenceMetadata.TryRead`, so the shape's author and reader stay adjacent.
Do **not** change the `Serialize(new { metadata = …, companyHints = … })` call — the mapper remains the
sole author.

---

## Tests

- **New — `EvidenceMetadataTests` (`tests/Radar.Application.Tests/Collectors/`):** unit-test the shared
  reader directly:
  1. A well-formed envelope with string metadata + hints → `TryRead` returns `true`, the metadata
     dictionary has the string properties (ordinal keys, non-string values excluded), and the hints array
     matches.
  2. A well-formed envelope with a `metadata` node containing non-string values → those keys are excluded;
     string keys survive (matches the `ReadMetadata` string-only projection).
  3. Null / blank / malformed JSON → `TryRead` returns `false`, `metadata` empty, `hints` `[]` (no throw).
  4. Missing `metadata` node / missing `companyHints` node / wrong-kind nodes → empty projections, no throw.
  5. Round-trip: serialize an envelope exactly as `CollectedEvidenceMapper` does
     (`new { metadata = …, companyHints = … }`) and assert `TryRead` recovers the same metadata + hints —
     this is the compile-adjacent proof that author and reader agree.
- **Regression (must stay green UNCHANGED — this is the proof the refactor is behaviour-preserving):**
  - `KeywordSignalExtractor` tests covering `GovernmentContract` award-amount → Strength tiers (including
    the missing/blank/zero/negative → fixed Strength fallback).
  - `DirectionalFilingSignalSource` tests (spec 75) covering the `form == "8-K"` + item-`2.02` gate and the
    accession cross-check (these read the metadata dictionary).
  - `FileRawEvidenceStore` tests asserting the serialized `RawEvidenceFile` JSON (hints + metadata) — the
    byte-identical on-disk gate.
  If any of these referenced a now-deleted private method reflectively (they should not), update the call
  path; otherwise no edits to the existing tests.

---

## Constraints

- Target `net10.0`, C# 14.
- **This is a CLEANUP slice — NO new feature behaviour and NO scoring-output change.** Extracted signals
  (incl. `GovernmentContract` Strength), the directional-filing gate, and the on-disk `RawEvidenceFile`
  JSON must be **byte-identical** before and after. It therefore does **NOT** bump
  `ScoringEngine.ScoringConfigVersion` (stays `radar-scoring-config-v3`) — no AD-10 obligation is
  triggered.
- **The mapper stays the sole author** of the envelope; the new type is a **reader** only.
- **Layering (AD-5):** the shared reader lives in `Radar.Application` (pure BCL `System.Text.Json`, no
  package/provider SDK); Domain stays pure; the three consumers already reference Application.
- **Provenance preserved** — evidence → signal → score links are untouched; only the JSON-traversal plumbing
  is consolidated.
- Determinism, files-first (AD-8), no AI/provider SDK/DB in this slice. No advice language; AD-9 labels
  unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Out of scope / future (informational — do NOT plan or implement this round)

- **L2 — SEC HTTP-reader duplication.** `HttpSecEarningsReleaseReader` and `HttpSecFilingReader` duplicate
  the SEC URL construction / `StripLeadingZeros` CIK handling / 403-fetch handling. A future cleanup could
  extract a shared SEC-HTTP helper. **Not this round.**
- **L3 — Domain `FilingSentiment` doubling as the `GetResponseAsync<T>` DTO.** The Domain
  `FilingSentiment` record is currently reused as the AI structured-output DTO. A future slice could
  separate the wire/DTO shape from the Domain record. **Not this round.**

These are recorded only so the next planner has context; no work is planned for them here.

---

## Acceptance criteria

- [ ] A single `EvidenceMetadata` reader exists in `Radar.Application.Collectors` (next to
      `CollectedEvidenceMapper`, the envelope's author), exposing a `TryRead(string? metadataJson, out
      IReadOnlyDictionary<string,string> metadata, out IReadOnlyList<string> hints)` (or the maintainer-
      equivalent surface), defensive on null/blank/malformed input (never throws), projecting string-valued
      `metadata` properties (ordinal) and `companyHints` strings.
- [ ] All three consumers read the envelope **through** `EvidenceMetadata`:
      `DirectionalFilingSignalSource.ReadMetadata` is removed/delegated; `KeywordSignalExtractor.TryGetAwardAmount`
      derives its `awardAmount` from the shared reader (keeping the exact decimal/`<= 0m` rules);
      `FileRawEvidenceStore.ParseMetadataJson` derives its hints (and, per the chosen option, metadata) from
      the shared reader. No consumer retains its own `JsonDocument.Parse` + `TryGetProperty("metadata")`
      envelope traversal.
- [ ] The mapper (`CollectedEvidenceMapper`) remains the **sole author** of the
      `{ "metadata": …, "companyHints": … }` envelope; its serialize logic is unchanged.
- [ ] **Behaviour byte-identical:** extracted signals (incl. `GovernmentContract` Strength tiers), the
      directional-filing `2.02` gate + accession cross-check, and the serialized `RawEvidenceFile` JSON are
      unchanged — proven by the existing `KeywordSignalExtractor`, `DirectionalFilingSignalSource`, and
      `FileRawEvidenceStore` tests passing **unchanged**.
- [ ] New `EvidenceMetadataTests` cover well-formed / non-string-values / null-blank-malformed / missing-node
      cases and the author-round-trip.
- [ ] `ScoringEngine.ScoringConfigVersion` is **NOT** bumped (stays `radar-scoring-config-v3`); no AD-10
      obligation applies (no scoring-output change). Layering (AD-5) and determinism preserved.
- [ ] `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
