# Task: Hiring / ATS job-board collector (leading talent-demand signal)

> **DIRECTED FEATURE — the maintainer-chosen next collector.** This adds a brand-new signal axis: **hiring
> demand** read directly from companies' public ATS job boards (Greenhouse / Lever). It is a *leading*
> talent-demand indicator — a company staffing up ahead of the market noticing. This is a **single cohesive
> slice** (~2–3 h) that lands Domain + extractor + Infrastructure collector + seed + wiring together, because
> the collector's emitted phrase and the extractor rule are a **verbatim contract** and must ship as one unit
> (same pattern as specs 99+100 for InstitutionalOwnership). It is **opt-in / OFF by default** — the baseline
> run does not enable it.

## Overview

A new deterministic collector (`IEvidenceCollector`, config kind `"hiringats"`, `CollectorName "hiring-ats"`,
`EvidenceSourceType.JobPosting`) reads each seeded company's public ATS job board, counts open roles (total +
senior/leadership + engineering/R&D), and emits **one** `JobPosting` `CollectedEvidence` per company carrying a
**fixed hiring phrase** in Title/RawText plus rich provenance metadata (platform, board token, the three
counts, a few sample titles, retrieved timestamp). A new `SignalType.HiringActivity` and one
`KeywordSignalExtractor` rule map that phrase to a **Neutral** `HiringActivity` signal.

**v1 is deliberately Neutral, not directional** (the settled signal decision — see below). It establishes the
hiring axis end-to-end (provenance: board → `JobPosting` evidence → `HiringActivity` signal → score), lifts
source diversity / Attention / Velocity breadth, and **accrues the open-role counts in timestamped evidence
metadata** — without misfiring Trajectory. Directional *surge* detection is a deferred future slice B that
reads exactly this accrued history; no separate history store is built now.

This threads the normal `collect → map → resolve → review → store → score → report` path, so provenance is
intact end-to-end. Opt-in via `Radar:Collectors`; the default baseline run is byte-for-byte unchanged **in
scoring math** (see the Fingerprint section for the one automatic re-stamp).

---

## Assignment

Worktree: any — mostly new Infrastructure files under a new `Hiring/` folder + one additive Domain enum value +
one additive extractor rule + additive DI + one enable-able collector kind + seed data. It edits shared
surfaces (`SignalType.cs`, `KeywordSignalExtractor.cs`, `RadarWorkerServices.cs`, `RadarWorkerOptions.cs`,
`appsettings.json`, `InfrastructureServiceCollectionExtensions.cs`, `data/companies.json`, and the
fingerprint/descriptor tests), so **sequence** it rather than parallelizing against any slice that touches the
extractor rule table, `SignalType`, the scoring fingerprint, Worker composition/DI, or the seed.
Dependencies: 99/100 (the `SignalType` + extractor-phrase-contract precedent this mirrors — merged), 95 (the
`SignalSourceDescriptor` that folds `RuleSetVersion` into the fingerprint — merged), 97 (feed-Id folds feed
type — merged), 83 (shared query-feed-token parser precedent — merged), 98 (`SeedFeedInventoryValidator` — must
verify no false warning; merged).
Conflicts with: any slice touching `KeywordSignalExtractor` rules / `RuleSetVersion`, `SignalType`,
`ScoringConfigFingerprint`/its tests, Worker composition/DI, or the seed.
Estimated time: ~2–3 h

---

## Grounding facts (verified against the code, 2026-07-07)

- **`SignalType`** (`src/Radar.Domain/Signals/SignalType.cs`) currently ends `…, PatentActivity,
  DeveloperAdoption, MediaAttention, Other`. Note a **pre-existing, unused** `HiringExpansion` member sits at
  index 7 (referenced only by `docs/radar-schema-spec.md`, no extractor rule, no collector). **Do not repurpose
  it** — the maintainer chose a new, honest name `HiringActivity` ("activity", not a *proven* surge). Leave
  `HiringExpansion` reserved/untouched.
- **`KeywordSignalExtractor`** (`src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs`): a fixed
  ordered `KeywordSignalRule[]` table matched case-insensitively as substrings of the composed searchable text
  (`EvidenceSearchableText.Compose(Title, RawText)`); first match per `SignalType` wins; matches sorted by
  `(int)SignalType` then index. `RuleSetVersion = "radar-keyword-rules-v2"` (const, line 57) is the rule-set
  identity folded into the fingerprint. The **NewsArticle** source type is the one branch that suppresses the
  keyword rules (emits Neutral `MediaAttention`) — so hiring evidence must **not** be `NewsArticle` (it is
  `JobPosting`, so the keyword rules fire normally). The extractor only reads **metadata** for the two
  materiality keys (`awardAmount`, `insiderNetValue`); it does **not** scan metadata for phrases — so anything
  in the metadata bag is invisible to rule matching (this is why sample job titles go in metadata, not RawText).
