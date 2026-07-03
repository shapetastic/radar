# Task: Third-party news attention collector — wire the spec-80 reader into a live collector that lifts AttentionScore

## Overview

This slice **completes the attention-restoration arc** begun in spec 80. Spec 80 lands the design decision
plus an offline-tested reader seam (`INewsSearchReader` → `NewsSearchReadResult` over a keyless,
**non-per-IP-throttled** third-party news source — the fix for GDELT's per-IP quota that made `AttentionScore`
0 in live runs). But spec 80 ships **no collector, no wiring, and no seed**, so no evidence flows and Attention
is still 0 in a live run. THIS slice adds the missing collector: it turns each watch-universe company's
third-party news coverage into `NewsArticle`-type `CollectedEvidence` with full provenance, deduped,
relevance-filtered, rate-limit-safe, and surfaced in the collection summary under a third-party source type
that lifts Attention.

**Why this matters — the payoff.** `EvidenceSourceType.NewsArticle` already exists and
`EvidenceSourceTypes.IsThirdPartyAttentionSource(NewsArticle) == true` (AD-6), and spec 70 already emits one
Neutral `MediaAttention` signal per `NewsArticle` evidence item. So the **moment** this collector produces
`NewsArticle` evidence for a corroborated company, that company's `reach` (distinct third-party source names +
`0.5·mediaCount`) becomes non-zero, `AttentionScore` becomes non-zero, and the Opportunity term's
under-the-radar discount (`1 − Attention/200`, AD-6) finally engages — **with no scoring, Domain, or
extractor change**. This is the exact structural payoff GDELT (spec 67) was meant to deliver but could not,
because it is per-IP throttled.

**Arc position:** spec 80 (source decision + reader seam) → **THIS collector (81)**. This slice contains no
new HTTP/XML parsing itself — it composes the merged `INewsSearchReader` behind the `IEvidenceCollector`
contract, applies the relevance/provenance guard, maps to evidence, and wires it as an enable-able collector
kind.

---

## Assignment

Worktree: any (a self-contained collector slice, exactly what the feed-type seam was built for)
Dependencies: **80** (`INewsSearchReader`/`NewsSearchReadResult`/`NewsArticleItem`/`NewsFeedTarget` — the
offline-tested reader), 54 (multi-collector composition), 55 (feed-type seam + source types), 62/67 (the
`UsaSpendingContractCollector`/`GdeltNewsCollector` patterns this mirrors — fuzzy query, client-side relevance
filter, typed outcome, feed-bound attribution, graceful degradation, `CollectionSummary`), 70 (news →
`MediaAttention` — confirms the payoff) — all merged (80 must land first).
Conflicts with: touches `RadarWorkerServices.cs` (adds a `Radar:Collectors` kind + valid-kinds messages),
`RadarWorkerOptions.cs` (a `News` options block), `InfrastructureServiceCollectionExtensions.cs` (additive
`AddNewsAttentionCollector` + named `HttpClient`), `appsettings.json`, and `data/companies.json`. It must NOT
run in parallel with another worker-DI/collectors-list slice — sequence it. It does NOT touch the runner,
merge logic, scoring, extraction, or the report.
Estimated time: ~2 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **Collectors compose as a set; kinds are enabled via `Radar:Collectors`.** `RadarWorkerServices.cs`
  switches on kind (`"rss"`/`"localfile"`/`"sec"`/`"usaspending"`/`"news"` — see lines ~60, 71, 80–121) and
  the three "valid kinds are …" fail-fast messages must all be updated when a kind is added.
- **`GdeltNewsCollector` is the exact structural precedent** (`src/Radar.Infrastructure/Gdelt/
  GdeltNewsCollector.cs`): sequential `foreach` over `context.FeedsOfType(<kind>)` in deterministic
  `(CompanyId, Id)` order, per-feed pace delay, typed read outcome → `SourceFailure`/no-evidence on
  non-`Success` (never abort), client-side **title relevance post-filter** (company name OR ticker,
  whitespace-normalised), dedupe within feed by `url`, `MaxRecords` cap, map surviving items to
  `CollectedEvidence`, populate `CollectionSummary`. Copy this shape.
- **`GdeltNewsCollector` already uses `CollectorName = "news"` and `SourceType = NewsArticle`.** This slice
  introduces a **different** third-party news source. Choose a **distinct** collector name (e.g.
  `"newssearch"`) so both can coexist and be enabled independently, OR — if the maintainer prefers a single
  news collector — state explicitly in the PR that this REPLACES GDELT as the `"news"` kind (retiring the
  GDELT registration path) and update the valid-kinds list accordingly. **RECOMMENDED: add a distinct kind
  (`"newssearch"`)** so GDELT is untouched and the two are independently enable-able; the maintainer can
  retire GDELT later in a separate decision. Pick one and state it in the PR.
