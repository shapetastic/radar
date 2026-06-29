# Task: Surface a collection summary through the collector contract and pipeline result

## Overview

After slice 41 the RSS reader knows which feeds failed, but that knowledge dies inside Infrastructure
logs. The `IEvidenceCollector` contract returns only `IReadOnlyCollection<CollectedEvidence>`, so the
`RadarPipelineRunner`, the `RadarPipelineResult`, and the `Worker` log have **no structured handle on
collection health** — they can report "12 new evidence" but not "checked 10 feeds, 2 unreadable". The
master spec makes collectors responsible for answering *"what new public information did we find?"* and
explicitly requires the RSS collector to **log what it collected**; a run summary that distinguishes
coverage from failures is the structured form of that.

This slice enriches the collector contract to return a small, **source-agnostic** `CollectionSummary`
alongside the evidence (sources checked / succeeded / failed, items collected, and a deterministic list
of failures), threads it through the runner into `RadarPipelineResult`, and has the `Worker` log
collection health. It is a mechanical contract change rippling through both collectors, the runner, the
result record, the worker, and their tests — no new behaviour beyond reporting. It unblocks slice 43,
which renders the summary into the weekly report.

---

## Assignment

Worktree: pending
Dependencies: **Slice 41** (the RSS reader's `RssFeedReadResult` outcomes populate the per-feed failure
entries). Sequence 41 → 42.
Conflicts with: Slice 41 and Slice 43 (shared files: `RssPressReleaseCollector.cs`, the runner, the
result record). Sequence them; do not parallelize — this slice changes a shared Application interface
(`IEvidenceCollector`) and a shared result record.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Collectors/CollectionSummary.cs    # NEW: summary + SourceFailure records
src/Radar.Application/Collectors/CollectionResult.cs     # NEW: (Evidence, Summary) pair
src/Radar.Application/Collectors/IEvidenceCollector.cs   # MODIFIED: CollectAsync returns CollectionResult
src/Radar.Application/Pipeline/RadarPipelineResult.cs    # MODIFIED: add SourcesChecked / SourcesFailed
src/Radar.Application/Pipeline/RadarPipelineRunner.cs    # MODIFIED: consume .Evidence, surface summary
src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs # MODIFIED: build the summary
src/Radar.Infrastructure/Sources/LocalFileEvidenceCollector.cs # MODIFIED: build a trivial summary
src/Radar.Worker/Worker.cs                               # MODIFIED: log collection health

tests/Radar.Infrastructure.Tests/Rss/RssPressReleaseCollectorTests.cs        # MODIFIED
tests/Radar.Infrastructure.Tests/Sources/LocalFileEvidenceCollectorTests.cs  # MODIFIED
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs           # MODIFIED
tests/Radar.Worker.Tests/WorkerTests.cs                                      # MODIFIED
tests/Radar.IntegrationTests/PipelineEndToEndTests.cs                        # MODIFIED if it asserts the result shape
```

---

## Implementation details

### `CollectionSummary` and `SourceFailure` (new, Application/Collectors)

Source-agnostic so both collectors (RSS feeds, local files) can report uniformly — a "source" is one
feed for RSS, one file for the local collector:

```csharp
public sealed record SourceFailure(string SourceName, string? SourceUrl, string Reason);

public sealed record CollectionSummary(
    int SourcesChecked,
    int SourcesSucceeded,
    int SourcesFailed,
    int ItemsCollected,
    IReadOnlyList<SourceFailure> Failures)
{
    public static CollectionSummary Empty { get; } =
        new(0, 0, 0, 0, []);
}
```

`Failures` must be in a deterministic order (the order sources were processed, which is already stable
in both collectors). `ItemsCollected` is the count of `CollectedEvidence` returned (pre-dedupe-store —
it is a collection count, not a "new evidence" count; the runner already tracks new-vs-collected).

### `CollectionResult` (new)

```csharp
public sealed record CollectionResult(
    IReadOnlyCollection<CollectedEvidence> Evidence,
    CollectionSummary Summary);
