# Task: GDELT news collector (first third-party attention source; makes AttentionScore non-zero)

## Overview

Radar now runs multiple collectors (spec 54) via the `Radar:Collectors` list, the feed-type seam +
`EvidenceSourceType` values are in place (spec 55), and three real API collectors — RSS press releases
(spec 55), SEC EDGAR filings (spec 56), and USASpending.gov contract awards (spec 62) — have proven the
per-company API-collector pattern (typed reader outcome, feed-bound company attribution, declared quality,
graceful degradation, client-side relevance filter). This slice adds the **fourth real collector and the
FIRST third-party market-attention source**: a GDELT DOC 2.0 news collector that fetches each watch-universe
company's recent news coverage and turns each surviving article into `NewsArticle`-type evidence.

**Why this matters — the whole payoff.** `AttentionScore` is `0` for every company today, and that is
*correct*: per **AD-6**, attention counts distinct source names **only among third-party
(market-attention) source types**, and every collector we ship so far is first-party (a company's own press
releases, filings, RSS). AD-6 says so explicitly: "With only first-party collectors today `reach → 0` and
Attention → 0 (correct: market attention is unmeasurable from own disclosures); a news/media collector makes
it meaningful automatically." `EvidenceSourceType.NewsArticle` **already exists** in the append-only enum and
`EvidenceSourceTypes.IsThirdPartyAttentionSource(NewsArticle)` **already returns `true`** — so **producing
`NewsArticle` evidence makes `AttentionScore` non-zero with NO scoring or Domain enum change**. A corroborated
company can finally start earning Attention (and move toward `Investigate`).

USASpending (spec 62) is the closest precedent and should be mirrored **exactly** in structure and quality,
because it has the **same core hazard**: a fuzzy query with **no exact-entity key**, requiring a client-side
relevance filter to protect provenance. Where USASpending client-side-filters on the exact `recipient_id`,
GDELT has no such key — so this collector client-side-filters on **article `title` relevance** instead.

Scope of THIS slice is the **collector + its reader + wiring + seed**, producing `NewsArticle` evidence with
full provenance, deduped, relevance-filtered to the seeded company, rate-limit-safe, and surfaced in the
collection summary under a third-party source type that lifts Attention. Extracting *signals* from news
(e.g. mapping a media-attention burst to a `MediaAttention` signal) is the immediate follow-up (a future
spec) and is explicitly **OUT of scope** here — this slice is proven by evidence being collected,
relevance-filtered, deduped, stored, and counted under a third-party source type.

All external facts below were verified live against `api.gdeltproject.org` on 2026-07-02. The headless
implementer **cannot reach the API** (sandbox is rate-limited / TLS-intercepted), so treat every value here as
authoritative and do NOT re-research it.

---

## Assignment

Worktree: any (this is exactly the kind of self-contained collector slice the seam was built for)
Dependencies: 54 (multi-collector composition), 55 (feed-type seam + source types), 62 (USASpending collector
— the fuzzy-query / client-side-filter pattern this copies) — all merged.
Conflicts with: None — new Infrastructure files + additive DI + a new seed feed type. Does not touch the
runner, the merge logic, RSS, SEC, USASpending, or the scoring / extraction / report path.
Estimated time: ~2 h

---

## Verified GDELT facts (do NOT re-research; use these)

- **Endpoint (keyless, GET):** `GET https://api.gdeltproject.org/api/v2/doc/doc`
  - **No API key and no User-Agent required.** Public. HTTP 200 verified with no auth headers.
  - **Query params (verified):** `query=<phrase>` (URL-encoded), `mode=ArtList`, `format=json`,
    `maxrecords=<N>` (1–250), `timespan=<e.g. 1w|2w|1m>`, `sort=DateDesc`.
  - Example that returned 200 + JSON:
    `https://api.gdeltproject.org/api/v2/doc/doc?query=%22Mercury+Systems%22&mode=ArtList&format=json&maxrecords=4&timespan=2w&sort=DateDesc`
