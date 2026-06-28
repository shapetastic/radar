# Task: RSS press-release collector — Radar's first real fetching collector

## Overview

This is the first *real-world* collector and the point of the collector-driven MVP (AD-8): Radar reads
the per-company `sourceFeeds` configured in slice 27, fetches each RSS feed, and turns new feed items
into `CollectedEvidence`. It does **not** score, resolve, or persist — it just answers "what new public
information did we find?" The mapper (slice 26) and runner already turn `CollectedEvidence` into stored
evidence.

The HTTP/RSS dependency lives in `Radar.Infrastructure` only (AD-5), behind a small abstraction so the
collector is **fully offline-testable** — tests feed fixture RSS XML, never the network.

---

## Assignment

Worktree: any
Dependencies: 26-collector-seam, 27-watch-universe-source-feeds
Conflicts with: 30 (resolution reads the hints this collector sets), 32 (run-pipeline wiring); also
edits `CollectionContext` (shared with 26). Sequence 26 → 27 → 28.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Collectors/
  CollectionContext.cs               # MODIFIED: add SourceFeeds (so collectors get feeds from Radar)

src/Radar.Infrastructure/
  Rss/
    IRssFeedReader.cs                # NEW (Infrastructure-internal abstraction)
    HttpRssFeedReader.cs             # NEW: HttpClient + SyndicationFeed
    RssFeedItem.cs                   # NEW: parsed item DTO
    RssPressReleaseCollector.cs      # NEW: IEvidenceCollector
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddRssPressReleaseCollector()

src/Radar.Application/Pipeline/RadarPipelineRunner.cs   # MODIFIED: populate context.SourceFeeds

tests/Radar.Infrastructure.Tests/Rss/
  RssPressReleaseCollectorTests.cs   # NEW (fake reader)
  HttpRssFeedReaderTests.cs          # NEW (fake HttpMessageHandler + fixture XML)
```

Add `System.ServiceModel.Syndication` (NuGet) to `Radar.Infrastructure` only.

---

## Implementation details

### `CollectionContext` extension

Add `IReadOnlyList<CompanySourceFeed> SourceFeeds` to `CollectionContext` (default `[]`). The runner
populates it from `ICompanyRepository.GetSourceFeedsAsync` so collectors receive their feeds from Radar
rather than each re-loading the seed:

```csharp
public sealed record CollectionContext(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanySourceFeed> SourceFeeds);
```

In `RadarPipelineRunner`, build the context with both companies and feeds before `CollectAsync`.

### Infrastructure-internal RSS reader (offline-testable)

```csharp
internal interface IRssFeedReader
{
    Task<IReadOnlyList<RssFeedItem>> ReadAsync(string feedUrl, CancellationToken ct);
}

internal sealed record RssFeedItem(
    string? Id, string Title, string? Summary, string? Link, DateTimeOffset? PublishedAt);
