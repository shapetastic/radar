# Task: Daily price-history acquisition + reference store (validation-only; STRUCTURALLY not evidence, not a signal, not a scoring input)

> **DIRECTED + FIRST STEP TOWARD A DEFERRED VISUAL (read first).** This slice is a **directed** task the
> maintainer asked for — **NOT** the generic planner loop and **NOT** architecture-gated. It is the first,
> de-risking step toward a **future** price-efficacy validation visual/backtest (its own later spec): fetch
> daily stock-price history for the watch-universe tickers and persist it as a **REFERENCE / VALIDATION
> dataset** the future backtest will consume. Price is **raw factual market data**, deliberately kept
> **outside** the evidence → signal → score → report pipeline.
>
> **THE LOAD-BEARING CONSTRAINT — get this right or the slice is wrong.** Price must NOT be an
> `IEvidenceCollector` and must NOT produce `CollectedEvidence` / `EvidenceItem`. If price entered the
> evidence pipeline it would become eligible for signal extraction → scoring, which violates Radar's
> "signals before stories… not a trading bot" ethos (philosophy) and the maintainer's explicit "price is
> not used in a signal" requirement. It must be **STRUCTURALLY impossible** for price to become a signal:
> price rides a **separate seam** (`IPriceHistoryReader` + a dedicated `data/prices/` store), consumed by
> **nothing** in the scoring/evidence/signal/report path today. This slice proposes **AD-14** to make that
> boundary a settled, reviewer-checkable decision.

## Overview

Radar's premise is corroborated business-trajectory *evidence*, never price action ("signals before
stories", "avoid hype loops" — philosophy). But to *validate* whether Radar's signals actually preceded
business improvement, a future spec will need a **factual price series** to backtest against — a reference
dataset, not an input. This slice acquires and persists exactly that reference dataset and lands the
**reader seam** the future backtest/report will consume. It ships **acquire + store + reader interface
ONLY**: no efficacy computation, no visual/report, and **zero** change to scoring, evidence, signals, or
the weekly report.

The design keeps price on a seam that **cannot reach signals**:

- **`IPriceHistoryReader`** (a new Application abstraction) — fetches a ticker's daily bars; it is **not**
  `IEvidenceCollector`, returns **no** `CollectedEvidence`, and is not in the collector `IEnumerable` the
  runner consumes.
- An **Infrastructure HTTP reader** (`HttpPriceHistoryReader`) behind that interface (all HTTP/JSON stays
  in Infrastructure, AD-5), mirroring the typed-graceful-outcome pattern of the SEC/News readers.
- A **dedicated `data/prices/{ticker}.json` store** (`IPriceHistoryStore` + `FilePriceHistoryStore`),
  reusing the shared `GracefulFileWriter` + `RadarFileStoreJson.Options` scaffolding (the "reuse over copy"
  rule), consumed by **nothing** in the scoring/evidence/signal/report path.
- An **opt-in acquisition step** gated behind `Radar:Prices:Enabled` (default `false`), run as a Worker
  step **distinct from** the collector loop and **outside** the `IRadarPipeline` evidence→signal→score
  path — so with prices disabled (the default) the pipeline graph is **byte-for-byte unchanged**.

The reader/store interfaces are the seam the future backtest/report will consume. Surfacing a reference
price in the report is **deferred** to that later validation-report spec (where factual-reference framing
can be done carefully).

---

## Assignment

Worktree: any (mostly all-new files under new `Prices/` folders; the shared-file edits are small and
additive — `RadarWorkerServices` composition, `RadarWorkerOptions`, `InfrastructureServiceCollectionExtensions`,
`appsettings.json`, and one new AD entry). Sequence rather than parallelize against any other slice that
touches Worker composition / DI.
Dependencies: None (self-contained new seam). Read for the exact patterns to mirror:
`src/Radar.Infrastructure/Sec/HttpSecEarningsReleaseReader.cs` + `SecEarningsReleaseReadResult.cs` (typed
graceful-outcome reader — the shape to copy), `src/Radar.Infrastructure/FileSystem/FilePipelineRunStore.cs`
+ `GracefulFileWriter.cs` + `RadarFileStoreJson.cs` (files-first store scaffolding to reuse),
`src/Radar.Worker/RadarWorkerServices.cs` + `RadarWorkerOptions.cs` (opt-in gate + options binding — mirror
the `Radar:Ai` opt-in pattern), and `src/Radar.Application/Abstractions/Persistence/ICompanyRepository.cs`
(`GetAllAsync` → `Company.Ticker`, the watch-universe ticker source).
Conflicts with: any slice editing `RadarWorkerServices.cs`, `RadarWorkerOptions.cs`,
`InfrastructureServiceCollectionExtensions.cs`, `appsettings.json`, or `docs/architecture-decisions.md` —
sequence, do not parallelize.
Estimated time: ~1.5–2 h (a typed HTTP reader + a files-first store mirroring existing patterns + a small
opt-in Worker acquisition step + AD-14).

