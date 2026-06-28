# Task: Watch-universe source feeds + themes — persist per-company RSS feeds

## Overview

The collector-driven MVP (AD-8) needs each watched company to declare **where** Radar should fetch
evidence. The master spec's watch-universe entry carries `sourceFeeds` (type/name/url) and `themes`:

```json
{
  "ticker": "RKLB",
  "name": "Rocket Lab USA",
  "aliases": ["Rocket Lab"],
  "sourceFeeds": [
    { "type": "rss", "name": "Rocket Lab Investor News", "url": "https://example.com/rss" }
  ],
  "themes": ["space", "defence"]
}
```

Today the seed (`LocalFileCompanySeedSource` → `CompanySeedData`) only carries companies + aliases, so
the RSS collector (slice 28) has no feeds to read. This slice persists per-company source feeds like
aliases (seed → repository → available at run time) and parses `themes` onto the company. It adds **no
collector and no fetching** — just the configuration surface, modelled and stored conservatively.

> New JSON fields are **optional**: existing seed files (no `sourceFeeds`/`themes`) keep working and
> simply yield no feeds and empty themes.

---

## Assignment

Worktree: any
Dependencies: 26-collector-seam (sequence after), 23-company-watch-universe-seed (existing)
Conflicts with: 28 (RSS reads feeds), 30 (resolution reads hints). Sequence 27 → 28 → 30.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Domain/Companies/
  Company.cs                         # MODIFIED: add IReadOnlyList<string> Themes
  CompanySourceFeed.cs               # NEW

src/Radar.Application/
  EntityResolution/CompanySeedData.cs              # MODIFIED: add SourceFeeds
  Abstractions/Persistence/ICompanyRepository.cs   # MODIFIED: add feed add/get
  EntityResolution/CompanyUniverseSeeder.cs        # MODIFIED: store feeds

src/Radar.Infrastructure/
  Persistence/InMemory/InMemoryCompanyRepository.cs # MODIFIED: feed storage
  Sources/LocalFileCompanySeedDocument.cs           # MODIFIED: sourceFeeds + themes DTOs
  Sources/LocalFileCompanySeedSource.cs             # MODIFIED: parse feeds + themes

tests/Radar.TestSupport/CompanyBuilder.cs           # MODIFIED: Themes default []
tests/Radar.Domain.Tests/DomainRecordsTests.cs      # MODIFIED: Company ctor + new record
tests/Radar.Infrastructure.Tests/Sources/LocalFileCompanySeedSourceTests.cs   # MODIFIED
tests/Radar.Infrastructure.Tests/Persistence/InMemoryCompanyRepositoryTests.cs (or existing) # MODIFIED/NEW
tests/Radar.Application.Tests/EntityResolution/CompanyUniverseSeederTests.cs   # MODIFIED
```

---

## Implementation details

### Domain: `CompanySourceFeed`

```csharp
namespace Radar.Domain.Companies;

/// <summary>
/// A configured collection source bound to one watched company (e.g. an RSS investor-news feed).
/// Collectors read these to know what to fetch; the bound CompanyId is the high-confidence company
/// hint for evidence from this feed (slice 30).
/// </summary>
public sealed record CompanySourceFeed(
    Guid Id,
    Guid CompanyId,
    string FeedType,     // e.g. "rss"
    string Name,
    string Url,
    DateTimeOffset CreatedAtUtc);