- **`NewsArticle` is already third-party (AD-6) and spec 70 already extracts `MediaAttention` from it.** So
  this collector needs **no** scoring/extractor/Domain change — producing the evidence is sufficient.
- **The relevance post-filter is a HARD provenance rule (the GDELT/USASpending analogue).** News search has
  **no exact-entity key**, so a full-text query returns some off-topic articles that merely match the words
  (spec 67 verified a MASSPHOTON false positive under a "Mercury Systems" query). Company attribution comes
  from the feed-bound `CompanyId`; the **title relevance post-filter is what prevents a loosely-matched
  unrelated article from being attached** — it is tested (an article whose title references neither the
  company name nor the ticker is dropped).

---

## Project structure changes

```text
src/Radar.Infrastructure/News/
  NewsAttentionCollector.cs      # NEW: IEvidenceCollector; CollectorName e.g. "newssearch"; SourceType NewsArticle;
                                 #      SEQUENTIAL + paced; title relevance filter; dedupe; maps to CollectedEvidence
  NewsCollectorOptions.cs        # MODIFIED (from spec 80): add collector-level knobs — InterRequestDelay (pace),
                                 #      MaxRetriesOn429 if not already, Timespan/recency if the source supports it

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddNewsAttentionCollector(NewsCollectorOptions)
                                 #      additive registration + named HttpClient (gzip/deflate; UA only if the source needs it)

src/Radar.Worker/
  RadarWorkerServices.cs         # MODIFIED: add the new kind to the Collectors switch; update all valid-kinds messages
  RadarWorkerOptions.cs          # MODIFIED: add NewsWorkerOptions (MaxRecordsPerCompany/EnglishOnly/InterRequestDelaySeconds/...)
  appsettings.json               # MODIFIED: leave Collectors=["rss"] default; add a documented Radar:News section

data/companies.json              # MODIFIED (config/data): add a per-company news-search feed for the watch universe

tests/Radar.Infrastructure.Tests/News/
  NewsAttentionCollectorTests.cs # NEW: fake INewsSearchReader -> NewsArticle evidence with provenance/hints/summary;
                                 #      title relevance drops off-topic; dedupe by url; MaxRecords; malformed feed token;
                                 #      RateLimited/empty degrade without throw; SEQUENTIAL ordering; declared quality
```

`Radar.Domain` is unchanged (`NewsArticle` + its third-party classification already exist). No scoring,
extraction, report, runner, or merge change. No DB (AD-8).

---

## Implementation details

### `NewsAttentionCollector` (mirror `GdeltNewsCollector` exactly)

- `IEvidenceCollector` with `CollectorName = "newssearch"` (or the maintainer-chosen name) and
  `SourceType = EvidenceSourceType.NewsArticle`.
- For each `context.FeedsOfType(<kind>)` feed (deterministic `(CompanyId, Id)` order), processed **strictly
  sequentially** (a `foreach`, never `Task.WhenAll`):
  1. Parse the feed `Url` token via `NewsFeedTarget.Parse` → `(QueryPhrase, Ticker?)`. A malformed/unparsable
     token → `SourceFailure` (logged Warning), skip — not a throw.
  2. Build a `NewsSearchQuery` (`QueryPhrase`, `MaxRecords = _options.MaxRecordsPerCompany` clamped,
     `EnglishOnly`).
  3. **Pace:** before each request *after the first*, `await Task.Delay(_options.InterRequestDelay, ct)` (use
     `TimeProvider` for testable delay if practical). Do not block the thread.
  4. `await _reader.ReadAsync(query, ct)`. On any non-`Success` outcome (incl. `RateLimited`): record a
     `SourceFailure(feed.Name, feed.Url, result.Detail ?? outcome)`, log Warning, contribute no evidence for
     that feed (do NOT abort the run — later feeds still get their turn after the pace delay).
  5. **CLIENT-SIDE RELEVANCE FILTER (the hard rule):** keep only articles whose `Title` (case-insensitive,
     whitespace-normalised — collapse runs of whitespace, strip any `" - Publisher"` suffix per the spec-80
     verified facts) contains the company name **or** the ticker token. An article referencing neither is
     DROPPED. Then dedupe within the feed by `Url` and cap at `MaxRecordsPerCompany`.
