# Task: De-duplicate file-store write scaffolding (M-1 cleanup)

## Overview

This is a **cleanup slice** (no new feature). The architecture checkpoint at HEAD `bbfc0b0` returned a
CLEANUP verdict with a single MEDIUM finding, **M-1**, introduced/compounded by slices 46–47: the
`Radar.Infrastructure/FileSystem` write-mirror stores now duplicate two pieces of scaffolding, and the
copies have already begun to diverge:

1. **Serializer options duplicated and divergent.** `FileSignalStore` (lines 29–36) and
   `FileScoreSnapshotStore` (lines 28–35) declare a byte-identical
   `static readonly JsonSerializerOptions` (`WriteIndented` + `JsonNamingPolicy.CamelCase` +
   `JsonStringEnumConverter`). `FileRawEvidenceStore` (lines 22–26) declares the *same options minus the
   enum converter*. The next store that copies the wrong one would silently persist enum ordinals
   instead of names.

2. **Graceful-degrade write block duplicated.** The `Directory.CreateDirectory(...)` +
   `await File.WriteAllTextAsync(...).ConfigureAwait(false)` + `catch (Exception ex) when (ex is
   IOException or UnauthorizedAccessException)` → `LogWarning` → `return path` sequence is near-identical
   in `FileSignalStore` (lines 74–94), `FileScoreSnapshotStore` (lines 77–102), and `FileReportWriter`
   (lines 45–65, which additionally needs a UTF-8 no-BOM encoding).

The fix extracts two small shared helpers inside `Radar.Infrastructure` and routes the stores through
them. This is **pure de-duplication**: no observable behaviour change, no Application/Domain change, no
new feature. It protects the trunk from the divergence the reviewer flagged.

`FileRawEvidenceStore`'s bespoke insert-only `FileStream(FileMode.CreateNew)` write path (AD-1) is
**out of scope** and must not be touched — only its serializer-options field is unified (see decision
below).

---

## Assignment

Worktree: any
Dependencies: None (47 is merged and promoted to `docs/`).
Conflicts with: any concurrent slice editing the four `Radar.Infrastructure/FileSystem` file stores.
None are queued, so this can run alone.
Estimated time: ~1-2 hours

---

## Decision — raw-evidence serializer options (resolved: full unification, Option 1)

The reviewer asked whether unifying `FileRawEvidenceStore` onto shared options (which **adds** the
`JsonStringEnumConverter`) would change the on-disk raw-evidence JSON.

**Verified answer: it does not.** `FileRawEvidenceStore.Serialize` writes a `RawEvidenceFile` record
(`FileRawEvidenceStore.cs:226-237`) whose **only enum-derived field, `SourceType`, is already a
pre-converted `string`** (built via `ToSnakeCase(evidence.SourceType.ToString())`, e.g.
`"press_release"`). Every other field is a `Guid`, `string`, `string[]`, `DateTimeOffset?`, or
`JsonElement`. There is **no enum-typed property** in the serialized shape, so a
`JsonStringEnumConverter` is a no-op for this record and the raw-evidence JSON stays **byte-for-byte
identical**.

**Therefore choose Option 1 (full unification):** all three JSON stores share one
`RadarFileStoreJson.Options`. This is both the cleaner option and the safer one — it removes the
divergent third copy entirely while changing zero bytes on disk. Option 2 (keep raw-evidence options
separate) is explicitly **rejected** because the only reason to keep them separate — fear of changing
raw-evidence output — does not apply here.

> Implementation note: the coder must keep `FileRawEvidenceStore`'s **write path** exactly as-is
> (the `FileStream(FileMode.CreateNew, … FileOptions.Asynchronous)` insert-only logic, the dedupe
> `File.Exists` skip, the concurrent-writer `IOException when (File.Exists(path))` Debug branch).
> Only the `SerializerOptions` field reference changes to `RadarFileStoreJson.Options`.

---

## Project structure changes

```text
src/Radar.Infrastructure/FileSystem/
  RadarFileStoreJson.cs        # NEW: shared static JsonSerializerOptions holder
  GracefulFileWriter.cs        # NEW: shared create-dir + write + graceful-degrade helper
  FileSignalStore.cs           # MODIFIED: use shared options + GracefulFileWriter
  FileScoreSnapshotStore.cs    # MODIFIED: use shared options + GracefulFileWriter
  FileReportWriter.cs          # MODIFIED: use GracefulFileWriter (encoding overload)
  FileRawEvidenceStore.cs      # MODIFIED: SerializerOptions field → RadarFileStoreJson.Options ONLY
                               #           (write path untouched)

tests/Radar.Infrastructure.Tests/FileSystem/
  GracefulFileWriterTests.cs   # NEW: focused IO-failure / success / encoding tests
  RadarFileStoreJsonTests.cs   # NEW (small): locks the camelCase + enum-name convention
  # existing File*StoreTests.cs / FileReportWriterTests.cs: unchanged, must still pass
```

