# Task: SEC EDGAR filing collector (first real API collector; proves the multi-collector fan-out)

## Overview

Radar can now run multiple collectors (spec 54) via the `Radar:Collectors` list, and the feed-type seam +
`EvidenceSourceType` values are in place (spec 55). This slice adds the **first real API collector** beyond RSS:
a SEC EDGAR filing collector that fetches each watch-universe company's recent filings and turns them into
`Filing`-type evidence. It is the pattern every later collector (gov contracts, patents, …) will copy, and it is
the concrete step toward **source diversity** — the measured unlock from the first live run (single-source
`EvidenceConfidenceScore` capped every company at "Ignore"; the score rewards distinct source types).

Scope of THIS slice is the **collector + its reader + wiring + seed**, producing `Filing` evidence with full
provenance. Extracting *signals* from filing content (e.g. mapping 8-K item codes to signal types) is the
immediate follow-up (spec 57) and is explicitly OUT of scope here — this slice is proven by evidence being
collected, deduped, stored, and surfaced in the collection summary under a second source type.

All external facts below were verified live against SEC endpoints (User-Agent `Radar Research <email>`).

---

## Assignment

Worktree: any (this is exactly the kind of self-contained collector slice the seam was built for)
Dependencies: 54 (multi-collector composition), 55 (feed-type seam + source types) — both merged.
Conflicts with: None — new Infrastructure files + additive DI + a new seed feed type. Does not touch the runner,
the merge logic, RSS, or the scoring/extraction path.
Estimated time: ~2 h

---

## Verified EDGAR facts (do NOT re-research; use these)

- **Endpoint (use this one):** `https://data.sec.gov/submissions/CIK##########.json` — CIK zero-padded to 10
  digits. One request returns the company's recent filing history. Verified 200 with a compliant User-Agent.
- **Response shape:** top-level `name`, `tickers`, `exchanges`, … and `filings.recent`, which is a **columnar
  (parallel-array) structure** — all arrays share one index, newest-first. Relevant arrays:
  `form`, `filingDate` (YYYY-MM-DD), `reportDate`, `acceptanceDateTime` (full UTC, `...Z`), `accessionNumber`
  (e.g. `0000320193-26-000011`), `primaryDocument`, `primaryDocDescription`, `items` (comma-joined 8-K item
  codes like `"2.02,9.01"`; empty for non-8-K). To get the N most recent of chosen forms: walk the arrays in
  order, filter `form[i] ∈ desired`, take first N.
- **Mandatory `User-Agent` (HARD requirement):** requests without a declared UA get **HTTP 403 Forbidden** (not
  a throttle). Verified: WebFetch (non-compliant UA) → 403; `curl` with `User-Agent: Radar Research <email>` →
  200. The collector MUST send a compliant UA; treat a missing UA as a fail-fast config error.
- **Rate limit:** 10 requests/second max (global). Also send `Accept-Encoding: gzip, deflate`. For a handful of
  companies (one request each per run) sequential requests are comfortably under the ceiling; no elaborate
  limiter needed for the MVP, but do not fan out unbounded parallel requests.
- **Filing → canonical URL (for provenance):** with `accNoNoDashes` = accession without dashes and `cik` =
  CIK with leading zeros stripped:
  - Index (stable landing page): `https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{accessionWithDashes}-index.htm`
  - Primary document: `https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{primaryDocument}`
- **Signal-bearing forms:** `8-K` (item codes encode the event: 1.01 material agreement, 2.01 acquisition
  completion, 2.02 results, 5.02 director/officer change), `10-Q`/`10-K` (periodic results), `Form 4` (insider).
  Default the collector's form filter to **8-K, 10-Q, 10-K** (config-overridable); Form 4 can be added later.
- **CIKs for the 7 seeded companies:** MRCY `0001049521`, AEHR `0001040470`, AGYS `0000078749`,
  CYRX `0001124524`, ERII `0001421517`, HLIO `0001024795`, SPNS `0000885740`.
  **SPNS caveat:** Sapiens went private / filed Form 15-12G on 2026-01-02 and is **no longer an active filer**
  (empty `tickers`/`exchanges`; historically a foreign private issuer filing 20-F/6-K, never 8-K/10-K). A SEC
  feed for SPNS returns no recent signal-form filings — the collector must degrade gracefully (empty, not an
  error). Recommend **omitting the SEC feed for SPNS in the seed**; the collector must still not choke if present.

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  ISecFilingReader.cs              # NEW: ReadAsync(cik/url, ct) -> typed SecFilingReadResult
  SecFilingReadResult.cs           # NEW: outcome enum (Success/Unreachable/HttpError/Timeout/Malformed/Forbidden) + items
  SecFilingItem.cs                 # NEW: parsed filing (form, filingDate, reportDate, acceptanceDateTimeUtc, accession, primaryDocument, primaryDocDescription, items, indexUrl)
  HttpSecFilingReader.cs           # NEW: HttpClient (UA + gzip) -> data.sec.gov submissions JSON -> parse columnar arrays -> outcome
  SecEdgarFilingCollector.cs       # NEW: IEvidenceCollector; CollectorName "sec-edgar"; SourceType Filing
  SecCollectorOptions.cs           # NEW: required UserAgent, form-type filter (default 8-K/10-Q/10-K), max filings per company

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddSecEdgarCollector(...) additive registration + HttpClient

