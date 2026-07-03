# Task: Third-party attention source — DESIGN + reader seam (choose a non-per-IP-throttled news source; build the offline-testable reader)

## Overview

Radar's `AttentionScore` is **effectively unavailable in live runs today**, and this slice takes the first,
de-risking step toward restoring it. Under `radar-formula-v2` (AD-6), `AttentionScore` counts distinct
third-party **market-attention** source names only among `EvidenceSourceTypes.IsThirdPartyAttentionSource`
types (`NewsArticle`, `SocialMedia`, `ConferenceMention`). Every collector Radar ships that actually reaches
its API — RSS press releases (spec 55), SEC EDGAR filings (spec 56), USASpending awards (spec 62) — is
**first-party** (a company's own disclosures/awards), which correctly add **nothing** to Attention. The one
third-party collector, **GDELT** (spec 67, `NewsArticle`), is fully built and wired but **hard-throttles this
IP**: verified against live AI runs, only ~1 of ~7 news feeds get through and the per-IP quota does not clear
within ~1h. So in practice `reach → 0`, `AttentionScore → 0` for every company, and the Opportunity term's
under-the-radar discount (`1 − Attention/200`, AD-6) never engages.

The fix is a **different third-party news/attention source that is not per-IP throttled** the way GDELT is,
emitting third-party `NewsArticle` (or `SocialMedia`/`ConferenceMention`) evidence that lifts Attention
automatically — no scoring or Domain-enum change required (the enum values and their third-party
classification already exist; spec 70 already produces `MediaAttention` signals from `NewsArticle` evidence).

**Why this is split into two slices (state it in the PR).** A live third-party attention collector is
genuinely network-heavy and carries real unknowns: which source is reachable and keyless/low-friction from
this environment, its response shape, its rate-limit posture, whether it exposes an exact-entity key or needs
a client-side relevance post-filter (the GDELT/USASpending hazard), and how each item maps to
`CollectedEvidence` with full provenance. Cramming source selection **and** a live wired collector into one
slice risks an oversized, hard-to-review change. **THIS slice (80) is the design + offline-testable reader
seam only**; the live collector, its DI wiring, watch-universe seed, and the enable-able `Radar:Collectors`
kind are the **follow-up (spec 81)**. This slice ships **no new collector, no wiring, no seed, and does not
change any live run** — it lands a typed, offline-tested reader plus a written source decision the coder can
implement against in 81.

**Source selection is part of THIS slice's work.** The recommended default is **Google News RSS**
(`https://news.google.com/rss/search?q=<query>&hl=en-US&gl=US&ceid=US:en`) — keyless, returns standard RSS
2.0 XML (so it can reuse the merged RSS parsing helpers), and is not GDELT's per-IP DOC-API quota. It has the
**same core hazard as GDELT**: a full-text query with **no exact-entity key**, so it needs the identical
client-side **title-relevance post-filter** (company name or ticker) to protect provenance. The implementer
must **verify reachability from the target environment during design** and record the chosen endpoint,
response shape, rate-limit posture, and relevance strategy in the spec's "Verified source facts" section
(mirroring how spec 67 recorded verified GDELT facts) so spec 81 does not re-research. If Google News RSS is
not reachable/appropriate, pick another keyless, non-per-IP-throttled third-party source (e.g. a Bing News
RSS variant) and record why — but **do not** re-adopt GDELT and **do not** introduce an API key or a paid
service (secrets-in-source and cost are out of bounds).

Scope of THIS slice: **the reader interface + typed read result/outcome + parsed item type + the feed-target
token parser + the offline-tested HTTP reader implementation + the written source decision**. It produces no
evidence, touches no runner/scoring/report/Domain code, and enables no collector.

---

## Assignment

Worktree: any (self-contained new Infrastructure files + their tests; no shared-file edits)
Dependencies: 55 (feed-type seam + `EvidenceSourceType` values), 62 (USASpending fuzzy-query / client-side
relevance-filter pattern — the closest precedent), 67 (GDELT reader — the typed-outcome + rate-limit-aware
reader precedent this mirrors), 70 (news → `MediaAttention` — confirms the payoff once evidence flows) — all
merged. Read the GDELT reader (`src/Radar.Infrastructure/Gdelt/HttpGdeltNewsReader.cs`) and the USASpending
reader (`src/Radar.Infrastructure/UsaSpending/HttpUsaSpendingAwardReader.cs`) for the exact reader shape to
copy.
Conflicts with: **None** — all-new files under a new `src/Radar.Infrastructure/News/` folder + a new test
folder. Does NOT touch DI, the worker, the seed, the runner, scoring, extraction, the report, or Domain.
Estimated time: ~1.5–2 h

---

## Verified source facts (the implementer MUST fill this in during design — do NOT skip)

Before coding, verify the chosen source from the target environment and record the authoritative facts here
in the PR/spec (mirroring spec 67's "Verified GDELT facts"), so spec 81 does not re-research:

- **Endpoint + method** (keyless GET expected), e.g. Google News RSS:
  `GET https://news.google.com/rss/search?q=%22Rocket+Lab%22&hl=en-US&gl=US&ceid=US:en`.
- **Response shape** — confirm it is RSS 2.0 XML with `<item>` `<title>`/`<link>`/`<pubDate>`/`<source>`
  (Google News wraps the real publisher in `<source url="…">Publisher</source>` and appends `" - Publisher"`
  to the title). Record the exact element names/namespaces so the parser is grounded.
- **Rate-limit posture** — the whole reason for this slice: confirm the source is NOT GDELT's per-IP quota;
  note any observed throttling and whether a modest inter-request pace is prudent (spec 81 will handle
  pacing/sequencing at the collector level — do NOT build a collector here).
- **Relevance / entity key** — confirm there is **no exact-entity key** (there is not, for news search), so
  spec 81's collector MUST client-side title-filter (company name OR ticker). Record whether the publisher
  name appears in the title (it does for Google News) so the relevance filter can strip the `" - Publisher"`
  suffix before matching if needed.
- **`pubDate` format** — record the exact format (RFC 1123 for RSS 2.0) so the reader parses it to a UTC
  instant; unparseable/absent → `null` (the collector will fall back to `CollectedAt`).

If, during verification, the recommended source is unreachable/unsuitable, choose an alternative keyless,
non-per-IP-throttled third-party source and record the same facts for it. Do NOT re-adopt GDELT; do NOT add a
key/paid source.

---

## Project structure changes

```text
src/Radar.Infrastructure/News/
  INewsSearchReader.cs        # NEW: ReadAsync(NewsSearchQuery, ct) -> NewsSearchReadResult (offline-testable, internal)
  NewsSearchReadResult.cs     # NEW: outcome enum (Success/Unreachable/HttpError/Timeout/Malformed/RateLimited)
                              #      + items + IsSuccess + advice-free Detail; Success/Failure factories
  NewsArticleItem.cs          # NEW: parsed article (Url, Title, Publisher/SourceName, PublishedAt?, ...)
  NewsSearchQuery.cs          # NEW: typed request (QueryPhrase, MaxRecords, EnglishOnly, ...)
  NewsFeedTarget.cs           # NEW: parses a feed Url token into (QueryPhrase, Ticker?); null on malformed
  HttpNewsSearchReader.cs     # NEW (internal): HttpClient GET -> parse items[] -> typed outcome (429 -> RateLimited)
  NewsCollectorOptions.cs     # NEW (internal): MaxRecordsPerCompany, EnglishOnly, (endpoint template), etc.
                              #      (collector-level pacing/timespan land in spec 81; keep only reader-relevant knobs here)

tests/Radar.Infrastructure.Tests/News/
  HttpNewsSearchReaderTests.cs  # NEW: offline (fake HttpMessageHandler + fixture XML): parse, empty/absent items -> Success 0,
                                #      429 -> RateLimited, HttpError, malformed, timeout, cancellation
  NewsFeedTargetTests.cs        # NEW: parse valid token (with & without ticker); reject malformed -> null
```

No production code outside `src/Radar.Infrastructure/News/` changes. No DI edit, no worker edit, no seed, no
Domain change, no scoring/extraction/report change. All new types are `internal` (rely on the existing
`InternalsVisibleTo` for the Infrastructure test project, exactly like the GDELT/USASpending readers).

---

## Implementation details

### `NewsSearchReadResult` + outcome (mirror `GdeltReadResult` / `UsaSpendingReadResult`)

- Outcome enum: `Success`, `Unreachable`, `HttpError`, `Timeout`, `Malformed`, `RateLimited` (keep the same
  set as GDELT so spec 81's collector degradation logic is a straight port). `Success` may carry **zero
  items** (a company with no recent coverage is `Success`-with-nothing, NOT an error — mirror GDELT).
- `Success(items)` / `Failure(outcome, detail)` factories, `IsSuccess`, and an **advice-free** `Detail`
  string for logging (no banned tokens; it is diagnostic text only).

### `NewsArticleItem`

- Real fields only, parsed from the source: `Url` (stable landing page; skip rows missing it —
  unattributable/undedupeable), `Title` (the headline as-is), `SourceName`/`Publisher` (the third-party
  outlet — this is what becomes the distinct third-party source *name* that lifts Attention), `PublishedAt`
  (parsed UTC `DateTimeOffset?`, `null` when absent/unparseable), and any cheap extras the source gives
  (language, etc.). **Do NOT fabricate a body** — news search returns headlines/snippets, not full text.

### `NewsFeedTarget.Parse` (mirror `GdeltFeedTarget`)

- Parse the feed's single `Url` token into `(QueryPhrase, Ticker?)`, using the same documented token shape as
  GDELT: `query=<company phrase>&ticker=<TICKER>` (ticker optional). An unparsable/empty token returns
  `null` (the collector in spec 81 will degrade a `null` to a `SourceFailure`, not a throw). **Do NOT add
  fields to `CompanySourceFeed`** — its `Url` is documented as carrying the collector's per-company input.

### `HttpNewsSearchReader` (internal; mirror `HttpGdeltNewsReader` exactly)

- Take an injected `HttpClient` + `ILogger<HttpNewsSearchReader>`; guard-clause null-check both.
- `ReadAsync(NewsSearchQuery query, CancellationToken ct)` builds the verified endpoint URL (URL-encode the
  query phrase; add English/locale params per the verified facts), GETs it, and parses the response into
  `NewsArticleItem`s. For an RSS-2.0 source, **reuse the merged RSS parsing helpers** rather than
  hand-rolling XML parsing (check `src/Radar.Infrastructure/Rss/`); if the shared parser is not cleanly
  reusable for search RSS, parse with `System.Xml.Linq` defensively and note why. Strip the Google-News
  `" - Publisher"` title suffix into `SourceName` if the verified facts show that shape.
- **Typed outcomes, NEVER throw on a bad response** (mirror GDELT/USASpending precisely):
  - non-success status → `HttpError` (with the status in `Detail`);
  - **HTTP 429 → `RateLimited`** specifically (distinct outcome; the reader may do one optional bounded
    delayed retry then still return `RateLimited` — keep any retry in Infrastructure and simple);
  - `HttpRequestException` → `Unreachable`;
  - `TaskCanceledException` with `ct` NOT requested → `Timeout`;
  - malformed/unexpected XML (or a valid document with no usable item container) → `Malformed`; a valid
    document with **zero** items → `Success` with zero items;
  - `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` — caller cancellation
    is the ONLY thing that propagates.
- Parse `pubDate` with the verified format (`DateTimeOffset.TryParse`/`TryParseExact`, invariant culture,
  assume/adjust UTC); unparseable/absent → `null`. Skip items missing `Url`.
- All HTTP/XML/source specifics stay in `Radar.Infrastructure` (AD-5). No provider SDK, no AI, no DB.

### `NewsCollectorOptions`

- Only the reader-relevant knobs this slice needs: `MaxRecordsPerCompany` (clamp to a sane range),
  `EnglishOnly`, and (if useful) an endpoint/template string. Collector-level pacing/sequencing/timespan and
  the `Radar:News` worker options are **spec 81** — do not add them here.

---

## Tests

### `HttpNewsSearchReaderTests` (offline; fake `HttpMessageHandler` + fixture XML; no network)

1. A fixture feed with two `<item>`s parses into two `NewsArticleItem`s with the correct `Url`, `Title`,
   `SourceName`/publisher, and `PublishedAt` parsed to the exact UTC instant.
2. A feed with **no items** (or an absent item container in an otherwise-valid document) → `Success` with
   **zero items** (a quiet company, not an error).
3. An **HTTP 429** response → `RateLimited` outcome, zero items, no throw (and, if the reader owns a retry, it
   retries the configured count then still returns `RateLimited`).
4. Any other non-success status → `HttpError`; malformed/empty body or unexpected root → `Malformed`.
5. A thrown `TaskCanceledException` (timeout) → `Timeout`; an already-cancelled caller token re-throws
   `OperationCanceledException`.
6. An item missing `Url` is skipped (not emitted, no throw).

### `NewsFeedTargetTests`

7. `query=Rocket Lab&ticker=RKLB` parses to the exact pair (phrase preserving spaces, ticker `RKLB`); a
   `query=…`-only token parses with a null/empty ticker; a malformed/empty token returns `null`.

No advice language in any parsed field or `Detail`. No existing test is touched (all-new files).

---

## Spec-implementation checklist

1. **Code paths replaced:** none — this is purely additive (new `News/` folder). GDELT stays exactly as-is
   (this slice does NOT remove or modify the GDELT collector/reader; the maintainer may keep it as a
   secondary source or retire it in a later, separate decision).
2. **Tests:** add the reader cases (1–6) and feed-target cases (7); all offline.
3. **Delete nothing.**
4. **CLAUDE.md / architecture-decisions.md:** no architecture rule change; the reader mirrors the established
   typed-outcome, rate-limit-aware, Infrastructure-only reader pattern (AD-5). No new AD entry — note in the
   PR. Record the **verified source facts** in the PR so spec 81 can implement without re-researching.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic reader; all HTTP/XML/source specifics confined to
  `Radar.Infrastructure` (AD-5). No provider SDK, no AI, no DB (AD-8, files-first).
- **No key, no paid service, no secrets in source.** Keyless, non-per-IP-throttled source only.
- Typed outcomes; **NEVER throw** on a bad response (degrade to outcome + zero items); only caller
  cancellation propagates. HTTP 429 is a distinct `RateLimited` outcome.
- **No scoring/extraction/report/Domain change and no collector/DI/seed change** — this is the reader seam +
  design only; the live collector is spec 81. No live run is affected by this slice.
- Provenance-preserving by construction: real `Url`/`Title`/`SourceName`/`PublishedAt` only; no fabricated
  bodies; the relevance post-filter that guards attribution is implemented in spec 81's collector.
- Never emit advice language.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] A written **source decision** is recorded (chosen keyless, non-per-IP-throttled third-party news source;
      endpoint; response shape; rate-limit posture; relevance strategy; `pubDate` format) — verified from the
      target environment, mirroring spec 67's verified-facts section — so spec 81 implements without
      re-researching. GDELT is NOT re-adopted; no API key/paid source is introduced.
- [ ] `INewsSearchReader.ReadAsync(NewsSearchQuery, ct)` returns a typed `NewsSearchReadResult`
      (`Success`/`Unreachable`/`HttpError`/`Timeout`/`Malformed`/`RateLimited`, `IsSuccess`, advice-free
      `Detail`) and **NEVER throws** on a bad response; **HTTP 429 → `RateLimited`**; absent/zero items →
      `Success` with zero items; caller cancellation propagates.
- [ ] `HttpNewsSearchReader` (Infrastructure, internal) parses the verified source's items into
      `NewsArticleItem`s with real `Url`/`Title`/`SourceName`/`PublishedAt` (UTC), skips items missing `Url`,
      and confines all HTTP/XML/source specifics to Infrastructure (AD-5).
- [ ] `NewsFeedTarget.Parse` reads a `query=<phrase>&ticker=<TICKER>` token (ticker optional) into
      `(QueryPhrase, Ticker?)` and returns `null` on a malformed/empty token; `CompanySourceFeed` is
      unchanged.
- [ ] Offline tests (fake `HttpMessageHandler` + fixture XML, no network) cover parse, empty/absent items →
      `Success` 0, `RateLimited`(429), `HttpError`, `Malformed`, `Timeout`, cancellation, missing-`Url` skip,
      and feed-token parse (with/without ticker, malformed → null). No advice language.
- [ ] Purely additive: no collector/DI/worker/seed/runner/scoring/extraction/report/Domain change; no live run
      affected. `dotnet build`/`dotnet test` on `Radar.sln -c Release` green. Spec 81 will wire the collector
      that turns this reader into `NewsArticle` evidence that lifts `AttentionScore`.
