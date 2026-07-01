# Task: USASpending.gov government-contract collector (second real API collector; adds official-record source diversity)

## Overview

Radar already runs multiple collectors (spec 54) via the `Radar:Collectors` list, the feed-type seam +
`EvidenceSourceType` values are in place (spec 55), and the SEC EDGAR filing collector (spec 56) proved the
per-company API-collector pattern (typed reader outcome, feed-bound company attribution, declared quality,
graceful degradation). This slice adds the **next real API collector**: a USASpending.gov government-contract
collector that fetches each watch-universe company's recent **federal contract awards** and turns them into
`GovernmentContract`-type evidence. It is a direct copy of the SEC collector's shape and the concrete next step
toward **source diversity** — the measured unlock from the first live run (single-source `EvidenceConfidenceScore`
capped every company at "Ignore"; the score rewards distinct source types). A federal contract award is
official-record evidence (like a filing), so it adds source-type DIVERSITY to `EvidenceConfidence`, but it is NOT
third-party market attention.

Scope of THIS slice is the **collector + its reader + wiring + seed**, producing `GovernmentContract` evidence
with full provenance, deduped, client-side-filtered to the exact seeded recipient, and surfaced in the collection
summary under a new source type. Extracting *signals* from contract awards (e.g. mapping a large new award to a
directional signal) is the immediate follow-up (a future spec) and is explicitly **OUT of scope** here — this
slice is proven by evidence being collected, filtered, deduped, stored, and counted under a new source type.