No `.csproj`, DI, Application, or Domain changes.

---

## Implementation details

All new types live in namespace `Radar.Infrastructure.FileSystem` and are `internal` (these are
infrastructure-internal helpers; the stores in the same namespace consume them, the Application never
sees them).

### `RadarFileStoreJson` (NEW)

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Single source of truth for the JSON serialization shape of Radar's on-disk file-store mirrors
/// (signals, score snapshots, raw evidence). Indented for human readability, camelCase property names,
/// and enums rendered as their string names (never integer ordinals) so the on-disk shape is lossless
/// and stable. Shared so the three JSON stores cannot diverge.
/// </summary>
internal static class RadarFileStoreJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
```

- `FileSignalStore`: delete its private `SerializerOptions` field; `Serialize` calls
  `JsonSerializer.Serialize(file, RadarFileStoreJson.Options)`.
- `FileScoreSnapshotStore`: same — delete private field, use `RadarFileStoreJson.Options`.
- `FileRawEvidenceStore`: delete its private `SerializerOptions` field, use `RadarFileStoreJson.Options`
  in `Serialize`. (No other change — see the decision section; output is byte-for-byte identical.)

### `GracefulFileWriter` (NEW)

Encapsulates the duplicated create-dir + write + graceful-degrade sequence. Returns `true` on a
successful write, `false` on a graceful degrade, so the **caller keeps its own success
`LogInformation`** (which carries the per-store domain id — signal id, snapshot id, report id — and must
not change).

```csharp
using System.Text;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Shared write helper for the file-store mirrors. Ensures the target directory exists, writes the text
/// content, and degrades gracefully on disk failure (logs a warning and returns <c>false</c> instead of
/// throwing) so a disk hiccup never crashes the pipeline run — the in-memory repository copy still
/// exists. Callers own path construction and any success logging.
/// </summary>
internal static class GracefulFileWriter
{
    /// <returns><c>true</c> if the file was written; <c>false</c> if the write degraded gracefully.</returns>
    public static async Task<bool> TryWriteAllTextAsync(
        string path,
        string content,
        ILogger logger,
        CancellationToken ct,
        Encoding? encoding = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (encoding is null)
            {
                await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(path, content, encoding, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must not crash the run; the in-memory copy still exists.
            logger.LogWarning(ex, "Failed to write file to {Path}; skipping.", path);
            return false;
        }
    }
}
```

Notes the coder must respect:

- The **exact catch filter** `when (ex is IOException or UnauthorizedAccessException)` and the
  no-throw / return-path degrade semantics are preserved.
- When `encoding is null`, call the **no-encoding** `File.WriteAllTextAsync(path, content, ct)` overload
  (the current signal/score stores use it) so their bytes are unchanged. The report writer passes its
  existing `Utf8NoBom` encoding to get the encoding overload.
- `ct` is honoured on the write, as before.

### Caller pattern (signal store shown; score store analogous)

`FileSignalStore.WriteAsync` keeps the provenance guard (`review.SignalId != signal.Id` throw), the
path construction, and the serialize call unchanged. Replace **only** the inner `try { CreateDirectory;
WriteAllTextAsync; LogInformation; return path } catch { LogWarning; return path }` block with:

```csharp
if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
{
    _logger.LogInformation("Wrote signal {SignalId} to {Path}.", signal.Id, path);
}

return path;
```

`FileScoreSnapshotStore.WriteAsync` keeps its per-link provenance guard
(`link.ScoreSnapshotId != snapshot.Id` throw) and path, and uses the same pattern, retaining its
existing success message:
`_logger.LogInformation("Wrote score snapshot {SnapshotId} for company {CompanyId} to {Path}.", snapshot.Id, snapshot.CompanyId, path);`.

`FileReportWriter.WriteAsync` keeps the `Utf8NoBom` field and path, calling:

```csharp
if (await GracefulFileWriter.TryWriteAllTextAsync(path, report.MarkdownContent, _logger, ct, Utf8NoBom)
        .ConfigureAwait(false))
{
    _logger.LogInformation("Wrote weekly report {ReportId} to {Path}.", report.Id, path);
}

return path;
```

### Accepted, intentional consolidation (call out in the PR)

The **warning** message is consolidated into the single shared
`"Failed to write file to {Path}; skipping."` (with the exception attached). This is the one
deliberate change to log text. It is acceptable because the attempted `{Path}` already identifies the
file (signal/snapshot ids are in the filename; the report path encodes the period) and the exception is
logged. All **success** `LogInformation` messages stay per-store and unchanged. The per-store success
messages, provenance guards, path layouts, return values, and the catch/no-throw degrade semantics are
otherwise byte-for-byte equivalent.

---

## Tests

### `GracefulFileWriterTests` (NEW; temp dir, clean up like the sibling tests)

- **Success**: writing into a fresh temp path creates intermediate directories, writes the content, and
  returns `true`; the file round-trips the exact string.
- **IO failure degrades**: point the path under an existing *file* (so `Directory.CreateDirectory`
  throws `IOException`) — the helper returns `false`, does **not** throw, and no file is created. Use
  `NullLogger.Instance`.
- **Encoding overload — UTF-8 no BOM**: write with a `new UTF8Encoding(false)` and assert the file's
  first bytes are **not** the BOM (`EF BB BF`); write with `null` encoding and assert no BOM either
  (default behaviour) so the report writer's no-BOM guarantee is locked.

### `RadarFileStoreJsonTests` (NEW; small)

- Serializing a record with an enum property via `RadarFileStoreJson.Options` emits the enum **name**
  (e.g. `"Positive"`), not its ordinal, and uses camelCase property names. This locks the convention so
  a future store cannot reintroduce ordinal enums.

### Existing tests (unchanged, must still pass)

`FileSignalStoreTests`, `FileScoreSnapshotStoreTests`, `FileRawEvidenceStoreTests`, and
`FileReportWriterTests` already assert the on-disk shapes, overwrite/insert-only semantics, provenance
guards, and IO-failure-returns-path behaviour. They must pass **without modification** — that is the
proof this slice changed no observable behaviour. In particular `FileRawEvidenceStoreTests` confirms the
raw-evidence JSON is unchanged after the options unification.

---

## Constraints

- Target .NET 10 / `net10.0`, C# 14.
- All changes stay in `Radar.Infrastructure` (file-writing concern). No Application/Domain/DI changes.
- **Do not touch** `FileRawEvidenceStore`'s insert-only `FileStream(FileMode.CreateNew)` write path,
  its dedupe `File.Exists` skip, or its concurrent-writer Debug branch — only swap its
  `SerializerOptions` field for `RadarFileStoreJson.Options`.
- Pure de-duplication: provenance guards, path layouts, return values, success log messages, and the
  graceful-degrade semantics stay byte-for-byte equivalent (the single warning-message consolidation is
  the only intended change).
- Preserve provenance (AD-1): evidence stays insert-only/immutable; signals and score snapshots stay
  upsert-by-Id last-write-wins. This slice does not change any of that.
- UTC timestamps, `InvariantCulture` formatting unchanged.
- No database, no AI. No new packages.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `RadarFileStoreJson.Options` exists (camelCase + indented + `JsonStringEnumConverter`) and is the
      sole serializer-options source for `FileSignalStore`, `FileScoreSnapshotStore`, and
      `FileRawEvidenceStore`; the three private `SerializerOptions` fields are deleted.
- [ ] `GracefulFileWriter.TryWriteAllTextAsync` (with the optional `Encoding` parameter) is the sole
      create-dir + write + graceful-degrade path for `FileSignalStore`, `FileScoreSnapshotStore`, and
      `FileReportWriter`; the duplicated inner try/catch blocks are removed.
- [ ] `FileRawEvidenceStore`'s insert-only `FileStream` write path is unchanged; only its
      serializer-options reference moved. Existing `FileRawEvidenceStoreTests` pass unmodified (proving
      byte-for-byte identical raw-evidence JSON).
- [ ] Per-store success `LogInformation` messages, provenance guards, path layouts, and return values
      are unchanged; only the shared warning message is consolidated.
- [ ] New `GracefulFileWriterTests` cover success, IO-failure graceful degrade, and UTF-8-no-BOM
      encoding; new `RadarFileStoreJsonTests` locks the camelCase + enum-name convention.
- [ ] All pre-existing file-store tests pass without modification.
- [ ] `dotnet build` / `dotnet test` on `Radar.sln -c Release` are green.