src/Radar.Worker/
  RadarWorkerServices.cs           # MODIFIED: add "sec" as an enable-able kind in the Collectors switch
  RadarWorkerOptions.cs            # MODIFIED: SEC config (UserAgent etc.) surfaced from Radar config
  appsettings.json                 # MODIFIED: leave Collectors=["rss"] by default; add a documented Sec section

data/companies.json                # MODIFIED (config/data): add per-company "sec" feeds (CIK submissions URL), omit SPNS

tests/Radar.Infrastructure.Tests/Sec/
  HttpSecFilingReaderTests.cs      # NEW: offline (fake HttpMessageHandler + fixture JSON): parse, 403->Forbidden, malformed->Malformed, timeout
  SecEdgarFilingCollectorTests.cs  # NEW: fake reader -> Filing evidence with provenance, hints, summary; empty/degraded feed
```

## Implementation details

### Reader (`HttpSecFilingReader`)
- Use `IHttpClientFactory` (add `Microsoft.Extensions.Http` is already referenced by Infrastructure). Configure
  the named client with the **required `User-Agent`** (from `SecCollectorOptions.UserAgent`) and
  `Accept-Encoding: gzip, deflate`; enable automatic decompression.
- Fetch the company's submissions JSON (the feed `Url` — see seam below). Parse `filings.recent` columnar arrays
  with `System.Text.Json` into a list of `SecFilingItem` (newest-first), constructing each filing's index URL
  per the formula above.
- Return a typed `SecFilingReadResult` mirroring the RSS reader's outcome pattern
  (`RssFeedReadResult`/`RssFeedReadOutcome`): `Success` (+ items), `Forbidden` (HTTP 403 — almost always a
  missing/invalid UA; log a clear, actionable message), `HttpError`, `Unreachable`, `Timeout`, `Malformed`
  (bad/absent JSON). **Never throw** on a bad feed — degrade to the outcome + empty items; re-throw only genuine
  caller cancellation (`OperationCanceledException when ct.IsCancellationRequested`), exactly as the RSS reader
  does. XXE is not a concern (JSON), but guard against malformed/oversized JSON gracefully.
- All infrastructure (HttpClient, JSON, SEC specifics) stays in `Radar.Infrastructure` (AD-5). All new types
  `internal` (mirroring the RSS reader), with `InternalsVisibleTo` already present for the test project.

### Collector (`SecEdgarFilingCollector`)
- `IEvidenceCollector` with `CollectorName = "sec-edgar"` and `SourceType = EvidenceSourceType.Filing`.
- For each `context.FeedsOfType("sec")` feed (deterministic `(CompanyId, Id)` order from the seam), read the
  filings, filter to the configured form types, take the most-recent `MaxFilingsPerCompany` (default e.g. 25).
- Map each filing to a `CollectedEvidence`:
  - `SourceType` = `EvidenceSourceType.Filing`.
  - **Title/summary**: synthesize from real metadata, e.g. `"{form} — {primaryDocDescription or form title} ({filingDate})"`, and for 8-Ks include the item codes. RawText: the human descriptions of the form / 8-K items (real filing semantics — do NOT fabricate text). This gives spec 57 something meaningful to extract from; this slice does not itself extract signals.
  - **Provenance**: `SourceUrl` = the filing **index URL**; carry the `accessionNumber` (in metadata) so distinct filings never collide under `ContentHash` dedupe (two same-form filings on different dates must hash differently — include accession/date in the hashed text).
  - **Timestamps (UTC)**: set the evidence published/observed instant from `acceptanceDateTimeUtc` (full UTC) so windowing/recency work; keep `filingDate` for display. This is essential — filings carry real dates and must fall in the scoring window correctly.
  - **CompanyHints**: the SEC feed is bound to a `CompanyId` (like RSS feeds), so pass that as the high-confidence hint — resolution attaches the filing to the right company via the feed binding (no company guessing).
  - **Quality**: SEC filings are the highest-integrity primary source. Declare a baseline quality of **`High`**
    (reviewer may choose `Primary`) via the same `Metadata["quality"]` seam spec 50 used — higher than the
    press-release `Medium`, and it reinforces the diversity/confidence story.
- Log per-feed outcome (name, CIK/URL, reason on failure) and a checked/failed/collected aggregate, and populate
  the `CollectionSummary` (sources checked/failed + `SourceFailure` list) exactly like `RssPressReleaseCollector`
  — the merged run summary (spec 54) then reflects both collectors.

### Config & seam
- `SecCollectorOptions`: `UserAgent` (**required** — fail fast with a clear message if missing/blank, since
  every request 403s without it), `Forms` (default `["8-K","10-Q","10-K"]`), `MaxFilingsPerCompany` (default 25).
  Surface `UserAgent` (at least) through `RadarWorkerOptions`/`appsettings.json` `Radar:Sec:UserAgent`.
- **Feed seam**: store the submissions JSON URL in the `sec` feed's `Url`, i.e.
  `https://data.sec.gov/submissions/CIK##########.json` (seam-consistent — `Url` is a URL to fetch). The reader
  fetches it directly. (Storing the bare CIK and constructing the URL in the collector is an acceptable
  alternative; pick one and document it.)
