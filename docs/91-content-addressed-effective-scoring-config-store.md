# Task: Content-addressed effective-scoring-config store (persist the weights the fingerprint hashes)

> **DIRECTED + PROVENANCE-COMPLETION (read first).** This slice is a **directed** task the maintainer
> asked for — **NOT** the generic planner loop and **NOT** architecture-gated. It **completes** the
> spec-89 / AD-10-amended provenance story: spec 89 stamps every snapshot with a one-way SHA256
> **fingerprint** of the effective scoring config (`ScoringConfigVersion`), which gates comparability
> (spec 69) and proves integrity — but the actual weight **values** cannot be recovered from the hash.
> This slice persists the **full effective scoring config**, **content-addressed by that same
> fingerprint**, so a historical snapshot's stamp dereferences back to the exact weights that produced
> it. It is **ADDITIVE persistence**: it does **NOT** change scoring output, the formula, the component
> math, or the fingerprint **value** — therefore **NO `_formula.Version` bump and NO
> `ScoringConfigVersion` change** (`radar-formula-v5` / its default fingerprint stay). It adds one
> content-addressed file store and an AD note; it is the natural completion of AD-10-as-amended, so it
> is recorded **Accepted**, not a settled-convention reversal.

> **STRICT SEQUENCING — implement AFTER spec 89 merges.** This slice needs spec 89 to exist: the
> `ScoringConfigVersion` fingerprint, the `ScoringWeights` record, and the
> `IAttentionSourceWeights.CanonicalDescriptor()` / `ScoringConfigFingerprint` canonicalization it
> introduces are the inputs this store persists. **It is INDEPENDENT of spec 90** (the attention
> tier-defaults calibration): 90 touches the tier map / formula magnitudes; 91 is a persistence
> side-channel touching different files (a new file store + its DI + `ScoringEngine`/runner wiring).
> They do not conflict, but **both sequence after 89**. Re-read the **merged** spec-89 code before
> starting (`ScoringEngine.cs`, `ScoringWeights`, `ScoringConfigFingerprint`,
> `IAttentionSourceWeights.CanonicalDescriptor()`, `ConfiguredAttentionSourceWeights`) — persist what
> the fingerprint actually hashes in the merged tree, not this spec's description of it.

## Overview

"Provenance is sacred" (CLAUDE.md): evidence → signal → score must trace to its inputs. Spec 89 made
`ScoringConfigVersion` a **deterministic content hash** of the effective resolved scoring config
(engine version + `_formula.Version` + every `ScoringWeights` value + the attention tier-map descriptor
via `IAttentionSourceWeights.CanonicalDescriptor()`). That hash gates comparability and proves two
snapshots came from the same generation — but it is **one-way**. Given a historical snapshot stamped
`radar-scoring-fp-<hex>`, an operator **cannot recover the weights** that produced it.

Today that is only a latent hole: **only the default weights have ever run live** (recorded in code
defaults and pinned by the spec-89 default-fingerprint test), so nothing is lost **yet**. But the whole
point of spec 89's config-driven weights is to run **custom `Radar:Scoring:Profile`s** (and `--`
CLI/appsettings overrides) in parallel experiments. The **first** custom-profile run would produce
snapshots whose weights live nowhere durable — a provenance hole in a provenance-first system. This
slice MUST land **before any custom-profile experiment run**.

The fix (lead recommendation): a **content-addressed effective-config store**. On each scoring run,
serialize the FULL resolved effective scoring config — engine version, `_formula.Version`, every
`ScoringWeights` value, and the full attention tier map (via the same `CanonicalDescriptor()` the
fingerprint hashes) — to canonical JSON and write it **idempotently / insert-if-new** to
`data/scoring-configs/{fingerprint}.json` (filename = the `ScoringConfigVersion` fingerprint). This
gives, in one small store:

- **Lossless traceability** — snapshot stamp → dereferences to the exact effective config.
- **No per-snapshot bloat** — stored **once per distinct config** (content-addressed), not per
  company/snapshot; a run over 8 companies writes the config file once.