```

`HttpRssFeedReader : IRssFeedReader` — ctor deps: `HttpClient`, `ILogger<HttpRssFeedReader>`.
- GET the feed url; on non-success / `HttpRequestException` / `TaskCanceledException`, `LogWarning` and
  return `[]` (a flaky feed must not crash the run).
- Parse with `SyndicationFeed.Load(XmlReader.Create(stream))`; map each `SyndicationItem` to
  `RssFeedItem` (`Id`, `Title.Text`, `Summary.Text`, first `Links[].Uri`, `PublishDate`). Wrap parse in
  try/catch (`XmlException`) → `LogWarning` + `[]`.

### `RssPressReleaseCollector : IEvidenceCollector`

Ctor deps (`ThrowIfNull`): `IRssFeedReader`, `ILogger<RssPressReleaseCollector>`, `TimeProvider`.
- `CollectorName => "RssPressReleaseCollector"`, `SourceType => "press_release"`.
- `CollectAsync(context, ct)`:
  1. Filter `context.SourceFeeds` to RSS feeds (`FeedType` equals `"rss"`, case-insensitive).
  2. For each feed (deterministic order: by `CompanyId` then `Id`), `ReadAsync(feed.Url, ct)`.
  3. For each item with a non-blank `Title`, build a `CollectedEvidence`:
     - `SourceType = "press_release"`, `SourceName = feed.Name`, `SourceUrl = item.Link`,
       `Title = item.Title`, `RawText = item.Summary ?? item.Title` (raw; the mapper normalizes),
       `PublishedAt = item.PublishedAt`, `CollectedAt = _timeProvider.GetUtcNow()`.
     - `Metadata = { ["rssFeedUrl"]=feed.Url, ["rssItemId"]=item.Id ?? item.Link ?? "" }`.
     - `CompanyHints`: the bound company's ticker (and/or name) — look up `feed.CompanyId` in
       `context.Companies`; add the ticker when present, else the name. This is the high-confidence hint
       slice 30 consumes. Never invent a ticker.
  4. **Dedupe within the batch** by `SourceUrl` (when present) else by `Title` — skip repeats so one run
     never emits the same item twice. (Cross-run dedupe is by content hash in the repository/file store,
     slices 26/29 — not this collector's job.)
  5. `LogInformation` a one-line summary (feeds read, items collected). Return the list.

### DI

```csharp
public static IServiceCollection AddRssPressReleaseCollector(this IServiceCollection services)
{
    services.AddHttpClient<IRssFeedReader, HttpRssFeedReader>();   // typed HttpClient
    services.TryAddSingleton(TimeProvider.System);
    services.AddSingleton<IEvidenceCollector, RssPressReleaseCollector>();
    return services;
}
```

Keep `AddLocalFileCollector` for tests/debug. The host (slice 32) chooses which collector(s) to
register; do **not** change `RadarWorkerServices` here beyond what slice 32 needs — this slice only adds
the helper and leaves existing wiring intact.

---

## Tests

### `RssPressReleaseCollectorTests` (fake `IRssFeedReader`)
- Two configured feeds → fake returns fixed `RssFeedItem`s → asserts one `CollectedEvidence` per item
  with correct `SourceType="press_release"`, `SourceName`, `SourceUrl`, `Metadata` keys, and
  `CompanyHints` containing the bound company's ticker.
- Duplicate items (same link) within a feed are deduped.
- An item with a blank title is skipped.
- No RSS feeds in context → empty result (no reader calls).
- `CollectedAt` comes from the injected `TimeProvider`.

### `HttpRssFeedReaderTests` (fake `HttpMessageHandler` + fixture XML string)
- Valid RSS fixture → parsed items (title/link/pubDate/summary).
- Non-success status / malformed XML → `[]`, no throw (warning logged).

No test hits the network.

---

## Constraints

- Target .NET 10. `System.ServiceModel.Syndication`, `HttpClient`, and all XML/HTTP code live in
  `Radar.Infrastructure` only (AD-5). The collector implements the Application `IEvidenceCollector`
  seam from slice 26.
- A failing/empty feed degrades gracefully (warn + skip); never throw out of `CollectAsync` for a bad
  feed. Honour `ct`.
- Preserve provenance: feed url + item id recorded in `Metadata`; company hint set only from configured
  feed bindings — **never hallucinate tickers**.
- No scoring, resolution, or persistence in the collector. No advice language anywhere.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `RssPressReleaseCollector` implements the seam (`SourceType="press_release"`), reads
      `context.SourceFeeds`, and emits one `CollectedEvidence` per new item with feed metadata + a
      company hint from the feed binding.
- [ ] RSS fetch/parse sits behind `IRssFeedReader` in Infrastructure; the collector and reader are
      tested entirely offline (fake reader; fake HttpMessageHandler + fixture XML).
- [ ] Within-batch dedupe by url/title; flaky/malformed feeds degrade to empty without throwing.
- [ ] `CollectionContext.SourceFeeds` is populated by the runner from
      `ICompanyRepository.GetSourceFeedsAsync`.
- [ ] `AddRssPressReleaseCollector()` registers the collector + typed `HttpClient`; `build`/`test` green.
