# Task: End-to-End Integration Tests + Evidence-Quality Input + Same-Run Window Fix

## Overview

A smoke-run of the full pipeline surfaced two real issues and confirmed we lack a true black-box
end-to-end test. This slice fixes both issues and adds the missing test, in one coherent change
("make the pipeline production-correct, then lock it with an end-to-end test"):

- **Part A — Evidence quality is an input, not hard-coded.** `LocalFileEvidenceCollector` currently
  stamps **every** item `EvidenceQuality.Unknown`. That always trips the reviewer's "weak source" rule
  (halving confidence) and caps `EvidenceConfidenceScore`, so *every* company is stuck on
  "Needs more evidence" regardless of how strong its trajectory is. Let an evidence file **declare its
  quality**.
- **Part B — Same-run window edge.** `RadarPipelineRunner` captures `asOfUtc` (the scoring window end)
  at the *start* of the run, but the collector stamps each evidence's `CollectedAtUtc` slightly *later*.
  Evidence with no `publishedAtUtc` therefore gets `ObservedAtUtc` just **after** `asOfUtc` and falls
  outside the `(start, end]` window — so freshly collected evidence scores from **0 signals** in the
  same run. Capture `asOfUtc` **after** collection so just-collected evidence is in-window.
- **Part C — `Radar.IntegrationTests`.** A new project that drives the **real wired DI graph**
  (real `LocalFile*` sources, real keyword extractor, all stages) from **temp-dir JSON fixtures** and
  asserts on **outputs** (the `RadarPipelineResult` and the rendered report markdown). Faked only at the
  true seams: a fixed `FakeTimeProvider` clock and the file inputs. With Part A done, the test can prove
  quality flows through to a stronger outcome; with Part B done, same-run evidence scores reliably.

CI already runs `dotnet test Radar.sln`, so adding the project to the solution is enough — **no CI YAML
change.**

> Provenance and layering are unchanged. Parts A/B are minimal, surgical production fixes; do not
> expand their scope. Keep all existing tests green (update only those whose expectations the fixes
> legitimately change).

---

## Assignment

Worktree: pending
Dependencies: 22-pipeline-runner, 23-company-watch-universe-seed, 24-worker-host-wiring
Conflicts with: none
Estimated time: ~2-3 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/Sources/
  LocalFileEvidenceDocument.cs   # CHANGED (Part A): add optional Quality
  LocalFileEvidenceCollector.cs  # CHANGED (Part A): map declared quality (default Unknown)

src/Radar.Application/Pipeline/
  RadarPipelineRunner.cs         # CHANGED (Part B): capture asOfUtc after collection

tests/Radar.Infrastructure.Tests/Sources/
  LocalFileEvidenceCollectorTests.cs  # CHANGED (Part A): quality parsing cases

tests/Radar.Application.Tests/Pipeline/
  RadarPipelineRunnerTests.cs    # CHANGED (Part B): same-run-window regression test

tests/Radar.IntegrationTests/    # NEW (Part C)
  Radar.IntegrationTests.csproj
  PipelineEndToEndTests.cs
  TempPipelineFixtures.cs        # optional helper
