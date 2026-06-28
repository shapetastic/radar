# Task: End-to-End Pipeline Integration Tests (real graph, faked seams)

## Overview

We have strong unit tests and a runner-level integration-*style* test (`RadarPipelineRunnerTests`),
but **no black-box end-to-end test** that drives the *actual wired dependency graph* — including the
real `LocalFileEvidenceCollector`, real `KeywordSignalExtractor`, real resolver/reviewer/scoring/
report stages — from **file inputs** and asserts on the **rendered report output**. The existing
runner test fakes the collector *and* extractor, news-up the runner directly (not via DI), and asserts
on result counts / repo state — never on the report markdown.

This slice adds a new **`Radar.IntegrationTests`** project that closes that gap. It composes the real
graph via the same `Add*` extension methods the Worker uses, **fakes only the true external seams**,
and asserts on inputs→outputs (the `RadarPipelineResult` and the persisted report markdown).

**Fake only at the seams:**
- **Clock** → a fixed `FakeTimeProvider` (deterministic).
- **Inputs** → real JSON fixtures written to a **temp directory**, read by the real
  `LocalFileEvidenceCollector` / `LocalFileCompanySeedSource`.

Everything else (persistence, resolution, extraction, review, scoring, report building/rendering, DI
wiring) is the real production code. CI already runs `dotnet test Radar.sln`, so adding this project to
the solution is sufficient — **no workflow/CI YAML change is needed.**

> This is a test-only slice: **no production code changes.** If the test surfaces a production bug,
> stop and report it rather than editing production code to make the test pass.

---

## Assignment

Worktree: pending
Dependencies: 22-pipeline-runner, 23-company-watch-universe-seed, 24-worker-host-wiring
Conflicts with: none (new project + solution entry only)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
tests/Radar.IntegrationTests/
  Radar.IntegrationTests.csproj      # NEW: net10.0 xUnit; refs Application, Infrastructure, Domain, TestSupport
  PipelineEndToEndTests.cs           # NEW: the end-to-end tests
  TempPipelineFixtures.cs            # NEW (optional): helper to write seed/evidence JSON to a temp dir
Radar.sln                            # CHANGED: add the new test project
```

Mirror an existing test csproj (e.g. `Radar.Worker.Tests`) for the SDK/xunit/`Microsoft.NET.Test.Sdk`
package set and `net10.0` target. Add `Microsoft.Extensions.TimeProvider.Testing` (already used by
`Radar.Worker.Tests`, version `10.1.0`) for `FakeTimeProvider`, and
`Microsoft.Extensions.DependencyInjection` for `ServiceCollection`. Reference
`Radar.Application`, `Radar.Infrastructure`, `Radar.Domain`, and `tests/Radar.TestSupport`. Register
the project in `Radar.sln` (`dotnet sln Radar.sln add ...`).

---

## Implementation details

### Composition — real graph, faked seams

Build the provider with the **same extension methods** `RadarWorkerServices.AddRadarWorker` uses, but
register the fixed clock **first** so it wins (`AddRadarApplicationServices` / `AddLocalFileCollector`
/ `AddLocalFileCompanySeed` each call `TryAddSingleton(TimeProvider.System)`, which is a no-op once our
instance is present):

```csharp
var fixedNow = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
var clock = new FakeTimeProvider(fixedNow);

