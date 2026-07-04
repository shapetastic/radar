# Task: Make AttentionScore breadth real — map news evidence SourceName to the article publisher

## Overview

The first four-collector live run (`["rss","sec","usaspending","newssearch"]`, Ollama/llama3.1, 2026-07-04)
proved the `newssearch` (Google News RSS) collector works well — 150 articles / 7 companies / 0 failures —
and `AttentionScore` moved off `0` (where GDELT's per-IP throttling had pinned it). But it surfaced a
**calibration defect: every one of the 7 companies scored the IDENTICAL `Attention 71`**, so the Opportunity
under-the-radar discount `(1 − Attention/200)` (AD-6) fired ~equally for all of them, stopped differentiating
companies, and pushed the strongest earner (MRCY) down from `Investigate` to `Watch`. The Attention dimension
is doing no useful work.

### Root cause (confirmed by code reading — state it precisely in the PR)

`AttentionScore`'s breadth term counts **distinct third-party evidence `SourceName`s** —
`RadarScoreFormulaV2.Compute` (`src/Radar.Application/Scoring/RadarScoreFormulaV2.cs`, ~lines 142–150):

```csharp
var distinctThirdPartySources = signals
    .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
    .Select(s => s.Evidence.SourceName)
    .Where(name => !string.IsNullOrWhiteSpace(name))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Count();
var reach = distinctThirdPartySources + MediaReachWeight * mediaCount; // 0.5 * mediaCount
var attentionScore = Score(100 * reach / (reach + AttentionHalfSaturation)); // reach / (reach + 5)
```

But `NewsAttentionCollector.MapToEvidence`
(`src/Radar.Infrastructure/News/NewsAttentionCollector.cs`, ~lines 236–279) sets the evidence
`SourceName` to the **per-company feed name** (e.g. `"Mercury — News"` / `"News attention (Google News)"`),
NOT to the article's actual publisher — while it already parses the real publisher into
`article.SourceName` (Reuters, Yahoo Finance, MarketBeat, …) and merely files it away in
`metadata["publisher"]`:

```csharp
var publisher = article.SourceName;               // the REAL outlet, parsed by the reader
...
return new CollectedEvidence(
    SourceType: SourceType,
    SourceName: feed.Name,                          // <-- the FEED name, one value per company
    ...
    Metadata: metadata /* metadata["publisher"] = publisher — the real outlet, discarded from scoring */)
```

`CollectedEvidenceMapper.ToEvidenceItem` carries `collected.SourceName` straight through to
`EvidenceItem.SourceName` (`src/Radar.Application/Collectors/CollectedEvidenceMapper.cs:58`). So **every one
of a company's news articles collapses to the single feed name**, `distinctThirdPartySources` is **always 1**,
and `reach = 1 + 0.5·mediaCount`. With ~20+ retained articles per company the `0.5·mediaCount` term saturates
`100·reach/(reach+5)` to ~71 for everyone — the identical `Attention 71` observed. The breadth term, the
formula's whole point (how many distinct outlets are covering this company), is inert because the input it
counts is constant per company.

`HttpNewsSearchReader.ResolveSourceName` already resolves the real outlet from the RSS `<source>` element,
falling back to the `" - Publisher"` title suffix, then empty (`HttpNewsSearchReader.cs`, ~lines 227–250), and
the collector tests already build articles with real publishers (`sourceName: "Yahoo Finance"`, `"Reuters"`).
The correct outlet name is present and thrown away.

> Note: there is **no GDELT collector in the tree** — `GdeltNewsAttentionCollector` does not exist; spec 67's
> GDELT approach was superseded by the spec-80/81 keyless Google News RSS `newssearch` collector. So the
> "confirm GDELT has the same feed-name-vs-publisher issue" concern is moot — `newssearch` is the only
> third-party attention collector, and this slice fixes it in one place.

### The fix (minimal, root-cause, in-architecture)

Map the news evidence `SourceName` to the article's **actual publisher** (`article.SourceName`) so
`distinctThirdPartySourceNames` reflects the true number of distinct outlets covering a company. Preserve the
human-readable feed attribution as a new `metadata["feedName"]` key (the feed URL token is already kept in
`metadata["newsSearchFeedUrl"]`, and the collector's own name is `CollectorName = "newssearch"`), so
provenance and any feed-name display can still recover it. Also **dedupe breadth by distinct publisher within a
feed** so ten Reuters articles count as one outlet, not ten (the formula already `Distinct()`s
`SourceName`, so distinct-publisher `SourceName`s deliver this for free — but a blank publisher must not
collapse many outlets to one empty-string bucket; see below).

This is **scoring-affecting** (Attention moves) but is a **collector/evidence-mapping change, NOT a
formula-math change**: `RadarScoreFormulaV2.Compute` is untouched, so **`RadarScoreFormulaV2.Version` stays
`radar-formula-v2`** (no AD-6 formula-math change). Per **AD-10** the moved scoring output **bumps
`ScoringEngine.ScoringConfigVersion`** (`radar-scoring-config-v5` → `radar-scoring-config-v6`). See the
"Version-bump obligation" section — this reasoning must be stated explicitly in the PR.

Whether the saturation still flattens the 7 companies **after breadth is real** is deliberately **out of scope
here** — ship this collector fix, re-measure on a live run, and only then decide if the formula constants need
retuning (that would be a **separate** spec 85 → `radar-formula-v3` + AD-6 update). Prefer the smallest correct
change first.

---

## Assignment

Worktree: any
Dependencies: 81 (`NewsAttentionCollector` — the collector this modifies), 80 (the reader/source-type design),
58 (`radar-formula-v2` AttentionScore) — all merged. This is a directed scoring-calibration slice driven by
the 2026-07-04 live finding, not the generic planner loop.
Conflicts with: touches `NewsAttentionCollector.cs` (+ its tests) and `ScoringEngine.cs`
(`ScoringConfigVersion` constant + comment). Must **NOT** run in parallel with any other collector-mapping or
scoring/engine slice — sequence it. No Domain, DI-shape, report-renderer, or formula-math change.
Estimated time: ~1.5–2 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **The breadth input is the evidence `SourceName`.** `RadarScoreFormulaV2` counts
  `Distinct(StringComparer.OrdinalIgnoreCase)` of `s.Evidence.SourceName` restricted to
  `EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType)` (whitelist: `NewsArticle`,
  `SocialMedia`, `ConferenceMention`). It already skips blank names (`Where(name => !IsNullOrWhiteSpace)`).
  The formula does **not** read `metadata["publisher"]` — it only sees `Evidence.SourceName`. (`RadarScoreFormulaV2.cs` ~142–150.)
- **The collector sets `SourceName: feed.Name`** and stores the real outlet only in `metadata["publisher"]`
  (`NewsAttentionCollector.MapToEvidence`, ~236–279). `article.SourceName` is the parsed real publisher
  (`NewsArticleItem` doc-comment; `HttpNewsSearchReader.ResolveSourceName`), which can be the **empty string**
  when neither the `<source>` element nor a `" - Publisher"` suffix is present.
- **`SourceName` flows through unchanged** to `EvidenceItem.SourceName`
  (`CollectedEvidenceMapper.ToEvidenceItem`:58) and is what the report renders as the evidence source label
  (`MarkdownWeeklyReportRenderer.AppendEvidence`:260 — `ev.SourceName`). After this change the report will show
  the real publisher per article, which is an **improvement**, not a regression — but keep the feed name
  recoverable (new `metadata["feedName"]`).
- **The within-feed URL dedupe already exists** (`seenUrls`, ~128) — distinct articles by URL. Distinct
  *publisher* breadth is delivered by the formula's existing `Distinct(SourceName)`, so mapping `SourceName` to
  the publisher makes N Reuters articles count as **one** Reuters outlet automatically. No new dedupe pass is
  needed in the collector; just ensure a **blank** publisher does not attribute to an empty-string outlet (the
  formula skips blank names, so a blank-publisher article contributes **0 breadth** but still contributes its
  `MediaAttention` media-count — that is acceptable and correct: an unattributable article is not a distinct
  outlet). Confirm and note this.
- **`ScoringConfigVersion` is currently `"radar-scoring-config-v5"`** (`ScoringEngine.cs:39`); `ScoringVersion`
  = `EngineVersion` = `"mvp-engine-v1"`; formula `Version` = `"radar-formula-v2"`.

---

## Project structure changes

```text
src/Radar.Infrastructure/News/
  NewsAttentionCollector.cs           # MODIFIED: SourceName := article publisher (fallback feed.Name when blank);
                                      #   add metadata["feedName"]; keep metadata["publisher"] + ["newsSearchFeedUrl"].

src/Radar.Application/Scoring/
  ScoringEngine.cs                    # MODIFIED: bump ScoringConfigVersion v5 -> v6 + update comment. No formula change.

tests/Radar.Infrastructure.Tests/News/
  NewsAttentionCollectorTests.cs      # MODIFIED: assert SourceName == publisher; feedName in metadata; distinct
                                      #   publishers produce distinct SourceNames; blank-publisher fallback.
tests/Radar.Application.Tests/Scoring/
  RadarScoreFormulaV2Tests.cs         # MODIFIED/NEW: distinct publisher SourceNames raise breadth (differentiating
                                      #   AttentionScore); same publisher repeated does not (dedupe). Formula code UNCHANGED.
tests/Radar.Application.Tests/Scoring/
  ScoringEngineTests.cs               # MODIFIED: update the ScoringConfigVersion assertion (v5 -> v6) if asserted.
```

No Domain change. No DI-shape change. No report-renderer edit (it already renders `Evidence.SourceName`).
No formula-math edit (`RadarScoreFormulaV2.Compute` body unchanged).

---

## Implementation details

### 1. Map news evidence `SourceName` to the real publisher (`NewsAttentionCollector.MapToEvidence`)

- Compute the publisher once: `var publisher = article.SourceName;` (already present).
- Set the evidence `SourceName` to the publisher, **falling back to `feed.Name` when the publisher is blank**
  so an unattributable article still carries a non-empty, human-readable source label for the report — but note
  that a blank-publisher article contributes **0** to breadth because the formula skips blank names, and the
  `feed.Name` fallback is per-company constant so it will **not** inflate breadth (many blank-publisher
  articles → one feed-name bucket, deduped by the formula's `Distinct`). Prefer:

  ```csharp
  var publisher = article.SourceName;
  var sourceName = string.IsNullOrWhiteSpace(publisher) ? feed.Name : publisher;
  ```

  Use `sourceName` for `CollectedEvidence.SourceName`.
  > Reasoning to capture in a comment: the breadth term counts distinct third-party `SourceName`s, so it must
  > be the OUTLET, not the per-company feed; the feed-name fallback keeps a readable label for the rare
  > blank-publisher article without ever manufacturing false breadth (it is a single constant bucket the
  > formula dedupes, and blank would be skipped anyway).

- Keep `metadata["publisher"] = publisher` (unchanged — provenance of the parsed outlet, even when blank).
- **Add** `metadata["feedName"] = feed.Name` so the per-company feed attribution is still recoverable for
  provenance/display now that `SourceName` is the outlet. Keep `metadata["newsSearchFeedUrl"] = feed.Url`
  (the feed token, unchanged) and `metadata["url"]`, `metadata["pubDate"]`, `metadata["quality"] = "Medium"`.
- Do **not** change `Title` (still the full headline incl. the `" - Publisher"` suffix, kept for provenance),
  `RawText`, `SourceUrl` (`article.Url`), `PublishedAt`, `CollectedAt`, or `CompanyHints`.

### 2. Preserve every spec-81 invariant (do NOT regress)

- The **title-relevance provenance filter** (`IsRelevant`, the `" - Publisher"` suffix strip before the check,
  whitespace-normalisation, the MASSPHOTON/`MRCY Wire` false-match guards) is **unchanged** — the publisher is
  used only for the stored `SourceName`/metadata, never for relevance.
- Sequential paced feed processing, within-feed URL dedupe (`seenUrls`), `MaxRecordsPerCompany`, malformed-token
  and non-success degrade-to-source-failure, company hints from the feed binding, cancellation propagation, and
  the "no fabricated body text" rule are all **unchanged**.

### 3. Bump the scoring generation stamp (AD-10) — no formula change

- Bump `ScoringEngine.ScoringConfigVersion` `"radar-scoring-config-v5"` → `"radar-scoring-config-v6"` and update
  the accompanying comment to record that THIS slice (news attention breadth by publisher) ships this
  generation, so a cross-run delta across the pre/post boundary renders `(scoring updated)` instead of a
  fabricated `Thesis improving`/`Thesis deteriorating` (the AD-10 comparability gate).
- Do **NOT** touch `ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the formula math is
  byte-for-byte unchanged; only the *evidence input it counts* changed, upstream in the collector. (This is the
  exact AD-10 case: a scoring-affecting change that is not a formula/engine identity change.)

---

## Version-bump obligation (state explicitly in the PR)

- **`RadarScoreFormulaV2.Version` = `radar-formula-v2` — UNCHANGED.** The formula's code, constants, and shape
  (`100·reach/(reach+5)`, `reach = distinctThirdPartySourceNames + 0.5·mediaSignals`, the third-party whitelist)
  are not edited. This is **not** a formula-math change, so it is **not** `radar-formula-v3` and requires **no
  AD-6 update**. (Had the math changed — e.g. retuning the `+5` half-saturation or the `0.5` media weight —
  that WOULD be `radar-formula-v3` + an AD-6 refinement entry; that is the separate, deferred spec 85.)
- **`ScoringEngine.ScoringConfigVersion` = `radar-scoring-config-v5` → `radar-scoring-config-v6` — BUMPED
  (AD-10).** Attention (and thus Opportunity, ranking, and possibly action labels) moves for every company with
  multi-outlet coverage, so per AD-10 the whole-generation stamp must bump in this same slice; the comparability
  gate then renders `(scoring updated)` rather than fabricating a thesis-trajectory label across the boundary.
- **`EngineVersion` / `ScoringVersion` = `mvp-engine-v1` — UNCHANGED.**

---

## Tests

### `NewsAttentionCollectorTests` (Infrastructure)

1. **SourceName is the publisher (the fix):** the existing
   `CollectAsync_MapsArticlesToNewsEvidenceWithProvenanceAndHints` test (article publisher `"Yahoo Finance"`)
   must now assert `item.SourceName == "Yahoo Finance"` (was `"Mercury — News"`), while
   `item.Metadata["publisher"] == "Yahoo Finance"` and `item.Metadata["feedName"] == "Mercury — News"` and
   `item.Metadata["newsSearchFeedUrl"] == MrcyToken` all still hold. Update the existing assertions rather than
   leaving the stale `Assert.Equal("Mercury — News", item.SourceName)`.
2. **Distinct publishers → distinct SourceNames (breadth becomes real):** one feed returning articles from
   `"Reuters"`, `"Yahoo Finance"`, `"MarketBeat"` (all title-relevant, distinct URLs) → three evidence items
   with three distinct `SourceName`s; assert the set equals those three publishers.
3. **Same publisher repeated → same SourceName (dedupe by outlet holds via distinct SourceName):** three
   distinct-URL `"Reuters"` articles → three evidence items all with `SourceName == "Reuters"` (so the formula's
   `Distinct(SourceName)` counts one outlet). URL dedupe still applies to identical URLs (existing test).
4. **Blank publisher falls back to feed name:** an article with `sourceName: ""` (relevant title, valid URL) →
   `item.SourceName == feed.Name`, `item.Metadata["publisher"] == ""`, `item.Metadata["feedName"] == feed.Name`.
5. **Provenance/relevance/order unchanged:** the sequential-order, malformed-token, rate-limited, no-coverage,
   MASSPHOTON drop, and `MRCY Wire` false-match tests stay green (the publisher change is orthogonal). In
   `CollectAsync_ProcessesFeedsSequentiallyInDeterministicOrder` the surviving item's `SourceName` is now the
   article publisher (`"Reuters"`) — update that one assertion accordingly, and assert `metadata["feedName"]`
   still carries `"Mercury — News"`.

### `RadarScoreFormulaV2Tests` (Application) — formula code UNCHANGED, new coverage of the now-real input

6. **Distinct third-party publisher SourceNames raise AttentionScore:** build signals over `NewsArticle`
   evidence with 1 vs 3 distinct `SourceName`s (same media-count) and assert AttentionScore strictly increases
   with distinct-outlet breadth — locking that breadth now differentiates once `SourceName` varies.
7. **Repeated same publisher does not inflate breadth:** three `NewsArticle` evidence items with identical
   `SourceName` produce the same breadth as one (the existing `Distinct` behaviour) — a regression lock that the
   fix relies on for outlet-dedupe. (If an equivalent case already exists, extend it rather than duplicate.)

### `ScoringEngineTests` (Application)

8. **`ScoringConfigVersion` stamp:** update any assertion of the stamp to `"radar-scoring-config-v6"`;
   `ScoringVersion`/`EngineVersion`/formula `Version` unchanged.

Existing collector, formula, engine, and report tests stay green.

---

## Spec-implementation checklist

1. **Code paths replaced:** `NewsAttentionCollector.MapToEvidence` now derives `SourceName` from the article
   publisher (feed-name fallback for blank) and adds `metadata["feedName"]`. `ScoringConfigVersion` bumped. No
   formula body change; no report-renderer change.
2. **Tests:** update the stale `SourceName == "Mercury — News"` assertions to the publisher; add the distinct/
   repeated-publisher and blank-fallback cases (2–4); add/extend the formula breadth cases (6–7); update the
   `ScoringConfigVersion` assertion (8); keep all other collector/formula/engine/report tests green.
3. **Delete nothing still used** (`metadata["publisher"]` and `["newsSearchFeedUrl"]` are retained; a new
   `["feedName"]` is added).
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule and **no AD-6 change** (formula math
   unchanged). This realises AD-6's stated intent that "a news/media collector makes [Attention] meaningful
   automatically" by feeding the breadth term its intended input (distinct outlets). Note in the PR that only
   `ScoringConfigVersion` moved (AD-10), already documented in the CLAUDE.md scoring checklist. If a live
   re-measure after this ships shows the saturation still flattens companies, that is the separate deferred
   spec 85 (`radar-formula-v3` + AD-6 update) — do not pre-empt it here.

---

## Constraints

- Target `net10.0`, C# 14. The collector-mapping change stays in `Radar.Infrastructure` (AD-5); the scoring
  stamp lives in `Radar.Application`. No provider SDK, no AI, no DB (AD-8, files-first).
- **Provenance is sacred and strengthened:** evidence still traces to its article URL; the parsed publisher is
  now the first-class source name AND retained in metadata; the feed attribution is preserved in
  `metadata["feedName"]` + `metadata["newsSearchFeedUrl"]` + `CollectorName`. Signal→evidence→score links are
  unchanged.
- **Preserve every spec-81 guard:** the title-relevance provenance filter, the `" - Publisher"` suffix strip
  (relevance-only), the per-collector `IsRelevant`/publisher-suffix hooks, sequential paced reads, URL dedupe,
  `MaxRecordsPerCompany`, degrade-to-source-failure, and no-fabricated-body rules must NOT regress.
- **AD-6 formula UNCHANGED** (`radar-formula-v2` stays; no `radar-formula-v3`, no AD-6 edit) — this slice only
  changes the *evidence input* the breadth term counts, upstream in the collector.
- **Bump `ScoringEngine.ScoringConfigVersion`** (`radar-scoring-config-v5` → `radar-scoring-config-v6`, AD-10);
  do NOT touch `ScoringVersion`/`EngineVersion`/formula `Version`.
- AD-9 labels/advice rules unchanged; never emit advice language. Deterministic ordering (AD-3) unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (do NOT do here)

- **Any change to `RadarScoreFormulaV2.Compute`** — the `100·reach/(reach+5)` saturation, the `0.5·mediaSignals`
  weight, and the third-party whitelist are untouched. Retuning those (if a live re-measure after this ships
  still shows flattening) is the **separate, deferred spec 85** → `radar-formula-v3` + an AD-6 refinement entry
  + its own `ScoringConfigVersion` bump. Do not bundle a formula-math change with this collector fix.
- Any report-renderer restructuring (it already renders `Evidence.SourceName`; the label simply improves).
- Any new collector, Domain record, or DI-shape change.

---

## Acceptance criteria

- [ ] `NewsAttentionCollector.MapToEvidence` sets `CollectedEvidence.SourceName` to the article's real publisher
      (`article.SourceName`), falling back to `feed.Name` only when the publisher is blank; a comment explains
      that breadth counts distinct OUTLETS and the fallback never manufactures false breadth.
- [ ] The parsed publisher is still in `metadata["publisher"]`; a new `metadata["feedName"] = feed.Name` is
      added; `metadata["newsSearchFeedUrl"]`, `["url"]`, `["pubDate"]`, `["quality"] = "Medium"` are retained.
- [ ] Every spec-81 guard is intact: title-relevance filter (incl. MASSPHOTON drop and `MRCY Wire` false-match),
      `" - Publisher"` suffix strip for relevance only (stored Title keeps the suffix), sequential paced reads,
      URL dedupe, `MaxRecordsPerCompany`, degrade-to-source-failure, cancellation, no fabricated body.
- [ ] `RadarScoreFormulaV2.Compute` is **unchanged** and `RadarScoreFormulaV2.Version` remains
      `"radar-formula-v2"` (no `radar-formula-v3`, no AD-6 edit). Tests show distinct-publisher `SourceName`s now
      raise `AttentionScore` while repeated same-publisher does not (outlet dedupe via the existing `Distinct`).
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped `"radar-scoring-config-v5"` → `"radar-scoring-config-v6"`
      (AD-10) with an updated comment recording this slice; `ScoringVersion`/`EngineVersion`/formula `Version`
      unchanged; any test asserting the old stamp is updated.
- [ ] Collector tests assert `SourceName == publisher` (incl. the updated existing test), distinct/repeated
      publisher behaviour, and the blank-publisher feed-name fallback; formula tests lock breadth
      differentiation and outlet dedupe.
- [ ] Layering (AD-5), files-first (AD-8), determinism (AD-3), AD-6 formula rules, and AD-9 label/advice rules
      preserved; no Domain/DI-shape/report/formula-math change. `dotnet build`/`dotnet test` on
      `Radar.sln -c Release` green.
