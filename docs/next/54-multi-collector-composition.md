# Task: Multi-collector composition and additive, config-driven collector enablement

## Overview

This is the **keystone** of the multi-collector prep arc. Today the pipeline can run exactly one
evidence collector: `RadarPipelineRunner` takes a single `IEvidenceCollector`, and
`RadarWorkerServices.AddRadarWorker` selects exactly one collector by the `Radar:CollectorKind` string
in a mutually-exclusive `if/else` (`rss` | `localfile`, fail-fast on unknown). That single-source limit
is the measured bottleneck behind the first live run: with only the RSS press-release feed, every one
of the 7 watch-universe companies scored `EvidenceConfidenceScore` ~40 and landed on "Ignore / Low
signal", because `RadarScoreFormulaV1`'s `EvidenceConfidenceScore` rewards **source-type diversity**
(the `divFactor` term, AD-6). A customer win corroborated across, say, a filing **and** a government
contract clears the `Watch`/`Investigate` threshold — but diversity requires running multiple
collectors at once, which the pipeline cannot do.

This slice makes the pipeline run **all registered collectors** and **merge** their `CollectionResult`s
(evidence concatenated in a deterministic order; `CollectionSummary` aggregated across sources), and
makes collector **enablement config-driven and additive** (a list of collector kinds) instead of a
single mutually-exclusive choice. It does **not** add any new collector — it removes the single-collector
ceiling so the SEC / government-contract / patent collectors can be built later as independent
worktrees. After this lands, adding a collector is a small, low-conflict change: register it additively
and add its kind to the enabled list.

Determinism and provenance are preserved: collectors run in a stable order, evidence keeps each
collector's declared `SourceType`, and cross-collector duplicates are still resolved by the existing
insert-only `ContentHash` dedupe (`IEvidenceRepository.AddIfNewAsync`, AD-1).

---

## Assignment

Worktree: pending
Dependencies: None (builds on the merged collector contract from slice 42).
Conflicts with: Slice 55 only in spirit — they touch **disjoint** files (54: runner + worker DI +
worker options + appsettings + their tests; 55: the source-type enum, `CollectionContext`,
`CompanySourceFeed`, the RSS collector + their tests), so they *could* run in parallel. Recommend
sequencing **54 → 55** because 54 is the keystone. Do not parallelize 54 with any future slice that
edits `RadarPipelineRunner.cs`, `RadarWorkerServices.cs`, or `RadarWorkerOptions.cs`.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Collectors/CollectionResultMerger.cs   # NEW: pure merge of many CollectionResults
src/Radar.Application/Pipeline/RadarPipelineRunner.cs        # MODIFIED: consume IEnumerable<IEvidenceCollector>, merge
src/Radar.Worker/RadarWorkerOptions.cs                       # MODIFIED: replace CollectorKind with Collectors (list)
src/Radar.Worker/RadarWorkerServices.cs                      # MODIFIED: additive, config-driven collector enablement
src/Radar.Worker/appsettings.json                            # MODIFIED: "CollectorKind": "rss" -> "Collectors": ["rss"]
src/Radar.Worker/appsettings.Development.json                # MODIFIED if it sets CollectorKind