- **Self-verification** — recomputing the fingerprint from the stored config MUST equal the filename;
  the hash stops being opaque and becomes **checkable**.

This mirrors **AD-1** (evidence is immutable / insert-if-new): a given fingerprint's config is by
definition fixed, so the same config always yields the same file content — write it once, never
overwrite.

---

## Assignment

Worktree: any (but see sequencing)
Dependencies: **spec 89 (`radar-formula-v5` + the fingerprint) MUST be merged first** — this persists
what the fingerprint hashes (`ScoringWeights`, `IAttentionSourceWeights.CanonicalDescriptor()`,
`ScoringConfigFingerprint`, the `ScoringConfigVersion` stamp on `ScoringEngine`). Also: AD-1
(insert-if-new immutability — the pattern to mirror), AD-3 (canonical/deterministic serialization),
AD-5 (layering), AD-8 (files-first), AD-10-as-amended-by-89 (the fingerprint is the generation stamp).
Expected tree state at start: `radar-formula-v5`, `ScoringConfigVersion` = the spec-89 default
fingerprint (`radar-scoring-fp-7dcf0bea7b8f` or whatever spec 89 pins).
Conflicts with: **None** with spec 90 (different files — 90 = attention tier defaults / formula
magnitudes; 91 = a persistence side-channel). Touches `ScoringEngine.cs` (to expose the effective-config
projection — additive, no math change), `RadarPipelineRunner.cs` (one best-effort write per run),
`InfrastructureServiceCollectionExtensions.cs` (register the new store + its options), and the Worker
host wiring that calls the `AddFile*Store` extensions (add `AddFileScoringConfigStore`). Adds a new
Application interface + Domain-free projection record + Infrastructure file store. **Sequence strictly
after 89; may run after or alongside 90 only if 90 has merged (both touch scoring-adjacent code —
prefer to sequence 91 after both 89 and 90 to avoid a `ScoringEngine`/DI merge race).**
Estimated time: ~1.5–2 h (a new files-first store mirroring the existing `FilePipelineRunStore` pattern,
plus additive engine/runner wiring and a self-verification test).

---

## Grounding facts (verified against the current tree — do NOT re-research)

> These describe the tree as it is **today** (pre-89) plus the spec-89 shape this slice builds on.
> Re-read the **merged** spec-89 code before starting.

- **Existing files-first store pattern (mirror this).** `FilePipelineRunStore`
  (`src/Radar.Infrastructure/FileSystem/FilePipelineRunStore.cs`) writes one JSON file per run to
  `{RootDirectory}/{yyyy}/{MM}/run-...json`, serializing with `RadarFileStoreJson.Options`
  (indented, camelCase, string enums, frozen — the single source of truth for on-disk shape) via
  `GracefulFileWriter.TryWriteAllTextAsync` (creates the dir, writes, and on `IOException`/
  `UnauthorizedAccessException` **logs a warning and returns `false` instead of throwing** — the
  best-effort posture). Its options record is a plain
  `sealed class FilePipelineRunStoreOptions { public required string RootDirectory { get; init; } }`
  (see also `FileScoreSnapshotStoreOptions.cs`). This slice's `FileScoringConfigStore` follows this
  pattern **exactly**, including the graceful-degrade posture.
- **File-store error posture is already "log + continue" (confirmed).** `GracefulFileWriter` swallows
  `IOException`/`UnauthorizedAccessException` and returns `false`; `FileScoreSnapshotStore` /
  `FilePipelineRunStore` never abort the run on a write failure — the in-memory copy / the snapshot's
  own stamp still exist. A config-store write failure must degrade the **same** way: log + continue;
  the snapshot still carries its fingerprint. **Confirmed this matches the existing posture.**
