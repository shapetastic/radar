# Task: Strongly-Typed Collector Source-Type Seam

## Overview

The periodic architecture checkpoint (post slices 26–32) found one MEDIUM cross-slice drift (M-1):
the source-type vocabulary is split across the collector seam as free strings. Collectors declare a
canonical token via `IEvidenceCollector.SourceType` as a `string` (`LocalFileEvidenceCollector` →
`"local_file"`, `RssPressReleaseCollector` → `"press_release"`), and `CollectedEvidenceMapper`
translates that string to `EvidenceSourceType` through a hand-maintained, case-insensitive table that
**silently defaults any unrecognised token to `EvidenceSourceType.Manual`** (logged only at Debug,
`CollectedEvidenceMapper.cs:72-93`).

This is a provenance-attribution erosion. A new collector whose token has a typo, or simply isn't in
the table, has **every** item silently mis-attributed to `Manual` — which then feeds the
`EvidenceConfidenceScore` diversity factor (AD-6) and the reviewer's weak-source rule. "Provenance is
sacred": source-type attribution must not be able to fail silently.

This slice converges the seam by making it **strongly typed**: the collector declares an
`EvidenceSourceType` directly, the same type flows through `CollectedEvidence`, and the mapper copies
it across with no string parsing. The collector→evidence source-type link becomes compile-checked, the
hand-maintained string table is deleted, and the "unknown token → Manual" failure mode is removed by
construction. This is pure convergence cleanup — no new features, no new public types beyond the type
change on two existing members.

Two co-located LOW findings are folded in because they touch the same seam files and are trivial:
**L-1** (rename the three collector-seam `cancellationToken` parameters to `ct`, matching the rest of
the tree) and **L-2** (seal `Radar.Worker/Worker.cs`).

> Out of scope (do **not** touch this round): L-3 (doc wording) and L-4 (a dedupe edge case) were
> flagged as informational / for the per-spec reviewer.

This does **not** disturb AD-7 or AD-8. AD-7 #1 — evidence *quality* stays a declared, document-level
input parsed by the mapper (`ParseQuality`), defaulting to `Unknown`; that string parsing is unchanged.
Only *source-type*, which is **collector-declared** (not document-declared), becomes typed.
`LocalFileEvidenceCollector`'s source type stays `LocalFile` and `RssPressReleaseCollector`'s stays
`PressRelease`. AD-8 (collector-driven, files-first MVP) is unaffected.

---

## Assignment

Worktree: any
Dependencies: None (specs 26–32 are merged and promoted to `docs/`)
Conflicts with: None — modifies the existing collector seam files, the mapper, `Worker.cs`, and their
tests. Touches `IEvidenceCollector.cs` / `CollectedEvidence.cs` (shared Application contracts), so
sequence it alone; do not parallelize with any other collector/mapper work.
Estimated time: ~1-2 hours

---

## Project structure changes

Modify only (no new production files):

```text
src/Radar.Application/Collectors/
  IEvidenceCollector.cs            # SourceType: string -> EvidenceSourceType (M-1); ct rename (L-1)
  CollectedEvidence.cs             # SourceType: string -> EvidenceSourceType (M-1)
  CollectedEvidenceMapper.cs       # delete ResolveSourceType table; copy enum across (M-1)

src/Radar.Infrastructure/Sources/
  LocalFileEvidenceCollector.cs    # SourceType -> EvidenceSourceType.LocalFile (M-1); ct rename (L-1)

src/Radar.Infrastructure/Rss/
  RssPressReleaseCollector.cs      # SourceType -> EvidenceSourceType.PressRelease (M-1); ct rename (L-1)

src/Radar.Worker/
  Worker.cs                        # seal the class (L-2)

tests/Radar.Application.Tests/Collectors/
  CollectedEvidenceMapperTests.cs  # update source-type tests for the typed seam

tests/Radar.Infrastructure.Tests/Sources/
  LocalFileEvidenceCollectorTests.cs   # assert EvidenceSourceType.LocalFile

tests/Radar.Infrastructure.Tests/Rss/
  RssPressReleaseCollectorTests.cs     # assert EvidenceSourceType.PressRelease
```

`Radar.Application` already references `Radar.Domain`, so `EvidenceSourceType` is available to the
seam types — add `using Radar.Domain.Evidence;` where needed. No project-reference or DI-registration
changes are required.

---

## Implementation details

### M-1 — Make the source-type seam strongly typed

1. **`IEvidenceCollector.SourceType`** — change the property type from `string` to
   `EvidenceSourceType`. Update the XML doc to describe it as the canonical
   `EvidenceSourceType` this collector attributes its evidence to (provenance / logging), e.g.:

   > Canonical `EvidenceSourceType` this collector attributes its emitted evidence to. Strongly typed
   > so a collector cannot silently mis-declare its provenance.

2. **`CollectedEvidence.SourceType`** — change the positional record member from `string` to
   `EvidenceSourceType`. Update the `<summary>` reference to source-type "resolution" (it is now a
   straight carry-through, not a resolution step).

3. **`CollectedEvidenceMapper`** — delete the entire `ResolveSourceType(string?)` method and its
   string table. In `ToEvidenceItem`, set the source type directly from the carried value:

   ```csharp
   var sourceType = collected.SourceType;
   ```

   Remove the now-dead `default → Manual` Debug log. Update the class `<summary>` so it no longer
   claims to perform `EvidenceSourceType` resolution (it still centralises normalization, hashing, and
   quality parsing). Do **not** touch `ParseQuality` — quality remains a declared, defaulting input
   (AD-7).

4. **`LocalFileEvidenceCollector`** — change `public string SourceType => "local_file";` to
   `public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;`. It already passes
   `SourceType: SourceType` into the `CollectedEvidence` it builds — that now carries the enum
   unchanged.

