# Task: Collector feed-type seam helper and pre-added evidence source types

## Overview

With the multi-collector ceiling removed (slice 54), the next collectors (SEC filings, government
contracts, patents, RNS, …) will be built as **independent worktrees**. This slice does the cheap,
conflict-reducing prep that lets those worktrees stay small and merge cleanly, without building any
actual new collector.

Two concrete prep items, validated against the code:

1. **Centralise the feed-type seam.** Each company's collection inputs already live on
   `CollectionContext.SourceFeeds` as `CompanySourceFeed(FeedType, Name, Url, …)`, and the seed reader
   (`LocalFileCompanySeedSource`) already accepts **arbitrary** `feed.Type` strings from
   `data/companies.json` — so the per-collector configuration seam *already exists*: a collector filters
   the feeds whose `FeedType` is its own kind and reads each feed's `Url` (a URL, or an API endpoint /
   identifier such as an SEC CIK URL). Today `RssPressReleaseCollector` re-rolls that filter inline
   (`Where(FeedType == "rss")` + `OrderBy(CompanyId).ThenBy(Id)`). Promote it to a single
   `CollectionContext.FeedsOfType(...)` convenience so every future collector filters and orders feeds
   the same deterministic way instead of each re-inventing it (and risking inconsistent ordering).

2. **Pre-add the missing `EvidenceSourceType` values in one place.** `EvidenceSourceType.cs` is touched
   by every collector and is therefore the sharpest future merge-conflict point when collectors fan out
   in parallel. The collector roadmap names sources whose types are not yet present:
   `RegulatoryAnnouncement` (UK RNS), `InsiderTransaction` (insider transactions), and
   `ConferenceMention` (conference agenda). Adding them now — in one commit, before the fan-out — means
   each later collector worktree references an existing value instead of editing the enum and colliding.

No new collector, no model reshaping, no DB, no AI. The seam is documented so the convention is explicit
for the collector worktrees that follow.

---

## Assignment

Worktree: pending
Dependencies: None. Independent of slice 54 (disjoint files), but recommend sequencing **after 54** so
the keystone lands first.
Conflicts with: None among the current `docs/next/` specs. Touches `EvidenceSourceType.cs`,
`CollectionContext.cs`, `CompanySourceFeed.cs`, and `RssPressReleaseCollector.cs` (+ their tests) —
none of which slice 54 edits.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Domain/Evidence/EvidenceSourceType.cs          # MODIFIED: append 3 source-type values
src/Radar.Domain/Companies/CompanySourceFeed.cs          # MODIFIED: doc the FeedType seam convention
src/Radar.Application/Collectors/CollectionContext.cs    # MODIFIED: add FeedsOfType(...) helper
src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs # MODIFIED: use context.FeedsOfType("rss")

tests/Radar.Application.Tests/Collectors/CollectionContextTests.cs   # NEW: FeedsOfType behaviour
tests/Radar.Domain.Tests/Evidence/EvidenceSourceTypeTests.cs         # NEW or MODIFIED: enum membership
tests/Radar.Infrastructure.Tests/Rss/RssPressReleaseCollectorTests.cs # MODIFIED if it depends on the inline filter
```

---

## Implementation details

### `EvidenceSourceType` (append only — do not reorder existing members)

Append three values after the existing ones, keeping the current order intact (the enum is persisted by
name, but treat additions as append-only to avoid churn):

```csharp
    SocialMedia,
    RegulatoryAnnouncement,   // UK RNS / regulatory news service announcements
    InsiderTransaction,       // director / insider buy-sell filings
    ConferenceMention         // conference / event agenda appearances