---

## Verified source facts (verified from THIS environment 2026-07-04 — do NOT re-research)

The lead candidate (**stooq.com** keyless daily CSV, `https://stooq.com/q/d/l/?s=mrcy.us&i=d`) is **NOT
usable** from this environment: it now returns an HTML **JavaScript proof-of-work anti-bot challenge**
(HTTP 200, `Content-Type: text/html`, a `crypto.subtle.digest` SHA-256 loop that must be solved in a
browser before the CSV is served) — a headless `HttpClient` receives the challenge page, never the CSV.
Rejected: solving a JS PoW challenge server-side is out of bounds (no headless browser, no bypassing an
explicit anti-bot wall). **Do NOT use stooq.**

**Chosen source: Yahoo Finance chart endpoint (keyless JSON).** Verified reachable and clean from this
environment:

- **Endpoint + method:** `GET https://query1.finance.yahoo.com/v8/finance/chart/{TICKER}?interval=1d&range={range}`
  where `{range}` is one of the API's `validRanges` (`1d,5d,1mo,3mo,6mo,1y,2y,5y,10y,ytd,max`). Verified
  `range=1mo` and `range=5d` for `MRCY`. **Keyless.** Requires a browser-like `User-Agent` header (send
  `Mozilla/5.0 (Windows NT 10.0; Win64; x64)`); no cookie/crumb was needed for the `chart` endpoint (unlike
  the older `download`/`quoteSummary` endpoints). **No API key, no secret, no paid service.**
- **Verified success shape (HTTP 200, `application/json`):**
  ```
  {"chart":{"result":[{
     "meta":{"currency":"USD","symbol":"MRCY","regularMarketPrice":126.21, ... },
     "timestamp":[1782480600,1782739800, ...],                 // Unix EPOCH SECONDS, one per bar
     "indicators":{
        "quote":[{ "open":[...],"high":[...],"low":[...],"close":[...],"volume":[...] }],
        "adjclose":[{ "adjclose":[...] }]
     }}],
     "error":null}}
  ```
  The parallel arrays `timestamp[]` and `indicators.quote[0].{open,high,low,close,volume}[]` are index-aligned
  (bar `i` = `timestamp[i]` + each `quote[0].X[i]`); `indicators.adjclose[0].adjclose[]` is the split/dividend-
  adjusted close (store it too — cheap, and the backtest will likely want adjusted close). For a daily `1d`
  interval each `timestamp[i]` is the market-open instant of a trading day; the reader converts it to a **UTC
  calendar date** (`DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.Date`) for the stored bar's `Date`.
- **Verified failure shape:** an unknown/delisted ticker returns **HTTP 404** with body
  `{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}`
  (`result` is `null`). Map non-success status → `HttpError`; a 200 whose `chart.result` is null/empty or whose
  arrays are absent/ragged → `Malformed`; a valid document with zero bars → `Success` with zero bars.
- **Null bars:** Yahoo occasionally emits `null` inside the OHLCV arrays (a non-trading gap inside the range).
  **Skip any bar whose `timestamp` OR `close` is null** (an unpriced/holiday row is not a usable bar) — do not
  fabricate a value.
- **Rate-limit posture:** the `chart` endpoint is **not** GDELT-style per-IP quota-throttled for the modest
  volume here (≤ ~8 tickers/run, one request each). A small polite inter-request pace (default ~1s, like the
  Google-News collector) is prudent; HTTP 429, if ever seen, maps to a distinct `RateLimited` outcome (mirror
  the News reader) — no retry required for the MVP.

If Yahoo becomes unreachable/unsuitable, pick another keyless, non-JS-challenge source and record the same
facts here. Do NOT adopt a source requiring a browser JS challenge, an API key, or a paid tier.

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **Files-first store scaffolding to REUSE (not copy).** `FilePipelineRunStore` writes one JSON file per key
  via `GracefulFileWriter.TryWriteAllTextAsync(path, json, logger, ct)` (creates the dir, writes, and on
  `IOException`/`UnauthorizedAccessException` **logs a warning + returns `false` instead of throwing**) using
  `RadarFileStoreJson.Options` (indented, camelCase, string enums, frozen — the single source of truth for
  on-disk shape). `FilePriceHistoryStore` uses these **exact** helpers; do NOT introduce a second
  serialization or a second write helper.