- **Response shape:**
  ```json
  { "articles": [
    { "url": "https://…",
      "url_mobile": "https://…",
      "title": "Mercury Systems , Inc . ( MRCY ): Among The Best Mid Cap Defense …",
      "seendate": "20260627T123000Z",
      "socialimage": "https://…",
      "domain": "finance.yahoo.com",
      "language": "English",
      "sourcecountry": "United States" } ] }
  ```
  - `articles` may be **ABSENT or empty** for a query with no recent coverage → yields **no evidence, not an
    error** (a company with no coverage, treated exactly like a quiet USASpending recipient).
  - **`seendate` parse:** exact format `yyyyMMddTHHmmssZ`, invariant culture, UTC — use it as the evidence
    observed/published instant so windowing/recency work.
  - GDELT inserts spaces around punctuation in `title` (e.g. `"Inc . ( MRCY )"`) — cosmetic; it is still the
    real headline. Do NOT try to "fix" it; use as-is for the evidence Title/RawText. (Note this when writing
    the title relevance filter — see gotcha #2 — so a spaced ticker like `( MRCY )` still matches.)

- **PROVENANCE-CRITICAL gotcha #1 — aggressive rate-limiting → HTTP 429 (the dominant operational
  constraint).** Verified: two quick back-to-back requests → the second returned **429 Too Many Requests**.
  GDELT throttles hard (roughly ~1 request every several seconds per client). The collector MUST therefore:
  - be **strictly SEQUENTIAL** across feeds (never fan out / never parallel);
  - **pace** requests with a small configurable inter-request delay between feeds;
  - treat **429 as a distinct typed outcome** (e.g. `RateLimited`) that logs + degrades that feed to no
    evidence (optionally a single bounded backoff-retry, then give up) — **NEVER throws / crashes**.
  Caller-requested cancellation must still propagate. This is the single most important behaviour to get
  right; a naive fan-out collector would get 429'd on every company after the first.

- **PROVENANCE-CRITICAL gotcha #2 — loose relevance / NO exact-entity key (the USASpending-`recipient_id`
  analogue).** GDELT phrase search is a full-text match with **no precise company identifier** (unlike
  USASpending's `recipient_id` or SEC's CIK). Verified: `query="Mercury Systems"` returned two genuinely
  MRCY/defense articles PLUS one **UNRELATED** article ("MASSPHOTON Launches … Water Disinfection System",
  manilatimes.net) that merely matched the words. Attaching that to Mercury would be a provenance error (a
  fabricated attention signal). Therefore the collector MUST:
  - (a) query with a **PRECISE phrase** — the quoted exact company name (the feed can also carry the ticker);
  - (b) **client-side POST-FILTER** returned articles to those whose `title` (case-insensitive) plausibly
    references the company — i.e. the title contains the company name **or** an explicit ticker token (e.g.
    `MRCY` / `( MRCY )`). Company attribution comes from the feed-bound `CompanyId` (exactly like RSS/SEC/
    USASpending); the **title post-filter is what prevents a loosely-matched unrelated article from being
    attached**. This is the `NewsArticle` analogue of USASpending's exact-`recipient_id` client-side filter —
    it is a **hard rule with a test** (an off-topic article whose title references neither the name nor the
    ticker is dropped).

- **Source-type payoff (no Domain change needed).** Map each surviving article →
  `EvidenceSourceType.NewsArticle`. **`NewsArticle` already exists** in the append-only
  `EvidenceSourceType` enum and **`EvidenceSourceTypes.IsThirdPartyAttentionSource(NewsArticle)` already
  returns `true`** (AD-6) — so producing `NewsArticle` evidence makes `AttentionScore` non-zero
  automatically. **Do NOT modify the enum and do NOT modify `EvidenceSourceTypes`.** (An existing Domain test
  already locks `IsThirdPartyAttentionSource(NewsArticle) == true`; if none does, add one — but no production
  Domain change.)