Radar.sln                        # CHANGED: add the new test project
```

---

## Part A — Evidence quality as an input

### `LocalFileEvidenceDocument`
Add an optional `string? Quality` member (keep all members nullable, per the DTO's skip-don't-throw
contract).

### `LocalFileEvidenceCollector.ReadDocumentAsync`
Replace the hard-coded `Quality: EvidenceQuality.Unknown` with a parse of `doc.Quality`:

- Parse case-insensitively to `EvidenceQuality`; accept only a **defined** enum name.
- **Reject digit-only input** (e.g. `"4"`) — `Enum.TryParse` would otherwise accept it; mirror the
  guard `ExtractedSignalMapper` already uses.
- On missing / blank / unparseable value → default to `EvidenceQuality.Unknown` (and log at Debug).

Use a small private helper, e.g.:

```csharp
private static EvidenceQuality ParseQuality(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Trim().All(char.IsDigit))
        return EvidenceQuality.Unknown;
    return Enum.TryParse<EvidenceQuality>(value, ignoreCase: true, out var q) && Enum.IsDefined(q)
        ? q : EvidenceQuality.Unknown;
}
```

`SourceType` stays `LocalFile` (out of scope). Note the **behavioural consequence** to honour in tests:
non-weak quality (`Medium`/`High`/`PrimarySource`) no longer trips the reviewer's weak-source rule, so
such signals keep full confidence and reach `Approve`, and `EvidenceConfidenceScore` rises.

### Tests (`LocalFileEvidenceCollectorTests`)
Add cases: `"PrimarySource"` / `"High"` → mapped to that quality; missing → `Unknown`; garbage
(`"bogus"`, `"4"`) → `Unknown`. Keep existing cases green.

---

## Part B — Capture `asOfUtc` after collection

### `RadarPipelineRunner.RunAsync`
Move the `asOfUtc` capture to **after** `await _collector.CollectAsync(ct)` returns (before it is first
used — by the mapper, scoring window, and report period). Update the comment to explain why: the run
instant must not precede the collection that produced the run's evidence, or just-collected evidence
(whose `ObservedAtUtc` falls back to `CollectedAtUtc`) would sort just outside the `(start, end]` window.
No other logic changes; `asOfUtc` still feeds mapper `createdAtUtc`, scoring `windowEndUtc`, and report
`periodEndUtc` identically.

### Regression test (`RadarPipelineRunnerTests`)
The existing fixed clock can't reproduce the edge (collection time == `asOfUtc`). Add a deterministic
**advancing** clock test double whose `GetUtcNow()` returns a monotonically increasing instant
(e.g. base + N ticks per call), and a collector whose `CollectAsync` stamps the returned evidence's
`CollectedAtUtc` from that injected clock (so collection time precedes the post-collection `asOfUtc`).
With **no** `publishedAtUtc`, assert the freshly collected evidence's signal **is** scored
(`CompaniesScored >= 1`, the snapshot reflects the signal). This passes with the fix and fails without
it — a genuine guard. Keep the other runner tests unchanged.

---

## Part C — `Radar.IntegrationTests` (real graph, faked seams)

### Project
`net10.0` xUnit (mirror `Radar.Worker.Tests` for the `Microsoft.NET.Test.Sdk`/xunit package set). Add
`Microsoft.Extensions.TimeProvider.Testing` (`10.1.0`) for `FakeTimeProvider` and
`Microsoft.Extensions.DependencyInjection`. Reference `Radar.Application`, `Radar.Infrastructure`,
`Radar.Domain`, `tests/Radar.TestSupport`. Register in `Radar.sln`
(`dotnet sln Radar.sln add tests/Radar.IntegrationTests/Radar.IntegrationTests.csproj`).

### Composition — real graph, faked seams only
Build with the same extension methods `RadarWorkerServices.AddRadarWorker` uses, registering the fixed
clock **first** so it wins over the libraries' `TryAddSingleton(TimeProvider.System)`:

```csharp
var fixedNow = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(new FakeTimeProvider(fixedNow));  // FIRST
services.AddLogging();
services.AddInMemoryRadarPersistence();
services.AddRadarApplicationServices();
services.AddLocalFileCollector(evidenceDir);       // real collector, temp dir
services.AddLocalFileCompanySeed(seedFilePath);    // real seed source, temp file
services.AddRadarPipeline();
await using var sp = services.BuildServiceProvider();

