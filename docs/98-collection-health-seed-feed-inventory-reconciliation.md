# Task: Collection-health validation — flag feeds lost between seed-load and collection

## Overview

The feed-Id collision fixed in spec 97 hid for a **full live run** because **nothing flagged that an
enabled data source silently got zero data**. The `sec` collector reporting "0 feed(s) checked"
looked no different from a source legitimately having no feeds. This spec adds a **pipeline
validation / health step** that surfaces exactly that class of silent drop: feeds that were **defined
in the seed but never reached the collectors**.

The sharp, low-false-positive check: reconcile the **feed-type inventory declared in the seed**
against the **feed-type inventory that actually reached the collection context**, universe-wide, and
warn on any **shrinkage** (e.g. the seed declared 7 `sec` feeds but 0 reached the collectors ⇒
FLAG). This is **collector-agnostic** and **seed-relative**, so it catches both the `sec`/`secform4`
and `news`/`newssearch` collapses without needing to know which collector consumes which feed type,
and it does **not** false-positive on legitimate absence: a company that simply has no `usaspending`
feed contributes nothing to either the declared or the reached count for that type, so there is no
shrinkage. It only fires when feeds that *were* declared **vanish** before collection.

**This is diagnostic / observability only** — a health guardrail, mirroring the AD-14 "validation
only, never a scoring input" discipline. It **must not** become evidence, a signal, or a scoring
input; it **must not** change any scoring output; and it **must not** fail-fast the run. It emits
warnings that a human sees: the console log, the weekly report, and the durable run record.

> Sequencing: this builds on spec 97. After the fix, the reconciliation over the real seed is clean
> (no shrinkage) — the value here is **regression protection**: if a future change re-drops feeds,
> this fires. Its tests exercise the shrinkage path with a synthetic collapsed context.

---

## Assignment

Worktree: pending
Dependencies: Spec 97 (fix feed-Id collision) — must be merged first
Conflicts with: None
Estimated time: ~2 hours

---

## Project structure changes

Add (Application):

- `src/Radar.Application/Pipeline/CollectionHealth.cs` — `CollectionHealthSeverity` enum,
  `CollectionHealthWarning` record, `CollectionHealthReport` record.
- `src/Radar.Application/Pipeline/ICollectionHealthValidator.cs` — the validator interface.
- `src/Radar.Application/Pipeline/SeedFeedInventoryValidator.cs` — the reconciliation implementation.

Modify (Application):

- `src/Radar.Application/Pipeline/RadarPipelineRunner.cs` — inject the validator, run it after the
  `CollectionContext` is built, log warnings, thread the report into the run record and the weekly
  report.
- `src/Radar.Application/Pipeline/PipelineRunRecord.cs` — add an optional collection-warnings field.
- `src/Radar.Application/Reporting/WeeklyReportModel.cs` — add an optional health field.
- `src/Radar.Application/Reporting/WeeklyReportBuilder.cs` — thread the health report into the model.
- `src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs` — render a "Collection health"
  section when there are warnings.

Modify (Worker DI):

- Register `ICollectionHealthValidator` → `SeedFeedInventoryValidator` wherever the Application
  pipeline services are registered (find the existing `AddRadar*` registration that news up
  `RadarPipelineRunner` / `WeeklyReportBuilder` and add the validator alongside).

Tests:

- `tests/Radar.Application.Tests/Pipeline/SeedFeedInventoryValidatorTests.cs` (new).
- Extend the existing pipeline-runner and report-renderer tests (see Tests below).

---

## Implementation details

### Typed health model (`CollectionHealth.cs`)

```csharp
namespace Radar.Application.Pipeline;

/// <summary>Diagnostic severity for a collection-health finding. Observability only —
/// never a label, score, or advice language.</summary>
public enum CollectionHealthSeverity
{
    /// <summary>A note, not a defect (e.g. a config observation).</summary>
    Info,
    /// <summary>A real problem: expected data went missing.</summary>
    Warning,
}

/// <summary>One collection-health finding. Purely observational: it references no evidence,
/// carries no label/score, and never influences scoring.</summary>
public sealed record CollectionHealthWarning(
    string Code,
    CollectionHealthSeverity Severity,
    string FeedType,
    int DeclaredInSeed,
    int ReachedCollectors,
    string Message);

/// <summary>The collection-health findings for one run, in deterministic order.</summary>
public sealed record CollectionHealthReport(IReadOnlyList<CollectionHealthWarning> Warnings)
{
    public static CollectionHealthReport Empty { get; } = new([]);
    public bool HasWarnings => Warnings.Count > 0;
}
```

- Use a stable string `Code`, e.g. `"feeds-lost-before-collection"`, so the finding is greppable and
  future-stable independent of the message text.

### Validator interface