- **The runner already makes best-effort file-store calls per run.** `RadarPipelineRunner`
  (`src/Radar.Application/Pipeline/RadarPipelineRunner.cs`) injects `IScoreSnapshotFileStore`
  `_scoreFileStore` and `IPipelineRunStore` `_runStore`; it writes each snapshot in the per-company
  loop (line ~335) and writes one `PipelineRunRecord` at the end (line ~395), both best-effort. The
  effective config is **identical for every company in a run** (same engine, formula, weights, tier
  map), so it should be written **once per run**, not per company — the runner is the natural caller
  (like the single per-run `_runStore.WriteAsync`).
- **`ScoringEngine` already holds every fingerprint input (post-89).** After spec 89 the engine injects
  `ScoringWeights` + `IAttentionSourceWeights` (or its descriptor source) and computes the fingerprint
  once (stamped as `CompanyScoreSnapshot.ScoringConfigVersion`) via `ScoringConfigFingerprint`. It
  therefore already has, in one place, exactly the tuple this slice must serialize:
  `(EngineVersion, _formula.Version, ScoringWeights, IAttentionSourceWeights.CanonicalDescriptor())`
  and the resulting fingerprint. **Expose that as a projection** rather than re-gathering the inputs in
  the runner (which would re-introduce the AD-5 coupling of the runner to weights/tier-map internals).
- **Reuse the spec-89 canonicalization — do NOT invent a second serialization.** The stored JSON and
  the hashed input MUST be derivable from the **same** canonical projection, or the file could drift
  from what the fingerprint actually hashed (defeating self-verification). Reuse
  `ScoringConfigFingerprint` (spec 89) to compute the hash of the stored projection in the
  self-verification test; the stored JSON is a **superset human-readable view** of that same tuple.
- **`Convert.ToHexStringLower(SHA256.HashData(...))` idiom is established in Application** (AD-3):
  `EvidenceNormalizer.ComputeHash` and (post-89) `ScoringConfigFingerprint`. Do not add a new hashing
  idiom.
- **No Domain change, no new package, no AI, no HTTP, no DB.** Files-first (AD-8); a plain JSON file
  under `data/` alongside `data/scores`, `data/signals`, `data/runs`, `data/reports`.

---

## Design

### 1. The effective-config projection (Application, Domain-free)

A single immutable record capturing exactly what the fingerprint hashes, in a form that is both
serializable to the stored JSON and re-hashable for self-verification. Place it in
`Radar.Application/Scoring/`:

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// The FULL effective resolved scoring config for one run — the exact inputs the ScoringConfigVersion
/// fingerprint (spec 89) hashes: engine identity, formula structure identity, every ScoringWeights
/// value, and the attention tier-map canonical descriptor. Persisted content-addressed by the
/// fingerprint so a historical snapshot's stamp dereferences back to the weights that produced it
/// (provenance completion — AD-10-as-amended). Immutable and Domain-free (an Application projection,
/// not an aggregate). Recomputing the fingerprint from Engine/FormulaVersion/Weights/AttentionDescriptor
/// MUST equal Fingerprint — the store's self-verification invariant.
/// </summary>
public sealed record EffectiveScoringConfig(
    string Fingerprint,          // == the CompanyScoreSnapshot.ScoringConfigVersion stamp
    string EngineVersion,        // "mvp-engine-v1"
    string FormulaVersion,       // "radar-formula-v5"
    ScoringWeights Weights,      // every magnitude value (the spec-89 record)
    string AttentionDescriptor); // IAttentionSourceWeights.CanonicalDescriptor()
