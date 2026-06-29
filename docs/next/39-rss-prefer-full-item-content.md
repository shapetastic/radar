# Task: Prefer full RSS item content over the summary teaser

## Overview

The RSS collector currently builds each evidence's body from `item.Summary ?? item.Title`, and
`HttpRssFeedReader` only reads `item.Summary?.Text`. On real company press-release feeds the
`<description>`/`<summary>` is frequently a **one-line teaser**, while the full announcement body lives
in the RSS `content:encoded` element or the Atom `<content>` element. By reading only the summary,
Radar discards most of the evidence text — the exact paragraphs where customer wins, contracts,
partnerships, capital raises, and guidance changes are described.

This starves the deterministic keyword extractor (slices 34–35): fewer words in `RawText` means fewer
matched rules and weaker, less-supported signals, even though the richer body was available in the
feed. It also makes report excerpts thinner than they need to be.

This slice teaches `HttpRssFeedReader` to capture the full item content when present and the collector
to prefer it: `RawText = fullContent ?? summary ?? title`. The full content is HTML, so it depends on
slice 38 (HTML stripping in normalization) to land first — otherwise the richer body just imports more
markup. With 38 in place, this slice straightforwardly increases extraction recall and excerpt quality
from real feeds, with no change to scoring, resolution, storage, or provenance.

---

## Assignment

Worktree: any
Dependencies: 38 (HTML stripping) should be merged first so the HTML body is cleaned. (Technically
builds without 38 — it would still compile and pass — but lands much of its value only once 38 cleans
the markup.)
Conflicts with: None among the queued specs. Edits only Infrastructure RSS files; does not touch the
normalizer (38) or the renderer (40).
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/Rss/RssFeedItem.cs                 # MODIFIED: add Content field
src/Radar.Infrastructure/Rss/HttpRssFeedReader.cs           # MODIFIED: read content:encoded / Atom content
src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs    # MODIFIED: prefer Content for RawText

tests/Radar.Infrastructure.Tests/Rss/HttpRssFeedReaderTests.cs        # MODIFIED/extended
tests/Radar.Infrastructure.Tests/Rss/RssPressReleaseCollectorTests.cs # MODIFIED/extended
```

No Application/Domain changes, no DI changes, no new interfaces.

---

## Implementation details

### `RssFeedItem`

Add a nullable `string? Content` field to the record (keep the existing fields and ordering; append the
new one or place it next to `Summary` — update the doc comment to note `Content` is the full body when
the feed supplies it, `Summary` the teaser). Both stay raw/un-normalized.

### `HttpRssFeedReader`

When mapping each `SyndicationItem`, populate `Content` from, in priority order:

1. The RSS `content:encoded` element if present. `SyndicationFeed` surfaces unknown/extension elements
   via `item.ElementExtensions`; read the `encoded` element in the
   `http://purl.org/rss/1.0/modules/content/` namespace (e.g. read it as a string extension). Guard
   with try/skip — a missing or unreadable extension just yields `null`, never an exception.
2. Otherwise the Atom/syndication `item.Content` when it is a `TextSyndicationContent` (use its `.Text`).
3. Otherwise `null`.

Leave `Summary` populated exactly as today (`item.Summary?.Text`). Do not throw if neither exists.
Keep all existing resilience behaviour unchanged: non-success status, transport errors, timeouts, and
malformed XML still degrade to an empty list with a warning; caller cancellation still propagates; the
XXE-hardened `XmlReaderSettings` (DtdProcessing.Prohibit, null resolver) is untouched.

### `RssPressReleaseCollector`

Change the body selection to prefer the fuller field:

```text
RawText = item.Content ?? item.Summary ?? item.Title
```

Everything else stays as-is: the within-batch dedupe, the `rssFeedUrl`/`rssItemId` metadata, the
feed→company hint binding (never invent a ticker), the deterministic `OrderBy(CompanyId).ThenBy(Id)`
ordering, and the `EvidenceSourceType.PressRelease` source type. The collector still does not strip
HTML — that is normalization's job (slice 38), reached via the mapper.

### Determinism / scope

- No clock or randomness introduced; ordering and dedupe behaviour are unchanged.
- Do not add packages. `content:encoded` is read via the existing `System.ServiceModel.Syndication`
  extension API already in Infrastructure.
- Do not change provenance: `SourceUrl`, `PublishedAt`, hints, and metadata are unchanged; only the
  body text source becomes richer.

---

## Tests

### `HttpRssFeedReaderTests` (extended)

- **`content:encoded` captured:** a fixture RSS feed (string/stream) whose item has a short
  `<description>` and a longer `<content:encoded>` (with the content namespace declared) parses to an
  `RssFeedItem` whose `Content` is the encoded body and whose `Summary` is the short description.
- **Atom `<content>` captured:** an Atom feed item with `<summary>` and a longer `<content>` yields
  `Content` from the content element.
- **Neither present:** an item with only `<description>` yields `Content == null`, `Summary` set
  (regression — existing behaviour preserved).
- **Malformed/extension-read failure does not throw:** an item with an odd/unreadable content extension
  still parses (Content falls back to null) and the reader returns the other items; existing
  malformed-XML-returns-empty test still passes.

### `RssPressReleaseCollectorTests` (extended)

- **Prefers Content:** given a reader stub returning an item with both `Content` and `Summary`, the
  collected `RawText` equals `Content`.
- **Falls back to Summary then Title:** Content null → `RawText == Summary`; Content and Summary null →
  `RawText == Title`.
- **Regression:** existing dedupe, hint-binding, ordering, and source-type assertions still hold.

---

## Constraints

- Target .NET 10; C# 14.
- All HTTP/XML/Syndication code stays in `Radar.Infrastructure` (AD-5); no provider SDK or new package.
- Preserve provenance and determinism: only the body-text source changes; ordering, dedupe, hints,
  metadata, timestamps, and resilience behaviour are unchanged.
- The collector still does not strip HTML; cleanup remains normalization's responsibility (slice 38).
- Keep changes scoped to the three RSS files and their tests. Do not touch extraction, scoring, the
  report, or DI. Do not add AI.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `RssFeedItem` carries a nullable `Content` field for the full item body.
- [ ] `HttpRssFeedReader` populates `Content` from RSS `content:encoded`, else Atom `<content>`, else
      null, without throwing and without changing existing resilience/XXE behaviour.
- [ ] `RssPressReleaseCollector` sets `RawText = Content ?? Summary ?? Title`; all other collector
      behaviour (dedupe, hints, ordering, source type, metadata) is unchanged.
- [ ] New tests cover content:encoded capture, Atom content capture, neither-present fallback, and the
      collector's Content→Summary→Title preference; build/test green.