```csharp
public interface ICollectionHealthValidator
{
    Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct);
}
```

### `SeedFeedInventoryValidator`

- Depends on `ICompanySeedSource` (Application interface — already the seam the seeder uses) and
  `ILogger<SeedFeedInventoryValidator>`.
- `ValidateAsync`:
  1. Re-read the declared inventory: `var seed = await _seedSource.GetSeedAsync(ct);` then group
     `seed.SourceFeeds` by `FeedType` (case-insensitive, `StringComparer.OrdinalIgnoreCase`) →
     declared count per type. (`ICompanySeedSource` is deterministic and, for the local-file source,
     degrades gracefully to an empty seed on read failure — an empty declared inventory yields **no**
     warnings, which is the correct fail-safe: never invent a warning when the baseline is unknown.)
  2. Group `context.SourceFeeds` by `FeedType` the same way → reached count per type.
  3. For each declared feed type where `declared > reached`, emit a `Warning`:
     `Code = "feeds-lost-before-collection"`, `Severity = Warning`, with `DeclaredInSeed` /
     `ReachedCollectors` and a message like:
     `"Seed declares {declared} '{feedType}' feed(s) but only {reached} reached the collectors — "
     + "feeds were lost between seed-load and collection (duplicate/colliding feed Ids?)."`
  4. Return `new CollectionHealthReport(warnings)` with warnings ordered by `FeedType`
     (`StringComparer.Ordinal`) for determinism (AD-3). Never throw for a data condition; only
     `ct.ThrowIfCancellationRequested()` propagates.
- Keep it a pure reconciliation of two counts-by-type maps — no scoring, no evidence, no signals.

> Design note (record in the PR, not the code): the declared inventory is obtained by **re-reading
> the seed** rather than by detecting the collapse at seed-load time. Re-reading keeps the check
> inside the Application pipeline (so its output flows into the run record and the report) and is a
> cheap, deterministic local-file read with no network. A seed-load-time "N feeds collapsed to M
> distinct Ids" detector was considered but rejected here: it runs at Worker startup, separate from
> the pipeline run, so it would not reach the run record / weekly report.

### Runner wiring (`RadarPipelineRunner`)

- Add `ICollectionHealthValidator` as a constructor dependency (non-nullable; always registered —
  unlike the opt-in AI dependency).
- Immediately after `var context = new CollectionContext(companies, sourceFeeds);` (~line 159),
  before running collectors:
  ```csharp
  var health = await _healthValidator.ValidateAsync(context, ct).ConfigureAwait(false);
  foreach (var w in health.Warnings)
  {
      _logger.LogWarning(
          "Collection health [{Code}]: {Message}", w.Code, w.Message);
  }
  ```
  (Log at `Warning` for `Warning` severity; if any `Info`-severity findings are added later, log
  those at `Information`. Today only `Warning` is produced.)
- Thread `health` into the `PipelineRunRecord` (new field) and pass it to the report builder so it
  reaches the weekly report. The health check does **not** touch any counter, the scoring loop, the
  evidence/signal path, or `asOfUtc` — it is read-only over the already-built context.

### Run record (`PipelineRunRecord`)

- Add a **trailing optional** field so existing on-disk run JSON (written before this slice) still
  deserializes:
  ```csharp
  IReadOnlyList<CollectionHealthWarning>? CollectionWarnings = null);
  ```
  `FilePipelineRunStore` serializes/deserializes `PipelineRunRecord` via `System.Text.Json` with
  `RadarFileStoreJson.Options`; a record field of a list-of-records round-trips automatically.
  Follow the existing enum-serialization convention in `RadarFileStoreJson.Options` (do not
  introduce a new one). Old files lacking the field deserialize to `null` (report footer unaffected;
  `RecentRunSummary` does not read it).
- In `RunAsync`, populate the field from `health.Warnings` when building the `PipelineRunRecord`.

### Weekly report surface

- `WeeklyReportModel`: add a trailing optional `CollectionHealthReport? Health = null`.
- `WeeklyReportBuilder.GenerateAsync`: the builder does not run collection, so pass the health report
  **in**. Simplest seam: add a `CollectionHealthReport? health` parameter to `GenerateAsync`
  (alongside the existing `CollectionSummary collection`) and have `RadarPipelineRunner` pass the run's
  `health` when it calls `_reportBuilder.GenerateAsync(...)`. Set it on the `WeeklyReportModel`.
- `MarkdownWeeklyReportRenderer`: when `model.Health is { HasWarnings: true }`, render a
  `## Collection health` section listing each warning as a bullet
  (`- [{Severity}] {FeedType}: declared {DeclaredInSeed}, reached {ReachedCollectors} — {Message}`),
  in the model-supplied order. Omit the section entirely when there are no warnings (a clean run adds
  nothing to the report). This is plain diagnostic text — **no** report action/label, so it does not
  interact with the AD-9 allowed-label enforcement.