```

- `ScoringWeights` is already an immutable record of primitive `double`s (spec 89) → it serializes
  losslessly under `RadarFileStoreJson.Options` with no custom converter.
- `AttentionDescriptor` is the **already-canonical** string from `CanonicalDescriptor()` — store it
  verbatim so the tier map is fully recoverable and re-hashable.
- **`Fingerprint` is stored inside the file too** (not only as the filename) so a copied/renamed file
  is still self-describing and the self-verification test can compare `content.Fingerprint` vs. both the
  filename and the recomputed hash.

### 2. `ScoringEngine` exposes the projection (additive; no math change)

The engine already computes the fingerprint from `(EngineVersion, _formula.Version, _weights,
_sourceWeights.CanonicalDescriptor())` (spec 89). Add a **read-only** member that returns the
`EffectiveScoringConfig` for the currently-injected config — e.g. a `Lazy<EffectiveScoringConfig>` field
built in the constructor alongside the existing fingerprint computation, exposed on `IScoringEngine`:

```csharp
// IScoringEngine (Radar.Application/Scoring):
/// <summary>The effective resolved scoring config for this engine instance — the inputs the
/// ScoringConfigVersion fingerprint hashes, for content-addressed persistence (provenance).</summary>
EffectiveScoringConfig EffectiveConfig { get; }
```

- This is a **pure accessor** — no clock/IO/randomness, no scoring-math change. The engine's
  `ScoreCompanyAsync` behaviour, the stamped fingerprint value, contributions, and links are **byte-for-byte
  unchanged**. The engine's `<remarks>` "no scoring formula" boundary is preserved (this exposes the
  already-held config identity, it does not compute scores).
- Building it from the same fields the fingerprint uses guarantees `EffectiveConfig.Fingerprint` equals
  the stamp on every snapshot from this engine (assert in a test).

### 3. `IScoringConfigStore` + `FileScoringConfigStore` (the content-addressed store)

Application interface (`Radar.Application/Scoring/IScoringConfigStore.cs`):

```csharp
public interface IScoringConfigStore
{
    /// <summary>
    /// Insert-if-new (AD-1-style immutable): writes the effective config to
    /// {RootDirectory}/{config.Fingerprint}.json ONLY if no file for that fingerprint exists yet — a
    /// given fingerprint's config is by definition fixed, so the same config always yields the same
    /// content. Best-effort (AD-8): a disk failure logs + returns the attempted path, never aborts the
    /// run (the snapshot still carries the fingerprint). Returns the (existing or written) path.
    /// </summary>
    Task<string> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct);
}
```

Infrastructure implementation (`Radar.Infrastructure/FileSystem/FileScoringConfigStore.cs`), mirroring
`FilePipelineRunStore`:

- Path: `Path.Combine(_options.RootDirectory, config.Fingerprint + ".json")`. The fingerprint is already
  a filename-safe lowercase-hex-with-prefix token (e.g. `radar-scoring-fp-7dcf0bea7b8f`) — no path
  separators, no invalid chars. (Defensively, if the coder wants belt-and-braces, reject a fingerprint
  containing `Path.GetInvalidFileNameChars()` by logging + skipping; not required if 89 guarantees the
  shape.)
- **Insert-if-new:** if `File.Exists(path)`, skip the write and return `path` (idempotent — no
  overwrite, no error, mirroring AD-1 evidence semantics). This is deliberate and DIFFERS from
  `FileScoreSnapshotStore`'s upsert-by-Id: a config is content-addressed and immutable, so re-writing is
  never necessary and overwriting would violate the immutability contract. Document this in the class
  `<remarks>` (the same way `FileScoreSnapshotStore` documents its *opposite* choice).
  - Note the benign check-then-write race is acceptable for the MVP single-process runner (same as the
    existing stores' non-atomic writes); two concurrent writers would write **identical bytes** anyway
    (content-addressed), so a race is harmless. Do not add locking.
- Serialize with `RadarFileStoreJson.Options` (indented, camelCase, string enums) via
  `GracefulFileWriter.TryWriteAllTextAsync` — reuse the shared helper so the on-disk shape and the
  graceful-degrade posture cannot diverge from the other stores.
- Options record `FileScoringConfigStoreOptions { public required string RootDirectory { get; init; } }`
  (identical shape to `FilePipelineRunStoreOptions`).
- DI: `AddFileScoringConfigStore(this IServiceCollection, string rootDirectory)` registering the options
  + `IScoringConfigStore -> FileScoringConfigStore` singletons — mirror `AddFilePipelineRunStore`
  exactly. Wire it in the Worker host next to `AddFileScoreStore` / `AddFilePipelineRunStore`, with a
  `data/scoring-configs` default root consistent with how the other `data/*` roots are configured.

### 4. `RadarPipelineRunner` writes the config once per run (best-effort)

Inject `IScoringConfigStore _scoringConfigStore`. **Once per run** (not per company — the effective
config is identical for every company), write it best-effort. Cleanest placement: immediately before or
after the per-company scoring loop, guarded so it runs once:

```csharp
// The effective scoring config is identical for every company this run; persist it ONCE,
// content-addressed by its fingerprint (insert-if-new), so a historical snapshot's ScoringConfigVersion
// stamp dereferences back to the exact weights (provenance completion, AD-10-as-amended). Best-effort
// like the other file stores: a disk failure logs + continues — the snapshots still carry the stamp.
await _scoringConfigStore
    .WriteIfNewAsync(_scoringEngine.EffectiveConfig, ct)
    .ConfigureAwait(false);
```

- Placing it just **before** the per-company loop means the config file exists as soon as the first
  snapshot referencing it is written (nice for browsing), but either side is fine — it is idempotent.
- It must **not** change any counter or the `RadarPipelineResult`/`PipelineRunRecord` counts.

### 5. Run-history reference (recommend the MINIMAL touch: NONE required; optional pointer)

The task asks whether to also record a reference from the run-history record (spec 59 `data/runs`)
and/or the report footer. **Recommendation: the content-addressed store keyed by the fingerprint is the
core and is sufficient** — every persisted snapshot already carries `ScoringConfigVersion` (== the
filename), so any snapshot already dereferences to its config with zero extra plumbing. Adding a
`ScoringConfigFingerprint` field to `PipelineRunRecord` is a **nice-to-have** run-level shortcut (open
`data/runs/...json` → jump to the config that run used) but is **not required for the provenance
property** and touches the run record + its file-store round-trip tests.

- **Default: do NOT touch `PipelineRunRecord`.** Keep this slice a pure additive side-channel; the
  snapshot→fingerprint→config chain already closes the provenance loop.
- **If the coder judges the run-level pointer is cheap** (one nullable field `string? ScoringConfigFingerprint`
  on `PipelineRunRecord`, populated from `_scoringEngine.EffectiveConfig.Fingerprint`, trailing +
  nullable so old run files deserialize to `null`), it is acceptable — but keep it OPTIONAL and behind
  the same "additive, no behaviour change" bar, and update `FilePipelineRunStoreTests` round-trip. **Do
  NOT** touch the report footer this round (out of scope — the fingerprint is an internal token, spec 89
  kept it out of the report body).

The PR must state which it chose.

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  EffectiveScoringConfig.cs        # ADD: immutable Domain-free projection of the fingerprint inputs
                                   #   (Fingerprint, EngineVersion, FormulaVersion, ScoringWeights,
                                   #   AttentionDescriptor).
  IScoringConfigStore.cs           # ADD: WriteIfNewAsync(EffectiveScoringConfig, ct) — insert-if-new,
                                   #   best-effort; the Application seam (all file I/O in Infrastructure).
  IScoringEngine.cs                # MODIFIED: add EffectiveScoringConfig EffectiveConfig { get; } (pure
                                   #   accessor for the already-computed config identity).
  ScoringEngine.cs                 # MODIFIED (additive): build EffectiveConfig from the same
                                   #   (EngineVersion, _formula.Version, _weights, sourceWeights
                                   #   .CanonicalDescriptor()) the fingerprint already uses; expose it.
                                   #   NO scoring-math change; stamped fingerprint value unchanged.

src/Radar.Infrastructure/FileSystem/
  FileScoringConfigStore.cs        # ADD: writes {RootDirectory}/{fingerprint}.json insert-if-new via
                                   #   GracefulFileWriter + RadarFileStoreJson.Options; mirrors
                                   #   FilePipelineRunStore; graceful-degrade posture; <remarks> notes the
                                   #   deliberate insert-if-new (opposite of FileScoreSnapshotStore's upsert).
  FileScoringConfigStoreOptions.cs # ADD: { required string RootDirectory }.

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: add AddFileScoringConfigStore(rootDirectory)
                                   #   registering options + IScoringConfigStore -> FileScoringConfigStore
                                   #   (mirror AddFilePipelineRunStore).

src/Radar.Application/Pipeline/
  RadarPipelineRunner.cs           # MODIFIED: inject IScoringConfigStore; write _scoringEngine
                                   #   .EffectiveConfig ONCE per run (best-effort); no counter/result change.
  PipelineRunRecord.cs             # OPTIONAL (default: unchanged) — only if adding the run-level pointer.

src/Radar.Worker/ (host wiring)
  <the file that calls AddFileScoreStore/AddFilePipelineRunStore>  # MODIFIED: call
                                   #   AddFileScoringConfigStore with a data/scoring-configs default root,
                                   #   consistent with the other data/* roots.

tests/Radar.Infrastructure.Tests/FileSystem/
  FileScoringConfigStoreTests.cs   # ADD: write creates {fingerprint}.json; idempotent second write does
                                   #   not duplicate/overwrite/error; stored content round-trips; the
                                   #   stored config's recomputed fingerprint == filename == content
                                   #   .Fingerprint (self-verification); write-failure degrades gracefully.

tests/Radar.Application.Tests/Scoring/
  ScoringEngineTests.cs            # MODIFIED: assert engine.EffectiveConfig.Fingerprint == the stamped
                                   #   ScoringConfigVersion on a produced snapshot (default + a changed
                                   #   weight); EffectiveConfig carries the injected ScoringWeights values.

tests/Radar.Application.Tests/Pipeline/
  RadarPipelineRunnerTests.cs      # MODIFIED: the runner writes the effective config once per run
                                   #   (best-effort, via a fake/real IScoringConfigStore); a config-store
                                   #   write failure does not abort the run or change counts.

docs/architecture-decisions.md     # MODIFIED: short AD note (amend AD-10) — the effective config is
                                   #   persisted content-addressed by the fingerprint so weights are
                                   #   recoverable (Accepted; provenance completion).
```

No `Radar.Domain` change (unless the optional run-record pointer is taken, which is Application). No
collector, no report-renderer body change, no new package, no provider SDK, no AI, no HTTP, no DB.

---

## Tests

Match the existing test style (`FilePipelineRunStoreTests` / `FileScoreSnapshotStoreTests` for the store;
`ScoringEngineTests` helpers for the engine). News up the store with a temp `RootDirectory` under the
scratchpad/test temp dir.

1. **Write creates the content-addressed file.** `WriteIfNewAsync(config, ct)` writes
   `{RootDirectory}/{config.Fingerprint}.json`; the file exists and deserializes back to an equal
   `EffectiveScoringConfig` (fingerprint, engine/formula versions, every `ScoringWeights` value, and the
   attention descriptor all round-trip).
2. **Idempotent (insert-if-new — the AD-1 mirror).** A second `WriteIfNewAsync` with the **same** config
   does NOT throw, does NOT duplicate, and does NOT overwrite — assert the file's content/last-write is
   unchanged (e.g. write, capture bytes/mtime, write again, assert identical) and only one file exists
   for that fingerprint. (Optionally: writing a file with tampered bytes and confirming the second
   `WriteIfNewAsync` leaves the tampered bytes — proving it truly skips rather than rewrites.)
3. **Self-verification (the crux).** Recomputing the fingerprint from the **stored** config via the
   spec-89 `ScoringConfigFingerprint` (using the stored `EngineVersion`, `FormulaVersion`, `Weights`,
   `AttentionDescriptor`) EQUALS **both** the filename **and** the stored `content.Fingerprint`. This
   proves the hash is no longer opaque — the persisted config is exactly what was hashed, and the store
   is content-addressed correctly.
4. **A custom profile produces a different file with the custom values recorded.** Build an
   `EffectiveScoringConfig` from a `ScoringWeights` with one changed magnitude (e.g.
   `AttentionHalfSaturation = 12.0`) and its (different) fingerprint; `WriteIfNewAsync` creates a
   **second, distinctly-named** file whose stored `Weights.AttentionHalfSaturation == 12.0` — i.e. the
   custom weights ARE recoverable from disk (the whole point). Both files coexist (content-addressed, no
   collision).
5. **Write-failure degrades gracefully.** With a `RootDirectory` that forces an `IOException`/
   `UnauthorizedAccessException` (mirror how `FilePipelineRunStoreTests` / `FileScoreSnapshotStoreTests`
   exercise the graceful path), `WriteIfNewAsync` logs + returns the attempted path and does **not**
   throw — the run would continue and the snapshot still carries its fingerprint.
6. **Engine exposes a consistent projection (`ScoringEngineTests`).** `engine.EffectiveConfig.Fingerprint`
   EQUALS the `ScoringConfigVersion` stamped on a snapshot the engine produces (under default weights,
   and under a changed weight the two match each other and differ from the default). `EffectiveConfig`
   carries the injected `ScoringWeights` values and the injected attention descriptor.
7. **Runner writes once, best-effort (`RadarPipelineRunnerTests`).** A pipeline run calls
   `IScoringConfigStore.WriteIfNewAsync` (assert once per run, not once per company — verify with a
   counting fake over a multi-company universe); a config-store write **failure** does not abort the run
   or change any `RadarPipelineResult` / `PipelineRunRecord` count.
8. **(If the optional run-record pointer is taken)** `PipelineRunRecord.ScoringConfigFingerprint`
   round-trips through `FilePipelineRunStore`; an old run file lacking the field deserializes to `null`.

Keep all other scoring / pipeline / report / store tests green. Grep for any test that constructs
`ScoringEngine` and update for the added `EffectiveConfig` member / the new runner dependency.

---

## Constraints

- Target `net10.0`, C# 14. `EffectiveScoringConfig`, `IScoringConfigStore`, and the engine accessor live
  in `Radar.Application`; the file store + its options + DI live in `Radar.Infrastructure`; host wiring
  in `Radar.Worker` (AD-5 layering — Application defines the seam, Infrastructure does the file I/O). No
  provider SDK, no AI, no HTTP, no DB (AD-8, files-first).
- **AD-1-style immutability:** the config store is **insert-if-new** — a given fingerprint's config is by
  definition fixed; never overwrite an existing `{fingerprint}.json`. (Deliberately the OPPOSITE of
  `FileScoreSnapshotStore`'s upsert-by-Id; document the choice in `<remarks>` as that store documents
  its own.)
- **AD-3 determinism / reuse the spec-89 canonicalization:** the stored JSON MUST be a lossless view of
  the SAME tuple the fingerprint hashes; the self-verification test recomputes the fingerprint from the
  stored fields via the spec-89 `ScoringConfigFingerprint` and asserts equality with the filename. Do
  **not** invent a second serialization that could drift from the fingerprint input.
- **AD-8 files-first / graceful posture:** write via `GracefulFileWriter` + `RadarFileStoreJson.Options`
  (the shared helper + shape); a write failure logs a warning and continues — it must **NOT** abort
  scoring (the snapshot still carries the fingerprint). Confirmed this matches the existing
  `FilePipelineRunStore` / `FileScoreSnapshotStore` posture.
- **Provenance preserved and strengthened:** this ADDS a recoverable trace from the snapshot stamp back
  to the exact weights. `ScoreEvidenceLink` construction, the snapshot record, and the fingerprint
  **value** are all unchanged.
- **No scoring-output / formula / fingerprint change:** `_formula.Version` stays `radar-formula-v5`; the
  default fingerprint stays the spec-89 pinned value; component math and clamps are untouched. **No
  `_formula.Version` bump, no `ScoringConfigVersion` value change** (this is additive persistence).
- **AD-9:** no advice language; no report-body/label change.
- **AD-10 note is a clarifying provenance ADDITION, not a settled-convention reversal** — record it
  **Accepted** (the natural completion of AD-10-as-amended-by-89), not Proposed.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (note, do NOT implement this round)

- **The attention tier-defaults calibration (spec 90)** — 91 is a persistence side-channel; it neither
  reads nor changes the tier defaults' *values*, only serializes whatever descriptor 89 produces.
- **A price-efficacy visual / any price data ingestion.**
- **News-volume within-source signal-multiplicity dedup.**
- **Any UI / report surface to browse configs** — the fingerprint stays an internal comparability +
  provenance token; do not render the config or fingerprint into the report body this round (spec 89
  kept it out; keep it out).
- **Rehydrating / reading configs back in the pipeline** — this slice only WRITES the content-addressed
  store (the provenance record). A `ReadByFingerprint` accessor / a "diff two configs" tool is a
  separate backlog idea; add only `WriteIfNewAsync` now (plus whatever the tests need to deserialize).
- **The report-footer reference** — even if the optional run-record pointer is taken, do not touch the
  report footer.
- **Migrating / backfilling configs for pre-existing snapshots** — only default weights have run, and
  the default config is written the next run; no backfill needed.

---

## Acceptance criteria

- [ ] **Implemented AFTER spec 89 merges** (needs the fingerprint, `ScoringWeights`,
      `ScoringConfigFingerprint`, and `IAttentionSourceWeights.CanonicalDescriptor()`); does not conflict
      with spec 90 (different files); sequenced after both 89 and 90.
- [ ] On each scoring run, the FULL effective scoring config (engine version, `_formula.Version`, every
      `ScoringWeights` value, the attention tier-map descriptor, and the fingerprint) is serialized to
      canonical JSON and written to `data/scoring-configs/{fingerprint}.json` (filename = the
      `ScoringConfigVersion` fingerprint), **once per run** (not per company).
- [ ] The write is **insert-if-new** (AD-1 mirror): if the `{fingerprint}.json` already exists it is NOT
      overwritten, NOT duplicated, and does NOT error (a given fingerprint's config is fixed). Tested.
- [ ] **Self-verification:** recomputing the fingerprint from the STORED config (via the spec-89
      `ScoringConfigFingerprint`) equals the filename AND the stored `Fingerprint` field — the hash is
      now checkable, not opaque. Tested.
- [ ] A **custom profile** produces a distinctly-named config file whose stored weights record the
      custom values (the weights that produced a historical snapshot are recoverable). Tested.
- [ ] A **write failure degrades gracefully** (log + continue via `GracefulFileWriter`) and never aborts
      scoring — the snapshot still carries its fingerprint. Tested; matches the existing file-store
      posture.
- [ ] `ScoringEngine` exposes `EffectiveConfig` as a pure accessor built from the SAME inputs the
      fingerprint uses; `EffectiveConfig.Fingerprint` == the `ScoringConfigVersion` stamped on the
      engine's snapshots (default + changed weight). Tested. No scoring-math / fingerprint-value change.
- [ ] `IScoringConfigStore` / `FileScoringConfigStore` / `FileScoringConfigStoreOptions` /
      `AddFileScoringConfigStore` mirror the `FilePipelineRunStore` pattern (shared `GracefulFileWriter`
      + `RadarFileStoreJson.Options`); Worker host wires it with a `data/scoring-configs` default root.
- [ ] The runner writes the config once per run, best-effort, changing no counter / `RadarPipelineResult`
      / `PipelineRunRecord` count. The optional run-record `ScoringConfigFingerprint` pointer is either
      omitted (default) or added additively (nullable, round-trips, old files → null) — the PR states
      which.
- [ ] **NO `_formula.Version` bump, NO `ScoringConfigVersion` value change** — this is additive
      persistence; scoring output, the formula, the component math, and the default fingerprint are
      unchanged.
- [ ] `docs/architecture-decisions.md`: AD-10 amended (or a short adjacent note) recording that the
      effective config is persisted content-addressed by the fingerprint so weights are recoverable —
      **Accepted** (provenance completion), cross-referencing AD-1 (insert-if-new), AD-3, AD-8, spec 89.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance (strengthened), and AD-9
      preserved; no Domain / collector / report-renderer-body / component-math change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