```

Add a brief leading XML-doc on the enum noting it is the canonical provenance source-type and that
values are **append-only** (each collector declares one). Do **not** wire these into any collector here —
they are reserved for the future collector worktrees.

### `CollectionContext.FeedsOfType`

Add a convenience method that centralises the deterministic feed filter the RSS collector currently
inlines:

```csharp
/// <summary>
/// The configured <see cref="CompanySourceFeed"/>s whose <c>FeedType</c> matches <paramref name="feedType"/>
/// (case-insensitive), in the canonical deterministic order (by CompanyId, then feed Id). Each collector
/// calls this with its own kind (e.g. "rss", "sec") to get exactly the feeds it should fetch; the feed's
/// <c>Url</c> carries that collector's per-company input (a feed URL, or an API endpoint / identifier).
/// Returns an empty list when no feed matches. Provenance: the bound CompanyId remains the high-confidence
/// company hint for evidence from that feed.
/// </summary>
public IReadOnlyList<CompanySourceFeed> FeedsOfType(string feedType)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(feedType);
    return SourceFeeds
        .Where(f => string.Equals(f.FeedType, feedType, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f.CompanyId)
        .ThenBy(f => f.Id)
        .ToList();
}
```

This matches the RSS collector's existing ordering exactly (`OrderBy(CompanyId).ThenBy(Id)`, AD-3 style),
so behaviour is unchanged — it is just centralised.

### `RssPressReleaseCollector`

Replace the inline `context.SourceFeeds.Where(... "rss" ...).OrderBy(...).ThenBy(...).ToList()` with
`var feeds = context.FeedsOfType("rss");`. No other behaviour changes; the existing per-feed read,
dedupe, hint-building, and summary logic stay as-is.

### `CompanySourceFeed`

Extend the type's XML-doc to state the seam convention explicitly: `FeedType` is the collector-kind
discriminator (`"rss"`, and later `"sec"`, `"govcontract"`, …); a collector selects its feeds via
`CollectionContext.FeedsOfType(kind)`; `Url` carries that collector's per-company input (a feed URL or an
API endpoint/identifier such as a CIK-based SEC URL). No fields change — the existing
`(FeedType, Name, Url)` shape is already sufficient, so no model reshaping is needed.

---

## Tests

### `CollectionContextTests` (new)

- **Filters by type:** a context with feeds of mixed `FeedType` returns only the matching ones.
- **Case-insensitive:** `FeedsOfType("RSS")` matches a feed whose `FeedType` is `"rss"`.
- **Deterministic order:** results are ordered by `CompanyId` then feed `Id` (build feeds out of order,
  assert the returned order).
- **No match → empty:** an unmatched kind returns an empty list.
- **Null/whitespace argument throws** `ArgumentException`.

### `EvidenceSourceTypeTests` (new or modified)

- Assert the three new values are defined and parse by name
  (`Enum.IsDefined`, `Enum.Parse<EvidenceSourceType>("RegulatoryAnnouncement")`, etc.). Keep it light —
  this guards against an accidental rename/removal.

### `RssPressReleaseCollectorTests` (modified only if needed)

- Existing tests should pass unchanged (the filter/order behaviour is identical). If any test reached
  into the inline filter, retarget it at `FeedsOfType`. Confirm the collector still fetches only `rss`
  feeds and ignores feeds of other `FeedType`.

---

## Constraints

- Target .NET 10; C# 14.
- `Radar.Domain` stays package-free (AD-5): the enum addition adds no dependency.
- **Determinism:** `FeedsOfType` preserves the established `(CompanyId, Id)` order (AD-3). Enum additions
  are append-only.
- **Provenance:** the feed→company binding remains the high-confidence hint; nothing here invents tickers
  or rewrites source types.
- Scope discipline: do **not** build or wire any new collector, and do **not** reshape `Company` /
  `CompanySourceFeed` fields — the existing `FeedType`/`Url` seam is sufficient. The new enum values are
  reserved for future collector worktrees and stay unused until then.
- No DB, no AI (AD-8). `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `EvidenceSourceType` gains `RegulatoryAnnouncement`, `InsiderTransaction`, `ConferenceMention`
      (append-only; existing members and order unchanged).
- [ ] `CollectionContext.FeedsOfType(feedType)` filters case-insensitively and returns feeds in the
      canonical `(CompanyId, Id)` order; throws on null/whitespace; empty on no match.
- [ ] `RssPressReleaseCollector` uses `context.FeedsOfType("rss")` and behaves identically to before.
- [ ] `CompanySourceFeed` / `CollectionContext` XML-docs document the feed-type seam convention.
- [ ] New `CollectionContextTests` and enum-membership tests added; RSS collector tests still green;
      build/test green.
