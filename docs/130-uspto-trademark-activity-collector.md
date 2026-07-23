# Task: USPTO Trademark activity collector (leading go-to-market / brand signal)

> **COLLECTOR-EXPANSION SLICE #4 — closes the documented expansion arc.** Follows
> `docs/radar-full-pipeline-spec.md` (*Patent (127) → FCC (128) → FDA (129) → USPTO Trademark*, sequenced
> because each bumps `RuleSetVersion` and re-pins the same scoring fingerprint). A newly-filed **trademark**
> registers a brand/product name **before** launch — it sits **closer to go-to-market than patents** and
> **reuses almost all of the patent collector's plumbing**. Free public USPTO API, Neutral count-based rule.
> This is a **single cohesive slice** (~2 h — the smallest of the four, because it is the patent collector's
> near-twin): the extractor rule + Infrastructure collector + reader seam + feed-token parser + seed +
> wiring land together because the collector's emitted phrase and the extractor rule are a **verbatim
> contract** and must ship as one unit (the exact pattern of specs 103/127/128/129). It is **opt-in / OFF by
> default** — the baseline run does not enable it.

## Overview

A new deterministic collector (`IEvidenceCollector`, config kind `"trademarks"`, `CollectorName
"trademarks"`, `EvidenceSourceType.Trademark`) reads each seeded company's trademark feed (a token carrying
that company's **owner/applicant organization name** to query), counts recently-filed trademark
applications in a bounded lookback window, and emits **one** `Trademark` `CollectedEvidence` per company
carrying a **fixed trademark-activity phrase** in Title/RawText plus rich provenance metadata (owner query,
filing-count, window, a few sample serial numbers + mark texts, retrieved timestamp). A new
`KeywordSignalExtractor` rule maps that phrase to a **Neutral** `SignalType.TrademarkActivity` signal.

