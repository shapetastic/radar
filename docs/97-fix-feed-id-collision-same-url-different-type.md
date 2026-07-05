# Task: Fix deterministic feed-Id collision for same-URL, different-type source feeds

## Overview

In the 2026-07-05 baseline live run the `sec` (SEC EDGAR 8-K) collector reported **0 feed(s)
checked** while `secform4` reported 7. The seed defines 7 `sec` feeds and 7 `secform4` feeds, so
this is not a config gap — the `sec` feeds were **silently lost between seed-load and collection**.
No 8-K earnings were collected and no AI directional `GuidanceChange` signals were produced, which
quietly depressed the top names' Trajectory.

**Confirmed root cause — a deterministic-Id collision.**

- `LocalFileCompanySeedSource.GetSeedAsync` (~line 159) derives each feed's Id from
  `DeterministicGuid(companyId, "feed", feed.Url)` — company + URL only. **The feed *type* is not
  part of the Id.**
- The `sec` and `secform4` feeds for each company share the **identical** URL
  (`https://data.sec.gov/submissions/CIK{cik}.json` — the submissions JSON serves both regular
  filings and Form 4s), so both feeds hash to the **same** feed Id.
- `InMemoryCompanyRepository.AddSourceFeedAsync` (line 51) does `_sourceFeeds[feed.Id] = feed;` — a
  dictionary keyed by Id, **last-write-wins**. `secform4` is listed *after* `sec` in
  `data/companies.json`, so it **overwrites** the `sec` feed for every company. Result:
  `CollectionContext.FeedsOfType("sec")` returns 0; `FeedsOfType("secform4")` returns 7.

**A second, independent instance of the same bug exists** (found while grepping for same-URL pairs):
every company's `news` (GDELT, `CollectorName` `news`) and `newssearch` (Google News,
`CollectorName` `newssearch`) feeds also share an identical per-company URL
(`query=<name>&ticker=<TICKER>`). `newssearch` is listed *after* `news`, so `newssearch` wins and
the GDELT `news` collector *also* silently got 0 feeds in the same run. This fix resolves **both**
collision pairs at once.

Fixing the Id derivation to fold the feed type in makes two feeds that share a URL but differ by
type produce **distinct** Ids, so neither overwrites the other in the repository.

---

## Assignment

Worktree: any
Dependencies: None
Conflicts with: None (spec 98 builds on this and must be sequenced **after** it)
Estimated time: ~1 hour

---

## Project structure changes

Modify:

- `src/Radar.Infrastructure/Sources/LocalFileCompanySeedSource.cs` — fold `FeedType` into the
  feed-Id seed; update the `DeterministicGuid` `<summary>` to reflect that a feed is now keyed on
  `type|url`, not `url` alone.

Add/modify tests:

- `tests/Radar.Infrastructure.Tests/Sources/LocalFileCompanySeedSourceTests.cs` — regression test
  for two same-URL-different-type feeds yielding two distinct feeds; adjust any existing assertion
  that pins a specific feed Id (the Id value churns — see Constraints).

No other production files change. Do **not** touch `data/companies.json` — the seed is correct; the
Id derivation was wrong.

---

## Implementation details

In `LocalFileCompanySeedSource.GetSeedAsync`, the feed construction (~line 158-164) currently is:

```csharp
feeds.Add(new CompanySourceFeed(
    Id: DeterministicGuid(companyId, "feed", feed.Url),
    CompanyId: companyId,
    FeedType: string.IsNullOrWhiteSpace(feed.Type) ? "rss" : feed.Type.Trim(),
    Name: feed.Name?.Trim() ?? string.Empty,
    Url: feed.Url.Trim(),
    CreatedAtUtc: now));
```

Compute the normalized feed type **once** (it is already used for the `FeedType` field), then fold
it into the Id seed so two feeds that share a URL but differ by type no longer collide:

```csharp
var feedType = string.IsNullOrWhiteSpace(feed.Type) ? "rss" : feed.Type.Trim();

feeds.Add(new CompanySourceFeed(
    Id: DeterministicGuid(companyId, "feed", $"{feedType}|{feed.Url}"),
    CompanyId: companyId,
    FeedType: feedType,
    Name: feed.Name?.Trim() ?? string.Empty,
    Url: feed.Url.Trim(),
    CreatedAtUtc: now));
```

- The `DeterministicGuid` helper already lower-invariants + trims its `value`, so `feedType` is
  folded case-insensitively and consistently with the existing URL normalization — keep using the
  helper, do not add a second normalization path.
- Update the `DeterministicGuid` `<summary>` (a source feed is now "keyed on its `type|url`", not
  "keyed on its url") so the doc comment matches the code (the reviewer checks this).
- Keep the derivation deterministic and stable within a run: the same `(companyId, type, url)`
  tuple always yields the same Id, so re-seeding still upserts the same row (idempotency preserved).

Do **not** change `InMemoryCompanyRepository` — last-write-wins on distinct Ids is correct (AD-1);
the defect was that two *different* feeds shared one Id, which this fix removes at the source.

---

## Tests

In `LocalFileCompanySeedSourceTests`:

1. **Regression — same URL, different type ⇒ two distinct feeds.** Seed a single company with two
   `sourceFeeds` sharing one `url` but with `type` `sec` and `secform4` (mirroring the real
   submissions-URL pair). Assert:
   - `seed.SourceFeeds` contains **two** feeds with **distinct** `Id`s.
   - Building a `CollectionContext(seed.Companies, seed.SourceFeeds)`, `FeedsOfType("sec")` returns
     exactly one feed and `FeedsOfType("secform4")` returns exactly one feed (they no longer
     collapse).

2. **Regression — the `news`/`newssearch` pair.** Same as above with `type` `news` and `newssearch`
   sharing a `query=...&ticker=...` URL; assert both survive as distinct feeds. (This pins the
   second real instance so it cannot regress.)

3. **Stability/idempotency unchanged.** Two `GetSeedAsync` calls over the same document yield feeds
   with the **same** Ids (the derivation is still deterministic).

If any existing test asserts a hard-coded feed-Id `Guid` value, recompute/adjust it — the Id value
for every feed changes (see Constraints). Prefer asserting **distinctness and count** over pinning
literal Guids where practical.

---

## Constraints

- Target .NET 10.
- **Provenance / Id-churn is safe and acceptable.** Feed Ids now change for every feed (the seed
  string gained a `type|` prefix). This is fine because a feed Id is a **per-run, in-memory-only**
  identity: `CompanySourceFeed` is seeded into `InMemoryCompanyRepository` fresh each run and is
  never persisted to disk. Nothing on disk references a feed Id — collected `EvidenceItem`s carry
  the feed's `SourceUrl`/`SourceName` (not its Id), signals reference evidence Ids, and score links
  reference signals/evidence. Verify this holds (grep confirms `feed.Id` is used only inside
  `Radar.Infrastructure` collectors and the in-memory repository, never serialized by any file
  store) and state it in the PR description.
- Keep the change scoped to the Id derivation + its doc comment + tests. Do not rework seed loading,
  the repository, or the collectors.
- Do not implement unrelated features. The validation/health step is spec 98.

---

## Acceptance criteria

- [ ] `LocalFileCompanySeedSource` derives feed Ids from `type|url`, not `url` alone; the
      `DeterministicGuid` doc comment matches.
- [ ] Two feeds sharing a URL but differing by type produce two **distinct** feed Ids, and
      `FeedsOfType("sec")` / `FeedsOfType("secform4")` (and `news` / `newssearch`) each return their
      feed instead of colliding.
- [ ] A regression test covers both the `sec`/`secform4` and `news`/`newssearch` same-URL pairs.
- [ ] Existing seed-source tests still pass (any pinned feed-Id literals updated).
- [ ] PR description notes the feed-Id churn is safe (feed Ids are in-memory-per-run, never
      persisted; nothing on disk references a feed Id).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