- `AddSecEdgarCollector` registers the reader + collector additively (`AddSingleton<IEvidenceCollector, …>`) and
  the named HttpClient; `RadarWorkerServices` gains `"sec"` as an enable-able kind. Default `Radar:Collectors`
  stays `["rss"]` so existing runs are unchanged until SEC is explicitly enabled (and a UA is configured).
- **Seed**: add a `sec` feed to each company in `data/companies.json` (the verified CIK submissions URL), OMIT
  Sapiens (delisted). This is config/data — include it so the follow-up live run can enable `["rss","sec"]`.

## Tests

- `HttpSecFilingReaderTests` (offline, fake `HttpMessageHandler`): a fixture submissions JSON parses into the
  expected `SecFilingItem`s (correct form/date/accession/index URL); a 403 response → `Forbidden` outcome;
  malformed/empty JSON → `Malformed`; a thrown `TaskCanceledException` (timeout) → `Timeout`; caller cancellation
  re-throws. No network.
- `SecEdgarFilingCollectorTests` (fake reader): filings map to `Filing`-type `CollectedEvidence` with the index
  URL, accession in metadata, UTC observed instant from `acceptanceDateTime`, the feed's CompanyId as hint, and
  the declared quality; the form filter and `MaxFilingsPerCompany` are honoured; a company whose read returns
  `Forbidden`/empty degrades to a `SourceFailure`/no evidence without throwing; the `CollectionSummary` counts
  checked/failed correctly. Deterministic ordering preserved.
- Existing tests (RSS, runner merge, DI list) stay green; the multi-collector runner now composes rss + sec.

## Constraints

- Target `net10.0`. Deterministic collector; all SEC/HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no
  provider SDK; no DB (AD-8); no AI. Provenance preserved (evidence → index URL; hints from feed binding).
- Never emit advice language. Honour the SEC User-Agent + rate rules (compliant UA required; sequential/polite
  requests under 10 rps; gzip).
- Scope to the new SEC collector + wiring + seed + tests. Do NOT modify the scoring formula, the keyword
  extractor, or the report (signal extraction from filings is spec 57).
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] `SecEdgarFilingCollector` fetches each `sec` feed's submissions JSON with a compliant, configurable
      User-Agent, filters to the configured forms, and maps recent filings to `Filing`-type evidence with a
      real index-URL provenance link, UTC observed instant from `acceptanceDateTime`, feed-bound company hint,
      and `High` (or `Primary`) declared quality.
- [ ] The reader returns typed outcomes (incl. a distinct `Forbidden` for 403/UA) and never throws on a bad
      feed; caller cancellation propagates. A missing `UserAgent` is a fail-fast config error.
- [ ] The collector is additively registered and enable-able via `Radar:Collectors` containing `"sec"`; default
      config is unchanged (`["rss"]`); the merged `CollectionSummary` reflects both collectors.
- [ ] The seed carries per-company `sec` feeds (verified CIK URLs), omitting SPNS; the collector degrades
      gracefully for a delisted/empty issuer.
- [ ] Offline tests cover parse, 403→Forbidden, malformed, timeout, cancellation, mapping/provenance/hints, form
      filter, and graceful degradation. No production scoring/extraction/report change. `build`/`test` green.