tests/Radar.Application.Tests/Collectors/CollectionResultMergerTests.cs   # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs        # MODIFIED: list ctor + multi-collector test
tests/Radar.Worker.Tests/RadarWorkerServicesTests.cs                     # MODIFIED: Collectors list + multi/empty/unknown
```

---

## Implementation details

### `CollectionResultMerger` (new, `Radar.Application.Collectors`)

A pure, stateless static helper so the merge logic is independently unit-testable and the runner stays
a thin orchestrator. No I/O, no AI, no scoring.

```csharp
public static class CollectionResultMerger
{
    /// <summary>
    /// Merges per-collector results into one. Evidence is concatenated in the order the results are
    /// supplied (the caller orders collectors deterministically); the summary sums the scalar counts and
    /// concatenates the per-source failures in the same order. An empty input yields an empty result
    /// (no evidence, CollectionSummary.Empty).
    /// </summary>
    public static CollectionResult Merge(IReadOnlyList<CollectionResult> results);
}
```

- `Evidence` = the concatenation of each result's `Evidence`, in input order (preserving each
  collector's own deterministic ordering). Do **not** re-sort or de-duplicate here — cross-collector
  duplicates are handled downstream by the insert-only `ContentHash` dedupe in the repository (AD-1),
  and re-sorting would lose each collector's intentional ordering.
- `Summary` = `new CollectionSummary(sum(SourcesChecked), sum(SourcesSucceeded), sum(SourcesFailed),
  sum(ItemsCollected), [all failures concatenated in input order])`.
- Empty input → `new CollectionResult([], CollectionSummary.Empty)`.
- Guard with `ArgumentNullException.ThrowIfNull(results)`.

### `RadarPipelineRunner`

- Change the constructor parameter `IEvidenceCollector collector` to
  `IEnumerable<IEvidenceCollector> collectors`. Keep the null-check; materialize **once** into a
  list ordered deterministically by `CollectorName` (`StringComparer.Ordinal`) so the merge order — and
  therefore which collector "wins" a `ContentHash` tie in `AddIfNewAsync` — is stable across runs and
  independent of DI registration order. Store the ordered list in a `private readonly` field.
- In `RunAsync`, replace the single `await _collector.CollectAsync(context, ct)` with: iterate the
  ordered collectors, `await` each `CollectAsync(context, ct)` (sequentially — keeps determinism and
  avoids hammering the network; collectors are I/O-light per run), collect the `CollectionResult`s into
  a list, then `var collected = CollectionResultMerger.Merge(results);`. Everything after that line is
  unchanged: it already consumes `collected.Evidence` and `collected.Summary`.
- `ct.ThrowIfCancellationRequested()` between collectors (cheap, keeps cancellation responsive).
- No other stage changes. The single run-instant `asOfUtc` is still captured **after** all collection
  completes (AD-7) — i.e. after the merge.
- Update the class XML-doc sentence that says it sequences "collect → …" to note it now runs **all**
  registered collectors and merges their results before storing evidence.

### `RadarWorkerOptions`

- Replace `public string CollectorKind { get; init; } = "rss";` with
  `public IReadOnlyList<string> Collectors { get; init; } = ["rss"];` (the ordered list of collector
  kinds to enable). Keep the doc-comment honest: "Which evidence collectors to run, additively. Each
  kind is one of: `rss`, `localfile`."

### `RadarWorkerServices`

- Replace the mutually-exclusive `if/else` block with a loop over `options.Collectors`:
  - Empty/null list → throw `InvalidOperationException` ("Radar:Collectors must enable at least one
    collector; valid kinds are \"rss\" and \"localfile\".") — mirrors the existing fail-fast style.
  - For each kind (case-insensitive): `rss` → `AddRssPressReleaseCollector()`; `localfile` →
    `AddLocalFileCollector(options.EvidenceSourceDirectory)`; anything else → throw
    `InvalidOperationException` naming the bad kind and the valid set (same message shape as today).
  - De-duplicate kinds defensively (e.g. ignore a repeated kind) so a config typo listing `rss` twice
    does not register the RSS collector twice. A `HashSet<string>(StringComparer.OrdinalIgnoreCase)`
    guard is enough.
- `AddRssPressReleaseCollector` / `AddLocalFileCollector` already `AddSingleton<IEvidenceCollector, …>`,
  so multiple enabled kinds compose into the `IEnumerable<IEvidenceCollector>` the runner now consumes.
  `AddRadarPipeline()` needs **no** change (DI injects the enumerable automatically).

### `appsettings.json` / `appsettings.Development.json`

- Change `"CollectorKind": "rss"` to `"Collectors": [ "rss" ]`. Update the Development file the same way
  if it sets `CollectorKind`. (List binding from configuration uses indexed keys, e.g.
  `Radar:Collectors:0`; the JSON array form binds correctly.)

---

## Tests

### `CollectionResultMergerTests` (new)

- **Two results merge:** evidence is `A.Evidence` followed by `B.Evidence` in that order; summary counts
  are the element-wise sums; failures are `A.Failures` then `B.Failures`.
- **Single result is identity:** merging `[r]` returns evidence and summary equal to `r`'s.
- **Empty input:** `Merge([])` returns no evidence and `CollectionSummary.Empty`.
- **Null input throws** `ArgumentNullException`.
- **No de-dup / no re-sort:** two results whose evidence would collide on a downstream hash are both
  present in the merged evidence (merger does not dedupe); ordering within each result is preserved.

### `RadarPipelineRunnerTests` (modified)

- Update the existing runner construction to pass a **single-element** collector list; all existing
  evidence/signal/scoring assertions must still pass (single-collector merge is identity).
- **Multi-collector run:** two fake collectors declaring different `SourceType`s and different
  `CollectorName`s, each returning distinct evidence; assert all evidence from both is stored (new
  count = sum) and that `RadarPipelineResult.Collection` reflects the aggregated summary
  (`SourcesChecked`/`SourcesFailed`/`ItemsCollected` summed). Assert ordering is by `CollectorName`
  (give the lexically-later name the colliding `ContentHash` and confirm the earlier-named collector's
  item is the one stored).

### `RadarWorkerServicesTests` (modified)

- Replace the `Radar:CollectorKind` single-kind tests with `Radar:Collectors:0` (and `:1`) list tests:
  - `Collectors:0 = rss` → graph resolves, at least one `IEvidenceCollector` registered.
  - `Collectors:0 = rss`, `Collectors:1 = localfile` → `GetServices<IEvidenceCollector>()` returns
    **two** collectors; `IRadarPipeline` resolves.
  - Default (no `Collectors` key) → resolves with the `["rss"]` default.
  - Unknown kind (`Collectors:0 = bogus`) → `InvalidOperationException`.
  - Empty list (e.g. set the section to no entries) → `InvalidOperationException`.

---

## Constraints

- Target .NET 10; C# 14.
- **Provenance preserved:** each collector keeps declaring its own `EvidenceSourceType` on its emitted
  evidence; the merger never rewrites `SourceType`. Cross-collector duplicates resolve via the existing
  insert-only `ContentHash` dedupe (AD-1) — the merger must not dedupe or mutate evidence.
- **Determinism:** collectors run in a stable `CollectorName`-ordinal order; the merger concatenates in
  that order without re-sorting; `Failures` stays in source-processing order. `asOfUtc` is still captured
  once, after all collection (AD-7).
- Layering unchanged: `CollectionResultMerger` lives in `Radar.Application.Collectors`; collectors stay
  in Infrastructure. No provider SDK leakage, no DB, no AI (AD-8).
- Keep the change scoped to composition + enablement. Do **not** add a new collector, retries, parallel
  network fan-out, or per-collector scheduling here.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `CollectionResultMerger.Merge` concatenates evidence in input order and aggregates the summary
      (summed counts, concatenated failures); empty input → `CollectionSummary.Empty`; null throws.
- [ ] `RadarPipelineRunner` consumes `IEnumerable<IEvidenceCollector>`, runs all collectors in stable
      `CollectorName` order, merges their results, and feeds the merged evidence/summary into the
      unchanged downstream stages.
- [ ] `RadarWorkerOptions.Collectors` (list) replaces `CollectorKind`; `RadarWorkerServices` enables
      each listed kind additively, de-dupes repeated kinds, and fails fast on an empty list or an unknown
      kind.
- [ ] `appsettings.json` (and Development) use `"Collectors": [ "rss" ]`.
- [ ] DI graph resolves with one and with two collectors; `GetServices<IEvidenceCollector>()` returns
      all enabled collectors.
- [ ] Merger, runner (incl. a multi-collector case), and worker-DI tests updated; build/test green.