```

### Domain: `Company.Themes`

Add a trailing positional member `IReadOnlyList<string> Themes` to the `Company` record. Update the
three construction sites: `LocalFileCompanySeedSource`, `CompanyBuilder` (default `[]`, no new `With`
needed), and `DomainRecordsTests`. Themes are not yet consumed by scoring/extraction — they are stored
for forthcoming theme-aware features; keep them as a plain string list.

### `ICompanyRepository` (feed methods, AD-1 upsert-by-Id)

```csharp
/// <remarks>Upsert by Id (last-write-wins), same semantics as companies/aliases.</remarks>
Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct);
Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct);
```

`InMemoryCompanyRepository`: add a `ConcurrentDictionary<Guid, CompanySourceFeed>`; `AddSourceFeedAsync`
upserts by `Id`; `GetSourceFeedsAsync` returns `OrderBy(CreatedAtUtc).ThenBy(Id)` (AD-3). Do not observe
`ct` on the in-memory path (AD-2).

### `CompanySeedData` + seeder

- Extend to `CompanySeedData(IReadOnlyList<Company> Companies, IReadOnlyList<CompanyAlias> Aliases,
  IReadOnlyList<CompanySourceFeed> SourceFeeds)`. Update construction sites (seed source, seeder tests,
  the empty-payload returns in `LocalFileCompanySeedSource` → `new CompanySeedData([], [], [])`).
- `CompanyUniverseSeeder.SeedAsync`: after aliases, `foreach (feed in seed.SourceFeeds)
  await _companyRepository.AddSourceFeedAsync(feed, ct)`; include the feed count in the log line.
  Idempotency still relies on stable Ids (AD-1).

### `LocalFileCompanySeedSource` + DTO

- DTO: add to `LocalFileCompanySeedEntry` an optional `IReadOnlyList<LocalFileSourceFeed?>? SourceFeeds`
  and `IReadOnlyList<string?>? Themes`; add internal `LocalFileSourceFeed(string? Type, string? Name,
  string? Url)`. All nullable so malformed entries are skipped, never thrown.
- Parsing: for each company entry, build `CompanySourceFeed`s from `SourceFeeds`, skipping any feed
  with a blank `url` (with a `LogWarning`) — **never fabricate a url**. `FeedType` defaults to `"rss"`
  when blank. `CreatedAtUtc = _timeProvider.GetUtcNow()`. Derive a **stable** `Id` via the existing
  `DeterministicGuid(companyId, "feed", url)` helper pattern so re-seeding upserts the same feed row.
- Themes: map non-blank `Themes` strings (trimmed) onto `Company.Themes`; default `[]` when absent.
- Preserve file order (AD-3).

---

## Tests

### `LocalFileCompanySeedSourceTests` (MODIFIED)
- A company with `sourceFeeds` yields `CompanySourceFeed`s (correct `CompanyId`/`Url`/`Name`,
  `FeedType="rss"`); a feed missing `url` is skipped; an entry with no `sourceFeeds` yields none.
- `themes` parsed onto `Company.Themes`; absent → `[]`.
- **Deterministic feed Ids**: reading the same file twice yields equal feed `Id`s for the same
  `(companyId, url)`.
- Existing missing-file / malformed-entry cases still return an empty `CompanySeedData([], [], [])`.

### `InMemoryCompanyRepository` feed tests (MODIFIED/NEW)
- `AddSourceFeedAsync` then `GetSourceFeedsAsync` returns the feed; re-adding the same `Id` upserts (no
  duplicate); order is `CreatedAtUtc` then `Id`.

### `CompanyUniverseSeederTests` (MODIFIED)
- After `SeedAsync`, `GetSourceFeedsAsync` returns the seeded feeds; running twice leaves the feed count
  unchanged (idempotent).

---

## Constraints

- Target .NET 10. `CompanySourceFeed` and `Company.Themes` are pure Domain records (no packages).
  All file/JSON parsing stays in `LocalFileCompanySeedSource` (Infrastructure).
- New JSON fields are optional and backward-compatible; never fabricate feed urls or company data.
- AD-1 (feeds upsert-by-Id; stable Ids for idempotency), AD-2 (don't observe `ct` in-memory), AD-3
  (deterministic ordered queries + preserved file order). UTC timestamps via injected `TimeProvider`.
- Do **not** add a collector or any HTTP/RSS code here, and do not change `CompanyResolver`.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` are green.

---

## Acceptance criteria

- [ ] `CompanySourceFeed` exists; `Company` carries `Themes`.
- [ ] `ICompanyRepository` has `AddSourceFeedAsync`/`GetSourceFeedsAsync` (upsert-by-Id, ordered) with
      an in-memory implementation.
- [ ] `CompanySeedData` carries `SourceFeeds`; `LocalFileCompanySeedSource` parses optional
      `sourceFeeds`/`themes` with stable feed Ids and skips feeds lacking a url.
- [ ] `CompanyUniverseSeeder` stores feeds idempotently.
- [ ] Tests cover feed parsing, deterministic feed Ids, themes, repository upsert/order, idempotent
      seeding; existing seed files without the new fields still load.
- [ ] `build` + `test` green.