- **Typed-graceful-outcome reader shape to MIRROR.** `HttpSecEarningsReleaseReader` /
  `SecEarningsReleaseReadResult` define the pattern: an outcome enum
  (`Success`/`Unreachable`/`HttpError`/`Timeout`/`Malformed`, plus SEC's `Forbidden`), a `Success(...)` /
  `Failure(outcome, detail)` factory pair, an `IsSuccess`, and an **advice-free** `Detail` string for logging.
  The reader **NEVER throws** on a bad response (degrades to a typed failure + warning); the ONLY throw is
  `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` (caller cancellation
  propagates); a `TaskCanceledException` with `ct` NOT requested → `Timeout`.
- **`HttpClient` wiring pattern.** `AddSecEarningsReleaseReader` uses
  `services.AddHttpClient<IIface, HttpImpl>(client => { client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ...); ... })`
  and `.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = ... })`. The
  price reader mirrors this: a typed `HttpClient` with the browser `User-Agent` header and gzip/deflate
  decompression enabled.
- **Opt-in gate pattern to MIRROR.** `Radar:Ai` is opt-in: a blank `Radar:Ai:Provider` (default) means the
  whole AI subgraph is **not registered** and the graph is byte-for-byte unchanged
  (`RadarWorkerServices` lines ~155–185). Prices mirror this with `Radar:Prices:Enabled` (default `false`):
  when disabled, **nothing** price-related is registered and the pipeline graph is unchanged.
- **Watch-universe ticker source.** `ICompanyRepository.GetAllAsync(ct)` returns `Company` records each with a
  `Ticker` string (see `data/companies.json` — MRCY, AEHR, AGYS, CYRX, ERII, SPNS, HLIO, EOSE). The price
  acquisition step reads tickers from the **already-seeded** repository (the `Worker` seeds the universe at
  startup before running), NOT by re-parsing `companies.json`. Skip a company with a blank `Ticker`.
- **Worker composition + run flow.** `RadarWorkerServices.AddRadarWorker` composes the graph;
  `Worker.ExecuteAsync` seeds the universe once, then runs `IRadarPipeline`. The price acquisition step is a
  **separate** step invoked from the Worker (after seeding), NOT inside `IRadarPipeline` and NOT a collector —
  it must be impossible for a price bar to enter `CollectAsync`/`CollectedEvidence`.
- **`Convert.ToHexStringLower` / determinism idiom** and UTC-only timestamps are established conventions (AD-3;
  CLAUDE.md "Store all timestamps in UTC"). Store bar `Date` as a UTC calendar date; parse prices as `decimal`.
- **No Domain change strictly required.** The price bar/series are **Application-layer** reference records (not
  Domain aggregates and deliberately not evidence). Keeping them out of `Radar.Domain` reinforces that price is
  not part of the evidence→signal→score domain model. (A tiny pure record could live in Domain, but the intent
  — "price is not a domain aggregate" — is better served by keeping it in Application; place it in
  `Radar.Application/Prices/`.)

---

## Design

### 1. Reference records (Application, `Radar.Application/Prices/`)

Immutable, Domain-free reference records — **not** evidence, **not** signals:

```csharp
namespace Radar.Application.Prices;

/// <summary>One daily price bar — raw factual market data (UTC trading date, decimal OHLC + adjusted close
/// + volume). REFERENCE / VALIDATION data only: never evidence, never a signal, never a scoring input (AD-14).</summary>
public sealed record PriceBar(
    DateOnly Date,          // UTC trading date (from FromUnixTimeSeconds(ts).UtcDateTime.Date)
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjClose,
    long Volume);

/// <summary>A ticker's daily price history as persisted to data/prices/{ticker}.json — reference/validation
/// data (AD-14). Bars are ordered ascending by Date and deduped by Date. Carries the source + fetch instant
/// for provenance of the *reference dataset* (this provenance is deliberately DISCONNECTED from the
/// evidence provenance chain — price is not evidence).</summary>
public sealed record PriceHistory(
    string Ticker,
    string Source,                  // e.g. "yahoo-chart-v8"
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<PriceBar> Bars);
```

### 2. `IPriceHistoryReader` (Application seam — NOT `IEvidenceCollector`)

```csharp
namespace Radar.Application.Prices;

/// <summary>Fetches a ticker's daily price history from an external source. This is a SEPARATE seam from
/// IEvidenceCollector by deliberate design (AD-14): price is validation/reference data and must be
/// structurally incapable of becoming CollectedEvidence / a signal / a scoring input. Typed graceful
/// outcomes — NEVER throws on a bad response; only caller cancellation propagates.</summary>
public interface IPriceHistoryReader
{
    Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct);
}
```

`PriceHistoryReadResult` (Application or Infrastructure — put it beside the reader; Application is fine since
it is provider-neutral) mirrors `SecEarningsReleaseReadResult`:

- Outcome enum `Success, Unreachable, HttpError, Timeout, Malformed, RateLimited`.
- `Success(PriceBar[] bars)` / `Failure(outcome, detail)` factories, `IsSuccess`, advice-free `Detail`.
- `Success` may carry **zero bars** (a ticker with no bars in range is Success-with-nothing, not an error).

### 3. `HttpPriceHistoryReader` (Infrastructure, `Radar.Infrastructure/Prices/`) — mirror the SEC reader

- Inject `HttpClient` + `ILogger<HttpPriceHistoryReader>` (guard-clause null-check both).
- `ReadDailyAsync(ticker, ct)`: build the verified Yahoo URL (URL-encode the ticker; `interval=1d`,
  `range=` from options — default `1y`), GET it, deserialize with `System.Text.Json`, and project the
  index-aligned `timestamp[]` + `quote[0].{open,high,low,close,volume}[]` + `adjclose[0].adjclose[]` into
  `PriceBar[]`, converting each `timestamp[i]` to a UTC `DateOnly` and each price to `decimal`.
- **Skip** any bar whose `timestamp` or `close` is null (unpriced/holiday gap); never fabricate.
- **Typed outcomes, NEVER throw** (mirror the SEC reader exactly):
  - non-success status → `HttpError` (status in `Detail`); **HTTP 429 → `RateLimited`**;
  - `HttpRequestException` → `Unreachable`;
  - `TaskCanceledException` with `ct` NOT requested → `Timeout`;
  - a 200 whose `chart.result` is null/empty, whose arrays are absent, or whose OHLCV arrays are ragged
    (length ≠ `timestamp` length) → `Malformed`; a valid document with zero bars → `Success` with zero bars;
  - `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`.
- All HTTP/JSON/Yahoo specifics stay in Infrastructure (AD-5). No provider SDK, no AI, no DB, no evidence type.
- `PriceReaderOptions` (Infrastructure `internal`): `Range` (default `"1y"`, validated against the known
  `validRanges` set), and an optional endpoint template. `internal` types rely on the existing
  `InternalsVisibleTo` for the Infrastructure test project (like the SEC/News readers).

### 4. `IPriceHistoryStore` + `FilePriceHistoryStore` — `data/prices/{ticker}.json`, reuse the scaffolding

Application interface (`Radar.Application/Prices/IPriceHistoryStore.cs`):

```csharp
public interface IPriceHistoryStore
{
    /// <summary>Persists a ticker's price history to {RootDirectory}/{ticker}.json. Merges the new bars into
    /// any existing file, deduping by Date (see the merge posture below), ordered ascending by Date.
    /// Best-effort (AD-8): a disk failure logs + returns the attempted path, never throws. Returns the path.</summary>
    Task<string> WriteAsync(PriceHistory history, CancellationToken ct);

    /// <summary>Reads a ticker's persisted history, or null if none exists / it is unreadable (the future
    /// backtest's read seam). Best-effort: a malformed/unreadable file logs + returns null, never throws.</summary>
    Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct);
}
```

Infrastructure `FilePriceHistoryStore` (`Radar.Infrastructure/FileSystem/` alongside the other file stores):

- Path: `Path.Combine(_options.RootDirectory, SanitizeTicker(ticker) + ".json")`, where `SanitizeTicker`
  lowercases and validates the ticker contains no `Path.GetInvalidFileNameChars()` (a blank/invalid ticker
  logs + is skipped — never write outside the root).
- **Merge / dedupe posture (decide and document — recommend APPEND-MERGE by Date).** Price history is
  append-mostly: successive runs fetch overlapping windows, and each day's *final* OHLC is fixed once the day
  closes. On `WriteAsync`: read the existing file (if any), **union** existing + new bars **keyed by `Date`**,
  where a new bar for an existing `Date` **replaces** the stored one (last-write-wins per date — corrects an
  intraday/partial bar once the day settles), order the union **ascending by `Date`**, and write. This mirrors
  the spirit of AD-1's immutable-provenance-vs-upsert split without touching evidence: the *file* is a
  rewritten mirror (like `FileScoreSnapshotStore`'s upsert-by-Id), but each **bar** is stable-by-date. State
  this choice explicitly in the class `<remarks>` and the PR. (Insert-only-append that rejects a changed bar
  would strand partial last-day bars — rejected.)
- Serialize with `RadarFileStoreJson.Options` via `GracefulFileWriter.TryWriteAllTextAsync` — reuse the shared
  helper + shape so on-disk shape and graceful-degrade posture cannot diverge from the other stores.
- `ReadAsync` degrades gracefully: no file → `null`; `IOException`/`UnauthorizedAccessException`/`JsonException`
  → log + `null` (mirror `FilePipelineRunStore.ReadRecentAsync`'s skip-on-bad-file posture).
- Options record `FilePriceHistoryStoreOptions { public required string RootDirectory { get; init; } }`
  (identical shape to `FilePipelineRunStoreOptions`).
- DI: `AddFilePriceHistoryStore(this IServiceCollection, string rootDirectory)` registering options +
  `IPriceHistoryStore -> FilePriceHistoryStore` singletons (mirror `AddFilePipelineRunStore`). A separate
  `AddHttpPriceHistoryReader(PriceReaderOptions)` registers the typed `HttpClient` reader (mirror
  `AddSecEarningsReleaseReader`'s `AddHttpClient<IPriceHistoryReader, HttpPriceHistoryReader>` + UA/gzip).

### 5. The opt-in acquisition step — OUTSIDE the evidence→signal→score path

A dedicated Application service `PriceHistoryAcquirer` (`Radar.Application/Prices/`) that: enumerates the
watch universe (`ICompanyRepository.GetAllAsync`), for each non-blank `Ticker` calls
`IPriceHistoryReader.ReadDailyAsync`, and on `Success` calls `IPriceHistoryStore.WriteAsync` — with the small
polite inter-request pace between tickers (from options; use the injected `TimeProvider`, consistent with the
News collector pacing). It logs a per-run summary (tickers fetched / bars stored / sources unreadable). It has
**no** dependency on the evidence/signal/scoring types and produces **no** `CollectedEvidence`.

**Where it runs:** a distinct step in the Worker, gated on `Radar:Prices:Enabled`. Recommended minimal shape:
inject an **optional** `IPriceHistoryAcquirer?` into `Worker` (null when prices disabled, mirroring the
runner's optional `IDirectionalFilingSignalSource`), and after seeding — **before or after** `RunPipelineAsync`,
but as a **separate call, not inside `IRadarPipeline`** — invoke it when non-null. This guarantees price
acquisition is structurally outside the `collect → map → resolve → review → store → score → report` pipeline.
Keep `IRadarPipeline` / `RadarPipelineRunner` **untouched**.

- **Gate in `RadarWorkerServices`:** wrap all price registrations (reader `HttpClient`, store,
  `IPriceHistoryAcquirer`) in `if (options.Prices.Enabled) { ... }`. When `false` (default), **none** are
  registered, `Worker`'s optional `IPriceHistoryAcquirer?` is `null`, the step is skipped, and the graph is
  byte-for-byte unchanged.
- **Options:** add `PricesWorkerOptions Prices { get; init; } = new();` to `RadarWorkerOptions` with
  `bool Enabled = false`, `string Range = "1y"`, `int InterRequestDelaySeconds = 1`, and a
  `string PricesDirectory` default `data/prices` (or add `PricesDirectory` next to the other `data/*` roots).
  Bind from `Radar:Prices`. Fail fast on a bad `Range` / negative delay only when enabled (mirror the
  SEC-UserAgent-when-enabled pattern).

---

## Project structure changes

```text
src/Radar.Application/Prices/
  PriceBar.cs                    # ADD: immutable daily bar (UTC DateOnly + decimal OHLC/adjclose + long volume)
  PriceHistory.cs                # ADD: ticker + source + RetrievedAtUtc + ordered/deduped bars
  IPriceHistoryReader.cs         # ADD: ReadDailyAsync(ticker, ct) -> PriceHistoryReadResult (NOT IEvidenceCollector)
  PriceHistoryReadResult.cs      # ADD: outcome enum (Success/Unreachable/HttpError/Timeout/Malformed/RateLimited)
                                 #      + bars + IsSuccess + advice-free Detail; Success/Failure factories
  IPriceHistoryStore.cs          # ADD: WriteAsync (merge/dedupe by Date, best-effort) + ReadAsync (best-effort)
  IPriceHistoryAcquirer.cs       # ADD: AcquireAsync(ct) — the opt-in acquisition step seam
  PriceHistoryAcquirer.cs        # ADD: enumerate universe -> read -> store; paced; NO evidence/signal/score dep

src/Radar.Infrastructure/Prices/
  HttpPriceHistoryReader.cs      # ADD (internal): typed HttpClient GET -> parse Yahoo chart JSON -> typed outcome
  PriceReaderOptions.cs          # ADD (internal): Range (validated), optional endpoint template

src/Radar.Infrastructure/FileSystem/
  FilePriceHistoryStore.cs       # ADD: data/prices/{ticker}.json via GracefulFileWriter + RadarFileStoreJson.Options;
                                 #      merge/dedupe by Date; graceful-degrade; <remarks> documents the merge posture
  FilePriceHistoryStoreOptions.cs# ADD: { required string RootDirectory }

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: add AddHttpPriceHistoryReader(PriceReaderOptions)
                                 #      (typed HttpClient + UA/gzip, mirror AddSecEarningsReleaseReader) and
                                 #      AddFilePriceHistoryStore(rootDirectory) (mirror AddFilePipelineRunStore)

src/Radar.Worker/
  RadarWorkerServices.cs         # MODIFIED: gate all price registrations behind options.Prices.Enabled
                                 #      (mirror the Radar:Ai opt-in gate); register the acquirer inside the gate
  RadarWorkerOptions.cs          # MODIFIED: add PricesWorkerOptions Prices (+ PricesDirectory default data/prices)
  Worker.cs                      # MODIFIED: inject optional IPriceHistoryAcquirer?; after seeding, invoke it as a
                                 #      SEPARATE step (NOT inside IRadarPipeline) when non-null
  appsettings.json               # MODIFIED: add a documented, DISABLED-by-default Radar:Prices block

docs/architecture-decisions.md   # MODIFIED: ADD AD-14 (price is validation/reference-only — never evidence,
                                 #      never a signal, never a scoring input), status Accepted

tests/Radar.Infrastructure.Tests/Prices/
  HttpPriceHistoryReaderTests.cs # ADD: offline (fake HttpMessageHandler + fixture JSON) parse + typed failures
tests/Radar.Infrastructure.Tests/FileSystem/
  FilePriceHistoryStoreTests.cs  # ADD: write/round-trip, merge/dedupe by Date, graceful-degrade
tests/Radar.Application.Tests/Prices/
  PriceHistoryAcquirerTests.cs   # ADD: enumerate -> read -> store; failure per ticker does not abort others
tests/Radar.Worker.Tests/ (or wherever RadarWorkerServices composition is tested)
  <composition test>             # ADD/MODIFIED: prices DISABLED (default) registers NOTHING price-related and the
                                 #      pipeline graph/collector list is unchanged; enabled registers the seam
```

**No** change to: `IEvidenceCollector` / any collector / the collector `IEnumerable`, `CollectedEvidence` /
`CollectedEvidenceMapper`, `EvidenceItem` / evidence repository, `ISignalExtractor` / signals,
`IScoringEngine` / `ScoringEngine` / any formula / `ScoringConfigVersion`, `IRadarPipeline` /
`RadarPipelineRunner`, or the report renderer/builder.

---

## Tests

Match the existing style (`HttpSecEarningsReleaseReaderTests` for the reader; `FilePipelineRunStoreTests` /
`FileScoreSnapshotStoreTests` for the store; a counting fake for the acquirer). All reader/store tests are
**offline** (fake `HttpMessageHandler` + fixture JSON, temp `RootDirectory` under the test temp dir); no network.

### `HttpPriceHistoryReaderTests` (offline)

1. A fixture Yahoo `chart` JSON with N aligned bars parses into N `PriceBar`s with the correct UTC `Date`
   (from the epoch-seconds timestamp), `Open/High/Low/Close/AdjClose` as `decimal`, and `Volume`.
2. A bar with a `null` `close` (or `null` `timestamp`) inside the arrays is **skipped**, not fabricated; the
   remaining aligned bars parse.
3. A 200 whose `chart.result` is `null` (the verified delisted-ticker body) or whose arrays are absent/ragged
   → `Malformed`; a valid document with **zero** bars → `Success` with zero bars.
4. **HTTP 404** (verified delisted shape) / any other non-success → `HttpError`; **HTTP 429** → `RateLimited`;
   `HttpRequestException` → `Unreachable`.
5. A thrown `TaskCanceledException` (timeout, `ct` not requested) → `Timeout`; an already-cancelled caller
   token re-throws `OperationCanceledException`. No advice language in any `Detail`.

### `FilePriceHistoryStoreTests` (offline, temp dir)

6. `WriteAsync` creates `data/prices/{ticker}.json`; `ReadAsync` round-trips an equal `PriceHistory` (ticker,
   source, bars — every `PriceBar` field, ascending by `Date`).
7. **Merge/dedupe by Date.** Writing an overlapping second history unions the bars keyed by `Date`, the new
   bar **replaces** a same-`Date` stored bar (last-write-wins per date), the result stays ascending by `Date`,
   and there is exactly one bar per date (no duplicates).
8. **Graceful degrade.** A `RootDirectory`/ticker that forces an `IOException`/`UnauthorizedAccessException`
   on write → `WriteAsync` logs + returns the attempted path, does not throw; an unreadable/malformed file →
   `ReadAsync` logs + returns `null`, does not throw.

### `PriceHistoryAcquirerTests`

9. Over a multi-company universe (fake `ICompanyRepository`), `AcquireAsync` calls the reader once per
   non-blank ticker and stores each `Success` result; a company with a blank `Ticker` is skipped.
10. A per-ticker read **failure** (e.g. `Unreachable`) is logged and does **not** abort the others (the loop
    continues) and does **not** throw; caller cancellation propagates.

### Composition test (Worker services)

11. With `Radar:Prices:Enabled=false` (default), the built service provider registers **no**
    `IPriceHistoryReader` / `IPriceHistoryStore` / `IPriceHistoryAcquirer`, the collector `IEnumerable` and the
    rest of the pipeline graph are **unchanged**, and `Worker`'s optional acquirer resolves to `null`. With
    `Enabled=true`, all three are registered. (Mirror however the existing tests assert the `Radar:Ai` opt-in
    gate's on/off registration.)

Keep all existing tests green. Assert **no** price type appears in the evidence/signal/scoring paths (the
structural guarantee): grep-level, `Radar.Application/Prices/*` references neither `EvidenceItem` /
`CollectedEvidence` nor any scoring type, and no collector emits a price bar.

---

## Constraints

- Target `net10.0`, C# 14. Layering (AD-5): `IPriceHistoryReader` / `IPriceHistoryStore` /
  `IPriceHistoryAcquirer` / the reference records live in `Radar.Application/Prices/`; the HTTP reader + the
  file store + their options + DI live in `Radar.Infrastructure`; the opt-in wiring + the acquisition step live
  in `Radar.Worker`. `Radar.Domain` is **unchanged** (price is deliberately not a domain aggregate). No provider
  SDK, no AI, no DB (AD-8, files-first).
- **THE boundary (AD-14): price is NOT evidence, NOT a signal, NOT a scoring input.** `IPriceHistoryReader` is
  **not** `IEvidenceCollector`; it returns **no** `CollectedEvidence`/`EvidenceItem`; it is **not** in the
  collector `IEnumerable`; the acquisition step runs **outside** `IRadarPipeline`. `data/prices/` is consumed
  by nothing in the scoring/evidence/signal/report path in this slice.
- **Determinism (AD-3):** bars ordered ascending by `Date`, deduped by `Date`; UTC dates; `decimal` prices;
  culture-invariant parsing.
- **Files-first + graceful degradation (AD-8):** reuse `GracefulFileWriter` + `RadarFileStoreJson.Options`; a
  disk/read failure logs + continues (write returns the path, read returns null) and never aborts. The reader
  **NEVER throws** on a bad response (typed outcome + zero bars); only caller cancellation propagates; HTTP 429
  → distinct `RateLimited`.
- **Opt-in + default-unchanged:** gate behind `Radar:Prices:Enabled` (default `false`); when disabled the
  pipeline graph is **byte-for-byte unchanged** (mirror the `Radar:Ai` opt-in gate). No key, no secret, no paid
  service — keyless Yahoo `chart` endpoint only.
- **NO scoring/formula/ScoringConfigVersion impact — state this explicitly.** This slice is entirely outside
  scoring: it does not touch any formula, `ScoringWeights`, `ScoringConfigVersion`, or `_formula.Version`, and
  it changes no score output. **No bump.**
- **Provenance note:** price data is its own reference store with its own `Source`/`RetrievedAtUtc`,
  **deliberately disconnected** from the evidence→signal→score provenance chain (price is not evidence). This
  is intentional per AD-14, not provenance erosion.
- **AD-9:** no advice language anywhere — price is raw factual data; emit no targets, ratings, or
  recommendations (no "buy"/"sell"/"target"/"guaranteed"/"safe bet") in any field, log, or `Detail`.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## AD-14 (propose + record Accepted)

Add to `docs/architecture-decisions.md`:

> ## AD-14 — Price data is validation/reference-only: never evidence, never a signal, never a scoring input
>
> **Decision.** Daily stock-price history is acquired and persisted as a **reference / validation dataset**
> (`data/prices/{ticker}.json`) via a **dedicated seam** — `IPriceHistoryReader` (Application) + an
> Infrastructure HTTP reader + `IPriceHistoryStore` — that is **structurally separate** from the evidence
> pipeline. Price is **NOT** an `IEvidenceCollector`, produces **no** `CollectedEvidence`/`EvidenceItem`, is
> **not** in the collector `IEnumerable` the runner consumes, and its acquisition step runs **outside**
> `IRadarPipeline` (the collect→map→resolve→review→store→score→report path). Price is therefore **never**
> extracted into a signal and **never** an input to scoring. The `data/prices/` store is consumed by nothing
> in the scoring/evidence/signal/report path today; it exists solely for a **future** price-efficacy
> validation/backtest spec. Price acquisition is **opt-in** (`Radar:Prices:Enabled`, default `false`); when
> disabled the pipeline graph is byte-for-byte unchanged.
>
> **Why.** Radar is a research assistant, not a trading bot ("signals before stories", "avoid hype loops" —
> philosophy). If price entered the evidence pipeline it would become eligible for signal extraction and
> scoring, turning business-trajectory research into price-chasing — the exact failure mode Radar exists to
> avoid. Making the boundary **structural** (a separate seam and store, not a convention) means a future
> change cannot accidentally let price influence a signal or a score without deleting this seam and tripping a
> reviewer. The price reference dataset lets a later spec **validate** whether Radar's signals preceded
> business improvement, without ever feeding price back into the signals being validated. The reviewer/planner
> must **not** propose making price a collector/evidence/signal/scoring input; doing so requires superseding
> this decision.
>
> **Status.** Accepted · 2026-07-04 (maintainer established this intent; spec 92). Cross-references the
> philosophy (signals before stories / not a trading bot), AD-5 (layering), AD-8 (files-first),
> AD-9 (no advice language), AD-3 (determinism). Surfacing a reference price in the report is **deferred** to
> the future validation-report spec.

---

## Out of scope (note, do NOT implement this round)

- **The price-efficacy computation / backtest and the visual/report** — the whole point of the deferred later
  spec; this slice only acquires + stores + lands the reader seam.
- **Surfacing price in the weekly report** (any reference price line) — deferred to the future
  validation-report spec, where factual-reference framing is done carefully. Do NOT touch the report renderer.
- **Any use of price in signals, extraction, or scoring** — forbidden by AD-14 (the point of this slice).
- **Intraday / minute bars, corporate-action reconstruction, splits/dividends math** — daily bars + Yahoo's
  provided `adjclose` only.
- **A second price source / failover** — one keyless source (Yahoo `chart`) for now; record alternatives in
  the verified-facts section if the lead becomes unusable.
- **PostgreSQL / a database for prices** — files-first (AD-8).

---

## Acceptance criteria

- [ ] Price rides a **dedicated seam**, structurally separate from evidence: `IPriceHistoryReader` is **not**
      `IEvidenceCollector`, returns **no** `CollectedEvidence`/`EvidenceItem`, is **not** in the collector
      `IEnumerable`, and the acquisition step runs **outside** `IRadarPipeline`. Asserted (composition test +
      no evidence/scoring-type reference from `Radar.Application/Prices/*`).
- [ ] `HttpPriceHistoryReader` (Infrastructure) fetches the **verified keyless Yahoo `chart` endpoint** with a
      browser `User-Agent`, parses the index-aligned `timestamp`/OHLCV/`adjclose` arrays into `PriceBar`s (UTC
      `Date`, `decimal` prices), skips null-`close`/null-`timestamp` bars, and confines all HTTP/JSON specifics
      to Infrastructure (AD-5). No key/secret/paid service. (stooq rejected — JS anti-bot wall; recorded.)
- [ ] The reader returns typed graceful outcomes and **NEVER throws** on a bad response: non-success →
      `HttpError`, 429 → `RateLimited`, transport → `Unreachable`, timeout → `Timeout`, null/empty result or
      ragged arrays → `Malformed`, zero bars → `Success`-with-zero; caller cancellation propagates. Tested.
- [ ] `FilePriceHistoryStore` persists to `data/prices/{ticker}.json` via the shared `GracefulFileWriter` +
      `RadarFileStoreJson.Options`, **merges/dedupes bars by `Date`** (last-write-wins per date, ascending),
      round-trips via `ReadAsync`, and **degrades gracefully** on a disk/read failure (log + path / null, no
      throw). The merge posture is documented in `<remarks>` and the PR. Tested.
- [ ] `PriceHistoryAcquirer` enumerates the seeded watch universe (`ICompanyRepository`), reads + stores each
      non-blank ticker (paced), and a per-ticker failure does not abort the others. Tested.
- [ ] Price acquisition is **opt-in** behind `Radar:Prices:Enabled` (default `false`); when disabled **nothing**
      price-related is registered, `Worker`'s optional acquirer is `null`, the step is skipped, and the pipeline
      graph is **byte-for-byte unchanged**. Tested (on/off composition).
- [ ] **NO scoring/formula/`ScoringConfigVersion`/`_formula.Version` change** — this slice is entirely outside
      scoring and changes no score output. Stated in the PR.
- [ ] `docs/architecture-decisions.md`: **AD-14** added (price is validation/reference-only — never evidence,
      never a signal, never a scoring input), status **Accepted**, cross-referencing the philosophy, AD-5,
      AD-8, AD-9, AD-3. Report surfacing of a reference price noted as deferred.
- [ ] Layering (AD-5), determinism (AD-3), files-first + graceful degradation (AD-8), and AD-9 (no advice
      language) preserved; `Radar.Domain` unchanged; no collector/evidence/signal/scoring/report change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.