- **Quality.** Third-party news is lower-integrity than primary filings/awards — aggregators, wires, and
  listicles show up (the verified Yahoo "Among the Best Mid Cap Defense stocks" listicle). Declare a baseline
  `Metadata["quality"] = "Medium"` (below the SEC/USASpending `High`) via the spec-50 seam (read by
  `CollectedEvidenceMapper.ParseQuality`). Attention breadth counts distinct third-party source *names*;
  quality feeds `EvidenceConfidence`.

- **Provenance / dedupe.** `SourceUrl` = the article `url` (stable landing page). Include `url` + `title` +
  `seendate` in the hashed evidence text so distinct articles never collide under the mapper's `ContentHash`
  dedupe; also de-dupe within a feed by `url`.

- **Watch-universe coverage.** All 7 seeded companies have general news coverage, so — unlike USASpending
  (which omitted AEHR/ERII/HLIO/SPNS) — **seed a `news` feed for all 7**. A company with zero recent coverage
  simply yields no evidence, gracefully (empty `articles`).

---

## Project structure changes

```text
src/Radar.Infrastructure/Gdelt/
  IGdeltNewsReader.cs             # NEW: ReadAsync(query, ct) -> typed GdeltReadResult (offline-testable)
  GdeltReadResult.cs             # NEW: outcome enum (Success/Unreachable/HttpError/Timeout/Malformed/RateLimited) + items
  GdeltArticleItem.cs            # NEW: parsed article (Url, Title, Domain, SeenDate, Language, SourceCountry)
  GdeltNewsQuery.cs              # NEW: typed request (QueryPhrase, Timespan, MaxRecords, EnglishOnly)
  GdeltFeedTarget.cs             # NEW: parses a feed Url token into (QueryPhrase, Ticker?); returns null on malformed
  HttpGdeltNewsReader.cs         # NEW: HttpClient GET doc/doc -> parse articles[] -> outcome (429 -> RateLimited)
  GdeltNewsCollector.cs          # NEW: IEvidenceCollector; CollectorName "news"; SourceType NewsArticle; SEQUENTIAL + paced
  GdeltCollectorOptions.cs       # NEW: Timespan (default "2w"), MaxRecordsPerCompany (default 25), EnglishOnly (default true), InterRequestDelay (default ~3s), MaxRetriesOn429 (default 1)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddGdeltNewsCollector(...) additive registration + named HttpClient

src/Radar.Worker/
  RadarWorkerServices.cs         # MODIFIED: add "news" as an enable-able kind; update the valid-kinds messages
  RadarWorkerOptions.cs          # MODIFIED: add GdeltWorkerOptions (Timespan/MaxRecords/EnglishOnly/InterRequestDelaySeconds/MaxRetriesOn429), surfaced from Radar config
  appsettings.json               # MODIFIED: leave Collectors=["rss"] by default; add a documented Gdelt section

data/companies.json              # MODIFIED (config/data): add a per-company "news" feed for ALL 7 companies

tests/Radar.Infrastructure.Tests/Gdelt/
  HttpGdeltNewsReaderTests.cs    # NEW: offline (fake HttpMessageHandler + fixture JSON): parse, 429 -> RateLimited, HttpError, malformed, timeout, cancellation, empty/absent articles
  GdeltNewsCollectorTests.cs     # NEW: fake reader -> NewsArticle evidence with provenance/hints/summary; title relevance filter drops off-topic; dedupe by url; MaxRecords; malformed feed token; RateLimited/empty degrade; SEQUENTIAL ordering
  GdeltFeedTargetTests.cs        # NEW: parse valid token (with & without ticker), reject malformed

tests/Radar.Domain.Tests/Evidence/
  EvidenceSourceTypeTests.cs     # MODIFIED (only if not already asserted): IsThirdPartyAttentionSource(NewsArticle) == true
```

---

## Implementation details

### Reader (`HttpGdeltNewsReader`)