- **Collector shape** to mirror exactly: `Sec13DGCollector` / `SecForm4Collector` / `UsaSpendingContractCollector`
  / `GdeltNewsCollector`. Each: `CollectorName`, `SourceType`, iterate `context.FeedsOfType("<kind>")`
  (deterministic order), build `companiesById`, per-feed read via an injected `IReader`, on `!IsSuccess` add a
  `SourceFailure` + log Warning + continue, map successes to `CollectedEvidence` via a private `MapToEvidence`,
  dedupe within a feed, return `CollectionResult(items, CollectionSummary(checked, ok, failed, count,
  failures))`. Company hints come **only** from `CollectorCompanyHints.For(feed.CompanyId, companiesById)`
  (`src/Radar.Infrastructure/Sources/CollectorCompanyHints.cs`) — never invent a ticker.
- **Reader pattern (non-SEC)**: there is **no shared generic HTTP-JSON fetch helper** — each non-SEC reader
  (`HttpUsaSpendingAwardReader`, `HttpGdeltNewsReader`, `HttpNewsSearchReader`) hand-rolls an **injected typed
  `HttpClient` + `System.Text.Json`** behind its own `internal` `IReader` interface, returning a typed
  read-result record with an **outcome enum** (`Success`/`Unreachable`/`HttpError`/`Timeout`/`Malformed`),
  logging Warnings, and **re-throwing only genuine caller cancellation**
  (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`). **Do NOT** reuse
  `SecHttpFetch`/`SecEdgarUrls` (SEC-specific) and **do NOT** invent a premature shared helper — mirror the
  established per-reader pattern.
- **Feed-token parsers**: two already exist and hand-roll the same order-robust two-key `&`-split —
  `QueryFeedTarget` (`query=…&ticker=…`) and `UsaSpendingFeedTarget` (`recipientId=…&recipientSearchText=…`),
  both `internal sealed record … { Parse(string?) -> …? }` in the relevant Infrastructure namespace, returning
  `null` on malformed input so the collector degrades to a `SourceFailure`. A third parser for
  `platform=…&board=…` is consistent with this pattern (see Design → reuse note).
- **Seed** (`data/companies.json`, read by `LocalFileCompanySeedSource`): each company has `sourceFeeds[]` with
  `{ type, name, url }`. The four target companies are present: **MRCY** (id `885ea986-…`), **AGYS**
  (`f0d50897-…`), **ERII** (`a825bf45-…`), **CVLT** (`c29674f6-…`). Feed Id is
  `DeterministicGuid(companyId, "feed", $"{feedType}|{feedUrl}")` — feed **type** is folded in (spec 97), so a
  `hiringats` feed never collides with any existing feed even if the URL matched (it won't — the token is
  unique).
- **`CollectionContext.SourceFeeds`** (`RadarPipelineRunner`) = **all** feeds from
  `ICompanyRepository.GetSourceFeedsAsync()` (seeded from `companies.json`), **independent of which collectors
  are enabled**. `SeedFeedInventoryValidator` compares seed-declared feed types to `context.SourceFeeds` — both
  populated from the same seed — so a declared-but-**disabled** `hiringats` feed type has **declared == reached**
  ⇒ **no** `feeds-lost-before-collection` warning. **No validator change is needed** (verify with a test).

---

## The signal decision (settled — encode as fixed; mirrors the 13G decision in spec 99)

**v1 emits a NEUTRAL `HiringActivity` signal, not directional.** Rationale to state in code comments and the PR:

- A **single-snapshot open-role COUNT cannot distinguish** a company that is genuinely *expanding* from one
  that simply *always hires at scale* (Commvault shows ~90 open roles at spike time vs Agilysys ~13). A
  mild-Positive on raw count would systematically favour the largest / always-hiring names — the exact
  selection-bias trap the diversified universe (8→19 companies) was built to avoid.
- So v1 **establishes the mechanism** (the hiring axis + `JobPosting` source type + provenance + evidence /
  Attention / Velocity breadth) and **accrues the counts in timestamped evidence metadata**, **without**
  moving Trajectory (Neutral contributes 0 to TrajectoryScore).
- **Directional surge detection** (Positive when open-role / senior-role counts *accelerate* vs the accrued
  hiring-evidence history) is **deferred to slice B**. No separate history store is needed now — the
  timestamped evidence metadata this slice writes **is** the record slice B will read. This is the same
  conservative-first / build-the-mechanism-defer-conviction pattern as the 13G Neutral (spec 99) and the price
  reference store (AD-14).
- **Name is `HiringActivity`** on purpose (honest — it is *activity*, not a proven surge). **Slice B changes
  the DIRECTION, not the type name.**

---

## Design

### 1. Domain — `SignalType.HiringActivity`

Add `HiringActivity` to `SignalType` (`src/Radar.Domain/Signals/SignalType.cs`), placed adjacent to the
existing hiring-axis member `HiringExpansion` for readability. `SignalType` is **persisted by name**
(`.ToString()` / parse), so member placement does not affect persisted values; the only ordinal use is the
extractor's deterministic in-evidence match sort, which stays deterministic. Confirm no exhaustive `switch` over
`SignalType` needs a new arm (there was none for `InstitutionalOwnership` in spec 99 — the extractor maps by
string and the formula folds by direction).

### 2. Extractor rule — one Neutral phrase (verbatim contract)

Add **one** rule group to the `KeywordSignalExtractor` `Rules` table (after the `InstitutionalOwnership` group),
mapping the single fixed phrase the collector emits to `HiringActivity` **Neutral** at routine strength
(mirror the existing Neutral routine rows such as `insider stock transaction (routine)` = 3/4/0.45 and
`passive beneficial-ownership stake (13g)` = 3/5/0.5):

```csharp
// HiringActivity (public ATS job board; spec 103). The HiringBoardCollector synthesizes exactly this fixed
// phrase into the JobPosting evidence Title/RawText; the extractor only maps phrase -> fixed type+direction+
// strength (never re-derives valence — the InstitutionalOwnership precedent). v1 is NEUTRAL by design: a
// single-snapshot open-role COUNT cannot tell genuine expansion from an always-large hirer, so it never
// misfires bullish (Neutral contributes 0 to Trajectory). Directional SURGE detection vs accrued hiring
// history is deferred to slice B (changes DIRECTION, not this type name). NO materiality metadata read here
// (unlike InsiderBuying) — Strength is the fixed rule Strength.
new("hiring activity (open roles)", SignalType.HiringActivity, SignalDirection.Neutral, 3, 4, 0.45m),
```

- **ONE phrase is chosen deliberately** (simplest; keeps the verbatim contract tiny). The metadata still carries
  the senior/eng breakdown for slice B — a tiered *phrase* split (e.g. a separate "senior hiring" phrase) is
  **not** needed in v1 because direction is deferred; note it as a slice-B option.
- Bump **`KeywordSignalExtractor.RuleSetVersion`** `"radar-keyword-rules-v2"` → **`"radar-keyword-rules-v3"`**
  and update the const's surrounding comment. Update the class XML-doc's "purely keyword-driven … including the
  `InstitutionalOwnership` group" sentence to also mention the new `HiringActivity` group carries **no** metadata
  read, so the "exactly ONE `EvidenceSourceType` branch + two metadata reads" invariant stays intact.
- **No-contamination rule (important):** the collector must embed **only** the fixed phrase + numeric counts +
  platform/board in Title/RawText — **never raw job titles** — because a job title like "VP, Strategic
  Partnerships" would otherwise trip the `partnership` rule. Sample titles live in **metadata only** (not scanned
  by the extractor). State this in both the collector and the extractor rule comment.

### 3. Infrastructure — reader seam + collector (new `Radar.Infrastructure/Hiring/` folder)

**Reader seam** (`internal`, mirrors the non-SEC reader pattern; two platforms have different JSON shapes):

- `JobBoardResult(int TotalRoles, IReadOnlyList<string> Titles)` — the normalized shape both platforms map to.
  `TotalRoles` is the **count of parsed job entries** (authoritative/deterministic from the returned payload) —
  do **not** trust Greenhouse's `meta.total` (may be a server-side/paginated figure); read it only as an
  optional cross-check log if desired.
- `JobBoardReadOutcome` enum: `Success, Unreachable, HttpError, Timeout, Malformed` (mirror
  `UsaSpendingReadOutcome`; no `Forbidden` — these public endpoints need no key/UA; a bad board token yields
  `HttpError` 404).
- `JobBoardReadResult(JobBoardReadOutcome Outcome, JobBoardResult? Result, string? Detail)` with `IsSuccess` +
  `Success(...)`/`Failure(...)` factories (mirror `UsaSpendingReadResult`).
- `IJobBoardReader { string Platform { get; } Task<JobBoardReadResult> ReadAsync(string boardToken, CancellationToken ct); }`.
- `GreenhouseBoardReader : IJobBoardReader` (`Platform => "greenhouse"`): GET
  `https://boards-api.greenhouse.io/v1/boards/{boardToken}/jobs`; parse `{"jobs":[{"title":…},…]}` — titles from
  `jobs[].title` (skip entries with a blank title). Empty `jobs` array ⇒ `Success` with zero roles (a valid
  no-openings board, not an error); root not an object / missing `jobs` array ⇒ `Malformed`.
- `LeverBoardReader : IJobBoardReader` (`Platform => "lever"`): GET
  `https://api.lever.co/v0/postings/{boardToken}?mode=json`; parse a **top-level JSON array** of `{"text": title,…}`
  — titles from each element's `text`. Empty array ⇒ `Success` zero roles; root not an array ⇒ `Malformed`.
- Both readers: injected typed `HttpClient` + `System.Text.Json`, `HttpCompletionOption.ResponseHeadersRead`,
  materialize body before dispose, the standard `HttpRequestException`→`Unreachable` /
  `TaskCanceledException`(non-ct)→`Timeout` / caller-cancellation re-throw / non-success→`HttpError` /
  `JsonException`→`Malformed` ladder, logging Warnings. All HTTP/JSON stays in `Radar.Infrastructure` (AD-5); all
  new types `internal`.
- **Title classifier** `JobTitleClassifier` (static, pure, `internal`): case-insensitive **substring** match on
  a title against two small fixed keyword sets, returning `(int Senior, int Engineering)` counts over a title
  list. **Senior/leadership**: `VP`, `Vice President`, `Chief`, `Head of`, `Director`, `Principal`.
  **Engineering/R&D**: `Engineer`, `Engineering`, `R&D`, `Research`, `Scientist`. (A single title may count
  toward both buckets — they are independent tallies, not a partition.)

**Feed-token parser** `HiringFeedTarget(string Platform, string BoardToken)` (`internal sealed record`, in
`Radar.Infrastructure.Hiring`): `Parse(string?) -> HiringFeedTarget?` for the token
`platform=<greenhouse|lever>&board=<token>`, mirroring `QueryFeedTarget`/`UsaSpendingFeedTarget` exactly
(order-robust, split on the first `&` between the two keys, trim, `null` on missing key / blank value).
**Reuse-over-copy:** this is the *third* copy of the order-robust two-key `&`-split. Prefer to **extract a shared
two-key token splitter** that `QueryFeedTarget`, `UsaSpendingFeedTarget`, **and** `HiringFeedTarget` all route
through (keys as the per-caller hook) — that is the `radar-architecture-reviewer`-preferred path (the recurring
76/77/83 MEDIUM). If extracting it cleanly risks changing the two existing parsers' behaviour, ship the minimal
`HiringFeedTarget` mirroring their shape and **note the shared-splitter extraction as an explicit follow-up** in
the PR — do not silently paste a third divergent copy.

**Collector** `HiringBoardCollector : IEvidenceCollector`:

- `CollectorName => "hiring-ats"`, `SourceType => EvidenceSourceType.JobPosting`.
- Injected: `IEnumerable<IJobBoardReader>` (built once into a platform→reader map, Ordinal-ignore-case),
  `ILogger`, `TimeProvider`, `HiringCollectorOptions`.
- Iterate `context.FeedsOfType("hiringats")`. Per feed: `HiringFeedTarget.Parse(feed.Url)`; `null` ⇒
  `SourceFailure(feed.Name, feed.Url, "malformed hiringats feed token")` + Warning + continue. Look up the reader
  by `target.Platform`; missing ⇒ `SourceFailure(… "unsupported hiring platform '…'" )` + continue. `ReadAsync`;
  `!IsSuccess` ⇒ `SourceFailure` (+ `result.Detail`) + continue.
- On success compute `total = result.Result.Titles.Count`, `(senior, eng) = JobTitleClassifier.Classify(titles)`,
  `sample = titles.Take(options.MaxSampleTitles)`, and emit **one** `CollectedEvidence` (`MapToEvidence`):
  - **Title** embeds the verbatim phrase + counts, e.g.
    `"Hiring activity (open roles) — {total} open roles ({senior} senior/leadership, {eng} engineering/R&D) via {platform} board '{board}'"`.
  - **RawText** embeds the phrase + counts + board + retrieved timestamp for hash distinctness (NO raw titles),
    e.g. `"{platform} job board '{board}': {total} open roles as of {retrievedAtUtc:o}; {senior} senior/leadership, {eng} engineering/R&D. Signal: hiring activity (open roles)."`.
  - `SourceUrl` = the resolved board API URL (the reader can expose it, or rebuild it from platform+board);
    `PublishedAt` = `CollectedAt` = `TimeProvider.GetUtcNow()` (a live snapshot has no per-role publish date).
    **Each run therefore produces a distinct timestamped snapshot evidence** (the RawText timestamp makes the
    ContentHash distinct run-to-run) — this **is** the accrued hiring history slice B reads; state this
    explicitly.
  - **Metadata** (`Dictionary<string,string>`, Ordinal): `quality = "Medium"` (a company's own careers page —
    primary but unaudited, below SEC/USASpending `High`, matching news `Medium`); `hiringFeedUrl` (= `feed.Url`);
    `platform`; `board`; `totalRoles`; `seniorRoles`; `engRoles`; `sampleTitles` (the first N titles joined with
    `" | "` — provenance/debug only, **not** scanned by the extractor); `retrievedAtUtc` (round-trip `o`). These
    counts are the history slice B will read.
  - `CompanyHints = CollectorCompanyHints.For(feed.CompanyId, companiesById)`.
- Populate `CollectionSummary` exactly like `SecForm4Collector`/`UsaSpendingContractCollector`; log per-feed +
  aggregate.
- `HiringCollectorOptions`: `MaxSampleTitles` (default 5). No `UserAgent` required (the spike confirmed keyless
  Greenhouse/Lever access); if an endpoint later demands one, a polite generic UA may be added on the named
  clients — note it, don't block on it.

### 4. Feed-token seed (additive, `data/companies.json`)

Add one `hiringats` feed to the **four** verified companies (the 2026-07-06 reachability spike — record the
exact tokens/platforms here as the seed of record; the other 15 companies use Workday/iCIMS/custom boards with
no clean public JSON and are intentionally left out — partial coverage is normal and consistent with Radar's
opt-in-per-company feed model, exactly like `usaspending` covering only 3/19):

| Ticker | Company id | Platform | Board token | Feed url |
|---|---|---|---|---|
| MRCY | `885ea986-041f-4fc2-8163-b815ae930a78` | Greenhouse | `mercury` | `platform=greenhouse&board=mercury` |
| CVLT | `c29674f6-1409-4d91-8451-a5674fdb9f5c` | Greenhouse | `commvault` | `platform=greenhouse&board=commvault` |
| AGYS | `f0d50897-7161-40e6-a367-4ce63fc5aa8c` | Greenhouse | `agilysys` | `platform=greenhouse&board=agilysys` |
| ERII | `a825bf45-a23f-431c-b392-a04a029f2400` | Lever | `energyrecovery` | `platform=lever&board=energyrecovery` |

Feed `name` e.g. `"Mercury Systems — Open roles (Greenhouse ATS)"`. Additive edit only — leave every existing
feed untouched. Per spec 97 the feed-Id folds the feed **type**, so these do not collide with any existing feed.

### 5. Wiring (opt-in, OFF by default)

- `AddHiringBoardCollector(this IServiceCollection, HiringCollectorOptions options)` in
  `InfrastructureServiceCollectionExtensions.cs`: register **two** named typed `HttpClient`s (one per reader,
  each `ConfigurePrimaryHttpMessageHandler` with gzip/deflate `AutomaticDecompression`, mirroring
  `AddUsaSpendingContractCollector`), register **both** `IJobBoardReader` impls, `AddSingleton(options)`,
  `TryAddSingleton(TimeProvider.System)`, and `AddSingleton<IEvidenceCollector, HiringBoardCollector>()`.
- `RadarWorkerServices.cs`: add a `"hiringats"` enable-able kind branch (register via `AddHiringBoardCollector`
  from a new `RadarWorkerOptions.Hiring` block); extend **all three** valid-kinds messages (the empty-list, the
  null/blank-entry, and the unknown-kind messages) to include `"hiringats"`.
- `RadarWorkerOptions.cs`: add `HiringWorkerOptions Hiring { get; init; } = new();` (bound from `Radar:Hiring`)
  with `MaxSampleTitles` (default 5), mirroring the other `*WorkerOptions` blocks.
- `appsettings.json`: add a documented, disabled-by-default `Radar:Hiring` section; **leave `Radar:Collectors`
  default unchanged** (hiring stays opt-in).
- **`scripts/run-profiles/default.json`: do NOT add `"hiringats"` to `Collectors`.** It stays opt-in/off — a run
  enables it only by explicitly listing `hiringats`. State this clearly. (Promoting it into the baseline is a
  later deliberate fingerprint re-stamp — out of scope, see below.)

---

## Fingerprint + sequencing (load-bearing)

Shipping this bumps `KeywordSignalExtractor.RuleSetVersion` **v2 → v3**, which spec 95's `SignalSourceDescriptor`
folds into the scoring-config fingerprint **independently of which collectors are enabled**. Therefore:

- **The DEFAULT/baseline fingerprint re-stamps the moment this merges — even though `hiringats` is opt-in-OFF.**
  The default descriptor's enabled-collector CSV is **unchanged** (still the 6-collector set — `hiringats` is not
  in `default.json`); the fingerprint moves **solely** because `rules=radar-keyword-rules-v2` → `…-v3`. **There
  is no fingerprint-safe way to add a new scoring-affecting signal type — this is spec 95 working as intended.**