```

### `IEvidenceCollector`

Change `CollectAsync` to return `Task<CollectionResult>`. Update the XML doc to note it now returns both
the evidence and a run summary.

### `RssPressReleaseCollector`

Already iterates feeds in deterministic order and (after slice 41) has each feed's `RssFeedReadResult`.
Build the summary as it goes: `SourcesChecked` = feeds attempted; `SourcesSucceeded` /`SourcesFailed`
from `result.IsSuccess`; a `SourceFailure(feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString())`
appended per failed feed; `ItemsCollected` = the returned evidence count. Return
`new CollectionResult(results, summary)`. Keep the existing per-feed warning + aggregate log from 41.

### `LocalFileEvidenceCollector`

A "source" here is a JSON file. Count files attempted as `SourcesChecked`; files that fail to read /
parse / validate (the existing skip paths) as `SourcesFailed` with a `SourceFailure(fileName, null,
reason)`; successfully-mapped files as `SourcesSucceeded`. The missing-directory and
enumeration-failure early returns become `CollectionResult` with `CollectionSummary.Empty` (or a single
failure entry for the directory — keep it simple and honest). Return
`new CollectionResult(items, summary)`.

### `RadarPipelineRunner`

`var collected = await _collector.CollectAsync(context, ct);` becomes a `CollectionResult`. Iterate
`collected.Evidence` where it currently iterates `collected`. Carry `collected.Summary` to the result.
Extend the final `LogInformation` to include sources checked/failed. No other stage changes.

### `RadarPipelineResult`

Add `int SourcesChecked` and `int SourcesFailed` (keep the existing fields). Optionally also expose the
full `CollectionSummary` (preferred — slice 43 needs the failure list); if so add a
`CollectionSummary Collection` field and document that the scalar counts mirror it. Keep the record's
"counts are observational; provenance lives in the persisted artefacts" remark.

### `Worker`

Extend the run-completed `LogInformation` to include `result.SourcesChecked` and `result.SourcesFailed`
(e.g. `"… {SourcesFailed}/{SourcesChecked} sources unreadable …"`).

---

## Tests

### Collector tests

- **RSS summary:** a fake reader with one success (N items) and one failure → summary reports
  `SourcesChecked = 2`, `SourcesSucceeded = 1`, `SourcesFailed = 1`, `ItemsCollected = N`, and one
  `SourceFailure` with the failed feed's name/url/reason. `Evidence` matches the prior assertions.
- **Local-file summary:** a directory with one good file and one malformed file → `SourcesChecked = 2`,
  `SourcesFailed = 1` with the bad file named; good file's evidence still returned. Missing directory →
  `CollectionSummary.Empty` and empty evidence.

### `RadarPipelineRunnerTests`

- Runner consumes `collected.Evidence` (existing evidence/signal/scoring assertions still pass with a
  fake collector now returning `CollectionResult`).
- `RadarPipelineResult.SourcesChecked` / `SourcesFailed` (and `Collection`, if added) reflect the fake
  collector's summary.

### `WorkerTests`

- The run-completed log includes the sources-checked/failed values (adapt the existing log assertion).

Update any fake `IEvidenceCollector` in the test suites to return `CollectionResult` (add a small helper
that wraps an evidence list with `CollectionSummary.Empty` for tests that don't care about the summary).

---

## Constraints

- Target .NET 10; C# 14.
- `CollectionSummary` is observational metadata, **not** a score or signal — it carries no labels and no
  advice language; provenance still lives in evidence/signals/snapshots/report.
- Layering unchanged: the new types live in `Radar.Application.Collectors`; collectors stay in
  Infrastructure. No provider SDK leakage, no DB, no AI.
- Determinism: `Failures` ordering is the stable source-processing order. The runner threads the summary
  without altering any stage behaviour.
- Keep the change mechanical and scoped to reporting collection health; do not add retry/conditional-GET
  or any new collection behaviour.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `IEvidenceCollector.CollectAsync` returns `CollectionResult(Evidence, Summary)`; both collectors
      populate a `CollectionSummary` (RSS per feed, local-file per file) with a deterministic `Failures`
      list.
- [ ] `RadarPipelineRunner` iterates `collected.Evidence` and surfaces the summary into
      `RadarPipelineResult` (`SourcesChecked`/`SourcesFailed`, and the full `CollectionSummary` if added);
      all existing stage behaviour and counts are unchanged.
- [ ] `Worker` logs sources checked/failed on run completion.
- [ ] Collector, runner, worker, and integration tests updated; build/test green.
