# Task: Run-pipeline polish — write the weekly report to disk and wire RSS end-to-end

## Overview

The MVP's final acceptance criterion (master spec): *Dean adds companies + RSS feeds to a watch-universe
file, runs **one local command**, and reads a weekly report with the evidence behind each surfaced
company.* Two gaps remain:

1. The weekly report is rendered and stored only in the in-memory `IReportRepository`
   (`RadarReport.MarkdownContent`) — it is **never written to disk**. The master spec wants
   `data/reports/weekly/radar-weekly-{yyyy-MM-dd}.md`.
2. The Worker host (`RadarWorkerServices`) wires only the **local-file** collector. The collector-driven
   MVP should run the **RSS** collector (slice 28) — selectable by configuration — so one `dotnet run`
   collects → extracts → scores → reports from real feeds.

This slice closes both: a file report writer + host wiring (collector selection + raw-evidence root +
report root). It adds no new pipeline stage; the report already carries collector-sourced evidence
(`SourceName`/`SourceUrl` per `ReportEvidenceRef`).

---

## Assignment

Worktree: any
Dependencies: 26-collector-seam, 28-rss-collector, 29-raw-evidence-file-writer
Conflicts with: 28, 29, 30 (host wiring / `RadarPipelineRunner` / DI). Sequence last in the chain.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Reporting/
  IReportFileWriter.cs               # NEW (Application abstraction)

src/Radar.Infrastructure/FileSystem/
  FileReportWriter.cs                # NEW: writes data/reports/weekly/...md
  FileReportWriterOptions.cs         # NEW: { required string RootDirectory }

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: write report markdown after build
src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs  # MODIFIED

src/Radar.Worker/
  RadarWorkerOptions.cs              # MODIFIED: CollectorKind, EvidenceRawDirectory, ReportDirectory
  RadarWorkerServices.cs             # MODIFIED: select collector + register file writers
  appsettings.json                   # MODIFIED: new config keys

tests/Radar.Infrastructure.Tests/FileSystem/FileReportWriterTests.cs   # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs     # MODIFIED (fake writer)
tests/Radar.Worker.Tests/...                                          # MODIFIED (DI graph resolves)
```

---

## Implementation details

### Report file writer

```csharp
namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>Writes a built weekly report's markdown to local storage. Returns the written path.</summary>
public interface IReportFileWriter
{
    Task<string> WriteAsync(RadarReport report, CancellationToken ct);
}
```

`FileReportWriter` — ctor deps (`ThrowIfNull`): `FileReportWriterOptions`, `ILogger<FileReportWriter>`.
- Path: `{RootDirectory}/weekly/radar-weekly-{PeriodEndUtc:yyyy-MM-dd}.md`.
- Create directories; write `report.MarkdownContent` (UTF-8, `\n` preserved). Overwriting an existing
  report file **is** allowed (a report is a derived view, not immutable evidence — contrast AD-1, which
  governs evidence only). `LogInformation` the written path. Catch
  `IOException`/`UnauthorizedAccessException` → `LogWarning` and return the attempted path (a disk hiccup
  must not crash the run; the in-memory report still exists).

`FileReportWriterOptions { public required string RootDirectory { get; init; } }` (e.g. `data/reports`).

### `RadarPipelineRunner`

Inject `IReportFileWriter` (new ctor dep, `ThrowIfNull`). In the Stage-7 block, after
`_reportBuilder.GenerateAsync(...)`, when a report was generated, `await _reportFileWriter.WriteAsync(
report.Report, ct)`. Keep the existing `GenerateReport` gate and `reportId` return unchanged.

### Host wiring (`RadarWorkerServices` + `RadarWorkerOptions`)

- `RadarWorkerOptions`: add
  - `string CollectorKind { get; init; } = "rss";`  (`"rss"` | `"localfile"`),
  - `string EvidenceRawDirectory { get; init; } = "data/evidence/raw";`
  - `string ReportDirectory { get; init; } = "data/reports";`
- `AddRadarWorker`:
  - Register the file writers: `services.AddFileRawEvidenceStore(options.EvidenceRawDirectory)` (slice
    29) and `services.AddFileReportWriter(options.ReportDirectory)`.
  - Select the collector by `CollectorKind` (case-insensitive): `"rss"` →
    `AddRssPressReleaseCollector()`; `"localfile"` → `AddLocalFileCollector(options.EvidenceSourceDirectory)`.
    Throw `InvalidOperationException` with a clear message for an unknown kind (fail fast, like the
    interval check).
  - Leave the seed/persistence/application/pipeline registrations as they are.
- `appsettings.json`: add `CollectorKind: "rss"`, `EvidenceRawDirectory`, `ReportDirectory`. A committed
  example `data/companies.json` with a `sourceFeeds` entry is helpful but **must not** point at a live
  url in source control beyond an illustrative `example.com` (no secrets/urls that scrape in violation
  of terms — master Non-Goals).

### DI helper

```csharp
public static IServiceCollection AddFileReportWriter(
    this IServiceCollection services, string rootDirectory)
{
    services.AddSingleton(new FileReportWriterOptions { RootDirectory = rootDirectory });
    services.AddSingleton<IReportFileWriter, FileReportWriter>();
    return services;
}
```

---

## Tests

### `FileReportWriterTests` (temp dir, clean up)
- Writes `weekly/radar-weekly-{periodEnd:yyyy-MM-dd}.md` containing the report's markdown.
- Re-writing the same period overwrites (reports are derived, not immutable).
- IO failure returns the attempted path without throwing.

### `RadarPipelineRunnerTests` (MODIFIED)
- With `GenerateReport = true` and a fake `IReportFileWriter`, the runner writes the built report's
  markdown exactly once; with `GenerateReport = false`, it does not write.

### `Radar.Worker.Tests` (MODIFIED)
- The DI graph resolves with `CollectorKind = "rss"` (the RSS collector + typed `HttpClient` register)
  and with `"localfile"`; an unknown kind throws a clear error.

---

## Constraints

- Target .NET 10. All file I/O in Infrastructure (`FileReportWriter`); Application sees only
  `IReportFileWriter`. No provider SDK leakage (AD-5).
- AD-1 governs **evidence** immutability only — reports are derived views and may be overwritten. Raw
  evidence files (slice 29) remain insert-only.
- Files-first MVP (AD-8): no database. The whole pipeline runs locally from one command
  (`dotnet run --project src/Radar.Worker`) and writes a markdown report + raw evidence files under
  `data/`.
- Output-language hard rule unchanged; report shows collector source name/url per evidence ref.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `IReportFileWriter` + `FileReportWriter` write the weekly report to
      `data/reports/weekly/radar-weekly-{yyyy-MM-dd}.md`.
- [ ] `RadarPipelineRunner` writes the built report to disk when `GenerateReport` is true.
- [ ] The Worker selects the collector via `CollectorKind` (`rss` default) and registers the
      raw-evidence + report file writers; an unknown kind fails fast.
- [ ] One local command runs collect (RSS) → extract → resolve → score → report, producing raw evidence
      files and a weekly markdown report, with collector source links visible per surfaced company.
- [ ] Tests cover report writing, runner wiring (write/no-write), and collector selection in the host DI
      graph; `build`/`test` green.