- Use `IHttpClientFactory` (`AddHttpClient<IGdeltNewsReader, HttpGdeltNewsReader>`). No User-Agent or key is
  required; optionally enable gzip/deflate decompression (polite, mirrors the USASpending client).
- `ReadAsync(GdeltNewsQuery query, CancellationToken ct)` builds and GETs
  `https://api.gdeltproject.org/api/v2/doc/doc` with `mode=ArtList`, `format=json`, `sort=DateDesc`,
  `timespan=<query.Timespan>`, `maxrecords=<query.MaxRecords>`, and `query=<phrase>` — where the phrase is the
  quoted company name (optionally `+sourcelang:english` when `EnglishOnly`). URL-encode the query value.
  Parse `articles[]` with `System.Text.Json` into `GdeltArticleItem`s.
- Return a typed `GdeltReadResult` mirroring `UsaSpendingReadResult`/`UsaSpendingReadOutcome`
  (Success/Failure factories, `IsSuccess`, an advice-free `Detail` for logging):
  - `Success` (+ items; items may be empty for a company with no coverage — absent/empty `articles` is NOT an
    error);
  - `RateLimited` — **HTTP 429** specifically (the distinct outcome for the special failure, the analogue of
    USASpending's `FiltersIgnored`); log + no items. The reader itself does the bounded backoff-retry when
    configured (`query`-independent) OR the collector handles retry — pick ONE and keep it in Infrastructure;
    prefer the reader owning a single delayed retry so the collector stays simple. After retries are
    exhausted, still return `RateLimited` (never throw);
  - `Unreachable` (`HttpRequestException`);
  - `HttpError` (any other non-success status);
  - `Timeout` (`TaskCanceledException` with `ct` NOT requested);
  - `Malformed` (bad/absent JSON or unexpected root shape — an object without a usable `articles` array where
    the body is not itself valid-but-empty is Malformed; a valid object with absent/empty `articles` is
    `Success` with zero items).
- **Never throw** on a bad response — degrade to the outcome + empty items; re-throw only genuine caller
  cancellation (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`), exactly as
  the SEC/RSS/USASpending readers do. Guard malformed/oversized JSON gracefully.
- Parse each article's `seendate` with `DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'",
  CultureInfo.InvariantCulture, AssumeUniversal | AdjustToUniversal, …)`; an unparseable/absent `seendate`
  falls back to `null` (mapper stamps `CollectedAt` regardless). Skip rows missing `url` (unattributable /
  can't dedupe or link) rather than throwing.
- All infrastructure (HttpClient, JSON, GDELT specifics) stays in `Radar.Infrastructure` (AD-5). All new types
  `internal` (mirroring the USASpending reader), relying on the existing `InternalsVisibleTo` for the test
  project.

### Collector (`GdeltNewsCollector`)

- `IEvidenceCollector` with `CollectorName = "news"` and `SourceType = EvidenceSourceType.NewsArticle`.
- For each `context.FeedsOfType("news")` feed (deterministic `(CompanyId, Id)` order from the seam),
  processed **strictly sequentially** (a `foreach`, never `Task.WhenAll` — the rate-limit rule):
  1. Parse the feed's `Url` token into `(QueryPhrase, Ticker?)` via `GdeltFeedTarget.Parse`. A malformed /
     unparsable token is a `SourceFailure` for that feed (logged Warning), not a throw — skip it.
  2. Build a `GdeltNewsQuery` from the parsed `QueryPhrase`, `_options.Timespan`, `_options.EnglishOnly`, and
     `MaxRecords = _options.MaxRecordsPerCompany` (clamp to the API's 1–250 range).
  3. **Pace:** before each request *after the first*, `await Task.Delay(_options.InterRequestDelay, ct)` so
     successive feeds do not trip the 429 throttle. (Use the injected `TimeProvider` for testable delay if
     practical; otherwise keep the delay real but small/config-driven and assert pacing indirectly. Do not
     block the thread.)
  4. `await _reader.ReadAsync(query, ct)`. On any non-`Success` outcome (incl. `RateLimited`): record a
     `SourceFailure(feed.Name, feed.Url, result.Detail ?? outcome)`, log a Warning, contribute no evidence for
     that feed (do NOT abort the whole run — later feeds still get their turn, after the pacing delay).
  5. **CLIENT-SIDE RELEVANCE FILTER (the hard rule):** keep only articles whose `title` (case-insensitive)
     contains the company name **or** the ticker token. Because GDELT spaces out punctuation in titles,
     normalise whitespace on both sides before the `Contains` check (collapse runs of whitespace to a single
     space) so `"( MRCY )"` still matches a `MRCY` ticker and `"Mercury Systems , Inc ."` still matches the
     `Mercury Systems` name. An article whose title references neither is DROPPED (the provenance guard). Then
     dedupe within the feed by `url` and cap at `MaxRecordsPerCompany`.
- Map each surviving article to a `CollectedEvidence`:
  - `SourceType` = `EvidenceSourceType.NewsArticle`; `SourceName` = `feed.Name`.
  - **Title/RawText**: synthesized from REAL fields only — Title = the article `title` as-is; RawText from
    `title`, `domain`, and `seendate` (e.g. `"{title} — {domain} ({seendate})"`). Include `url` + `title` +
    `seendate` in the hashed RawText so two distinct articles never collide under the mapper's Title+RawText
    `ContentHash` dedupe. **Do NOT fabricate an article body** (GDELT DOC ArtList returns no body text).
  - **Provenance**: `SourceUrl` = the article `url`. Carry `url`, `domain`, `seendate`, `language`,
    `sourcecountry`, and the feed `Url` in metadata (display + audit).
  - **Timestamps (UTC)**: `PublishedAt` = the `seendate` instant (parsed UTC), `null` when unparseable;
    `CollectedAt` = `TimeProvider.GetUtcNow()`.
  - **CompanyHints**: the feed is bound to a `CompanyId` (like RSS/SEC/USASpending), so pass the company's
    ticker (or name fallback) as the high-confidence hint via a small private `BuildCompanyHints` helper of
    the same shape the other collectors use — never invent a ticker. (The architecture reviewer's LOW-1
    note about the duplicated `BuildCompanyHints` across collectors is **out of scope**; this collector may
    keep its own private copy consistent with the others. Flag the shared-helper extraction as separate
    future cleanup — do NOT bundle it here.)
  - **Quality**: `Metadata["quality"] = "Medium"` (the spec-50 seam, read by
    `CollectedEvidenceMapper.ParseQuality`) — below the SEC/USASpending `High`, reflecting news's lower
    integrity.
- Log per-feed outcome (name, query phrase, count kept vs. returned, reason on failure) and a
  checked/failed/collected aggregate, and populate the `CollectionSummary`
  (`SourcesChecked`/`SourcesSucceeded`/`SourcesFailed`/`ItemsCollected` + `Failures`) exactly like
  `UsaSpendingContractCollector`/`SecEdgarFilingCollector` — the merged run summary (spec 54) then reflects
  rss + sec + usaspending + news.

### Config & seam

- `GdeltCollectorOptions`: `Timespan` (default `"2w"` — scoring-window-ish), `MaxRecordsPerCompany` (default
  `25`), `EnglishOnly` (default `true`), `InterRequestDelay` (default `~3s` — the politeness/pacing delay),
  `MaxRetriesOn429` (default `1`). No User-Agent (the API needs none).
- **Feed seam (Url token):** the `news` feed carries the query phrase (and optionally the ticker) in its
  single `Url` field, encoded as a documented, parseable token mirroring the USASpending
  `recipientId=…&recipientSearchText=…` pattern:
  `query=<company phrase>&ticker=<TICKER>` (e.g. `query=Mercury Systems&ticker=MRCY`). `ticker` is optional —
  a token that is just a bare phrase (no `query=`/`ticker=` keys) MAY be accepted as the phrase, but prefer
  the explicit `query=…` form for all seeds. `GdeltFeedTarget.Parse` splits this token; an unparsable/empty
  token yields `null`, which the collector degrades to a `SourceFailure` (not a throw). **Do NOT add fields to
  `CompanySourceFeed`** — its `Url` is documented as "carries that collector's per-company input".
- `AddGdeltNewsCollector(GdeltCollectorOptions options)` registers the reader + collector additively
  (`AddSingleton<IEvidenceCollector, GdeltNewsCollector>`) and the named `HttpClient` (gzip/deflate
  decompression; no UA). Fail fast (mirroring `AddUsaSpendingContractCollector`) when `MaxRecordsPerCompany`
  <= 0, `Timespan` is null/blank, or `InterRequestDelay` < `TimeSpan.Zero` — each would let the collector run
  yet either collect nothing or hammer the throttle.
- `RadarWorkerServices` gains `"news"` as an enable-able kind in the `Collectors` switch, building
  `GdeltCollectorOptions` from `options.Gdelt`. Update the three "valid kinds are …" fail-fast messages to
  include `"news"` (they currently list `"rss"`, `"localfile"`, `"sec"`, `"usaspending"`). Default
  `Radar:Collectors` stays `["rss"]` so existing runs are unchanged until `news` is explicitly enabled.
- `RadarWorkerOptions` gains a `Gdelt` property of a new `GdeltWorkerOptions`
  (`Timespan`/`MaxRecordsPerCompany`/`EnglishOnly`/`InterRequestDelaySeconds`/`MaxRetriesOn429`), defaulting so
  the rss-only config keeps working with no Gdelt config. `appsettings.json` adds a documented
  `Radar:Gdelt` section but **leaves `Collectors` as `["rss"]`**.
- **Seed**: add a `news` feed to ALL 7 companies in `data/companies.json` using the Url token above (e.g.
  `query=Mercury Systems&ticker=MRCY`). This is config/data — include it so a follow-up live run can enable
  `["rss","sec","usaspending","news"]` and watch Attention become non-zero for corroborated companies.

---

## Tests

- `HttpGdeltNewsReaderTests` (offline, fake `HttpMessageHandler`, fixture JSON, no network):
  - a fixture `articles[]` parses into the expected `GdeltArticleItem`s (correct url / title / domain /
    seendate parsed to the exact UTC instant / language / sourcecountry);
  - a response with **absent or empty `articles`** → `Success` with **zero items** (a company with no
    coverage, not an error);
  - an **HTTP 429** response → `RateLimited` outcome, zero items (and, if the reader owns the retry, it retries
    the configured number of times then still returns `RateLimited` — assert it does not throw);
  - any other non-success status → `HttpError`; malformed/empty JSON or unexpected root → `Malformed`;
  - a thrown `TaskCanceledException` (timeout) → `Timeout`; caller cancellation (`ct` requested) re-throws.
- `GdeltFeedTargetTests`: a valid `query=Mercury Systems&ticker=MRCY` token parses into the exact pair
  (phrase preserving spaces, ticker `MRCY`); a `query=…`-only token parses with a null/empty ticker; a
  malformed/empty token returns `null`.
- `GdeltNewsCollectorTests` (fake reader): articles map to `NewsArticle`-type `CollectedEvidence` with the
  article `url` as `SourceUrl`, `url`/`domain`/`seendate` in metadata, UTC `PublishedAt` from `seendate`, the
  feed's CompanyId as hint, and the declared `Medium` quality; **an off-topic article whose title references
  neither the company name nor the ticker is DROPPED** (the relevance/provenance guard — model it on the
  verified MASSPHOTON false positive); articles dedupe by `url` and `MaxRecordsPerCompany` is honoured; a
  spaced-punctuation title like `"( MRCY )"` still matches the ticker; a malformed feed token and a
  `RateLimited`/empty read each degrade to a `SourceFailure`/no evidence **without throwing**; the collector
  processes feeds **sequentially** (assert order / that it does not fan out) and the `CollectionSummary`
  counts checked/succeeded/failed/collected correctly.
- `EvidenceSourceTypeTests`: assert
  `EvidenceSourceTypes.IsThirdPartyAttentionSource(EvidenceSourceType.NewsArticle) == true` (locks the
  third-party classification that makes Attention non-zero). Add only if not already present; make **no**
  production Domain change.
- Existing tests (RSS, SEC, USASpending, runner merge, DI collector list) stay green; the multi-collector
  runner now composes rss + sec + usaspending + news.

---

## Constraints

- Target `net10.0`. Deterministic collector; all GDELT/HTTP/JSON confined to `Radar.Infrastructure` (AD-5);
  no provider SDK; no DB (AD-8, files-first); no AI. Provenance preserved (evidence → article `url`; hints
  from feed binding; title relevance post-filter guards attribution).
- Never emit advice language. **Honour GDELT rate limits: STRICTLY sequential across feeds + paced
  inter-request delay + 429 as a typed non-throwing outcome** (the dominant operational constraint). Caller
  cancellation still propagates.
- Scope to the new collector + wiring + seed + tests. Do NOT modify the scoring formula, the keyword
  extractor, or the report (signal extraction from news is a future spec). Do NOT change the
  `EvidenceSourceType` enum or `EvidenceSourceTypes` (`NewsArticle` and its third-party classification already
  exist — AD-6). Do NOT change the `CompanySourceFeed` record. Do NOT bundle the shared-`BuildCompanyHints`
  cleanup (LOW-1) — flag it as separate future work.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `GdeltNewsCollector` GETs each `news` feed's query phrase from `api.gdeltproject.org/api/v2/doc/doc`
      (`mode=ArtList&format=json&sort=DateDesc` + configured `timespan`/`maxrecords`), CLIENT-SIDE-FILTERS
      results by title relevance (company name or ticker, whitespace-normalised), and maps surviving articles
      to `NewsArticle`-type evidence with the article `url` as `SourceUrl`, UTC `PublishedAt` from `seendate`,
      feed-bound company hint, and `Medium` declared quality.
- [ ] Producing `NewsArticle` evidence makes `AttentionScore` non-zero automatically:
      `EvidenceSourceTypes.IsThirdPartyAttentionSource(NewsArticle)` is already `true` (AD-6); the
      `EvidenceSourceType` enum and `EvidenceSourceTypes` are NOT modified; a Domain test locks the `true`.
- [ ] The reader returns typed outcomes and NEVER throws on a bad response; **HTTP 429 is a distinct
      `RateLimited` outcome** (no evidence, optional bounded retry, no crash); absent/empty `articles` is
      `Success` with zero items; caller cancellation propagates.
- [ ] The collector processes `news` feeds **strictly sequentially** with a configurable inter-request pacing
      delay (never fans out) — the GDELT rate-limit rule — and degrades a `RateLimited`/failed/empty feed to a
      `SourceFailure`/no evidence without aborting the run.
- [ ] The feed `Url` token carries the query phrase (and optional ticker); a malformed token degrades to a
      `SourceFailure`; the shared `CompanySourceFeed` record is unchanged.
- [ ] The collector is additively registered and enable-able via `Radar:Collectors` containing `"news"`;
      default config is unchanged (`["rss"]`); the merged `CollectionSummary` reflects all enabled collectors.
- [ ] The seed carries a `news` feed for all 7 watch-universe companies; the collector degrades gracefully
      (empty, not error) for a company with zero recent coverage.
- [ ] Offline tests cover parse, empty/absent articles, `RateLimited`(429), `HttpError`, `Malformed`,
      `Timeout`, cancellation, feed-token parse, the title relevance drop, dedupe-by-url, `MaxRecords`,
      sequential ordering, and mapping/provenance/hints. No production scoring/extraction/report/Domain
      change. `build`/`test` green.
