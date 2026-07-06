# Task: Price-efficacy visual — join persisted score history to daily price, render a per-company score-vs-price overlay (READ-ONLY over AD-14; validation, never a scoring input)

> **DIRECTED FEATURE — the READ side of AD-14 (read this first).** This is the first slice of the
> long-backlogged **score-vs-price efficacy visual** — the validation/backtest layer that closes Radar's
> loop (it produces theses but never checks itself against price outcomes). It is a **directed** maintainer
> task, not the generic planner loop.
>
> **THE LOAD-BEARING CONSTRAINT — get this right or the slice is wrong.** Price is **validation-only**
> (AD-14). This efficacy layer **READS** score-snapshot history + the price reference store and **EMITS a
> visual/dataset**. It must **NEVER** flow back into `evidence → signal → score`. Price must never become
> evidence, a signal, or a scoring/signal input. The moment price feeds the formula, Radar becomes a
> momentum-chaser and betrays "signals before stories / not a trading bot" (CLAUDE.md,
> `radar-philosophy.md`). Spec 92 recorded **AD-14** (price = a separate reference seam); **this slice is
> the READ side of that same boundary.** It touches **no** collector, evidence type, signal, extractor,
> formula, `ScoringConfigVersion`, `IRadarPipeline`, or the weekly report. This slice **amends AD-14** to
> record that the efficacy/validation-reporting layer is **read-only over score history + price**.

## Overview

Radar persists a score snapshot per company per run (`data/scores/{companyId}/{snapshotId}.json`) and — since
spec 92 (AD-14) — a daily price reference series per ticker (`data/prices/{ticker}.json`). Nothing yet
**joins** them. This slice builds that JOIN and renders the smallest valuable, zero-philosophy-risk artifact:
for each seeded company, overlay its (sparse, per-run) score series against its (dense, daily) price series
over time, as a self-contained inline **SVG** plus a **CSV** dataset export, written to a new `data/efficacy/`
directory. It ships **the mechanism + a basic per-company visual ONLY** — no forward-return statistics, no
benchmark comparison, no dashboard (all sketched under "Future slices").

The value of slice 1 is **the mechanism**, not statistical power: 8 names × a handful of dev-run snapshots is
noise. But the join accrues value over months and more tickers, and building it read-only-first makes the
philosophy boundary structural before any statistics are computed.

**Two correctness requirements are load-bearing (not niceties):**

1. **Read-only over AD-14.** The efficacy subsystem reads `IScoreSnapshotFileStore` + `IPriceHistoryStore` +
   `ICompanyRepository` and writes **only** efficacy artifacts under `data/efficacy/`. It depends on **no**
   evidence/signal/scoring/pipeline write path. Enforced structurally + by test.
2. **Segment the score series by `ScoringConfigVersion`.** Our scoring fingerprint churns constantly (this
   week alone `radar-scoring-fp-55270b9d8fad → 7e56a8007342`; every scoring change re-stamps it — AD-10). A
   raw score-vs-time line would compare scores produced by **different formulas/weights**. The dataset and the
   SVG **MUST** segment/annotate the score series by `ScoringConfigVersion` and **never draw a trend line
   across a fingerprint boundary**. This is a hard correctness requirement, called out prominently below.

---

## Assignment

Worktree: any (mostly all-new files under new `Efficacy/` folders; the shared-file edits are small and
additive — one additive read method on `IScoreSnapshotFileStore`, `RadarWorkerServices` composition,
`RadarWorkerOptions`, `InfrastructureServiceCollectionExtensions`, `appsettings.json`, and one AD-14 amendment).
Dependencies: **spec 92 (AD-14 price seam) — SHIPPED/merged** (`IPriceHistoryStore`/`FilePriceHistoryStore`,
`PriceHistory`/`PriceBar`, `data/prices/{ticker}.json` already exist). No other dependency.
Conflicts with: any slice editing `IScoreSnapshotFileStore` / `FileScoreSnapshotStore`, `RadarWorkerServices.cs`,
`RadarWorkerOptions.cs`, `InfrastructureServiceCollectionExtensions.cs`, `appsettings.json`, or
`docs/architecture-decisions.md` — sequence, do not parallelize.
Estimated time: ~2 h (a deterministic JOIN + two pure string renderers + a files-first artifact store + an
opt-in Worker step, all mirroring existing patterns; one additive score-store read; one AD amendment).

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **Score history shape.** `Radar.Domain.Scoring.CompanyScoreSnapshot` carries `Id`, `CompanyId`,
  `ScoringVersion`, the five component scores (`TrajectoryScore`, `OpportunityScore`, `AttentionScore`,
  `EvidenceConfidenceScore`, `SignalVelocityScore`), `Explanation`, `ComponentJson`, `WindowStartUtc`,
  `WindowEndUtc`, **`CreatedAtUtc`** (the run instant — this is the series-point timestamp; AD-7), and the
  nullable **`ScoringConfigVersion`** fingerprint (AD-10). **There is NO `Label` field on the snapshot** — the
  human-action label is computed at report time by `WeeklyReportActionPolicyV1`, not persisted. **Slice 1
  therefore plots the numeric component score(s), not labels** (label-aligned analysis is deferred to slice 2).
