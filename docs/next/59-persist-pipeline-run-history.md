# Task: Persist pipeline run history (a durable run log under `data/runs/`)

## Overview

Radar mirrors evidence, signals, and score snapshots to disk (AD-8), and each run returns a
`RadarPipelineResult` with the run's counts — but that result is **thrown away** once `RunAsync` returns.
There is no durable record of *when* a run happened, *which collectors* it used, or *how it compared to the
run before it*. As Radar starts running week-over-week (the whole point of a weekly report), the maintainer
needs a run log to answer "did last night's run collect anything new, and how does it compare to the previous
run?" without diffing report markdown by hand.

This slice adds a **pipeline run record** persisted once per run to `data/runs/`. It is the foundation for
surfacing run history in the report (spec 61) and for future run-over-run trend work. It is deterministic,
files-first (AD-8), and adds no external dependency.

Scope is **the run record + its file store + the runner writing it + wiring + tests**. It does NOT change
scoring, extraction, resolution, or the report renderer (surfacing run history in the report is spec 61).

---

## Assignment

Worktree: any
Dependencies: existing trunk (pipeline runner + file-store pattern merged). None queued.
Conflicts with: 61 (both touch the runner/report path — sequence 59 → 61). Independent of 60.
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Application/Pipeline/
  PipelineRunRecord.cs             # NEW: immutable record of one completed run (id, instant, collectors, counts, reportId)
  IPipelineRunStore.cs             # NEW: WriteAsync(record, ct) -> written path; ReadRecentAsync(count, ct) -> newest-first records
  RadarPipelineRunner.cs           # MODIFIED: build a PipelineRunRecord at the end of RunAsync and write it via the store

src/Radar.Infrastructure/FileSystem/
  FilePipelineRunStore.cs          # NEW: IPipelineRunStore; writes JSON to {root}/{yyyy}/{MM}/run-{createdAt:yyyyMMddTHHmmssfffZ}-{id}.json
  FilePipelineRunStoreOptions.cs   # NEW: RootDirectory

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddFilePipelineRunStore(rootDirectory)

src/Radar.Worker/
  RadarWorkerServices.cs           # MODIFIED: register the run store; pass the enabled collector kinds into the runner/record
  RadarWorkerOptions.cs            # MODIFIED: RunsDirectory (default "data/runs")

tests/Radar.Application.Tests/Pipeline/
  RadarPipelineRunnerTests.cs      # MODIFIED: the runner writes exactly one run record with the result's counts + collectors
tests/Radar.Infrastructure.Tests/FileSystem/
  FilePipelineRunStoreTests.cs     # NEW: round-trip write/read, newest-first ordering, graceful disk-failure degradation