All external facts below were verified live against `api.usaspending.gov` on 2026-07-01. The headless implementer
**cannot reach the API** (sandbox TLS is MITM'd — `curl (60) self-signed certificate in chain`), so treat every
value here as authoritative and do NOT re-research it.

---

## Assignment

Worktree: any (this is exactly the kind of self-contained collector slice the seam was built for)
Dependencies: 54 (multi-collector composition), 55 (feed-type seam + source types), 56 (SEC collector pattern) —
all merged.
Conflicts with: None — new Infrastructure files + additive DI + a new seed feed type. Does not touch the runner,
the merge logic, RSS, SEC, or the scoring/extraction path.
Estimated time: ~2 h

---

## Verified USASpending facts (do NOT re-research; use these)

- **Endpoint (use exactly this, KEEP the trailing slash — the API is slash-sensitive):**
  `POST https://api.usaspending.gov/api/v2/search/spending_by_award/`
- **No API key, no mandatory User-Agent.** Verified HTTP 200 with no UA header (unlike SEC EDGAR, which 403s
  without one). Still send `Content-Type: application/json` and be polite: sequential requests, one page per
  company per run. Public data.
- **Time-period floor:** the API only searches back to `2007-10-01` (returned as a `messages[]` note); irrelevant
  for our recent-activity window, but never send an earlier `start_date`.
- **Request body:**
  ```json
  {
    "filters": {
      "award_type_codes": ["A","B","C","D"],
      "recipient_search_text": ["<search term>"],
      "time_period": [{"start_date":"YYYY-MM-DD","end_date":"YYYY-MM-DD"}]
    },
    "fields": ["Award ID","Recipient Name","Award Amount","Awarding Agency","Start Date","End Date",
               "Description","recipient_id","generated_internal_id"],
    "sort": "Award Amount", "order": "desc", "limit": 25, "page": 1
  }
  ```
  - **`fields` is a whitelist of DISPLAY-NAME strings** with exact casing/spacing: `"Award ID"`,
    `"Recipient Name"`, `"Award Amount"`, `"Awarding Agency"`, `"Start Date"`, `"End Date"`, `"Description"`. The
    API returns ONLY the fields you request, **plus** an always-present `internal_id`. To get `recipient_id` and
    `generated_internal_id` back you MUST list them in `fields` (they are absent otherwise — verified).
  - `limit` max 100; `order` ∈ `asc|desc`.
- **Award-type groups are MUTUALLY EXCLUSIVE per request (hard validation).** Sending codes from more than one
  group returns HTTP 400: `{"message":"'award_type_codes' must only contain types from one group.", ...}`. Groups
  (verified): **contracts** `A,B,C,D` (BPA Call / Purchase Order / Delivery Order / Definitive Contract);
  **idvs** `IDV_A..IDV_E`; **grants** `02,03,04,05`; **loans** `07,08,F003,F004`; plus direct payments / other.
  **Default the collector to the contracts group `["A","B","C","D"]`** (the meaningful "winning government
  business" signal). IDVs/grants can be separate configurable queries later — out of scope for this slice.
- **Response shape:**
  ```json
  { "spending_level":"awards", "limit":25,
    "results":[ { "internal_id":359630135, "Award ID":"N6893626P5106",
        "Recipient Name":"MERCURY SYSTEMS INC", "Award Amount":159160.0,
        "Awarding Agency":"Department of Defense", "Start Date":"2026-03-24", "End Date":"...",
        "Description":"PROCESSOR CARDS P/N# 910-56141-18",
        "recipient_id":"af09eaba-71de-97b6-660d-1adac9349c4d-C",
        "generated_internal_id":"CONT_AWD_N6893626P5106_9700_-NONE-_-NONE-" } ],
    "page_metadata":{ "page":1, "hasNext":true, ... },
    "messages":[ "..." ] }
  ```
  - `Award Amount` is a JSON number (may be large; parse as `decimal`/`double`). `Start Date` / `End Date` are
    `YYYY-MM-DD` strings. `Award Type` is often `null` for contracts — do not rely on it.
- **PROVENANCE-CRITICAL gotcha #1 — unsupported filter keys are SILENTLY IGNORED and the API returns the ENTIRE
  national firehose.** Sending a plausible-looking `filters.recipient_id` (a key that does NOT exist) returned the
  top awards in the country (Humana $51B, Lockheed $48B, …) with a `messages[]` entry:
  `"The following filters from the request were not used: {'recipient_id'}."`
  → The collector/reader **MUST inspect `messages[]` after every call** and treat any
  `"filters ... were not used"` warning as a **HARD FAILURE** (a distinct typed outcome, NO evidence emitted) —
  otherwise a typo silently ingests thousands of unrelated companies' awards and attaches them to our company.
  There is **no** `recipient_id` filter; recipient filtering is ONLY via `recipient_search_text`.
- **PROVENANCE-CRITICAL gotcha #2 — `recipient_search_text` is FUZZY full-text, NOT a precise single-entity
  filter (even with a UEI).** Verified: `"Mercury Systems"` also matched `MERCURY CABLING SYSTEMS LLC`,
  `MERCURY DISPOSAL SYSTEMS INC.` (unrelated companies) and two different `recipient_id`s for the real company.
  Searching by Mercury's UEI `J51ULX3CNCZ4` returned `PHYSICAL OPTICS CORPORATION` (a Mercury subsidiary,
  different `recipient_id`) ranked above Mercury itself.
  → The collector **MUST query by `recipient_search_text` and then CLIENT-SIDE-FILTER the results to keep only
  rows whose `recipient_id` == the feed's exact seeded `recipient_id`.** Company attribution comes from the
  feed-bound `CompanyId` (exactly like the SEC collector's per-feed CIK binding); the `recipient_id` equality
  check is what guarantees we never attach another entity's (or a subsidiary's) awards. Excluding subsidiaries is
  the correct conservative MVP default; parent/`-P` rollup can be a future refinement (note it, don't build it).
- **Provenance URL (for evidence `SourceUrl`):** `https://www.usaspending.gov/award/{generated_internal_id}` —
  the stable award landing page. Example:
  `https://www.usaspending.gov/award/CONT_AWD_47QFCA21F0018_4732_47QTCK18D0004_4732`. Include the `Award ID` and
  `generated_internal_id` in the hashed evidence text so distinct awards never collide under `ContentHash` dedupe.
- **Verified seed values (watch universe — resolved live 2026-07-01).** Seed a `usaspending` feed ONLY for
  companies that actually have federal contract awards; OMIT the rest (exactly as spec 56 omitted delisted SPNS).
  Store the exact `recipient_id` (the precise key) AND the recipient name (the fuzzy `recipient_search_text`
  query) in the feed:

  | Ticker | Company Id (seed)                      | recipient_search_text | recipient_id (exact filter key)            | Contracts? |
  |--------|----------------------------------------|-----------------------|--------------------------------------------|-----------|
  | MRCY   | `885ea986-041f-4fc2-8163-b815ae930a78` | `Mercury Systems`     | `af09eaba-71de-97b6-660d-1adac9349c4d-C`   | YES — active DoD contracts (millions, 2026). **Primary corroboration target.** UEI `J51ULX3CNCZ4`. |
  | AGYS   | `f0d50897-7161-40e6-a367-4ce63fc5aa8c` | `Agilysys`            | `5a343048-e1bb-6455-195f-d2213057e618-C`   | Marginal — one ~$6,775 HHS contract. Include (exercises the "one tiny award" path). |
  | CYRX   | `d4def651-0d9e-4198-9dcf-b0dd2ed918d3` | `Cryoport Systems`    | `c13d9361-755f-12da-2e17-8dda387a4a8f-C`   | Marginal — one ~$4,809 HHS contract. Include. |
  | AEHR / ERII / HLIO | —                          | —                     | —                                          | NO federal contracts → omit. |
  | SPNS   | —                                      | —                     | —                                          | Foreign (Sapiens), delisted → omit (consistent with spec 56). |

  The collector MUST degrade gracefully (empty, not error) for a recipient with zero matching awards.
- **`EvidenceSourceType.GovernmentContract` already exists** in the append-only enum — no enum change is needed.
  `EvidenceSourceTypes.IsThirdPartyAttentionSource` already returns `false` for it (the default), which is
  correct: a federal award is an official record, not third-party market attention (same treatment as `Filing`).
  Do NOT add `GovernmentContract` to the third-party whitelist; add a Domain test that locks this in.

---

## Project structure changes

```text
src/Radar.Infrastructure/UsaSpending/
  IUsaSpendingAwardReader.cs           # NEW: ReadAsync(query, ct) -> typed UsaSpendingReadResult (offline-testable)
  UsaSpendingReadResult.cs             # NEW: outcome enum (Success/Unreachable/HttpError/Timeout/Malformed/FiltersIgnored) + items
  UsaSpendingAwardItem.cs              # NEW: parsed award (AwardId, RecipientName, AwardAmount, AwardingAgency, StartDate, EndDate, Description, RecipientId, GeneratedInternalId, AwardUrl)
  UsaSpendingAwardQuery.cs             # NEW: typed request (SearchText, StartDate, EndDate, AwardTypeCodes, Limit)
  UsaSpendingFeedTarget.cs             # NEW: parses a feed Url token into (RecipientId, RecipientSearchText); returns null on malformed
  HttpUsaSpendingAwardReader.cs        # NEW: HttpClient POST spending_by_award -> parse results[] + inspect messages[] -> outcome
  UsaSpendingContractCollector.cs      # NEW: IEvidenceCollector; CollectorName "usaspending"; SourceType GovernmentContract
  UsaSpendingCollectorOptions.cs       # NEW: AwardTypeCodes (default A/B/C/D), LookbackDays (default 365), MaxAwardsPerCompany (default 25)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddUsaSpendingContractCollector(...) additive registration + named HttpClient

src/Radar.Worker/
  RadarWorkerServices.cs               # MODIFIED: add "usaspending" as an enable-able kind; update the valid-kinds messages
  RadarWorkerOptions.cs                # MODIFIED: add UsaSpending config (UsaSpendingWorkerOptions) surfaced from Radar config
  appsettings.json                     # MODIFIED: leave Collectors=["rss"] by default; add a documented UsaSpending section

data/companies.json                    # MODIFIED (config/data): add per-company "usaspending" feeds for MRCY/AGYS/CYRX only

tests/Radar.Domain.Tests/Evidence/
  EvidenceSourceTypeTests.cs           # MODIFIED (or add): assert IsThirdPartyAttentionSource(GovernmentContract) == false

tests/Radar.Infrastructure.Tests/UsaSpending/
  HttpUsaSpendingAwardReaderTests.cs   # NEW: offline (fake HttpMessageHandler + fixture JSON): parse, messages[] "not used" -> FiltersIgnored, 400/HttpError, malformed, timeout, cancellation
  UsaSpendingContractCollectorTests.cs # NEW: fake reader -> GovernmentContract evidence with provenance/hints/summary; recipient_id client-side filter; MaxAwardsPerCompany; malformed feed token; empty/degraded feed
  UsaSpendingFeedTargetTests.cs        # NEW: parse valid token, reject malformed
```

---

## Implementation details

### Reader (`HttpUsaSpendingAwardReader`)

- Use `IHttpClientFactory` (`AddHttpClient<IUsaSpendingAwardReader, HttpUsaSpendingAwardReader>`). No User-Agent is
  required; POST with `Content-Type: application/json`. Optionally enable gzip decompression (polite, not required).
- `ReadAsync(UsaSpendingAwardQuery query, CancellationToken ct)` POSTs the fixed endpoint
  `https://api.usaspending.gov/api/v2/search/spending_by_award/` (keep the trailing slash) with the request body
  shape above, built from `query` (its `SearchText`, `StartDate`/`EndDate`, `AwardTypeCodes`, `Limit`). Serialize
  the `fields` whitelist EXACTLY as the verified display-name strings (including `recipient_id` and
  `generated_internal_id`). Parse `results[]` with `System.Text.Json` into `UsaSpendingAwardItem`s (construct each
  award's `AwardUrl = https://www.usaspending.gov/award/{generated_internal_id}`).
- **REQUIRED robustness rule (provenance):** after a 200 parse, inspect `messages[]`; if ANY message contains
  `"were not used"` (case-insensitive — the silent-ignored-filter firehose warning), return a distinct
  `FiltersIgnored` failure with NO items. Never emit awards from a firehose response.
- Return a typed `UsaSpendingReadResult` mirroring `SecFilingReadResult`/`SecFilingReadOutcome`:
  `Success` (+ items; items may be empty for a recipient with no awards), `Unreachable` (`HttpRequestException`),
  `HttpError` (non-success status, incl. a 400 award-type-group validation error), `Timeout`
  (`TaskCanceledException` with `ct` NOT requested), `Malformed` (bad/absent JSON or unexpected root shape),
  `FiltersIgnored` (the messages[] warning). **Never throw** on a bad response — degrade to the outcome + empty
  items; re-throw only genuine caller cancellation
  (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`), exactly as the SEC/RSS
  readers do. Guard against malformed/oversized JSON gracefully.
- All infrastructure (HttpClient, JSON, USASpending specifics) stays in `Radar.Infrastructure` (AD-5). All new
  types `internal` (mirroring the SEC reader), relying on the existing `InternalsVisibleTo` for the test project.

### Collector (`UsaSpendingContractCollector`)

- `IEvidenceCollector` with `CollectorName = "usaspending"` and `SourceType = EvidenceSourceType.GovernmentContract`.
- For each `context.FeedsOfType("usaspending")` feed (deterministic `(CompanyId, Id)` order from the seam):
  1. Parse the feed's `Url` token into `(RecipientId, RecipientSearchText)` via `UsaSpendingFeedTarget.Parse`. A
     malformed/unparsable token is a `SourceFailure` for that feed (logged Warning), not a throw — skip it.
  2. Build a `UsaSpendingAwardQuery` from the parsed `RecipientSearchText`, the configured `AwardTypeCodes`, and a
     time window `[now - LookbackDays, now]` (clamped so `start_date` is never before `2007-10-01`), `Limit` =
     `MaxAwardsPerCompany` (capped at 100), using the injected `TimeProvider` for `now` (UTC, deterministic).
  3. `await _reader.ReadAsync(query, ct)`. On any non-`Success` outcome (incl. `FiltersIgnored`): record a
     `SourceFailure(feed.Name, feed.Url, result.Detail ?? outcome)`, log a Warning, contribute no evidence.
  4. **CLIENT-SIDE-FILTER**: keep only awards where `award.RecipientId == feed's RecipientId`
     (`StringComparison.Ordinal`) — this is what prevents subsidiaries/unrelated fuzzy matches from being
     attached. Then take the most-recent/most-relevant `MaxAwardsPerCompany` (the API already sorts by amount
     desc; dedupe within the feed by `generated_internal_id`).
- Map each surviving award to a `CollectedEvidence`:
  - `SourceType` = `EvidenceSourceType.GovernmentContract`; `SourceName` = `feed.Name`.
  - **Title/summary**: synthesize from REAL fields only, e.g.
    `"Federal contract award {AwardId} — {AwardingAgency} → {RecipientName} (${AwardAmount:N0}, {StartDate})"`.
    RawText: recipient, awarding agency, amount, description, start date, plus the `Award ID` and
    `generated_internal_id` (do NOT fabricate text). This gives the future signal-extraction slice something
    meaningful to read; this slice does not itself extract signals.
  - **Provenance**: `SourceUrl` = `AwardUrl` (`https://www.usaspending.gov/award/{generated_internal_id}`). Carry
    `awardId` + `generatedInternalId` + `recipientId` in metadata AND in the hashed RawText so two distinct awards
    never collide under the mapper's Title+RawText `ContentHash` dedupe.
  - **Timestamps (UTC)**: set `PublishedAt` from the award `Start Date` (parse `YYYY-MM-DD` as UTC midnight,
    invariant culture) so windowing/recency work; `CollectedAt` = `TimeProvider.GetUtcNow()`. Keep the raw start
    date string in metadata for display.
  - **CompanyHints**: the feed is bound to a `CompanyId` (like SEC/RSS feeds), so pass the company's ticker (or
    name fallback) as the high-confidence hint via the same helper shape `SecEdgarFilingCollector` uses — never
    invent a ticker; resolution attaches the award to the right company via the feed binding.
  - **Quality**: a federal contract award is an official primary record. Declare a baseline
    `Metadata["quality"] = "High"` (the same seam spec 50 established, read by
    `CollectedEvidenceMapper.ParseQuality`) — like SEC filings, above the press-release `Medium`, reinforcing the
    diversity/confidence story.
- Log per-feed outcome (name, recipient search text/id, reason on failure) and a checked/failed/collected
  aggregate, and populate the `CollectionSummary` (`SourcesChecked`/`SourcesSucceeded`/`SourcesFailed`/
  `ItemsCollected` + `Failures`) exactly like `SecEdgarFilingCollector`/`RssPressReleaseCollector` — the merged
  run summary (spec 54) then reflects rss + sec + usaspending.

### Config & seam

- `UsaSpendingCollectorOptions`: `AwardTypeCodes` (default `["A","B","C","D"]` — the contracts group),
  `LookbackDays` (default `365`), `MaxAwardsPerCompany` (default `25`). No User-Agent (the API needs none).
- **Feed seam (Url token):** the `usaspending` feed carries BOTH the exact `recipient_id` (precise key) and the
  `recipient_search_text` (fuzzy query) in its single `Url` field, encoded as a documented, parseable token:
  `recipientId=<recipient_id>&recipientSearchText=<recipient name>`
  (e.g. `recipientId=af09eaba-71de-97b6-660d-1adac9349c4d-C&recipientSearchText=Mercury Systems`). This keeps the
  shared `CompanySourceFeed` record unchanged — its `Url` is documented as "carries that collector's per-company
  input: a feed URL, or an API endpoint / identifier". `UsaSpendingFeedTarget.Parse` splits this token; an
  unparsable token yields a `SourceFailure`, not a throw. (Do NOT add fields to `CompanySourceFeed` — that would
  touch the shared Domain record.)
- `AddUsaSpendingContractCollector(UsaSpendingCollectorOptions options)` registers the reader + collector
  additively (`AddSingleton<IEvidenceCollector, UsaSpendingContractCollector>`) and the named `HttpClient`. Fail
  fast (mirroring `AddSecEdgarCollector`) when `AwardTypeCodes` is null/empty, `MaxAwardsPerCompany` <= 0, or
  `LookbackDays` <= 0 — each would let the collector run yet silently collect nothing.
- `RadarWorkerServices` gains `"usaspending"` as an enable-able kind in the `Collectors` switch, building
  `UsaSpendingCollectorOptions` from `options.UsaSpending`. Update the three "valid kinds are …" fail-fast
  messages to include `"usaspending"`. Default `Radar:Collectors` stays `["rss"]` so existing runs are unchanged
  until USASpending is explicitly enabled.
- `RadarWorkerOptions` gains a `UsaSpending` property of a new `UsaSpendingWorkerOptions` (AwardTypeCodes /
  LookbackDays / MaxAwardsPerCompany), defaulting so the rss-only config keeps working with no USASpending config.
- **Seed**: add a `usaspending` feed to MRCY (definitely), AGYS, and CYRX in `data/companies.json` using the Url
  token above and the verified `recipient_id`s; OMIT AEHR/ERII/HLIO (no contracts) and SPNS (delisted). This is
  config/data — include it so a follow-up live run can enable `["rss","sec","usaspending"]`.

---

## Tests

- `HttpUsaSpendingAwardReaderTests` (offline, fake `HttpMessageHandler`, fixture JSON, no network):
  - a fixture `results[]` parses into the expected `UsaSpendingAwardItem`s (correct AwardId / RecipientName /
    amount / agency / start date / recipient_id / generated_internal_id / AwardUrl);
  - a 200 response whose `messages[]` contains `"... were not used ..."` → `FiltersIgnored` outcome, **zero
    items** (the firehose guard);
  - HTTP 400 (award-type-group validation) → `HttpError`; malformed/empty JSON or unexpected root → `Malformed`;
    a thrown `TaskCanceledException` (timeout) → `Timeout`; caller cancellation re-throws.
- `UsaSpendingFeedTargetTests`: a valid `recipientId=...&recipientSearchText=...` token parses into the exact
  pair (search text preserving spaces); a malformed/empty token returns null.
- `UsaSpendingContractCollectorTests` (fake reader): awards map to `GovernmentContract`-type `CollectedEvidence`
  with the award landing-page `SourceUrl`, `awardId`/`generatedInternalId`/`recipientId` in metadata, UTC
  `PublishedAt` from `Start Date`, the feed's CompanyId as hint, and the declared `High` quality; **awards whose
  `recipient_id` != the feed's `recipient_id` are dropped** (the subsidiary/fuzzy-match guard); `MaxAwardsPerCompany`
  is honoured and awards dedupe by `generated_internal_id`; a malformed feed token and a `FiltersIgnored`/empty
  read each degrade to a `SourceFailure`/no evidence without throwing; the `CollectionSummary` counts
  checked/succeeded/failed/collected correctly; deterministic ordering preserved.
- `EvidenceSourceTypeTests`: assert `EvidenceSourceTypes.IsThirdPartyAttentionSource(EvidenceSourceType.GovernmentContract)`
  is `false` (locks the official-record classification, matching `Filing`).
- Existing tests (RSS, SEC, runner merge, DI collector list) stay green; the multi-collector runner now composes
  rss + sec + usaspending.

---

## Constraints

- Target `net10.0`. Deterministic collector; all USASpending/HTTP/JSON confined to `Radar.Infrastructure` (AD-5);
  no provider SDK; no DB (AD-8, files-first); no AI. Provenance preserved (evidence → award landing-page URL;
  hints from feed binding; exact-recipient client-side filter).
- Never emit advice language. Be polite to the public API (sequential requests, one page per company per run;
  contracts-group `award_type_codes` only, per the mutual-exclusivity rule).
- Scope to the new collector + wiring + seed + tests. Do NOT modify the scoring formula, the keyword extractor, or
  the report (signal extraction from awards is a future spec). Do NOT change the `EvidenceSourceType` enum
  (`GovernmentContract` already exists) or the `CompanySourceFeed` record.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `UsaSpendingContractCollector` POSTs each `usaspending` feed's `recipient_search_text` to
      `spending_by_award/` with the contracts-group `award_type_codes`, CLIENT-SIDE-FILTERS results to the feed's
      exact `recipient_id`, and maps surviving awards to `GovernmentContract`-type evidence with an award
      landing-page `SourceUrl`, UTC `PublishedAt` from `Start Date`, feed-bound company hint, and `High` declared
      quality.
- [ ] The reader returns typed outcomes and NEVER throws on a bad response; it inspects `messages[]` and returns a
      distinct `FiltersIgnored` failure (no evidence) on the "filters ... were not used" firehose warning; caller
      cancellation propagates.
- [ ] The feed `Url` token carries both `recipient_id` and `recipient_search_text`; a malformed token degrades to
      a `SourceFailure`; the shared `CompanySourceFeed` record is unchanged.
- [ ] The collector is additively registered and enable-able via `Radar:Collectors` containing `"usaspending"`;
      default config is unchanged (`["rss"]`); the merged `CollectionSummary` reflects all enabled collectors.
- [ ] The seed carries `usaspending` feeds for MRCY/AGYS/CYRX only (verified `recipient_id`s), omitting
      AEHR/ERII/HLIO/SPNS; the collector degrades gracefully (empty, not error) for a recipient with zero matching
      awards.
- [ ] `EvidenceSourceTypes.IsThirdPartyAttentionSource(GovernmentContract)` is `false` (official record, not
      market attention); a Domain test locks this in. The `EvidenceSourceType` enum is not modified.
- [ ] Offline tests cover parse, `FiltersIgnored`, 400/HttpError, malformed, timeout, cancellation, feed-token
      parse, recipient_id client-side filter, `MaxAwardsPerCompany`, mapping/provenance/hints, and graceful
      degradation. No production scoring/extraction/report change. `build`/`test` green.
```