5. **`RssPressReleaseCollector`** — change `public string SourceType => "press_release";` to
   `public EvidenceSourceType SourceType => EvidenceSourceType.PressRelease;`. Same carry-through.

6. **`FileRawEvidenceStore` is intentionally NOT changed.** It already derives the on-disk
   `sourceType` token and folder deterministically from the `EvidenceSourceType` enum
   (`ToSnakeCase` / `SourceTypeFolder`). With the seam typed, the enum is now the single source of
   truth from collector → mapper → store; the store's enum-driven rendering is correct and in scope to
   leave alone.

> Note on the alternative (do not implement): the reviewer's fallback was to keep the string token but
> raise the unknown-token log to Warning and add a startup/test assertion that every registered
> collector's token resolves to a non-`Manual` enum. The strongly-typed approach above is preferred
> because it removes the failure mode at compile time rather than detecting it at runtime; it is
> recorded here only for context.

### L-1 — Consistent cancellation-token parameter name

Rename the `cancellationToken` parameter to `ct` on the three collector-seam `CollectAsync` methods so
they match the prevailing convention in the rest of the tree (`FileRawEvidenceStore.WriteIfNewAsync`,
`LocalFileEvidenceCollector.ReadDocumentAsync`, etc. already use `ct`):

- `IEvidenceCollector.CollectAsync`
- `LocalFileEvidenceCollector.CollectAsync` (update the in-method `cancellationToken.ThrowIfCancellationRequested()` and the `ReadDocumentAsync(path, cancellationToken)` call site)
- `RssPressReleaseCollector.CollectAsync` (update the in-method `cancellationToken.ThrowIfCancellationRequested()` and the `_reader.ReadAsync(feed.Url, cancellationToken)` call site)

Behaviour is unchanged; this is a rename only.

### L-2 — Seal the Worker

Change `public class Worker : BackgroundService` to `public sealed class Worker : BackgroundService`
in `src/Radar.Worker/Worker.cs` (every other sealable type in the tree is sealed; spec 07 sealed an
earlier Worker that was later rewritten). No other Worker change.

---

## Tests

Update the existing tests to the typed seam (no behaviour-shifting new features — these assert the
convergence and keep coverage on the production path):

- **`CollectedEvidenceMapperTests`**
  - Change the `Build` helper's `sourceType` parameter type to `EvidenceSourceType` (default
    `EvidenceSourceType.LocalFile`).
  - Replace the `ToEvidenceItem_ResolvesSourceType` theory: the `("wat", Manual)` string-fallback case
    no longer compiles or has meaning. Assert instead that the mapper **carries the declared
    `EvidenceSourceType` through unchanged** — a `[Theory]` over e.g. `LocalFile`, `PressRelease`,
    `RssFeed`, `NewsArticle` asserting `item.SourceType == declared`. There is no longer any
    string-to-`Manual` path to test.
  - All other mapper tests (normalization/hash, quality parsing, metadata/hints serialization,
    timestamps) stay as-is.

- **`LocalFileEvidenceCollectorTests`** — change the source-type assertion from
  `Assert.Equal("local_file", item.SourceType)` to
  `Assert.Equal(EvidenceSourceType.LocalFile, item.SourceType)`.

- **`RssPressReleaseCollectorTests`** — change the source-type assertion from
  `Assert.Equal("press_release", i.SourceType)` to
  `Assert.Equal(EvidenceSourceType.PressRelease, i.SourceType)`.

Add `using Radar.Domain.Evidence;` to test files as needed. Use the established deterministic
hand-built record convention; no test should depend on the deleted string table.

---

## Constraints

- Target .NET 10, C# 14. Keep the layering intact: the seam types live in `Radar.Application` and may
  reference `Radar.Domain.Evidence.EvidenceSourceType` (Application → Domain is allowed); no new
  package or project references.
- Provenance is sacred: source-type attribution must be compile-checked end to end after this slice —
  there must be no remaining code path that silently attributes evidence to `Manual` from an
  unrecognised collector token.
- Do not disturb AD-7 (quality stays a declared, `Unknown`-defaulting document input — leave
  `ParseQuality` untouched) or AD-8 (collector-driven, files-first MVP).
- Pure convergence cleanup: no new features, no new DI registrations, no change to
  `FileRawEvidenceStore`, no change to the `EvidenceSourceType` enum members.
- Keep changes scoped to the files listed; do not broaden into L-3/L-4.

---

## Acceptance criteria

- [ ] `IEvidenceCollector.SourceType` and `CollectedEvidence.SourceType` are typed
      `EvidenceSourceType` (no longer `string`).
- [ ] `CollectedEvidenceMapper.ResolveSourceType` and its hand-maintained string table are deleted;
      the mapper copies the declared `EvidenceSourceType` through directly, with no `default → Manual`
      fallback.
- [ ] `LocalFileEvidenceCollector.SourceType` returns `EvidenceSourceType.LocalFile` and
      `RssPressReleaseCollector.SourceType` returns `EvidenceSourceType.PressRelease`.
- [ ] `ParseQuality` and all AD-7 quality behaviour are unchanged; `FileRawEvidenceStore` is unchanged.
- [ ] The three collector-seam `CollectAsync` cancellation parameters are named `ct` (L-1).
- [ ] `Radar.Worker/Worker.cs` declares `public sealed class Worker` (L-2).
- [ ] Tests are updated to the typed seam; the obsolete string-`Manual` resolution test is replaced
      with a carry-through assertion; coverage exercises the production path.
- [ ] `dotnet build Radar.sln -c Release` (warnings-as-errors) and
      `dotnet test Radar.sln -c Release --no-build` are green.