- **Score store + on-disk layout.** `Radar.Application.Scoring.IScoreSnapshotFileStore` /
  `Radar.Infrastructure.FileSystem.FileScoreSnapshotStore` write one file per snapshot to
  `data/scores/{companyId}/{snapshotId}.json` (verified on disk: 8 company GUID directories, many snapshot
  files each). The store already exposes `ReadLatestBeforeAsync(companyId, beforeUtc, ct)` which enumerates a
  company's directory, deserializes each `ScoreSnapshotFile`, guards `CompanyId`, and reconstructs **scalar
  snapshot fields only** (Links intentionally empty — not dropped provenance; the report's live provenance
  comes from the in-memory repo). **This slice adds one more read** (`ReadAllForCompanyAsync`) that reuses that
  exact enumerate-and-parse logic.
- **Price store shape + ticker keying.** `Radar.Application.Prices.IPriceHistoryStore.ReadAsync(ticker, ct)` →
  `PriceHistory?` (`Ticker`, `Source`, `RetrievedAtUtc`, `IReadOnlyList<PriceBar> Bars`), bars ascending by
  `PriceBar.Date` (a UTC `DateOnly`), deduped by date. `FilePriceHistoryStore.SanitizeTicker` **lowercases**
  the ticker (verified on disk: `mrcy.json`, `aehr.json`, …). Each `PriceBar` = `DateOnly Date`, decimal
  `Open/High/Low/Close/AdjClose`, `long Volume`.
- **Company ↔ ticker mapping.** Score snapshots key by `CompanyId` (`Guid`); price keys by ticker.
  `Radar.Application.Abstractions.Persistence.ICompanyRepository.GetAllAsync(ct)` returns `Company` records
  each with `Id` (Guid) + nullable `Ticker` — this is the join key source. Skip a company with a blank
  `Ticker`.
- **Opt-in Worker step precedent (AD-14 price acquirer).** `PriceHistoryAcquirer` is an Application service
  invoked by `Worker` **outside `IRadarPipeline`**, gated in `RadarWorkerServices` behind
  `Radar:Prices:Enabled` (default false); `Worker` injects an **optional** `IPriceHistoryAcquirer?` (null when
  disabled) and calls it after seeding. **This slice mirrors that shape exactly** with `Radar:Efficacy:Enabled`
  (default false) and an optional `IEfficacyReportGenerator?`.
- **Files-first scaffolding to REUSE (not copy).** `GracefulFileWriter.TryWriteAllTextAsync(path, text, logger,
  ct)` (creates the dir, writes, logs + returns `false` on `IOException`/`UnauthorizedAccessException` instead
  of throwing) and `RadarFileStoreJson.Options` are the shared write helpers. The SVG/CSV are plain text, so
  the artifact store uses `GracefulFileWriter.TryWriteAllTextAsync` directly (no JSON serializer needed for the
  text artifacts).
- **Rendering-in-Application precedent.** `MarkdownWeeklyReportRenderer` lives in
  `Radar.Application/Reporting/` — rendering text (markdown) in the Application layer is an established
  pattern. The SVG/CSV renderers follow it: pure, deterministic string producers in `Radar.Application/Efficacy/`.
- **Determinism idioms (AD-3).** UTC everywhere, culture-invariant number formatting (`CultureInfo.Invariant`,
  fixed `"F2"`/`"F4"`), stable ordering. The SVG must be **byte-identical for identical input** — embed **no**
  wall-clock timestamp.

---

## The JOIN (deterministic, UTC, no look-ahead)

For each company from `ICompanyRepository.GetAllAsync` with a non-blank `Ticker`:

1. `scoreSnapshots = IScoreSnapshotFileStore.ReadAllForCompanyAsync(company.Id, ct)` — ascending by
   `CreatedAtUtc`, then `Id` (AD-3).