await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
var result = await sp.GetRequiredService<IRadarPipeline>().RunAsync(ct);
var report = await sp.GetRequiredService<IReportRepository>().GetByIdAsync(result.ReportId!.Value, ct);
var items  = await sp.GetRequiredService<IReportRepository>().GetItemsAsync(result.ReportId!.Value, ct);
```

Default options (30-day window, 7-day period). Fixtures get `publishedAtUtc` a few days before
`fixedNow` (inside both windows). Use fixed literal Guids for seed companies (stable tiebreaks).

### Temp fixtures
Write to a unique temp dir per test (`Path.Combine(Path.GetTempPath(), "radar-it-" + Guid.NewGuid())`,
with an `evidence/` subfolder), delete on dispose (`IDisposable`/`IAsyncLifetime`). Fixture shapes:
- **companies.json:** `{ "companies": [ { "id": "<guid>", "name": "...", "ticker": "...", "aliases": ["..."] } ] }`.
- **evidence/*.json:** `{ "sourceName": "...", "title": "...", "rawText": "...", "publishedAtUtc": "<iso>", "quality": "..." }`.
  Resolution matches on `sourceName` (the extractor sets `CompanyMention = SourceName`), so a file's
  `sourceName` must equal a seeded company name/alias to resolve. `rawText` must contain
  `KeywordSignalExtractor` trigger phrases — e.g. `"launches"` (ProductLaunch), `"multi-year deal"`
  (CustomerWin), `"partners with"` (StrategicPartnership), `"raises $"` (CapitalRaise),
  `"awarded contract"` (GovernmentContract), `"cuts guidance"` (GuidanceChange **Negative**).

### Assertion guidance
Assert **behaviour/structure**, not formula magic numbers or generated Guids (don't duplicate
`RadarScoreFormulaV1Tests`; don't break under a future formula version): relative magnitudes,
ordering, label-in-allowed-set, provenance consistency, and substrings.

### Tests (all via real temp-file inputs)
1. **Golden path.** ~3 seeded companies with positive-signal evidence (one with multiple phrases).
   Assert exact `RadarPipelineResult` counts; markdown contains all three disclaimers; companies ranked
   by opportunity; each item shows its snapshot id and ≥1 evidence link; every label ∈ allowed set.
2. **Quality flows through (Part A end-to-end).** Two otherwise-equivalent positive-signal companies,
   one backed by `"quality": "PrimarySource"` evidence and one by `"Unknown"` (or omitted). Assert the
   PrimarySource company has a strictly higher `EvidenceConfidenceScore`, and that **at least one**
   surfaced label is something **other than** "Needs more evidence" (proving quality lifts the outcome —
   impossible before Part A).
3. **Unresolved mention → needs review.** An evidence file whose `sourceName` is not seeded does not
   score a company and appears under "Signals needing review".
4. **Direction matters.** A company whose only signal is `"cuts guidance"` (Negative) has
   `TrajectoryScore < 50` and ranks below a positive-signal company.
5. **Same-run window (Part B end-to-end).** An evidence fixture with **no** `publishedAtUtc` still
   scores its company (not dropped from the window) — guards the Part B fix through the real collector.
6. **Idempotent re-run.** Running twice over the same inputs yields `EvidenceNew == 0` /
   `SignalsExtracted == 0` on the second run (dedupe by content hash, AD-1) and still a valid report.
7. **Output-language guard.** The markdown contains **none** of `buy`, `sell`, `guaranteed upside`,
   `safe bet` (case-insensitive), and only allowed labels appear.

---

## Constraints

- Target .NET 10. Parts A/B are minimal production fixes; Part C is test-only.
- Preserve provenance, layering, AD-1..AD-6. Quality parsing defaults to `Unknown` and never throws
  (DTO skip-don't-throw contract). The Part B change is capture-ordering only — `asOfUtc` still feeds
  mapper/scoring/report identically.
- Integration tests fake **only** the clock and file inputs; the rest of the graph is the real
  production code. Deterministic: fixed clock + fixed-Guid seeds + fixed fixtures; assert on
  labels/ordering/structure/relative magnitudes/substrings, not Guids or exact formula scores.
- New project registered in `Radar.sln` so `dotnet test Radar.sln` runs it in CI (no YAML change).

---

## Acceptance criteria

- [ ] **Part A:** `LocalFileEvidenceDocument` has an optional `Quality`; the collector maps a declared
      quality (case-insensitive, defined-enum-only, digit-only rejected) and defaults to `Unknown`;
      collector tests cover mapped/missing/garbage. `SourceType` unchanged.
- [ ] **Part B:** `RadarPipelineRunner` captures `asOfUtc` after collection; a deterministic
      advancing-clock regression test proves freshly collected (no-`publishedAtUtc`) evidence scores.
- [ ] **Part C:** `tests/Radar.IntegrationTests` exists, is in `Radar.sln`, composes the real graph with
      `FakeTimeProvider` + real `LocalFile*` sources over temp-dir fixtures, and the seven scenarios pass
      (incl. quality-flows-through and same-run-window proving Parts A/B end-to-end).
- [ ] Assertions are behavioural/structural (no formula magic numbers, no generated-Guid literals).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