**Two new append-only Domain slots** are added at the end of their enums: `EvidenceSourceType.Trademark`
and `SignalType.TrademarkActivity` (never reorder/remove; persisted by name). `Trademark` is **first-party**
(a company's own trademark filing is not third-party market attention) — it is **not** added to
`EvidenceSourceTypes.IsThirdPartyAttentionSource`, so it contributes nothing to Attention reach — correct.

**v1 is deliberately Neutral, not directional** (mirrors the patent decision exactly — spec 127). A
single-window trademark-filing COUNT cannot distinguish a company whose brand activity is genuinely
*accelerating* from a large brand-heavy incumbent that *always* files many marks; a mild-Positive on raw
count would systematically favour the most-prolific filers — the selection-bias trap the diversified
universe avoids. So v1 **establishes the mechanism** end-to-end (provenance: USPTO trademark API →
`Trademark` evidence → `TrademarkActivity` signal → score), lifts source diversity / Evidence / Velocity
breadth, and **accrues the filing counts in timestamped evidence metadata** — without misfiring Trajectory
(Neutral contributes 0 to `TrajectoryScore`). **Directional surge detection** (Positive when the filing
count *accelerates* vs the accrued trademark-evidence history this slice writes) is a deferred **slice B**
that changes the DIRECTION, not the type name — no separate history store is built now.

This threads the normal `collect → map → resolve → review → store → score → report` path, so provenance is
intact end-to-end. Opt-in via `Radar:Collectors`; the default baseline run is byte-for-byte unchanged **in
scoring math** (see the Fingerprint section for the one automatic re-stamp).

---

## Assignment

Worktree: any — mostly new Infrastructure files under a new `Trademarks/` folder + one additive extractor
rule + two append-only Domain enum members + additive DI + one enable-able collector kind + seed data. It
edits shared surfaces (`SignalType.cs`, `EvidenceSourceType.cs`, `KeywordSignalExtractor.cs`,
`RadarWorkerServices.cs`, `RadarWorkerOptions.cs`, `appsettings.json`,
`InfrastructureServiceCollectionExtensions.cs`, `data/companies.json`, and the fingerprint/descriptor
tests), so **sequence** it rather than parallelizing against any slice that touches the extractor rule
table, `SignalType`/`EvidenceSourceType`, the scoring fingerprint, Worker composition/DI, or the seed.
Dependencies: **129 (FDA — must be MERGED first)** because the fingerprint re-pin is computed from the
post-129 tree (`RuleSetVersion` v6 → v7) and this slice routes its feed-token parser through the shared
`SingleKeyFeedToken` (spec 128). Also **127 (patents — merged)**, whose `HttpPatentSearchReader` /
`PatentActivityCollector` this slice mirrors almost exactly (it is the patent collector's near-twin). Also
103, 95, 97, 98 (all merged).
Conflicts with: **specs 127, 128, 129** (all bump `RuleSetVersion` and re-pin the same fingerprint) and any
slice touching `KeywordSignalExtractor` rules / `RuleSetVersion`, `SignalType`/`EvidenceSourceType`,
`ScoringConfigFingerprint`/its tests, Worker composition/DI, or the seed. **Do NOT dispatch in parallel with
any other `RuleSetVersion`-bumping collector.**
Estimated time: ~2 h

---

## Grounding facts to VERIFY (a short reachability spike) before dispatch

A new **external** source — confirm before/at implementation (as specs 103/127/128/129 required). The
collector + reader + parser + wiring + tests are **fully offline** (fake `ITrademarkSearchReader`, JSON
fixtures), so the coder can complete the whole slice against fixtures; only the live **endpoint/response
schema**, any **API-key handling**, and the **seed owner names** are the live gate.

- **Endpoint (USPTO trademark data).** Confirm the currently-reachable free machine form in the spike —
  candidates: the USPTO **Open Data Portal (ODP) trademark API**, the **TSDR** API, or the trademark
  **bulk/search** endpoints. **Confirm the base URL, request shape, and response envelope**, and whether an
  **API key** is required (several USPTO ODP endpoints now require a free key). Capture per filing: the
  **serial number**, the **mark text/wordmark**, and the **filing date**. Pin field/operator names as named
  constants; if the schema differs, adjust the reader parse + fixtures to the **observed** shape — the
  collector/extractor/wiring do not change.
- **Query.** Filter by owner/applicant organization name and a filing-date floor (last `LookbackDays`,
  default **365** — trademark filings are lower-frequency than press, a longer window avoids mostly-empty
  snapshots). Request only the fields the evidence needs. Cap at a bounded page; keep the **parsed page
  count** as `FilingCount` (deterministic from the payload); record any API-reported total in metadata as a
  cross-check.
- **Key handling (no secret in source — SEC-UA / DEEPINFRA / spec-127 precedent).** If the spike shows the
  reachable endpoint needs a key, config names the env var `Radar:Trademarks:ApiKeyEnvVar` (default e.g.
  `USPTO_API_KEY`), read at runtime; a **missing/blank key degrades every trademark feed to a
  `SourceFailure`** (logged Warning, zero evidence, `MissingApiKey` outcome) — it does **not** throw and does
  **not** affect the baseline (opt-in OFF). If no key is required, omit the key plumbing. Document exactly
  like the SEC User-Agent / spec-127 patent key.
- **Owner names (seed).** The per-company token is the company's **owner/applicant organization name** as it
  appears on trademark filings (often the legal entity). Verify **3–4** names that return non-empty results
  and seed only those; partial coverage is normal and expected (exactly like `usaspending`/`patents`). First
  candidates to verify: **WDFC** (WD-40 Company — a brand-heavy consumer name, ideal for trademarks),
  **HRL** (Hormel Foods), **SHOO** (Steven Madden, Ltd.), **RKLB** (Rocket Lab USA, Inc.).
- **Politeness.** Generic `User-Agent` on the named `HttpClient`; small bounded page size (the count matters,
  not full enumeration). No pagination beyond the first bounded page; cap at the reader's page size and note
  "≥ N" if the API reports a larger total.

---

## The signal decision (settled — encode as fixed; mirrors the patent Neutral decision exactly)

**v1 emits a NEUTRAL `TrademarkActivity` signal, not directional.** Rationale to state in code comments and
the PR (identical shape to spec 127):

- A **single-window trademark-filing COUNT cannot distinguish** genuine brand-activity *acceleration* from a
  large brand-heavy incumbent that *always* files many marks. A mild-Positive on raw count would
  systematically favour the most-prolific filers — the selection-bias trap the diversified universe avoids.
- So v1 **establishes the mechanism** (the trademark axis + `Trademark` source-type provenance + Evidence /
  Velocity breadth) and **accrues the counts in timestamped evidence metadata**, **without** moving
  Trajectory (Neutral contributes 0 to `TrajectoryScore`).
- **Directional surge detection** (Positive when the filing count *accelerates* vs the accrued
  trademark-evidence history) is **deferred to slice B**. The timestamped evidence metadata this slice
  writes **is** the record slice B will read. Same conservative-first pattern as patents (spec 127), FCC
  (spec 128 v1), the hiring Neutral (spec 103), and the 13G Neutral (spec 99).
- **Name is `TrademarkActivity`** on purpose (honest — it is *activity*, not a proven surge). **Slice B
  changes the DIRECTION, not the type name.**

---

## Design

### 1. Domain — two append-only enum members (minimal)

- `EvidenceSourceType.Trademark` appended at the **end** of `EvidenceSourceType` (append-only; persisted by
  name; never reorder/remove). Do **not** add it to `EvidenceSourceTypes.IsThirdPartyAttentionSource`
  (first-party filing, not market attention).
- `SignalType.TrademarkActivity` appended at the **end** of `SignalType`.
- Confirm no exhaustive `switch` over `SignalType`/`EvidenceSourceType` needs a new arm.

### 2. Extractor rule — one Neutral phrase (verbatim contract)

Add **one** rule group to the `KeywordSignalExtractor` `Rules` table (after the `RegulatoryApproval` row from
spec 129), mapping the single fixed phrase the collector emits to `TrademarkActivity` **Neutral** at routine
strength (mirror the Neutral routine rows — patents `3/5/0.45`, hiring `3/4/0.45`):

```csharp
// TrademarkActivity (USPTO trademark API; spec 130). The TrademarkActivityCollector synthesizes exactly
// this fixed phrase into the Trademark evidence Title/RawText; the extractor only maps phrase -> fixed
// type+direction+strength (never re-derives valence — the PatentActivity precedent, of which this is the
// near-twin). v1 is NEUTRAL by design: a single-window filing COUNT cannot tell genuine brand-activity
// acceleration from an always-prolific filer, so it never misfires bullish (Neutral contributes 0 to
// Trajectory). Directional SURGE detection vs accrued history is deferred to slice B (changes DIRECTION,
// not this type name). NO raw mark texts in searchable text (metadata only).
new("trademark activity (recent filings)", SignalType.TrademarkActivity, SignalDirection.Neutral, 3, 5, 0.45m),
```

- **ONE phrase** (simplest; keeps the verbatim contract tiny).
- Bump **`KeywordSignalExtractor.RuleSetVersion`** `"radar-keyword-rules-v6"` → **`"radar-keyword-rules-v7"`**
  and update the surrounding comment.
- **No-contamination rule:** the collector embeds **only** the fixed phrase + numeric count + owner/window in
  Title/RawText — **never raw mark texts** (a wordmark like "LAUNCHPAD" or "NEW HORIZON" could otherwise trip
  the `launches`/`new platform` rules). Sample serial numbers + mark texts live in **metadata only** (the
  extractor does not scan metadata for phrases). State this in both the collector and the rule comment.

### 3. Infrastructure — reader seam + collector (new `Radar.Infrastructure/Trademarks/` folder)

**Reader seam** (`internal`, mirrors `HttpPatentSearchReader` almost exactly — this is the patent
collector's near-twin):

- `TrademarkFiling(string SerialNumber, string MarkText, DateOnly FilingDate)` — the normalized per-filing
  shape.
- `TrademarkSearchResult(int FilingCount, IReadOnlyList<TrademarkFiling> Filings)` — `FilingCount` is the
  count of parsed filings in the returned page (deterministic from the payload); any API-reported larger
  total goes in metadata as a cross-check only.
- `TrademarkSearchOutcome` enum: `Success, Unreachable, HttpError, Timeout, Malformed` (+ `MissingApiKey`
  **only if** the spike proves a key is required; mirror spec 127's `PatentSearchOutcome`).
- `TrademarkSearchReadResult(TrademarkSearchOutcome Outcome, TrademarkSearchResult? Result, string? Detail)`
  with `IsSuccess` + `Success(...)`/`Failure(...)` factories.
- `ITrademarkSearchReader { Task<TrademarkSearchReadResult> ReadAsync(string ownerName, DateOnly filingFloor, CancellationToken ct); }`.
- `HttpTrademarkSearchReader : ITrademarkSearchReader`: injected typed `HttpClient` + `System.Text.Json`,
  builds the USPTO request (verified in the spike, operators pinned as constants), sends the key header from
  the env var if required (blank ⇒ `MissingApiKey`, no HTTP call), `HttpCompletionOption.ResponseHeadersRead`,
  materialize body before dispose, then the standard ladder: `HttpRequestException`→`Unreachable`,
  `TaskCanceledException`(non-ct)→`Timeout`, caller-cancellation re-throw
  (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`), non-success status→
  `HttpError`, `JsonException` / root-not-object / missing results array→`Malformed`, empty results→`Success`
  with 0 filings (a valid no-recent-filings result, not an error). All HTTP/JSON stays in
  `Radar.Infrastructure` (AD-5); all new types `internal`.

**Feed-token parser (reuse the shared single-key splitter — HARD RULE).**
`TrademarkFeedTarget(string OwnerName)` (`internal sealed record`, in `Radar.Infrastructure.Trademarks`):
`Parse(string?) -> TrademarkFeedTarget?` for the token `owner=<name>`, routed through the shared
`SingleKeyFeedToken.TrySplit` (spec 128) — do NOT paste a divergent copy. Trimming/blank-null discipline as
an explicit per-caller hook; malformed/blank ⇒ `null` so the collector degrades to a `SourceFailure`.

**Collector** `TrademarkActivityCollector : IEvidenceCollector`:

- `CollectorName => "trademarks"`, `SourceType => EvidenceSourceType.Trademark`.
- Injected: `ITrademarkSearchReader`, `ILogger`, `TimeProvider`, `TrademarkCollectorOptions`.
- Iterate `context.FeedsOfType("trademarks")` (deterministic order). Build `companiesById`. Per feed:
  `TrademarkFeedTarget.Parse(feed.Url)`; `null` ⇒ `SourceFailure(feed.Name, feed.Url, "malformed trademark
  feed token")` + Warning + continue. Compute `filingFloor = today - options.LookbackDays` (default 365) via
  the injected `TimeProvider`. `ReadAsync(target.OwnerName, filingFloor, ct)`; `!IsSuccess` ⇒ `SourceFailure`
  (+ `result.Detail`) + continue (a `MissingApiKey` outcome, if applicable, is logged clearly).
- On success compute `count = result.Result.FilingCount`,
  `sample = result.Result.Filings.Take(options.MaxSampleMarks)`, and emit **one** `CollectedEvidence`
  (`MapToEvidence`):
  - **Title** embeds the verbatim phrase + count, e.g.
    `"Trademark activity (recent filings) — {count} trademark applications filed by '{owner}' in the last {lookbackDays} days"`.
  - **RawText** embeds the phrase + count + owner + window + retrieved timestamp for hash distinctness (NO raw
    mark texts), e.g. `"Owner '{owner}': {count} trademark applications filed since {filingFloor:o}, as of
    {retrievedAtUtc:o}. Signal: trademark activity (recent filings)."`.
  - `SourceUrl` = a stable human-viewable USPTO/owner link if available from the spike, else the query URL;
    `PublishedAt` = `CollectedAt` = `TimeProvider.GetUtcNow()`. **Each run produces a distinct timestamped
    snapshot** (the RawText timestamp makes the ContentHash distinct run-to-run) — this **is** the accrued
    trademark history slice B reads; state this explicitly.
  - **Metadata** (`Dictionary<string,string>`, Ordinal): `quality = "High"` (USPTO filings are an
    authoritative public-record source, on par with SEC/USASpending `High`); `trademarkFeedUrl` (= `feed.Url`);
    `owner`; `filingCount`; `lookbackDays`; `filingFloor` (`o`/`yyyy-MM-dd`); `sampleMarks` (the first N
    `"{serialNumber}: {markText}"` joined with `" | "` — provenance/debug only, **not** scanned by the
    extractor); `apiReportedTotal` (optional cross-check); `retrievedAtUtc` (`o`).
  - `CompanyHints = CollectorCompanyHints.For(feed.CompanyId, companiesById)` — never invent a ticker.
- Populate `CollectionSummary` exactly like `PatentActivityCollector`/`FccEquipmentAuthorizationCollector`/
  `FdaClearanceCollector` (checked, ok, failed, count, failures); log per-feed + aggregate.
- `TrademarkCollectorOptions`: `LookbackDays` (default 365), `MaxSampleMarks` (default 5), `MaxPageSize`
  (default 100). (Add `ApiKeyEnvVar` (default `"USPTO_API_KEY"`) only if the spike proves a key is required.)

### 4. Feed-token seed (additive, `data/companies.json`)

Add one `trademarks` feed to the **3–4** companies verified in the reachability spike (record the exact owner
names here as the seed of record; leave the other names out — partial coverage is normal, exactly like
`usaspending`/`patents`/`fda`). Token form `owner=<verified owner organization name>`; feed `name` e.g.
`"WD-40 — Recent trademark filings (USPTO)"`. Additive edit only — leave every existing feed untouched. Per
spec 97 the feed-Id folds the feed **type**, so these do not collide with any existing feed (incl. the same
company's `patents` feed).

### 5. Wiring (opt-in, OFF by default)

- `AddTrademarkActivityCollector(this IServiceCollection, TrademarkCollectorOptions options)` in
  `InfrastructureServiceCollectionExtensions.cs`: register a named typed `HttpClient`
  (`ConfigurePrimaryHttpMessageHandler` with gzip/deflate `AutomaticDecompression`, generic `User-Agent`,
  mirroring `AddPatentActivityCollector`), register `ITrademarkSearchReader`, `AddSingleton(options)`,
  `TryAddSingleton(TimeProvider.System)`, and `AddSingleton<IEvidenceCollector, TrademarkActivityCollector>()`.
- `RadarWorkerServices.cs`: add a `"trademarks"` enable-able kind branch (register via
  `AddTrademarkActivityCollector` from a new `RadarWorkerOptions.Trademarks` block); extend **all three**
  valid-kinds messages (the empty-list, the null/blank-entry, and the unknown-kind messages) to include
  `"trademarks"`.
- `RadarWorkerOptions.cs`: add `TrademarkWorkerOptions Trademarks { get; init; } = new();` (bound from
  `Radar:Trademarks`) with `LookbackDays` (365), `MaxSampleMarks` (5), `MaxPageSize` (100) (+ `ApiKeyEnvVar`
  if required), mirroring the other `*WorkerOptions` blocks.
- `appsettings.json`: add a documented, disabled-by-default `Radar:Trademarks` section (note the key is read
  from the named env var if required, never committed); **leave `Radar:Collectors` default unchanged**
  (trademarks stays opt-in).
- **`scripts/run-profiles/default.json`: do NOT add `"trademarks"` to `Collectors`.** It stays opt-in/off.
  Promoting it into the baseline is a later deliberate fingerprint re-stamp after a live measurement
  validates the axis — out of scope.

---

## Fingerprint + sequencing (load-bearing)

Shipping this bumps `KeywordSignalExtractor.RuleSetVersion` **v6 → v7**, which spec 95's
`SignalSourceDescriptor` folds into the scoring-config fingerprint **independently of which collectors are
enabled**. Therefore:

- **The DEFAULT/baseline fingerprint re-stamps the moment this merges — even though `trademarks` is
  opt-in-OFF.** The default descriptor's enabled-collector CSV is **unchanged** (still the 6-collector set);
  the fingerprint moves **solely** because `rules=radar-keyword-rules-v6` → `…-v7`. There is no
  fingerprint-safe way to add a new scoring-affecting signal type — spec 95 working as intended (as specs
  103/127/128/129 re-stamped).
- **No `_formula.Version` bump** (`radar-formula-v8` stays). Only `RuleSetVersion` bumps. No weight /
  attention-tier / insider-tier change.
- **Efficacy interaction (spec 101/108) — this re-stamp is SCORE-NEUTRAL, a cosmetic boundary.** The
  collector is opt-in-OFF and the new rule matches only the trademark phrase (which no existing evidence
  contains), so **every company scores byte-identical before and after** — only the fingerprint *string*
  changes. Spec 108's continuity-aware segmentation connects the score line across a score-continuous
  re-stamp; state plainly that this boundary is an input-hash artifact, not a measurement break.
- **Re-pin the fingerprint by RUNNING the test, not by hand** (same procedure as specs 103/127/128/129):
  update the `RuleSetVersion` literal to v7, run `dotnet test`, read the **actual** default fingerprints the
  failing pins report, and paste those exact values in. Do **not** hand-compute a SHA. The re-pin surface
  (grep-verify against the post-129 tree before editing):
  - `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — the `RuleSetVersion` const (+ comment).
  - `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — the `…rules-v6…` descriptor
    literal → `…rules-v7…`; the `ChangedSignalSourceDescriptor` variant; the pinned **AI-OFF** default
    fingerprint → the NEW value from the run; the lineage comment.
  - `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs` — `…rules-v6…` → `…rules-v7…`.
  - `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs` — any `…rules-v6…` literal.
  - `tests/Radar.Infrastructure.Tests/FileSystem/FileScoringConfigStoreTests.cs` — the fixture descriptor
    `…rules-v6…` → `…rules-v7…` **if** it fails.
  - `scripts/run-profiles/default.json` — the `_comment`'s `radar-keyword-rules-v6` → `v7` reference and the
    AI-OFF / AI-ON default-fingerprint references (re-pin from the RUN, never by hand; note the AI-ON
    transition in the comment if no test pins it live).
  - `docs/architecture-decisions.md` — add an AD-10 lineage line recording `RuleSetVersion` v6→v7 (new
    `TrademarkActivity` rule group) and the default fingerprint transition(s) (old → new), noting scoring math
    is byte-identical and the collector is opt-in-off (mirror the spec-103/127/128/129 lineage entries).
- **Spec-98 collection-health check:** a declared-but-disabled `trademarks` feed type has **declared ==
  reached** ⇒ **no** `feeds-lost-before-collection` warning. **No `SeedFeedInventoryValidator` change.** Prove
  it with a test; introduce no spurious warning on a default (trademarks-off) run.

---

## Tests

- **`HttpTrademarkSearchReaderTests`** (offline, fake `HttpMessageHandler`): a results fixture → correct
  `FilingCount` + serial numbers + mark texts + filing dates; empty results → `Success` with 0 filings;
  missing results / root-not-object → `Malformed`; a blank configured API key (if key required) →
  `MissingApiKey` **with no HTTP call**; non-success status → `HttpError`; thrown `TaskCanceledException`
  (timeout) → `Timeout`; `HttpRequestException` → `Unreachable`; caller cancellation re-throws. **No
  network.**
- **`TrademarkFeedTargetTests`**: `owner=WD-40 Company` parses (value may contain spaces/hyphens); missing
  key / blank value / blank token → `null`; whitespace trimmed; routed through the shared `SingleKeyFeedToken`.
- **`TrademarkActivityCollectorTests`** (fake `ITrademarkSearchReader`): a successful search → **one**
  `Trademark` `CollectedEvidence` whose Title/RawText contain the verbatim `trademark activity (recent
  filings)` phrase and the count (and **no** raw mark texts); metadata carries
  `owner`/`filingCount`/`lookbackDays`/`filingFloor`/`sampleMarks`/`retrievedAtUtc`/`quality=High`;
  `CompanyHints` = feed-bound ticker; UTC instants from the injected `TimeProvider`; `filingFloor` = now −
  `LookbackDays`; a malformed token and a reader failure each degrade to a `SourceFailure`/no-evidence
  without throwing; `CollectionSummary` counts correct; deterministic order.
- **Extractor mapping** (extend `KeywordSignalExtractorTests`): `Trademark` evidence whose text contains
  `trademark activity (recent filings)` → exactly one `TrademarkActivity` **Neutral** signal at 3/5/0.45,
  with a verbatim excerpt; and a guard that the phrase does **not** also fire an unrelated rule (and that a
  sample mark text placed only in metadata does not leak into matching).
- **Fingerprint re-pin**: `ScoringConfigFingerprintTests` green with the `RuleSetVersion`-v7 descriptor and
  the NEW pinned default fingerprint(s) (obtained by running); `SignalSourceDescriptorTests`,
  `ScoringEngineTests`, `FileScoringConfigStoreTests` updated to v7 where they pin it.
- **Default (trademarks-off) composition / no false warning**: a Worker-DI or runner test with the default
  `Collectors` (no `trademarks`) but the seed containing the `trademarks` feeds registers the collector set
  **without** `trademarks` and produces **no** `feeds-lost-before-collection` warning for the `trademarks`
  feed type. If a Worker-DI test asserts the enable-able kind list, extend it to include `"trademarks"`.
- **Seed**: a `LocalFileCompanySeedSource` test asserts the verified companies carry a `trademarks` feed with
  the exact owner tokens, each with a distinct feed Id (does not collide with the same company's other feeds
  — spec 97).
- Existing tests (all collectors, runner merge, DI list, Worker DI, extractor, scoring) stay green. Full
  gate: `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based mapping; **NO AI**.
- All HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8). Reuse
  `CollectorCompanyHints`, `CollectionSummary`/`SourceFailure`, the `CollectedEvidence`/
  `CollectedEvidenceMapper` path, the established non-SEC reader pattern (mirror `HttpPatentSearchReader`),
  and the shared `SingleKeyFeedToken` (spec 128) — **no duplicated HTTP/parse/token primitives**. Do **not**
  reuse SEC-specific helpers.
- Append-only Domain enum edits (add at the END; never reorder/remove; persisted by name).
- **Secret handling (if a key is required):** the API key is read at runtime from the env var named in
  config and is **never** written to source, logged, or committed; a missing key degrades the collector to
  `SourceFailure`s (opt-in OFF ⇒ baseline untouched). Same posture as the SEC User-Agent / spec-127 key.
- Graceful degradation: typed non-throwing outcomes; a company with zero recent filings is a valid `Success`
  (not an error); a bad token / missing key / unreachable API yields a `SourceFailure` + zero evidence; only
  genuine caller cancellation propagates.
- **Provenance preserved** (USPTO trademark API → `Trademark` evidence with owner + count + window →
  `TrademarkActivity` signal → score). **No advice language** (AD-9): the factual "trademark activity (recent
  filings)" phrasing carries no recommendation; direction is internal to scoring (and Neutral in v1).
- Store timestamps in UTC; IDs `Guid`. Keep `MaxSampleMarks`/`MaxPageSize` bounded. Sample mark texts are
  metadata-only and never enter the extractor's searchable text.
- **Only `RuleSetVersion` bumps** (v6→v7); **no** `_formula.Version` / weight / tier change.
  `ScoringConfigVersion` re-stamps automatically via `SignalSourceDescriptor`; re-pin the tests to the value
  obtained by RUNNING them.

---

## Out of scope / future slices (record, do not build)

- **Slice B — directional surge detection** (Positive when the filing count *accelerates* vs the accrued
  trademark-evidence history this slice writes). Changes the `TrademarkActivity` **direction**, not the type
  name. No separate history store needed.
- **Promoting `trademarks` into the baseline `default.json`** — a later deliberate fingerprint re-stamp after
  a live measurement validates the axis.
- **Nice/goods-and-services-class enrichment** (which product/service classes a mark covers — a richer
  go-to-market read) — future slice.
- **Additional owner names / subsidiary de-duplication** — additive seed edits later.

---

## Acceptance criteria

- [ ] Two append-only Domain members added at the END of their enums: `EvidenceSourceType.Trademark` (NOT in
      `IsThirdPartyAttentionSource`) and `SignalType.TrademarkActivity`; no exhaustive
      `SignalType`/`EvidenceSourceType` switch broken.
- [ ] One `KeywordSignalExtractor` rule maps the verbatim phrase `trademark activity (recent filings)` →
      `TrademarkActivity` **Neutral** (3/5/0.45); the phrase does not trip another rule; `RuleSetVersion`
      bumped `radar-keyword-rules-v6` → `v7`.
- [ ] `TrademarkActivityCollector` (kind `"trademarks"`, `CollectorName "trademarks"`, `SourceType
      Trademark`) reads `FeedsOfType("trademarks")`, parses the `owner=…` token via the shared
      `SingleKeyFeedToken`, reads recent filings via `ITrademarkSearchReader` (USPTO), and emits **one**
      `Trademark` evidence per company with the fixed phrase in Title/RawText (no raw mark texts) + rich
      metadata (owner, count, window, sample marks, retrievedAtUtc, `quality=High`) + feed-bound hint.
- [ ] Reader returns typed non-throwing outcomes (empty result = `Success` 0 filings; blank API key, if
      required = `MissingApiKey` with no HTTP call); malformed token / missing key / reader failure degrade to
      `SourceFailure` + zero evidence; caller cancellation propagates.
- [ ] `TrademarkFeedTarget` parses `owner=…` via the shared `SingleKeyFeedToken` (spec 128) — no divergent
      copy; `null` on malformed.
- [ ] Additively registered via `AddTrademarkActivityCollector`; enable-able by `Radar:Collectors` containing
      `"trademarks"`; **default `Radar:Collectors` and `scripts/run-profiles/default.json` unchanged (opt-in
      OFF)**; the verified `trademarks` feeds seeded in `data/companies.json` with the exact owner tokens,
      each with a distinct feed Id (spec 97).
- [ ] A default (trademarks-off) run/composition registers the collector set **without** `trademarks` and
      produces **no** `feeds-lost-before-collection` warning for the declared `trademarks` feeds (spec-98
      interaction; no validator change).
- [ ] **No `_formula.Version`/weight/tier change; only `RuleSetVersion` v6→v7.** The default fingerprint(s)
      re-stamp automatically; `ScoringConfigFingerprintTests` (and the other v6-pinning tests) re-pinned to
      the value(s) obtained by **running** the test; `default.json` `_comment` + `architecture-decisions.md`
      AD-10 lineage updated; the efficacy-segment boundary is noted as score-neutral.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