- **No `_formula.Version` bump** (formula shape unchanged — `radar-formula-v5` stays). Only `RuleSetVersion` bumps.
- **Efficacy interaction (spec 101) — this re-stamp is SCORE-NEUTRAL, a cosmetic boundary.** Because the
  collector is opt-in-OFF and the new rule matches only the hiring phrase (which no existing evidence contains),
  **every one of the 19 companies scores byte-identical before and after** — only the fingerprint *string*
  changes (v2→v3), not any actual score. So the efficacy score-vs-price *data* is fully continuous across this
  boundary (e.g. AEHR's trajectory is unchanged). The current renderer still *segments* on raw
  `ScoringConfigVersion` equality (AD-10), so it will draw a cosmetic gap here even though no real discontinuity
  exists — do not treat that as a scoring change or a reason to defer. The proper fix is the deferred efficacy
  **slice-2 improvement** (per-company score-continuity-aware segmentation: connect equal-value boundaries,
  annotate config changes as ticks) — tracked in the efficacy backlog, not this spec. State this plainly so the
  boundary is understood as an input-hash artifact, not a break in the measurement.
- **Re-pin the fingerprint by RUNNING the test, not by hand** (same procedure as spec 102 / M2): update the
  `RuleSetVersion` literals to `v3`, run `dotnet test`, read the **actual** default fingerprint the failing
  `Compute_DefaultConfig_MatchesPinnedFingerprint` reports, and paste that exact value in. Do **not** hand-compute
  a SHA. The full re-pin surface (grep-verified):
  - `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — the `RuleSetVersion` const (+ comment).
  - `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — the `SourceDescriptor` literal
    (line ~25) `…rules-v2…`→`…rules-v3…`; the `ChangedSignalSourceDescriptor` variant (line ~141) `…v2…`→`…v3…`;
    the pinned `radar-scoring-fp-8d638b90d4aa` (line ~98) → the NEW value from the run; and the lineage comment
    (lines ~92–93) noting this v2→v3 rules bump supersedes the spec-100 6-collector stamp.
  - `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs` — lines ~87/94/102 `…rules-v2…`→`…rules-v3…`.
  - `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs` — line ~673 `…rules-v2…`→`…rules-v3…`.
  - `tests/Radar.Infrastructure.Tests/FileSystem/FileScoringConfigStoreTests.cs` — the fixture descriptor
    (line ~15) `…rules-v2…`→`…rules-v3…` **if** it fails (it is an independent sample string; update for
    consistency).
  - `scripts/run-profiles/default.json` — the `_comment`'s `radar-keyword-rules-v2`→`v3` reference and the
    `radar-scoring-fp-8d638b90d4aa`→NEW fingerprint reference.
  - `docs/architecture-decisions.md` — add an AD-10 lineage line recording the `RuleSetVersion` v2→v3 bump (new
    `HiringActivity` rule group) and the default fingerprint transition (old → new), noting scoring math is
    byte-identical and the collector is opt-in-off.
- **Spec-98 collection-health check:** because `CollectionContext.SourceFeeds` is populated from **all** seeded
  feeds regardless of enabled collectors, a declared-but-disabled `hiringats` feed type has **declared == reached**
  ⇒ **no** `feeds-lost-before-collection` warning. **No `SeedFeedInventoryValidator` change is required.** Prove
  it with a test (below); the spec must NOT introduce spurious warnings on a default (hiring-off) run.

---

## Tests

- **`GreenhouseBoardReaderTests`** (offline, fake `HttpMessageHandler`): the Greenhouse `{"jobs":[…]}` fixture →
  correct `TotalRoles` (= parsed job count) + titles; empty `jobs` array → `Success` with 0 roles; missing
  `jobs` / root-not-object → `Malformed`; entries with a blank/absent `title` skipped; non-success status →
  `HttpError`; thrown `TaskCanceledException` (timeout) → `Timeout`; `HttpRequestException` → `Unreachable`;
  caller cancellation re-throws. **No network.**
- **`LeverBoardReaderTests`** (offline): the Lever top-level-array `[{"text":…}]` fixture → correct total +
  titles; empty array → `Success` 0 roles; root-not-array → `Malformed`; missing `text` skipped; same
  failure-mode ladder.
- **`JobTitleClassifierTests`**: senior keywords (`VP`, `Vice President`, `Chief`, `Head of`, `Director`,
  `Principal`) and engineering keywords (`Engineer`, `Engineering`, `R&D`, `Research`, `Scientist`) each counted
  case-insensitively; a title counting toward both buckets; a title counting toward neither; empty list → (0,0).
- **`HiringFeedTargetTests`**: `platform=greenhouse&board=mercury` and the key-reversed order both parse; missing
  key / blank value / blank token → `null`; whitespace trimmed. (If the shared splitter is extracted, add its
  parity tests and keep `QueryFeedTarget`/`UsaSpendingFeedTarget` tests green.)
- **`HiringBoardCollectorTests`** (fake `IJobBoardReader`s): a successful board → **one** `JobPosting`
  `CollectedEvidence` whose Title/RawText contain the verbatim `hiring activity (open roles)` phrase and the
  counts (and **no** raw job titles); metadata carries `platform`/`board`/`totalRoles`/`seniorRoles`/`engRoles`/
  `sampleTitles`/`retrievedAtUtc`/`quality=Medium`; `CompanyHints` = feed-bound ticker; UTC instants from the
  injected `TimeProvider`; a malformed token, an unsupported platform, and a reader failure each degrade to a
  `SourceFailure`/no-evidence without throwing; `CollectionSummary` counts correct; deterministic order.
- **Extractor mapping** (extend `KeywordSignalExtractorTests`): `JobPosting` evidence whose text contains
  `hiring activity (open roles)` → exactly one `HiringActivity` **Neutral** signal at the chosen strength
  (3/4/0.45), with a verbatim excerpt; and a guard that the phrase does **not** also fire an unrelated rule.
- **Fingerprint re-pin**: `ScoringConfigFingerprintTests` green with the `RuleSetVersion`-v3 descriptor and the
  NEW pinned default fingerprint (obtained by running); `SignalSourceDescriptorTests`, `ScoringEngineTests`,
  `FileScoringConfigStoreTests` updated to v3 where they pin it.
- **Default (hiring-off) composition / no false warning**: a Worker-DI or runner test with the default
  `Collectors` (no `hiringats`) but the seed containing the 4 `hiringats` feeds registers the collector set
  **without** `hiring-ats` and produces **no** `feeds-lost-before-collection` warning for the `hiringats` feed
  type (regression guard for the spec-98 interaction). If a Worker-DI test asserts the enable-able kind list,
  extend it to include `"hiringats"`.
- **Seed**: a `LocalFileCompanySeedSource` test asserts the 4 companies carry a `hiringats` feed with the exact
  tokens, each with a distinct feed Id (does not collide with the same company's other feeds — spec 97).
- Existing tests (RSS/SEC/Form4/13DG/USASpending/News collectors, runner merge, DI list, Worker DI, extractor,
  scoring) stay green. Full gate: `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based classification; **NO AI**.
- All HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8). Reuse
  `CollectorCompanyHints`, `CollectionSummary`/`SourceFailure`, the `CollectedEvidence`/`CollectedEvidenceMapper`
  path, and the established non-SEC reader + feed-token-parser patterns — **no duplicated HTTP/parse primitives**
  (extract the shared two-key token splitter, or record it as a follow-up). Do **not** reuse SEC-specific helpers.
- Graceful degradation: typed non-throwing outcomes; a board with zero openings is a valid `Success` (not an
  error); a bad token / unsupported platform / unreachable board yields a `SourceFailure` + zero evidence; only
  genuine caller cancellation propagates.
- **Provenance preserved** (board API → `JobPosting` evidence with the board URL + counts → `HiringActivity`
  signal → score). **No advice language** (AD-9): the factual "hiring activity (open roles)" phrasing carries no
  recommendation; direction is internal to scoring (and is Neutral in v1).
- Store timestamps in UTC; IDs `Guid`. Keep `MaxSampleTitles` bounded. Sample titles are metadata-only and never
  enter the extractor's searchable text (no keyword contamination).
- **Only `RuleSetVersion` bumps** (v2→v3); **no** `_formula.Version` / weight / attention-tier / insider-tier
  change. `ScoringConfigVersion` re-stamps automatically via `SignalSourceDescriptor`; re-pin the tests to the
  value obtained by RUNNING them.

---

## Out of scope / future slices (record, do not build)

- **Slice B — directional surge detection** (Positive when open-role / senior-role counts *accelerate* vs the
  accrued hiring-evidence history this slice writes). Reads the timestamped evidence metadata; changes the
  `HiringActivity` **direction**, not the type name. No separate history store needed.
- **Ashby platform** (no hits in the spike) and **additional companies** via manual careers-page slug discovery
  (Workday/iCIMS/custom boards for the other 15 names). Additive seed edits later.
- **Any per-company board-token auto-discovery** (no authoritative company→board-token map exists; tokens are
  hand-verified).
- **AI role-function classification** (beyond the small deterministic keyword buckets) — a future
  `Microsoft.Extensions.AI` slice.
- **Promoting `hiringats` into the baseline `default.json`** — a later deliberate fingerprint re-stamp, after a
  live measurement validates the axis (mirrors how `secform4`/`sec13dg` were promoted).
- **Extracting the shared two-key `&`-token splitter** across `QueryFeedTarget`/`UsaSpendingFeedTarget`/
  `HiringFeedTarget` — do it here if cheap; otherwise a scoped follow-up.

---

## Acceptance criteria

- [ ] `SignalType.HiringActivity` added (adjacent to `HiringExpansion`, which is left untouched); persisted by
      name; no exhaustive `SignalType` switch broken.
- [ ] One `KeywordSignalExtractor` rule maps the verbatim phrase `hiring activity (open roles)` →
      `HiringActivity` **Neutral** (3/4/0.45); no materiality metadata read; the phrase does not trip another
      rule; `RuleSetVersion` bumped `radar-keyword-rules-v2` → `v3`.
- [ ] `HiringBoardCollector` (kind `"hiringats"`, `CollectorName "hiring-ats"`, `SourceType JobPosting`) reads
      `FeedsOfType("hiringats")`, dispatches by platform to `GreenhouseBoardReader`/`LeverBoardReader` behind
      `IJobBoardReader`, normalizes both JSON shapes to `JobBoardResult`, computes total + senior + engineering
      counts via `JobTitleClassifier`, and emits **one** `JobPosting` evidence per company with the fixed phrase
      in Title/RawText (no raw titles) + rich metadata (platform, board, the three counts, sample titles,
      retrievedAtUtc, `quality=Medium`) + feed-bound hint.
- [ ] Readers return typed non-throwing outcomes (empty board = `Success` 0 roles); malformed token / unsupported
      platform / reader failure degrade to `SourceFailure` + zero evidence; caller cancellation propagates.
- [ ] `HiringFeedTarget` parses `platform=…&board=…` (order-robust, `null` on malformed); the shared two-key
      splitter is either extracted (all three parsers routed through it) or the extraction is recorded as a
      follow-up in the PR.
- [ ] Additively registered via `AddHiringBoardCollector`; enable-able by `Radar:Collectors` containing
      `"hiringats"`; **default `Radar:Collectors` and `scripts/run-profiles/default.json` unchanged (opt-in
      OFF)**; the 4 verified `hiringats` feeds seeded in `data/companies.json` with the exact tokens, each with a
      distinct feed Id (spec 97).
- [ ] A default (hiring-off) run/composition registers the collector set **without** `hiring-ats` and produces
      **no** `feeds-lost-before-collection` warning for the declared `hiringats` feeds (spec-98 interaction; no
      validator change).
- [ ] **No `_formula.Version`/weight/tier change; only `RuleSetVersion` v2→v3.** The default fingerprint
      re-stamps automatically; `ScoringConfigFingerprintTests` (and the other v2-pinning tests) re-pinned to the
      value obtained by **running** the test; `default.json` `_comment` + `architecture-decisions.md` AD-10
      lineage updated; the efficacy-segment boundary is noted.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