- Map each surviving `NewsArticleItem` → `CollectedEvidence`:
  - `SourceType = NewsArticle`; `SourceName = feed.Name` (this is the distinct third-party source name that
    lifts Attention — one per feed; two articles from the same feed share it, giving `mediaCount 2` + 1
    distinct source name exactly as spec 70's worked example).
  - **Title/RawText** from REAL fields only: `Title` = the article title as-is; `RawText` composed from
    `title` + publisher + `pubDate` (e.g. `"{title} — {publisher} ({pubDate})"`). Include `url` + `title` +
    `pubDate` in the hashed RawText so distinct articles never collide under the mapper's Title+RawText
    `ContentHash`. **Do NOT fabricate an article body.**
  - **Provenance:** `SourceUrl` = the article `url`; carry `url`, publisher, `pubDate`, language, and the feed
    `Url` in metadata (display + audit).
  - **Timestamps (UTC):** `PublishedAt` = parsed `pubDate` (`null` when unparseable); `CollectedAt` =
    `TimeProvider.GetUtcNow()`.
  - **CompanyHints:** the feed is bound to a `CompanyId`, so pass the company's ticker (name fallback) as the
    high-confidence hint via a private `BuildCompanyHints` helper of the same shape as the other collectors —
    never invent a ticker. (The shared-`BuildCompanyHints`-across-collectors cleanup is a known LOW note and
    is **out of scope** — keep a private copy consistent with the others; flag it as future work, do not
    bundle it.)
  - **Quality:** `Metadata["quality"] = "Medium"` (the spec-50 seam read by
    `CollectedEvidenceMapper.ParseQuality`) — third-party news is lower-integrity than primary
    filings/awards; below the SEC/USASpending `High`, consistent with GDELT.
- Log per-feed outcome (name, query, kept vs. returned, failure reason) and a checked/succeeded/failed/
  collected aggregate; populate the `CollectionSummary`
  (`SourcesChecked`/`SourcesSucceeded`/`SourcesFailed`/`ItemsCollected` + `Failures`) exactly like
  `GdeltNewsCollector`/`UsaSpendingContractCollector` so the merged run summary (spec 54) reflects it.
- **All HTTP/XML/source specifics stay behind the injected `INewsSearchReader`** (AD-5) — this collector
  contains no `HttpClient` and no XML parsing.

### DI + worker wiring (mirror `AddGdeltNewsCollector` / the `"news"` kind)

- `AddNewsAttentionCollector(NewsCollectorOptions options)`: register the reader
  (`AddHttpClient<INewsSearchReader, HttpNewsSearchReader>` with gzip/deflate; UA only if the source requires
  one) + the collector (`AddSingleton<IEvidenceCollector, NewsAttentionCollector>`) additively. **Fail fast**
  (mirroring `AddGdeltNewsCollector`) when `MaxRecordsPerCompany <= 0` or `InterRequestDelay < TimeSpan.Zero`
  — each would let the collector run yet collect nothing or hammer the source.
- `RadarWorkerServices` gains the new kind in the `Collectors` switch, building `NewsCollectorOptions` from
  `options.News`. **Update all three "valid kinds are …" fail-fast messages** to include the new kind. Default
  `Radar:Collectors` stays `["rss"]`.
- `RadarWorkerOptions` gains a `News` property of a new `NewsWorkerOptions`
  (`MaxRecordsPerCompany`/`EnglishOnly`/`InterRequestDelaySeconds`/…) with defaults so the rss-only config
  keeps working. `appsettings.json` adds a documented `Radar:News` section but **leaves `Collectors` as
  `["rss"]`** so existing runs are unchanged until the news-search kind is explicitly enabled.
- **Seed:** add a news-search feed to the watch-universe companies in `data/companies.json` using the
  `query=<company>&ticker=<TICKER>` token (all companies that have general news coverage — likely all 7, as
  GDELT seeded). A company with zero coverage simply yields no evidence, gracefully.

---

## Tests

### `NewsAttentionCollectorTests` (offline; fake `INewsSearchReader`; no network)

Implement an `internal FakeNewsSearchReader : INewsSearchReader` returning scripted `NewsSearchReadResult`s
and counting calls. Cases (mirror `GdeltNewsCollectorTests`):

1. Articles map to `NewsArticle`-typed `CollectedEvidence` with the article `url` as `SourceUrl`,
   `url`/publisher/`pubDate` in metadata, UTC `PublishedAt` from `pubDate`, the feed's CompanyId as hint, and
   the declared `Medium` quality; `SourceName == feed.Name`.
2. **An off-topic article whose title references neither the company name nor the ticker is DROPPED** (the
   relevance/provenance guard — model on the verified MASSPHOTON false positive).
3. A spaced/suffixed title (e.g. `"Rocket Lab USA , Inc . ( RKLB ) - Reuters"`) still matches the `RKLB`
   ticker / the `Rocket Lab` name after whitespace-normalisation + publisher-suffix strip.
4. Articles dedupe by `url`; `MaxRecordsPerCompany` is honoured.
5. A malformed feed token → `SourceFailure`/no evidence, **no throw**.
6. A `RateLimited`/`HttpError`/empty read → `SourceFailure`/no evidence, **no throw**; the run continues to
   later feeds.
7. The collector processes feeds **strictly sequentially** (assert ordering / no fan-out) and the
   `CollectionSummary` counts checked/succeeded/failed/collected correctly.
8. Caller cancellation propagates (`OperationCanceledException`).

### Worker / DI

9. `AddNewsAttentionCollector` fail-fast: `MaxRecordsPerCompany <= 0` or negative `InterRequestDelay` throws
   with the documented `Radar:News:*` message; `null` options throws `ArgumentNullException`.
10. Enabling the new kind via `Radar:Collectors` registers the collector; default `["rss"]` does not; the
    valid-kinds messages include the new kind.

Existing RSS/SEC/USASpending/GDELT/runner-merge/DI-collector-list tests stay green; the multi-collector runner
now also composes the news-search collector when enabled.

---

## Spec-implementation checklist

1. **Code paths replaced:** none removed if you add a distinct kind (RECOMMENDED). If instead you replace
   GDELT as the `"news"` kind, remove the GDELT registration path and its worker wiring and update its tests
   — state this explicitly in the PR. Either way the spec-80 reader is the sole HTTP path for this collector.
2. **Tests:** add the collector cases (1–8) and DI/worker cases (9–10); keep existing collector/runner/DI
   tests green.
3. **Delete nothing still used** (unless deliberately retiring GDELT per the chosen option).
4. **CLAUDE.md / architecture-decisions.md:** no architecture rule change — this is the same collector pattern
   (AD-5 keeps HTTP/XML in Infra behind `INewsSearchReader`; AD-8 files-first; AD-6 unchanged; producing
   `NewsArticle` evidence lifts Attention via the already-shipped spec-70 `MediaAttention` extraction). No new
   AD entry — note in the PR.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector; all source/HTTP/XML specifics behind the spec-80
  `INewsSearchReader` (AD-5). No provider SDK, no AI, no DB (AD-8, files-first). No key/paid source/secrets.
- **Rate-limit-safe:** strictly sequential across feeds + configurable inter-request pace + typed
  non-throwing outcomes (429 → `RateLimited` handled by the reader); caller cancellation propagates.
- **Provenance preserved:** evidence → article `url`; feed-bound company hint; the title relevance
  post-filter guards attribution (the HARD rule, tested). Real fields only; no fabricated body.
- **No scoring/extraction/report/Domain change** (producing `NewsArticle` evidence is sufficient — spec 70
  already extracts `MediaAttention`; AD-6 formula already counts it). Do NOT modify the `EvidenceSourceType`
  enum, `EvidenceSourceTypes`, or `CompanySourceFeed`.
- Never emit advice language.
- Default `Radar:Collectors` stays `["rss"]`; the news-search collector is opt-in via config.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `NewsAttentionCollector` (a distinct enable-able kind, RECOMMENDED `"newssearch"`; or an explicitly
      stated GDELT replacement) reads each feed's query phrase via the spec-80 `INewsSearchReader`,
      CLIENT-SIDE-FILTERS results by title relevance (company name OR ticker, whitespace-normalised, publisher
      suffix stripped), and maps surviving articles to `NewsArticle`-type evidence with the article `url` as
      `SourceUrl`, UTC `PublishedAt` from `pubDate`, feed-bound company hint, and `Medium` declared quality.
- [ ] Producing `NewsArticle` evidence lifts `AttentionScore` automatically (via spec 70's `MediaAttention`
      extraction + the AD-6 formula) — **no scoring/extractor/Domain change**; the enum, `EvidenceSourceTypes`,
      and `CompanySourceFeed` are untouched.
- [ ] The collector processes feeds **strictly sequentially** with a configurable inter-request pace, and
      degrades a `RateLimited`/failed/empty/malformed-token feed to a `SourceFailure`/no evidence **without
      aborting the run**; caller cancellation propagates.
- [ ] The collector is additively registered and enable-able via `Radar:Collectors`; default config is
      unchanged (`["rss"]`); the merged `CollectionSummary` reflects it when enabled; all three valid-kinds
      messages include the new kind.
- [ ] The seed carries a news-search feed for the watch universe; a company with zero recent coverage degrades
      gracefully (empty, not error).
- [ ] Offline tests (fake `INewsSearchReader`, no network) cover mapping/provenance/hints/quality, the title
      relevance drop, spaced/suffixed-title match, dedupe-by-url, `MaxRecords`, malformed token, `RateLimited`/
      empty degrade, sequential ordering, cancellation, and DI fail-fast/opt-in. No advice language.
- [ ] `dotnet build`/`dotnet test` on `Radar.sln -c Release` green. This **restores a usable AttentionScore**
      in a live run (`Radar:Collectors` including the news-search kind) that GDELT's per-IP throttle prevented.