var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(clock);          // FIRST — beats the libraries' TryAddSingleton default
services.AddLogging();                                // NullLogger-equivalent; no console needed
services.AddInMemoryRadarPersistence();
services.AddRadarApplicationServices();
services.AddLocalFileCollector(evidenceDir);          // real collector, temp dir
services.AddLocalFileCompanySeed(seedFilePath);       // real seed source, temp file
services.AddRadarPipeline();
await using var sp = services.BuildServiceProvider();
```

Then run the same sequence the Worker runs:

```csharp
var seeded  = await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
var result  = await sp.GetRequiredService<IRadarPipeline>().RunAsync(ct);
var report  = await sp.GetRequiredService<IReportRepository>().GetByIdAsync(result.ReportId!.Value, ct);
var items   = await sp.GetRequiredService<IReportRepository>().GetItemsAsync(result.ReportId!.Value, ct);
// assert on result counts, report!.MarkdownContent, and items
```

Use the **default options** (30-day scoring window, 7-day report period) — the fixtures' dates are
chosen relative to `fixedNow` to fall inside both windows.

### Temp fixtures

Write fixtures to a unique temp directory per test class/instance and delete it on dispose
(`IDisposable`/`IAsyncLifetime`). Use `Path.Combine(Path.GetTempPath(), "radar-it-" + Guid.NewGuid())`
with an `evidence/` subfolder and a `companies.json` file. `Guid.NewGuid()` is fine in test setup (the
no-`Date.Now`/`Guid` determinism rule applies to *production*, not test fixtures).

Seed/evidence JSON shapes (match the real DTOs):
- **companies.json:** `{ "companies": [ { "id": "<guid>", "name": "...", "ticker": "...", "aliases": ["..."] } ] }`.
  `id` must be a parseable Guid; use fixed literal Guids so ordering/tiebreaks are stable.
- **evidence/*.json:** `{ "sourceName": "...", "title": "...", "rawText": "...", "publishedAtUtc": "<iso8601>" }`.
  **Resolution matches on `sourceName`** (the keyword extractor sets `CompanyMention = SourceName`), so
  a file's `sourceName` must equal a seeded company name/alias to resolve. Give each fixture a
  `publishedAtUtc` a few days before `fixedNow` (inside the 30-day scoring window AND the 7-day report
  period). Craft `rawText` to contain `KeywordSignalExtractor` trigger phrases, e.g.:
  `"launches"` (ProductLaunch), `"multi-year deal"` (CustomerWin), `"partners with"`
  (StrategicPartnership), `"raises $"` (CapitalRaise), `"awarded contract"` (GovernmentContract),
  `"cuts guidance"` (GuidanceChange, **Negative**).

> **Expected labels with local-file evidence.** `LocalFileEvidenceCollector` stamps every item
> `EvidenceQuality.Unknown`, which makes the reviewer halve confidence and keeps `EvidenceConfidenceScore`
> low — so the action policy will report **"Needs more evidence"** for these companies, NOT
> "Thesis improving". Assert that each surfaced label is one of the five allowed labels; do **not**
> write an assertion expecting "Thesis improving"/"Investigate" from Unknown-quality evidence.

### Assertion guidance

Assert **behaviour and structure**, not formula-specific magic numbers (so these tests don't duplicate
`RadarScoreFormulaV1Tests` and don't break under a future formula version):
- Relative/behavioural: a positive-signal company's `TrajectoryScore > 50`; the negative-signal
  (guidance-cut) company's `< 50`; report ordering is by opportunity (positive companies appear above
  the negative one).
- Structural/provenance: `result` counts are exact for the fixtures; every report item's
  `ScoreSnapshotId` resolves to a real snapshot; evidence links are present for scored companies; the
  markdown cites the snapshot id and the evidence.
- Output-language (lock the hard rule end-to-end).

---

## Tests (all via real temp-file inputs)

1. **Golden path.** ~3 seeded companies, each with a positive-signal evidence file (one with multiple
   trigger phrases). Assert: exact `RadarPipelineResult` counts (collected/new/extracted/valid/
   approved/needsReview/scored); the markdown contains all three disclaimers
   (`Not financial advice`, `For research only`, `Human review required`); companies appear ranked by
   opportunity; each item shows its score-snapshot id and at least one evidence link; every label is in
   the allowed set.
2. **Unresolved mention → needs review.** Add an evidence file whose `sourceName` is **not** seeded.
   Assert it does not score a company and the company/mention appears under "Signals needing review" in
   the markdown.
3. **Direction matters.** Include one company whose only signal is a `"cuts guidance"` (Negative) item.
   Assert its `TrajectoryScore < 50` and it ranks **below** a positive-signal company in the report.
4. **Idempotent re-run.** Run the pipeline twice over the same inputs. Assert the second run reports
   `EvidenceNew == 0` and `SignalsExtracted == 0` (dedupe by content hash; AD-1), and still produces a
   valid report.
5. **Output-language guard.** Assert the rendered markdown contains **none** of the banned phrases
   (case-insensitive): `buy`, `sell`, `guaranteed upside`, `safe bet`, and that the only report labels
   present are from the allowed set
   (`Investigate`, `Watch`, `Needs more evidence`, `Thesis improving`, `Thesis deteriorating`).
6. **Window exclusion (documents the boundary).** Add an evidence file dated **before** the 30-day
   scoring window (e.g. 60 days before `fixedNow`). Assert its company is not surfaced as an
   improver / its signal does not contribute to the in-window score (it may still appear as evidence
   collected). Keeps the windowing behaviour pinned.

---

## Constraints

- Target .NET 10. **Test-only slice — no production code changes.**
- Fake only the clock (`FakeTimeProvider`) and the file inputs (temp dir); use the **real** DI graph and
  the **real** `LocalFile*` sources. Do not fake the collector/extractor/resolver/reviewer/scoring/report.
- Deterministic: fixed clock + fixed-Guid seed companies + fixed fixtures. Assert on labels/ordering/
  structure/relative magnitudes and substrings — **not** on generated Guids or formula-specific exact
  scores.
- Clean up the temp directory on dispose.
- New project must be in `Radar.sln` so `dotnet test Radar.sln` runs it in CI (no YAML change).

---

## Acceptance criteria

- [ ] `tests/Radar.IntegrationTests` exists, targets `net10.0`, references Application/Infrastructure/
      Domain/TestSupport, and is registered in `Radar.sln`.
- [ ] Tests compose the real graph via the `Add*` extensions with a fixed `FakeTimeProvider` and real
      `LocalFile*` sources reading temp-dir JSON fixtures; no production code is modified.
- [ ] The six scenarios above pass: golden path, unresolved→needs-review, direction-matters ordering,
      idempotent re-run, output-language guard, and window exclusion.
- [ ] Assertions are behavioural/structural (no formula-specific magic numbers, no Guid literals from
      generated ids).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green (the new
      project included).