```

---

## Implementation details

### `PipelineRunRecord` (Application)
- Immutable `sealed record` capturing one completed run. Fields (all UTC where temporal):
  - `Guid Id` — run id (`Guid.NewGuid()` at record construction time in the runner).
  - `DateTimeOffset CreatedAtUtc` — the run instant (reuse the runner's existing `asOfUtc`; do NOT capture a
    second clock reading — one run, one instant, AD-7).
  - `IReadOnlyList<string> Collectors` — the collector kinds/names that ran, in the runner's stable
    `CollectorName` order (e.g. `["RssPressReleaseCollector","sec-edgar"]`). Source the names from the
    already-ordered `_collectors` list, not from config, so the record reflects what actually ran.
  - The counts already on `RadarPipelineResult`: `EvidenceCollected`, `EvidenceNew`, `SignalsExtracted`,
    `SignalsValid`, `SignalsApproved`, `SignalsNeedingReview`, `CompaniesScored`, `SourcesChecked`,
    `SourcesFailed`.
  - `Guid? ReportId` — the report id when a report was generated, else null.
- Keep it in `Radar.Application/Pipeline` (it is a run-observability projection, not a domain aggregate, and
  it references `RadarPipelineResult`'s shape). Do not add it to `Radar.Domain`.

### `IPipelineRunStore` (Application)
- `Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct)` — persists the record, returns the
  written path.
- `Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct)` — returns up to
  `count` most-recent records, **newest-first**, ordered by `CreatedAtUtc` desc then `Id` (AD-3 determinism).
  Provided now so spec 61 can read history without touching this interface again. `count <= 0` returns empty.

### `FilePipelineRunStore` (Infrastructure)
- Mirror the existing file-store conventions exactly (see `FileScoreSnapshotStore` / `FileSignalStore`):
  reuse `GracefulFileWriter.TryWriteAllTextAsync` and `RadarFileStoreJson.Options` (camelCase). All file I/O,
  JSON, and `System.Text.Json` stay in Infrastructure (AD-5).
- **Write** path: `{RootDirectory}/{CreatedAtUtc:yyyy}/{MM}/run-{CreatedAtUtc:yyyyMMddTHHmmssfffZ}-{Id}.json`.
  Run records are an **append-only run log**: each run has a fresh `Id`, so files never collide; a disk
  failure degrades gracefully (warn + return the attempted path, never throw/abort the run) exactly like the
  other stores.
- **Read** (`ReadRecentAsync`): enumerate `*.json` under `RootDirectory` recursively, deserialize each into a
  `PipelineRunRecord`, order newest-first, take `count`. Skip (warn, don't throw) any unreadable/malformed
  file so one bad file can't break the history read. No directory → empty list.
- Add a private persisted-shape record (like `ScoreSnapshotFile`) if you prefer an explicit DTO; a direct
  serialize of `PipelineRunRecord` is acceptable since it is a flat record.

### Runner change (`RadarPipelineRunner`)
- Inject `IPipelineRunStore` (constructor, with the standard `ArgumentNullException.ThrowIfNull`).
- At the very end of `RunAsync`, after building the `RadarPipelineResult`, construct a `PipelineRunRecord`
  from that result + `asOfUtc` + the ordered collector names, and `await _runStore.WriteAsync(record, ct)`.
  The write is best-effort persistence (like the other file stores): a disk failure must NOT change any
  counter or throw — the store already swallows disk errors, so just await it and ignore the returned path
  (or log at Debug).
- Do not change the returned `RadarPipelineResult` shape, the counters, or stage ordering.

### Wiring
- `AddFilePipelineRunStore(this IServiceCollection, string rootDirectory)` registers
  `FilePipelineRunStoreOptions { RootDirectory = rootDirectory }` + `IPipelineRunStore` → `FilePipelineRunStore`
  (mirror `AddFileScoreStore`).
- `RadarWorkerOptions.RunsDirectory` (default `"data/runs"`); `RadarWorkerServices` calls
  `AddFilePipelineRunStore(options.RunsDirectory)` alongside the other file stores.
- `.gitignore` already ignores `data/` run outputs — confirm `data/runs/` is covered (it is under `data/`);
  do not commit run-log files.

---

## Tests

- `FilePipelineRunStoreTests` (temp dir): a written record round-trips through `ReadRecentAsync` with all
  fields intact; writing three records with increasing `CreatedAtUtc` then `ReadRecentAsync(2)` returns the
  two newest, newest-first; `ReadRecentAsync(0)` → empty; a missing root directory → empty; a malformed
  `*.json` in the tree is skipped (warn) and does not break the read; a write to an unwritable path degrades
  gracefully (no throw, returns the attempted path).
- `RadarPipelineRunnerTests` (existing suite): after `RunAsync`, exactly one run record is written; its counts
  equal the returned `RadarPipelineResult`'s counts; its `Collectors` list equals the runner's ordered
  collector names; `ReportId` matches when a report was generated and is null when `GenerateReport` is false.
  Use a fake/in-memory `IPipelineRunStore` (capture the record) so the runner test stays I/O-free.
- Keep all existing runner/pipeline/DI tests green (constructor now needs the new dependency — update the
  test setup/fakes).

---

## Constraints

- Target `net10.0`. Deterministic: one run → one instant (`asOfUtc`, AD-7) → one record. No second clock read.
- Files-first (AD-8); all file I/O/JSON in Infrastructure (AD-5). Append-only run log (fresh `Id` per run) —
  do not overwrite prior runs. No DB, no AI.
- Best-effort persistence: a run-store failure never aborts the run or changes a counter (mirror the other
  file stores' graceful degradation).
- No advice language. Scope to the run record + store + runner write + wiring + tests; do NOT change scoring,
  extraction, resolution, or the report renderer.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] A `PipelineRunRecord` (id, `CreatedAtUtc` = the run's `asOfUtc`, ordered collector names, all
      `RadarPipelineResult` counts, `ReportId?`) is persisted once per run to
      `data/runs/{yyyy}/{MM}/run-...json`.
- [ ] `IPipelineRunStore.ReadRecentAsync(count)` returns up to `count` records newest-first (AD-3), skipping
      malformed files, and returns empty for a missing directory or non-positive count.
- [ ] The store degrades gracefully on disk failure (no throw, no counter change) like the other file stores;
      the run log is append-only (a fresh `Id` per run, never overwritten).
- [ ] The store is DI-registered (`AddFilePipelineRunStore`) and wired from `RadarWorkerServices`/
      `RadarWorkerOptions.RunsDirectory`; the runner writes the record without altering `RadarPipelineResult`
      or any stage.
- [ ] Offline tests cover round-trip, newest-first ordering, malformed-skip, graceful degradation, and the
      runner writing exactly one record matching the result. No scoring/extraction/report change.
      `build`/`test` green.