2. `priceHistory = IPriceHistoryStore.ReadAsync(company.Ticker, ct)` — bars already ascending by `Date`.
3. For each snapshot, `scoreDate = DateOnly.FromDateTime(snapshot.CreatedAtUtc.UtcDateTime)`. Pair it to the
   price bar with the **greatest `Date` ≤ `scoreDate`** (at-or-before — **NO LOOK-AHEAD**; never pair a score
   to a future bar). If the score predates all bars (or no price history exists), the paired price is `null`.
4. Emit one `EfficacyPoint` per snapshot carrying: `ScoreDate`, the five component scores, the snapshot's
   `ScoringConfigVersion` (may be `null` = pre-stamp/unknown), and the paired `PriceAsOfDate` + `PriceClose` +
   `PriceAdjClose` (all `null` when unpaired).
5. The `CompanyEfficacySeries` also carries the **full dense `PriceBar` list** over the render window (the
   score points are sparse — drawn as dots/steps; the price is dense — drawn as a continuous line).

The builder is **pure over the stores' output** — same persisted data ⇒ same dataset. It never writes.

### Fingerprint segmentation (HARD correctness requirement)

The score series is partitioned into **contiguous segments of equal `ScoringConfigVersion`** (`Ordinal`
equality; `null` is its own "unknown/pre-stamp" segment). A boundary occurs wherever consecutive points'
`ScoringConfigVersion` differ. **The renderer connects score points with a line ONLY within a single
segment** and marks each boundary (a thin dashed vertical rule + the short fingerprint suffix as a label). A
segment of length 1 renders as an isolated dot with **no** connecting line. Because the current dev history is
short and fingerprint-fragmented, many segments will be length 1 — that is the honest, correct picture, not a
bug to paper over. Price (a single objective series) is **not** segmented.

---

## Design

### 1. Additive read on the score store (reuse the existing enumerate-and-parse)

Add to `IScoreSnapshotFileStore` (Application) and implement in `FileScoreSnapshotStore` (Infrastructure):

```csharp
/// <summary>
/// Returns ALL persisted snapshots for the company, ascending by CreatedAtUtc then Id (AD-3), scalar fields
/// only (Links intentionally empty — same posture as ReadLatestBeforeAsync). The efficacy/validation layer's
/// read seam over score history (AD-14 amendment); read-only, never writes. A malformed or foreign-CompanyId
/// file is skipped + logged, never thrown; cancellation propagates.
/// </summary>
Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(Guid companyId, CancellationToken ct);
```

Implement by **factoring the existing per-file parse** in `ReadLatestBeforeAsync` (read text → deserialize
`ScoreSnapshotFile` → null/`CompanyId` guards → reconstruct the scalar `CompanyScoreSnapshot`) into a shared
private helper, and route **both** `ReadLatestBeforeAsync` and `ReadAllForCompanyAsync` through it
(reuse-over-copy — do not paste a second parse loop). `ReadAllForCompanyAsync` returns the full ordered list;
`ReadLatestBeforeAsync` keeps its single-best-candidate scan. No file exists / directory missing → empty list.

> **Why widen the existing store rather than add a new read seam:** `FileScoreSnapshotStore` already owns the
> `data/scores/{companyId}/` enumeration + parse (`ReadLatestBeforeAsync`). A separate reader would duplicate
> that logic (drift risk — only one copy gets the next fix). Widening the existing store is the reuse-correct
> home. The efficacy layer consumes it **read-only**.

### 2. Efficacy records (Application, `Radar.Application/Efficacy/`)

```csharp
namespace Radar.Application.Efficacy;

/// <summary>One joined efficacy point: a score snapshot's numeric components paired (no look-ahead) to the
/// price bar at-or-before its date. VALIDATION/RESEARCH data only (AD-14) — never a scoring input.</summary>
public sealed record EfficacyPoint(
    DateOnly ScoreDate,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string? ScoringConfigVersion,   // the fingerprint segment key (null = pre-stamp/unknown)
    DateOnly? PriceAsOfDate,        // the actual bar date used (at-or-before ScoreDate), null if unpaired
    decimal? PriceClose,
    decimal? PriceAdjClose);

/// <summary>A company's efficacy series: sparse score points (segment-keyed by ScoringConfigVersion) overlaid
/// on the dense daily price bars, for a per-company score-vs-price visual (AD-14 read side).</summary>
public sealed record CompanyEfficacySeries(
    Guid CompanyId,
    string CompanyName,
    string Ticker,
    IReadOnlyList<EfficacyPoint> Points,        // ascending by ScoreDate
    IReadOnlyList<PriceBar> PriceBars);         // ascending by Date (the dense line); Radar.Application.Prices.PriceBar
```