---

## Tests

`SeedFeedInventoryValidatorTests` (use a fake `ICompanySeedSource` returning a chosen
`CompanySeedData`; construct a `CollectionContext` with the "reached" feeds):

1. **Shrinkage flagged.** Seed declares 7 `sec` feeds; the context contains 0 `sec` feeds (simulating
   the pre-fix collapse) ⇒ one `Warning` with `Code = "feeds-lost-before-collection"`,
   `FeedType = "sec"`, `DeclaredInSeed = 7`, `ReachedCollectors = 0`.
2. **Clean when nothing is lost.** Seed declares 7 `sec` + 7 `secform4`; the context contains all 14
   (post-fix reality) ⇒ `Empty` / no warnings.
3. **No false positive on legitimate absence.** Seed declares no `usaspending` feed for a company and
   the context has none either ⇒ no warning for `usaspending`.
4. **Empty seed ⇒ no warnings.** A seed source returning an empty `CompanySeedData` (graceful-degrade
   path) yields no warnings (never invent a baseline).
5. **Deterministic order.** Multiple shrinkage warnings come back ordered by `FeedType` (Ordinal).
6. **Partial shrinkage.** Declared 7, reached 3 ⇒ one `Warning` with those counts (covers the
   count-based, not just zero-based, path).

Pipeline / report tests:

7. **Runner surfaces warnings.** In the existing runner test setup, inject a validator that returns a
   warning and assert the resulting `PipelineRunRecord.CollectionWarnings` carries it (and that
   scoring counters/output are unchanged — the health check is side-effect-free).
8. **Renderer section.** Rendering a `WeeklyReportModel` with a `CollectionHealthReport` containing a
   warning includes the `## Collection health` section and the warning text; a model with no
   warnings (or `Health = null`) renders **no** such section (assert byte-stability of the existing
   clean-run output is unaffected).
9. **Run-record round-trip.** A `PipelineRunRecord` with `CollectionWarnings` serializes and
   deserializes via `RadarFileStoreJson.Options` losslessly; a JSON payload **without** the field
   deserializes with `CollectionWarnings == null` (back-compat).

Update any existing `WeeklyReportBuilder`/renderer tests affected by the new optional
`GenerateAsync` parameter and the new model field (pass `null`/`Empty` where health is irrelevant).

---

## Constraints

- Target .NET 10.
- **Validation is diagnostic only (AD-14 discipline).** The health report is never evidence, never a
  signal, never a scoring input, and must not change any scoring output or any run counter. It is
  computed read-only over the already-built `CollectionContext`.
- **Warn, never fail-fast.** A finding logs + surfaces; it never throws or aborts the run. An
  unreadable seed yields an empty declared inventory and therefore no warnings (fail-safe).
- **Universe-level, seed-relative.** Reconcile declared-vs-reached by **feed type across the whole
  universe** — never a per-company "this company lacks feed X" check (that would false-positive on
  legitimate absence).
- Layering: all new types live in `Radar.Application`; no Infrastructure leakage (the validator
  depends only on the existing `ICompanySeedSource` Application seam). Deterministic ordering (AD-3).
- Keep changes scoped to the health step and its surfaces. Do not rework seed loading, the
  repository, the collectors, or the scoring/formula path.

### Out of scope (explicit)

- Mapping enabled collectors to their consumed feed type to flag "collector enabled but the seed
  declares no feeds of its type at all" (the low-severity config-note case). Collectors expose
  `CollectorName`, not the feed type they consume, so this needs a new seam; the feed-type shrinkage
  check already catches the real defect (lost feeds) without it. Leave as a possible future slice.
- Any hard-error / run-abort behaviour for missing feeds.
- Turning the health signal into a scoring gate or a report action/label.

---

## Acceptance criteria

- [ ] `ICollectionHealthValidator` + `SeedFeedInventoryValidator` reconcile the seed-declared
      feed-type inventory against the collection-context inventory and emit a `Warning` per feed type
      where declared > reached, in deterministic order.
- [ ] The check produces **no** warning for legitimately-absent feed types and **no** warning on an
      empty/unreadable seed.
- [ ] `RadarPipelineRunner` runs the validator after building the context, logs each warning at
      `Warning`, and does not change any counter, `asOfUtc`, or scoring output.
- [ ] Warnings are surfaced in the durable `PipelineRunRecord` (new optional field, old run files
      still deserialize) **and** in the weekly report's `## Collection health` section; a clean run
      adds nothing to the report.
- [ ] Tests cover shrinkage-flagged, clean, legitimate-absence, empty-seed, partial-shrinkage,
      deterministic order, runner surfacing, renderer section, and run-record round-trip/back-compat.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