### 3. `EfficacyDatasetBuilder` (Application, plain class) — the JOIN

Injects `ICompanyRepository`, `IScoreSnapshotFileStore`, `IPriceHistoryStore`, `ILogger<EfficacyDatasetBuilder>`
(guard-null all). `Task<IReadOnlyList<CompanyEfficacySeries>> BuildAsync(CancellationToken ct)` performs the
JOIN above. Skips companies with a blank `Ticker`. A company with score points but no price history still
yields a series (price fields `null`) — the **generator** decides whether that is renderable. No evidence /
signal / scoring **write** dependency (read-only).

### 4. `EfficacySvgRenderer` + `EfficacyCsvRenderer` (Application, pure classes)

- **`EfficacySvgRenderer.Render(CompanyEfficacySeries) -> string`** — a **self-contained** inline SVG (no
  external assets, no `<script>`, no `href`, no web fonts): fixed `viewBox` (e.g. `0 0 900 340`); a **left
  Y-axis 0–100** for the score components and a **right Y-axis** scaled to the price min/max over the window;
  an X-axis from the earliest to latest date across both series. Draw the **price** as one continuous polyline
  over `PriceBars` (adjusted close). Draw the chosen **score** component (default **OpportunityScore** — the
  headline; render Trajectory/Attention/etc. is out of scope for slice 1, but keep the component selectable via
  a simple field to avoid a rewrite later) as dots, connected by a polyline **only within a fingerprint
  segment**; at each `ScoringConfigVersion` boundary draw a thin dashed vertical rule + the short fingerprint
  suffix label. Include a legend (score line, price line) and a small **caption stating this is a research
  statistic segmented by scoring-config fingerprint — NOT advice** (AD-9: no "buy/sell/target/return/
  outperform/guaranteed/safe bet"). Deterministic: `CultureInfo.InvariantCulture` numeric formatting, **no
  embedded wall-clock**, stable element order.
- **`EfficacyCsvRenderer.Render(CompanyEfficacySeries) -> string`** — a header + **one row per `EfficacyPoint`**:
  `scoreDate,scoringConfigVersion,trajectory,opportunity,attention,evidenceConfidence,velocity,priceAsOfDate,priceClose,priceAdjClose`.
  Invariant formatting; empty cell for `null` price fields; ISO `yyyy-MM-dd` dates. Cheap, lets the joined data
  be inspected/re-plotted.

### 5. `IEfficacyArtifactStore` + `FileEfficacyArtifactStore` — `data/efficacy/{ticker}.{svg,csv}`

Application interface `IEfficacyArtifactStore.WriteAsync(string ticker, string svg, string csv, CancellationToken ct)`
→ the written paths (best-effort). Infrastructure `FileEfficacyArtifactStore`
(`Radar.Infrastructure/FileSystem/`) writes `{RootDirectory}/{sanitizedTicker}.svg` and `.csv` via
`GracefulFileWriter.TryWriteAllTextAsync` (reuse — no second write helper). Sanitize the ticker exactly as
`FilePriceHistoryStore` does (lowercase + reject `Path.GetInvalidFileNameChars`) so the efficacy artifact and
the price file share the same on-disk ticker key — **extract that sanitizer into a shared home if practical**
(reuse-over-copy; otherwise mirror it and note the duplication for a later shared-primitive slice). Graceful
degrade on I/O failure (log + return the attempted path, never throw). Options record
`FileEfficacyArtifactStoreOptions { required string RootDirectory }`.

### 6. `IEfficacyReportGenerator` + `EfficacyReportGenerator` (Application) — the opt-in step

`IEfficacyReportGenerator.GenerateAsync(CancellationToken ct)`. `EfficacyReportGenerator` injects
`EfficacyDatasetBuilder`, `EfficacySvgRenderer`, `EfficacyCsvRenderer`, `IEfficacyArtifactStore`,
`ILogger<EfficacyReportGenerator>`. It builds the dataset, and for each series that has **≥1 score point AND
≥1 price bar** renders the SVG + CSV and writes them; series lacking one side are **skipped with a logged
reason** (honest: "no price data" / "no score history"). Logs a per-run summary (companies rendered / skipped).
No evidence/signal/scoring dependency.

### 7. Wiring (opt-in, default OFF — mirror the AD-14 price gate)

- **`RadarWorkerOptions`**: add `EfficacyWorkerOptions Efficacy { get; init; } = new()` with `bool Enabled`
  (default `false`); add `string EfficacyDirectory { get; init; } = "data/efficacy"` next to the other
  `data/*` roots. Bind from `Radar:Efficacy`.
- **`RadarWorkerServices`**: inside `if (options.Efficacy.Enabled) { ... }`, register
  `AddFileEfficacyArtifactStore(options.EfficacyDirectory)` and `AddRadarEfficacyReport()` (the builder,
  the two renderers, and `IEfficacyReportGenerator -> EfficacyReportGenerator`). When disabled (default),
  **nothing** efficacy-related is registered, `Worker`'s optional `IEfficacyReportGenerator?` is `null`, and
  the graph is byte-for-byte unchanged.
- **`Worker`**: inject optional `IEfficacyReportGenerator? efficacyReportGenerator = null`. Invoke it
  **after** `RunPipelineAsync` (so the current run's freshly-persisted snapshot is included in the join) when
  non-null — a **separate step, NOT inside `IRadarPipeline`**. In `RunOnce` mode, call it after the single
  pipeline run and before `StopApplication`. (Continuous-mode price-refresh timing is a known limitation — see
  "Out of scope".)
- **`appsettings.json`**: add a documented, **disabled-by-default** `Radar:Efficacy` block
  (`"Enabled": false`).
- **DI extensions** (`InfrastructureServiceCollectionExtensions` for `AddFileEfficacyArtifactStore`; an
  Application `AddRadarEfficacyReport` alongside the other Application registrations) mirror
  `AddFilePriceHistoryStore` / `AddFileScoreStore`.

---

## Project structure changes

```text
src/Radar.Application/Efficacy/
  EfficacyPoint.cs                 # ADD: one joined score↔price point (record)
  CompanyEfficacySeries.cs         # ADD: sparse score points + dense price bars for one company (record)
  EfficacyDatasetBuilder.cs        # ADD: the JOIN (read-only over the 3 stores; no-look-ahead pairing)
  EfficacySvgRenderer.cs           # ADD: pure, deterministic, self-contained SVG (fingerprint-segmented)
  EfficacyCsvRenderer.cs           # ADD: pure CSV export (one row per score point)
  IEfficacyArtifactStore.cs        # ADD: WriteAsync(ticker, svg, csv, ct) best-effort
  IEfficacyReportGenerator.cs      # ADD: GenerateAsync(ct) — the opt-in step seam
  EfficacyReportGenerator.cs       # ADD: build → render → store; skip+log unrenderable; summary log

src/Radar.Application/Scoring/
  IScoreSnapshotFileStore.cs       # MODIFIED: add ReadAllForCompanyAsync(companyId, ct)

src/Radar.Infrastructure/FileSystem/
  FileScoreSnapshotStore.cs        # MODIFIED: implement ReadAllForCompanyAsync; factor shared per-file parse
  FileEfficacyArtifactStore.cs     # ADD: data/efficacy/{ticker}.{svg,csv} via GracefulFileWriter; sanitize+graceful
  FileEfficacyArtifactStoreOptions.cs  # ADD: { required string RootDirectory }

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: add AddFileEfficacyArtifactStore(rootDir)

src/Radar.Application/DependencyInjection/ (wherever Application registrations live)
  <ApplicationServiceCollectionExtensions>.cs   # MODIFIED: add AddRadarEfficacyReport() (builder+renderers+generator)

src/Radar.Worker/
  RadarWorkerServices.cs           # MODIFIED: gate all efficacy registrations behind options.Efficacy.Enabled
  RadarWorkerOptions.cs            # MODIFIED: add EfficacyWorkerOptions Efficacy + EfficacyDirectory default
  Worker.cs                        # MODIFIED: inject optional IEfficacyReportGenerator?; invoke after RunPipelineAsync
  appsettings.json                 # MODIFIED: add disabled-by-default Radar:Efficacy block

docs/architecture-decisions.md     # MODIFIED: AMEND AD-14 — efficacy/validation-reporting layer is read-only
                                   #   over score history + price (emits a visual/dataset; never feeds scoring)

.gitignore                         # MODIFIED: add /data/efficacy/ (generated output — mirror /data/prices/ etc.)

tests/Radar.Application.Tests/Efficacy/
  EfficacyDatasetBuilderTests.cs   # ADD: pairing/no-look-ahead, ordering, blank-ticker skip, missing-price null
  EfficacySvgRendererTests.cs      # ADD: deterministic, self-contained, fingerprint segmentation, no advice
  EfficacyCsvRendererTests.cs      # ADD: header + one row/point, invariant formatting, null price cells
  EfficacyReportGeneratorTests.cs  # ADD: renders renderable series, skips (logs) series missing a side
tests/Radar.Infrastructure.Tests/FileSystem/
  FileScoreSnapshotStoreTests.cs   # MODIFIED/ADD: ReadAllForCompanyAsync ordering, foreign-id/malformed skip, empty
  FileEfficacyArtifactStoreTests.cs# ADD: writes {ticker}.svg+.csv, sanitize, graceful degrade
tests/Radar.Worker.Tests/ (wherever composition is tested)
  <composition test>               # ADD/MODIFIED: Efficacy disabled (default) registers NOTHING + graph unchanged;
                                   #   enabled registers builder/renderers/store/generator; Worker acquirer null when off
```

**No** change to: any `IEvidenceCollector`/collector/`CollectedEvidence`/`EvidenceItem`, `ISignalExtractor`/
signals, `IScoringEngine`/`ScoringEngine`/any formula/`ScoringConfigVersion`/`_formula.Version`,
`IRadarPipeline`/`RadarPipelineRunner`, or the weekly report renderer/builder. The `IScoreSnapshotFileStore`
change is a **new read method only** (write path untouched).

---

## Tests

Offline/deterministic (no network, no real disk beyond temp dirs). Match the existing style
(`FileScoreSnapshotStoreTests`, `FilePriceHistoryStoreTests`, `PriceHistoryAcquirerTests`).

### `EfficacyDatasetBuilderTests`
1. **No-look-ahead pairing.** A score dated `D` pairs to the price bar with the greatest `Date ≤ D`; a score
   dated **before** all bars pairs to `null`; a score dated **after** the last bar pairs to the **last** bar
   (never a future bar). Ascending order preserved.
2. **Blank-ticker company** is omitted; a company with score points but **no price history** yields a series
   with all price fields `null`.
3. Multiple companies join independently by `CompanyId`↔`Ticker`; the builder writes nothing.

### `EfficacySvgRendererTests`
4. **Deterministic:** identical input rendered twice yields **byte-identical** SVG (no embedded wall-clock).
5. **Self-contained:** output starts with `<svg`, contains no `<script>`, no `http`/`href`/external-asset
   reference; contains a score axis (0–100) and a price axis.
6. **Fingerprint segmentation (hard requirement):** a series whose points span **two** `ScoringConfigVersion`
   values renders **two** separate score polyline segments (no line drawn across the boundary) plus a boundary
   marker; a length-1 segment renders as an isolated dot with no connecting line.
7. **AD-9:** the SVG contains none of `buy/sell/target/return/outperform/guaranteed/safe bet` (case-insensitive).

### `EfficacyCsvRendererTests`
8. Header + exactly one row per `EfficacyPoint`, expected columns/order, invariant formatting, `yyyy-MM-dd`
   dates, empty cells for `null` price fields.

### `EfficacyReportGeneratorTests`
9. A series with ≥1 score point and ≥1 price bar is rendered + written (SVG + CSV); a series missing either
   side is **skipped with a logged reason** and does not throw; caller cancellation propagates.

### `FileScoreSnapshotStoreTests` (add)
10. `ReadAllForCompanyAsync` returns **all** of a company's snapshots ascending by `CreatedAtUtc` then `Id`;
    a foreign-`CompanyId` or malformed file is skipped; a missing directory returns an **empty** list.

### `FileEfficacyArtifactStoreTests`
11. `WriteAsync` creates `data/efficacy/{ticker}.svg` + `.csv` (ticker lowercased/sanitized); an I/O failure
    logs + returns the attempted path(s) and does **not** throw.

### Composition test
12. With `Radar:Efficacy:Enabled=false` (default) the provider registers **no** `IEfficacyReportGenerator` /
    `IEfficacyArtifactStore` / builder / renderers, the collector `IEnumerable` and the rest of the graph are
    **unchanged**, and `Worker`'s optional generator resolves to `null`. With `Enabled=true` all are
    registered. (Mirror the `Radar:Prices` on/off assertion.)

### Structural guardrail (AD-14 read-only)
13. Grep-level / structural: `Radar.Application/Efficacy/*` references **no** `EvidenceItem`/`CollectedEvidence`,
    **no** `ISignalExtractor`/`Signal`, and **no** `IScoringEngine`/formula/`ScoringConfigFingerprint` **write**
    type — it reads only `IScoreSnapshotFileStore`, `IPriceHistoryStore`, `ICompanyRepository` and writes only
    `IEfficacyArtifactStore`. No price/efficacy type appears in any collector/evidence/signal/scoring path.

Keep all existing tests green.

---

## Constraints

- Target `net10.0`, C# 14. Layering (AD-5): records/builder/renderers/generator seam live in
  `Radar.Application/Efficacy/`; the file artifact store + options + DI live in `Radar.Infrastructure`; the
  opt-in wiring lives in `Radar.Worker`. `Radar.Domain` is **unchanged**.
- **THE boundary (AD-14, read side): the efficacy layer is READ-ONLY over score history + price.** It reads
  `IScoreSnapshotFileStore` + `IPriceHistoryStore` + `ICompanyRepository`, writes **only** efficacy artifacts,
  and runs **outside `IRadarPipeline`**. Price/score data **never** flows back into evidence, signals, or
  scoring. Enforced structurally (no such dependency) + by test 13.
- **Fingerprint segmentation is mandatory** — never draw a score trend line across a `ScoringConfigVersion`
  boundary (AD-10). Tested (test 6).
- **Determinism (AD-3):** UTC dates, no-look-ahead pairing, stable ordering, `CultureInfo.InvariantCulture`
  formatting, **no embedded wall-clock** in the SVG (byte-identical for identical input).
- **Files-first + graceful degradation (AD-8):** reuse `GracefulFileWriter`; a disk failure logs + continues
  (write returns the path), never aborts. Reuse the price store's ticker sanitizer (extract to a shared home if
  practical — reuse-over-copy).
- **AD-9 (no advice language):** the SVG/CSV/logs emit no "buy"/"sell"/"target"/"return"/"outperform"/
  "guaranteed"/"safe bet". A price-vs-score overlay is a **research statistic**, never a performance/advice
  claim. Slice 1 plots **numeric scores**, not labels.
- **NO scoring/formula/`ScoringConfigVersion`/`_formula.Version` change** — this slice is entirely outside
  scoring and changes no score output. State this in the PR. **No bump.**
- **Opt-in + default-unchanged:** gate behind `Radar:Efficacy:Enabled` (default `false`); when disabled the
  pipeline graph is byte-for-byte unchanged (mirror the `Radar:Prices` gate). No key, no secret, no network.
- **Gitignore the generated output:** add `/data/efficacy/` to `.gitignore` next to `/data/prices/` — the SVG/
  CSV artifacts are generated per run and must not be committed (only `data/companies.json` is tracked under
  `data/`). The `run-radar.ps1` live harness will later supply a `Radar:EfficacyDirectory` override the same way
  it does for prices; that harness edit is out of scope for the coder (I handle it at live-run time).
- **Honest limits (state in the PR / SVG caption):** 8 names × a handful of dev-run snapshots = **no
  statistical power** — the value is the mechanism accruing over time / more tickers; the score series is
  **sparse-per-run** while price is **dense-daily** (score = dots/steps, price = line); the score history is
  **short and fingerprint-fragmented** (expect many length-1 segments).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## AD-14 amendment (record as part of this slice)

Append to AD-14 in `docs/architecture-decisions.md` (amendment style, mirroring AD-6/AD-10 amendments):

> ### Amendment — spec 101: the efficacy/validation-reporting layer is the READ side of AD-14 (read-only over score history + price)
>
> The price reference dataset (this AD) gains its first consumer: a **price-efficacy visual** that JOINs a
> company's persisted score-snapshot history (`IScoreSnapshotFileStore`) to its daily price series
> (`IPriceHistoryStore`) and emits a per-company score-vs-price **SVG + CSV** under `data/efficacy/`. This
> efficacy subsystem is **strictly read-only over score history + price and emits artifacts only** — it
> **never** writes back into `evidence → signal → score`, is **not** in `IRadarPipeline`, and depends on no
> collector/evidence/signal/scoring **write** path. It runs as an **opt-in** Worker step
> (`Radar:Efficacy:Enabled`, default `false`); disabled leaves the graph byte-for-byte unchanged. The score
> series is **segmented by `ScoringConfigVersion`** (AD-10) so a trend line is never drawn across a
> formula/weight change. Framing stays AD-9-clean: a score-vs-price overlay is a **research statistic**, never
> a performance/advice claim (no "return/outperform/buy"). This amendment records that the READ side of AD-14
> exists and is bounded: **price (and score history) may be READ for validation/visualisation but must never
> flow back into scoring** — doing so still requires superseding AD-14. *Accepted · 2026-07-06 — the read side
> of the price-validation boundary; no scoring math change.*

Also update AD-14's **Status** line to note the spec-101 amendment.

---

## Out of scope (note, do NOT implement this round — see "Future slices")

- **Forward-return efficacy STATISTICS** (slice 2): aligning to each score-change/label date and computing
  forward price change at `+7d`/`+30d`, **relative to a benchmark** (a small-cap up 5% while the market is up
  8% is not "it worked"), strictly segmented by `ScoringConfigVersion`, no look-ahead. Deferred.
- **Label-aligned analysis** — the snapshot carries no label (labels are report-time); joining report labels
  to price is a slice-2 concern.
- **An interactive dashboard / `Radar.Api`** (slice 3, bigger) — where a web layer would earn its keep.
- **Any price → scoring/signal/evidence feedback** — forbidden by AD-14 (the whole point).
- **Per-tick price refresh.** Known limitation (arch observation 2026-07-06): price is acquired **once** at
  Worker startup (`Worker.cs` — before the periodic loop), so in continuous (non-`RunOnce`) mode prices do not
  refresh; the efficacy layer may eventually want acquisition inside the periodic tick. Out of scope for
  slice 1 — noted only.
- **Cross-company / index artifacts, additional score components in the SVG, PNG rendering, a DB** — files-first
  SVG+CSV per company only.

---

## Future slices (sketch only — do NOT create as files now)

- **Slice 2 — forward-return efficacy statistics.** For each score-change date, compute forward
  benchmark-relative price change at `+7d`/`+30d`, no look-ahead, strictly segmented by `ScoringConfigVersion`;
  emit a dataset + summary. Still read-only over AD-14.
- **Slice 3 — interactive dashboard** (`Radar.Api`), deferred and larger.

The dataset-builder ↔ renderer ↔ artifact-store seam is internal to slice 1 and does **not** require splitting
into separate specs now.

---

## Acceptance criteria

- [ ] The efficacy layer **JOINs** each seeded company's persisted score snapshots (`ReadAllForCompanyAsync`)
      to its daily price series (`IPriceHistoryStore.ReadAsync`) with **deterministic, no-look-ahead** pairing
      (score date → price bar at-or-before), producing a typed `CompanyEfficacySeries`. Tested.
- [ ] The score series is **segmented by `ScoringConfigVersion`**; the SVG never draws a score line across a
      fingerprint boundary and marks each boundary. Tested (hard requirement).
- [ ] Per company, a **self-contained inline SVG** (dual-axis score-vs-price, score dots/steps + dense price
      line) **and** a **CSV** dataset are written to `data/efficacy/{ticker}.{svg,csv}` via the shared
      `GracefulFileWriter`; the SVG is **byte-identical for identical input** (no embedded wall-clock) and
      contains no external assets/`<script>`. Tested.
- [ ] The subsystem is **READ-ONLY over AD-14**: it reads `IScoreSnapshotFileStore`/`IPriceHistoryStore`/
      `ICompanyRepository`, writes only efficacy artifacts, runs **outside `IRadarPipeline`**, and has **no**
      evidence/signal/scoring **write** dependency. Enforced structurally + tested (test 13).
- [ ] It is **opt-in** behind `Radar:Efficacy:Enabled` (default `false`): when disabled, **nothing**
      efficacy-related is registered, `Worker`'s optional `IEfficacyReportGenerator?` is `null`, and the
      pipeline graph is **byte-for-byte unchanged**. Tested (on/off composition).
- [ ] `IScoreSnapshotFileStore.ReadAllForCompanyAsync` added (scalar fields, ascending by `CreatedAtUtc`/`Id`,
      malformed/foreign-id skip, empty when none), implemented by **factoring the existing per-file parse**
      (no duplicated parse loop). Tested.
- [ ] **AD-9:** no advice language in any artifact/log; slice 1 plots numeric scores, not labels. **No
      scoring/formula/`ScoringConfigVersion` change** (stated in the PR).
- [ ] `docs/architecture-decisions.md`: **AD-14 amended** (spec 101 — efficacy layer is the read side,
      read-only over score history + price; never feeds scoring), Status line updated.
- [ ] Layering (AD-5), determinism (AD-3), files-first + graceful degradation (AD-8) preserved;
      `Radar.Domain` unchanged; no collector/evidence/signal/scoring/report change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
