# Task: Patent activity collector (leading innovation / R&D-output signal)

> **COLLECTOR-EXPANSION SLICE — the next new evidence axis.** With the scoring-calibration + model-quality
> rework (specs 109–119) landed and validated and the universe expanded to 43 companies (specs 120/125), the
> maintainer's standing direction ([[radar-collector-expansion-direction]]) — *more collectors* — is unblocked.
> This adds a brand-new signal axis: **granted-patent activity** read from a public patents API. It is a
> *leading innovation / R&D-output* indicator that fits Radar's hardware/deep-tech-heavy universe (Rocket Lab,
> Mercury Systems, Energy Recovery, EOS Energy, Sterling, MYR Group, Bel Fuse, Argan, …) far better than any
> software/dev-adoption axis. This is a **single cohesive slice** (~2–3 h) that lands the extractor rule +
> Infrastructure collector + reader seam + feed-token parser + seed + wiring together, because the collector's
> emitted phrase and the extractor rule are a **verbatim contract** and must ship as one unit (the exact pattern
> of specs 99/100 for InstitutionalOwnership and spec 103 for HiringActivity). It is **opt-in / OFF by default** —
> the baseline run does not enable it.

## Overview

A new deterministic collector (`IEvidenceCollector`, config kind `"patents"`, `CollectorName "patents"`,
`EvidenceSourceType.Patent`) reads each seeded company's patents feed (a token carrying that company's
**assignee organization name** to query), counts recently-granted patents in a bounded lookback window, and
emits **one** `Patent` `CollectedEvidence` per company carrying a **fixed patent-activity phrase** in
Title/RawText plus rich provenance metadata (assignee query, grant-count, window, a few sample patent titles +
numbers, retrieved timestamp). A new `KeywordSignalExtractor` rule maps that phrase to a **Neutral**
`SignalType.PatentActivity` signal.

**Both Domain slots already exist** — `EvidenceSourceType.Patent` (append-only enum, index 20) and
`SignalType.PatentActivity` are already reserved with no rule/collector yet — so this slice adds **no** Domain
enum member; it only wires the pre-provisioned slots. That is the strongest indicator patents are the intended
next collector.

**v1 is deliberately Neutral, not directional** (mirrors the settled hiring/13G signal decision — see below). A
single-window granted-patent COUNT cannot distinguish a company whose innovation output is genuinely
*accelerating* from a large incumbent that *always* grants many patents; a mild-Positive on raw count would
systematically favour the largest/most-prolific filers — the exact selection-bias trap the diversified universe
was built to avoid. So v1 **establishes the mechanism** end-to-end (provenance: patents API → `Patent` evidence
→ `PatentActivity` signal → score), lifts source diversity / Evidence / Velocity breadth, and **accrues the
grant counts in timestamped evidence metadata** — without misfiring Trajectory (Neutral contributes 0 to
`TrajectoryScore`). Directional *surge* detection is a deferred future slice B that reads exactly this accrued
history; no separate history store is built now.

This threads the normal `collect → map → resolve → review → store → score → report` path, so provenance is
intact end-to-end. Opt-in via `Radar:Collectors`; the default baseline run is byte-for-byte unchanged **in
scoring math** (see the Fingerprint section for the one automatic re-stamp).

---

## Assignment

Worktree: any — mostly new Infrastructure files under a new `Patents/` folder + one additive extractor rule +
additive DI + one enable-able collector kind + seed data. It edits shared surfaces
(`KeywordSignalExtractor.cs`, `RadarWorkerServices.cs`, `RadarWorkerOptions.cs`, `appsettings.json`,
`InfrastructureServiceCollectionExtensions.cs`, `data/companies.json`, and the fingerprint/descriptor tests), so
**sequence** it rather than parallelizing against any slice that touches the extractor rule table, `SignalType`,
the scoring fingerprint, Worker composition/DI, or the seed.
Dependencies: 103 (the HiringActivity opt-in-off collector this mirrors exactly — merged), 99/100 (the
`SignalType` + extractor-phrase-contract precedent — merged), 95 (the `SignalSourceDescriptor` that folds
`RuleSetVersion` into the fingerprint — merged), 97 (feed-Id folds feed type — merged), 83 (shared query-feed-
token parser precedent — merged), 98 (`SeedFeedInventoryValidator` — must verify no false warning; merged).
Conflicts with: any slice touching `KeywordSignalExtractor` rules / `RuleSetVersion`, `SignalType`,
`ScoringConfigFingerprint`/its tests, Worker composition/DI, or the seed. **Do NOT dispatch a second
`RuleSetVersion`-bumping collector in parallel with this one** — they collide on the extractor rule table and
the fingerprint pins.
Estimated time: ~2–3 h

---

## Grounding facts to VERIFY (a short reachability spike) before dispatch

This is a new **external** source, so — exactly as spec 103 required a maintainer-verified board-token spike
before it shipped — confirm these before/at implementation. The collector + reader + parser + wiring + tests are
**fully offline** (fake `IPatentSearchReader`, JSON fixtures), so the coder can complete the whole slice against
fixtures; only the live **endpoint/response schema**, the **API-key handling**, and the **seed assignee names**
are the live-verification gate.

- **Endpoint (PatentsView Search API).** `GET https://search.patentsview.org/api/v1/patent/` with a URL-encoded
  `q` (query) and `f` (fields) and `o` (options, e.g. page size) parameter. It requires an **API key** header
  `X-Api-Key` (free, requested from patentsview.org). **Confirm the current base URL, request shape, and the
  response JSON envelope** (historically `{ "patents": [ { "patent_id": …, "patent_title": …,
  "patent_date": … } ], "count": … }`). If the schema differs, adjust the reader's parse + the fixtures to the
  **actual** shape observed in the spike — the collector/extractor/wiring do not change.
- **Query.** Filter by assignee organization name and a grant-date floor, e.g.
  `q = {"_and":[{"_gte":{"patent_date":"<isoDateFloor>"}},{"_contains":{"assignees.assignee_organization":"<name>"}}]}`.
  Request only the fields the evidence needs (`patent_id`, `patent_title`, `patent_date`). **Confirm the exact
  field/operator names in the spike** (PatentsView field names evolve between API versions) and pin them as
  named constants in the reader.
- **Key handling (no secret in source — SEC-UA / DEEPINFRA precedent).** The key is **never** committed. Config
  names the env var: `Radar:Patents:ApiKeyEnvVar` (default e.g. `PATENTSVIEW_API_KEY`), read at runtime. A
  **missing/blank key degrades every patents feed to a `SourceFailure`** (logged Warning, zero evidence) — it
  does **not** throw and does **not** affect the baseline (opt-in OFF). Document this exactly like the SEC User-
  Agent placeholder.
- **Assignee names (seed).** The per-company token is the company's **assignee organization name** as it appears
  on granted patents (often the legal entity, e.g. `Rocket Lab USA, Inc.`, `Mercury Systems, Inc.`,
  `Energy Recovery, Inc.`). Verify 3–4 names that return non-empty results in the spike and seed only those;
  partial coverage is normal and expected (exactly like `usaspending` covering 3/43 and `hiringats` 4/43).
- **Politeness.** Set a generic `User-Agent` on the named `HttpClient`; respect a small page size (the count is
  what matters, not full enumeration). No pagination beyond the first bounded page is needed for a count-based
  v1 — cap at the reader's page size and note "≥ N" if the API reports a larger total.

---

## The signal decision (settled — encode as fixed; mirrors the 13G/hiring Neutral decision)

**v1 emits a NEUTRAL `PatentActivity` signal, not directional.** Rationale to state in code comments and the PR:

- A **single-window granted-patent COUNT cannot distinguish** a company whose innovation output is genuinely
  *accelerating* from a large incumbent that *always* grants many patents. A mild-Positive on raw count would
  systematically favour the most-prolific filers — the selection-bias trap the diversified universe avoids.
- So v1 **establishes the mechanism** (the patent axis + `Patent` source type provenance + Evidence / Velocity
  breadth) and **accrues the counts in timestamped evidence metadata**, **without** moving Trajectory (Neutral
  contributes 0 to `TrajectoryScore`).
- **Directional surge detection** (Positive when the grant count *accelerates* vs the accrued patent-evidence
  history) is **deferred to slice B**. No separate history store is needed now — the timestamped evidence
  metadata this slice writes **is** the record slice B will read. Same conservative-first / build-the-mechanism-
  defer-conviction pattern as the 13G Neutral (spec 99), the hiring Neutral (spec 103), and the price reference
  store (AD-14).
- **Name is `PatentActivity`** on purpose (honest — it is *activity*, not a proven surge). **Slice B changes the
  DIRECTION, not the type name.**

---

## Design

### 1. Domain — no change

`EvidenceSourceType.Patent` and `SignalType.PatentActivity` **already exist** (both reserved, no rule/collector).
`Patent` is correctly first-party (not in `EvidenceSourceTypes.IsThirdPartyAttentionSource` — a company's own
granted patents are not third-party market attention), so it adds nothing to the Attention reach — correct.
**No Domain edit.** Confirm no exhaustive `switch` over `SignalType`/`EvidenceSourceType` needs a new arm (there
was none for `HiringActivity`/`InstitutionalOwnership` — the extractor maps by string and the formula folds by
direction).

### 2. Extractor rule — one Neutral phrase (verbatim contract)

Add **one** rule group to the `KeywordSignalExtractor` `Rules` table (after the `HiringActivity` row), mapping the
single fixed phrase the collector emits to `PatentActivity` **Neutral** at routine strength (mirror the existing
Neutral routine rows such as `hiring activity (open roles)` = 3/4/0.45 and `passive beneficial-ownership stake
(13g)` = 3/5/0.5):

```csharp
// PatentActivity (public patents API; spec 127). The PatentActivityCollector synthesizes exactly this fixed
// phrase into the Patent evidence Title/RawText; the extractor only maps phrase -> fixed type+direction+strength
// (never re-derives valence — the InstitutionalOwnership/HiringActivity precedent). v1 is NEUTRAL by design: a
// single-window granted-patent COUNT cannot tell genuine acceleration from an always-prolific filer, so it never
// misfires bullish (Neutral contributes 0 to Trajectory). Directional SURGE detection vs accrued patent history
// is deferred to slice B (changes DIRECTION, not this type name). NO materiality metadata read here — Strength is
// the fixed rule Strength.
new("patent activity (recent grants)", SignalType.PatentActivity, SignalDirection.Neutral, 3, 5, 0.45m),
```

- **ONE phrase is chosen deliberately** (simplest; keeps the verbatim contract tiny).
- Bump **`KeywordSignalExtractor.RuleSetVersion`** `"radar-keyword-rules-v3"` → **`"radar-keyword-rules-v4"`**
  and update the const's surrounding comment.
- **No-contamination rule (important):** the collector must embed **only** the fixed phrase + numeric count +
  assignee/window in Title/RawText — **never raw patent titles** — because a patent title like "System for
  autonomous launch integration" could otherwise trip the `launches`/`integrates`/`new platform` rules. Sample
  patent titles + numbers live in **metadata only** (the extractor does not scan metadata for phrases). State
  this in both the collector and the extractor rule comment.

### 3. Infrastructure — reader seam + collector (new `Radar.Infrastructure/Patents/` folder)

**Reader seam** (`internal`, mirrors the non-SEC reader pattern — `HttpUsaSpendingAwardReader`,
`HttpNewsSearchReader`, the spec-103 job-board readers):

- `PatentGrant(string PatentId, string Title, DateOnly GrantDate)` — the normalized per-patent shape.
- `PatentSearchResult(int GrantCount, IReadOnlyList<PatentGrant> Grants)` — `GrantCount` is the count of parsed
  grants in the returned page (authoritative/deterministic from the payload); if the API reports a larger total,
  keep the parsed page count and record the API total in metadata as a cross-check only.
- `PatentSearchOutcome` enum: `Success, Unreachable, HttpError, Timeout, Malformed, MissingApiKey` (mirror
  `UsaSpendingReadOutcome`; `MissingApiKey` when the configured env var is blank — a distinct, clearly-logged
  degrade, not an exception).
- `PatentSearchReadResult(PatentSearchOutcome Outcome, PatentSearchResult? Result, string? Detail)` with
  `IsSuccess` + `Success(...)`/`Failure(...)` factories (mirror `UsaSpendingReadResult`).
- `IPatentSearchReader { Task<PatentSearchReadResult> ReadAsync(string assigneeName, DateOnly grantFloor, CancellationToken ct); }`.
- `HttpPatentSearchReader : IPatentSearchReader`: injected typed `HttpClient` + `System.Text.Json`, reads the
  API key from the env var named by options (blank ⇒ `MissingApiKey` outcome, no HTTP call), builds the
  PatentsView `q`/`f`/`o` request (verified in the spike, operators pinned as constants), sends the `X-Api-Key`
  header, `HttpCompletionOption.ResponseHeadersRead`, materialize body before dispose, then the standard ladder:
  `HttpRequestException`→`Unreachable`, `TaskCanceledException`(non-ct)→`Timeout`, caller-cancellation re-throw
  (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`), non-success status→
  `HttpError`, `JsonException` / root-not-object / missing `patents` array→`Malformed`, empty `patents` array→
  `Success` with 0 grants (a valid no-recent-grants result, not an error). All HTTP/JSON stays in
  `Radar.Infrastructure` (AD-5); all new types `internal`.

**Feed-token parser** `PatentFeedTarget(string AssigneeName)` (`internal sealed record`, in
`Radar.Infrastructure.Patents`): `Parse(string?) -> PatentFeedTarget?` for the token `assignee=<name>`.
**Reuse-over-copy:** the order-robust key/value `&`-split idiom now has several copies (`QueryFeedTarget`,
`UsaSpendingFeedTarget`, spec-103 `HiringFeedTarget`). This target is **single-key** (`assignee=…`), so it is
simpler than those two-key parsers; parse it with the **same** trimming/blank-null discipline. If a shared token
splitter was already extracted by the spec-103 follow-up, route this through it; otherwise mirror the established
single-key shape and note the shared-splitter extraction as a follow-up in the PR (do not paste a divergent
copy). Malformed/blank ⇒ `null` so the collector degrades to a `SourceFailure`.

**Collector** `PatentActivityCollector : IEvidenceCollector`:

- `CollectorName => "patents"`, `SourceType => EvidenceSourceType.Patent`.
- Injected: `IPatentSearchReader`, `ILogger`, `TimeProvider`, `PatentCollectorOptions`.
- Iterate `context.FeedsOfType("patents")` (deterministic order). Build `companiesById`. Per feed:
  `PatentFeedTarget.Parse(feed.Url)`; `null` ⇒ `SourceFailure(feed.Name, feed.Url, "malformed patents feed
  token")` + Warning + continue. Compute `grantFloor = today - options.LookbackDays` (default 180) using the
  injected `TimeProvider`. `ReadAsync(target.AssigneeName, grantFloor, ct)`; `!IsSuccess` ⇒ `SourceFailure`
  (+ `result.Detail`) + continue (a `MissingApiKey` outcome is logged clearly so an operator sees why patents
  produced nothing).
- On success compute `count = result.Result.GrantCount`, `sample = result.Result.Grants.Take(options.MaxSampleTitles)`,
  and emit **one** `CollectedEvidence` (`MapToEvidence`):
  - **Title** embeds the verbatim phrase + count, e.g.
    `"Patent activity (recent grants) — {count} patents granted to '{assignee}' in the last {lookbackDays} days"`.
  - **RawText** embeds the phrase + count + assignee + window + retrieved timestamp for hash distinctness (NO raw
    patent titles), e.g. `"Assignee '{assignee}': {count} patents granted since {grantFloor:o}, as of
    {retrievedAtUtc:o}. Signal: patent activity (recent grants)."`.
  - `SourceUrl` = a stable human-viewable PatentsView/assignee link if one is available from the spike, else the
    query URL; `PublishedAt` = `CollectedAt` = `TimeProvider.GetUtcNow()` (a snapshot window has no single
    publish date). **Each run therefore produces a distinct timestamped snapshot evidence** (the RawText
    timestamp makes the ContentHash distinct run-to-run) — this **is** the accrued patent history slice B reads;
    state this explicitly.
  - **Metadata** (`Dictionary<string,string>`, Ordinal): `quality = "High"` (granted patents are an authoritative
    public-record source, on par with SEC/USASpending `High`); `patentsFeedUrl` (= `feed.Url`); `assignee`;
    `grantCount`; `lookbackDays`; `grantFloor` (round-trip `o`/`yyyy-MM-dd`); `sampleTitles` (the first N
    `"{patentId}: {title}"` joined with `" | "` — provenance/debug only, **not** scanned by the extractor);
    `apiReportedTotal` (optional cross-check); `retrievedAtUtc` (round-trip `o`). These counts are the history
    slice B will read.
  - `CompanyHints = CollectorCompanyHints.For(feed.CompanyId, companiesById)` — never invent a ticker.
- Populate `CollectionSummary` exactly like `SecForm4Collector`/`UsaSpendingContractCollector`/
  `HiringBoardCollector` (checked, ok, failed, count, failures); log per-feed + aggregate.
- `PatentCollectorOptions`: `LookbackDays` (default 180), `MaxSampleTitles` (default 5), `ApiKeyEnvVar`
  (default `"PATENTSVIEW_API_KEY"`), `MaxPageSize` (default 100).

### 4. Feed-token seed (additive, `data/companies.json`)

Add one `patents` feed to the **3–4** companies verified in the reachability spike (record the exact assignee
names here as the seed of record; leave the other names out — partial coverage is normal, consistent with
Radar's opt-in-per-company feed model, exactly like `usaspending` and `hiringats`). Suggested first candidates
to verify (deep-tech names likely to have granted-patent activity): **RKLB** (Rocket Lab USA, Inc.), **MRCY**
(Mercury Systems, Inc.), **ERII** (Energy Recovery, Inc.), **EOSE** (EOS Energy Enterprises, Inc.). Token form
`assignee=<verified assignee organization name>`; feed `name` e.g. `"Rocket Lab — Recent granted patents
(PatentsView)"`. Additive edit only — leave every existing feed untouched. Per spec 97 the feed-Id folds the feed
**type**, so these do not collide with any existing feed.

### 5. Wiring (opt-in, OFF by default)

- `AddPatentActivityCollector(this IServiceCollection, PatentCollectorOptions options)` in
  `InfrastructureServiceCollectionExtensions.cs`: register a named typed `HttpClient`
  (`ConfigurePrimaryHttpMessageHandler` with gzip/deflate `AutomaticDecompression`, generic `User-Agent`,
  mirroring `AddUsaSpendingContractCollector`), register `IPatentSearchReader`, `AddSingleton(options)`,
  `TryAddSingleton(TimeProvider.System)`, and `AddSingleton<IEvidenceCollector, PatentActivityCollector>()`.
- `RadarWorkerServices.cs`: add a `"patents"` enable-able kind branch (register via `AddPatentActivityCollector`
  from a new `RadarWorkerOptions.Patents` block); extend **all three** valid-kinds messages (the empty-list, the
  null/blank-entry, and the unknown-kind messages) to include `"patents"`.
- `RadarWorkerOptions.cs`: add `PatentWorkerOptions Patents { get; init; } = new();` (bound from `Radar:Patents`)
  with `LookbackDays` (180), `MaxSampleTitles` (5), `ApiKeyEnvVar` (`"PATENTSVIEW_API_KEY"`), `MaxPageSize`
  (100), mirroring the other `*WorkerOptions` blocks.
- `appsettings.json`: add a documented, disabled-by-default `Radar:Patents` section (note the key is read from
  the named env var, never committed); **leave `Radar:Collectors` default unchanged** (patents stays opt-in).
- **`scripts/run-profiles/default.json`: do NOT add `"patents"` to `Collectors`.** It stays opt-in/off — a run
  enables it only by explicitly listing `patents` (and supplying `PATENTSVIEW_API_KEY`). Promoting it into the
  baseline is a later deliberate fingerprint re-stamp after a live measurement validates the axis (mirrors how
  `secform4`/`sec13dg` were promoted) — out of scope, see below.

---

## Fingerprint + sequencing (load-bearing)

Shipping this bumps `KeywordSignalExtractor.RuleSetVersion` **v3 → v4**, which spec 95's
`SignalSourceDescriptor` folds into the scoring-config fingerprint **independently of which collectors are
enabled**. Therefore:

- **The DEFAULT/baseline fingerprint re-stamps the moment this merges — even though `patents` is opt-in-OFF.**
  The default descriptor's enabled-collector CSV is **unchanged** (still the 6-collector set — `patents` is not
  in `default.json`); the fingerprint moves **solely** because `rules=radar-keyword-rules-v3` → `…-v4`. **There
  is no fingerprint-safe way to add a new scoring-affecting signal type — this is spec 95 working as intended**
  (exactly as spec 103 re-stamped for HiringActivity).
- **No `_formula.Version` bump** (formula shape unchanged — `radar-formula-v8` stays). Only `RuleSetVersion`
  bumps. No weight / attention-tier / insider-tier change.
- **Efficacy interaction (spec 101/108) — this re-stamp is SCORE-NEUTRAL, a cosmetic boundary.** Because the
  collector is opt-in-OFF and the new rule matches only the patent phrase (which no existing evidence contains),
  **every company scores byte-identical before and after** — only the fingerprint *string* changes (v3→v4), not
  any actual score. Spec 108's continuity-aware segmentation connects the score line across a score-continuous
  re-stamp, so state plainly that this boundary is an input-hash artifact, not a measurement break.
- **Re-pin the fingerprint by RUNNING the test, not by hand** (same procedure as spec 103 / M2): update the
  `RuleSetVersion` literals to `v4`, run `dotnet test`, read the **actual** default fingerprints the failing
  pins report, and paste those exact values in. Do **not** hand-compute a SHA. The re-pin surface (grep-verify
  against the current tree — the literals below are the ones spec 103 last touched, confirm before editing):
  - `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — the `RuleSetVersion` const (+ comment).
  - `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — the `SourceDescriptor` literal
    `…rules-v3…`→`…rules-v4…`; the `ChangedSignalSourceDescriptor` variant; the pinned **AI-OFF** default
    fingerprint (currently `radar-scoring-fp-cb80a5809882`) → the NEW value from the run; and the lineage comment.
  - `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs` — `…rules-v3…`→`…rules-v4…`.
  - `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs` — any `…rules-v3…` literal.
  - `tests/Radar.Infrastructure.Tests/FileSystem/FileScoringConfigStoreTests.cs` — the fixture descriptor
    `…rules-v3…`→`…rules-v4…` **if** it fails (independent sample string; update for consistency).
  - `scripts/run-profiles/default.json` — the `_comment`'s `radar-keyword-rules-v3`→`v4` reference and the
    `radar-scoring-fp-cb80a5809882` (AI-OFF) reference. **The AI-ON default `radar-scoring-fp-c908f03a554a` also
    re-stamps** (its descriptor contains the same `rules=` token) — update it to the value a live AI-ON run would
    stamp if a test pins it; otherwise note the transition in the comment. Re-pin from the RUN, never by hand.
  - `docs/architecture-decisions.md` — add an AD-10 lineage line recording the `RuleSetVersion` v3→v4 bump (new
    `PatentActivity` rule group) and the default fingerprint transition(s) (old → new), noting scoring math is
    byte-identical and the collector is opt-in-off (mirror the spec-103 lineage entry).
- **Spec-98 collection-health check:** because `CollectionContext.SourceFeeds` is populated from **all** seeded
  feeds regardless of enabled collectors, a declared-but-disabled `patents` feed type has **declared == reached**
  ⇒ **no** `feeds-lost-before-collection` warning. **No `SeedFeedInventoryValidator` change is required.** Prove
  it with a test; the spec must NOT introduce spurious warnings on a default (patents-off) run.

---

## Tests

- **`HttpPatentSearchReaderTests`** (offline, fake `HttpMessageHandler`): a `{"patents":[…]}` fixture → correct
  `GrantCount` (= parsed grant count) + titles + grant dates; empty `patents` array → `Success` with 0 grants;
  missing `patents` / root-not-object → `Malformed`; a blank configured API key → `MissingApiKey` **with no HTTP
  call** (assert the handler was not invoked); non-success status → `HttpError`; thrown `TaskCanceledException`
  (timeout) → `Timeout`; `HttpRequestException` → `Unreachable`; caller cancellation re-throws; the `X-Api-Key`
  header is set from the env var when present. **No network.**
- **`PatentFeedTargetTests`**: `assignee=Rocket Lab USA, Inc.` parses (value may contain spaces/commas); missing
  key / blank value / blank token → `null`; whitespace trimmed.
- **`PatentActivityCollectorTests`** (fake `IPatentSearchReader`): a successful search → **one** `Patent`
  `CollectedEvidence` whose Title/RawText contain the verbatim `patent activity (recent grants)` phrase and the
  count (and **no** raw patent titles); metadata carries `assignee`/`grantCount`/`lookbackDays`/`grantFloor`/
  `sampleTitles`/`retrievedAtUtc`/`quality=High`; `CompanyHints` = feed-bound ticker; UTC instants from the
  injected `TimeProvider`; the `grantFloor` = now − `LookbackDays`; a malformed token, a `MissingApiKey`, and a
  reader failure each degrade to a `SourceFailure`/no-evidence without throwing; `CollectionSummary` counts
  correct; deterministic order.
- **Extractor mapping** (extend `KeywordSignalExtractorTests`): `Patent` evidence whose text contains
  `patent activity (recent grants)` → exactly one `PatentActivity` **Neutral** signal at the chosen strength
  (3/5/0.45), with a verbatim excerpt; and a guard that the phrase does **not** also fire an unrelated rule (and
  that a sample patent title placed only in metadata does not leak into matching).
- **Fingerprint re-pin**: `ScoringConfigFingerprintTests` green with the `RuleSetVersion`-v4 descriptor and the
  NEW pinned default fingerprint(s) (obtained by running); `SignalSourceDescriptorTests`, `ScoringEngineTests`,
  `FileScoringConfigStoreTests` updated to v4 where they pin it.
- **Default (patents-off) composition / no false warning**: a Worker-DI or runner test with the default
  `Collectors` (no `patents`) but the seed containing the `patents` feeds registers the collector set **without**
  `patents` and produces **no** `feeds-lost-before-collection` warning for the `patents` feed type (regression
  guard for the spec-98 interaction). If a Worker-DI test asserts the enable-able kind list, extend it to include
  `"patents"`.
- **Seed**: a `LocalFileCompanySeedSource` test asserts the verified companies carry a `patents` feed with the
  exact assignee tokens, each with a distinct feed Id (does not collide with the same company's other feeds —
  spec 97).
- Existing tests (RSS/SEC/Form4/13DG/USASpending/News/Hiring collectors, runner merge, DI list, Worker DI,
  extractor, scoring) stay green. Full gate: `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based mapping; **NO AI**.
- All HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8). Reuse
  `CollectorCompanyHints`, `CollectionSummary`/`SourceFailure`, the `CollectedEvidence`/`CollectedEvidenceMapper`
  path, and the established non-SEC reader + feed-token-parser patterns — **no duplicated HTTP/parse primitives**
  (route through the shared token splitter if extracted, else record it as a follow-up). Do **not** reuse
  SEC-specific helpers.
- **Secret handling:** the API key is read at runtime from the env var named in config and is **never** written
  to source, logged, or committed; a missing key degrades the collector to `SourceFailure`s (opt-in OFF ⇒
  baseline untouched). Same posture as the SEC User-Agent / DEEPINFRA key.
- Graceful degradation: typed non-throwing outcomes; a company with zero recent grants is a valid `Success` (not
  an error); a bad token / missing key / unreachable API yields a `SourceFailure` + zero evidence; only genuine
  caller cancellation propagates.
- **Provenance preserved** (patents API → `Patent` evidence with the assignee + count + window → `PatentActivity`
  signal → score). **No advice language** (AD-9): the factual "patent activity (recent grants)" phrasing carries
  no recommendation; direction is internal to scoring (and is Neutral in v1).
- Store timestamps in UTC; IDs `Guid`. Keep `MaxSampleTitles`/`MaxPageSize` bounded. Sample patent titles are
  metadata-only and never enter the extractor's searchable text (no keyword contamination).
- **Only `RuleSetVersion` bumps** (v3→v4); **no** `_formula.Version` / weight / attention-tier / insider-tier
  change. `ScoringConfigVersion` re-stamps automatically via `SignalSourceDescriptor`; re-pin the tests to the
  value obtained by RUNNING them.

---

## Out of scope / future slices (record, do not build)

- **Slice B — directional surge detection** (Positive when the grant count *accelerates* vs the accrued
  patent-evidence history this slice writes). Reads the timestamped evidence metadata; changes the
  `PatentActivity` **direction**, not the type name. No separate history store needed.
- **Promoting `patents` into the baseline `default.json`** — a later deliberate fingerprint re-stamp, after a
  live measurement validates the axis (mirrors how `secform4`/`sec13dg` were promoted).
- **Additional assignee names / subsidiary de-duplication** (a company may hold patents under several assignee
  legal entities) — additive seed edits + optional multi-assignee token later.
- **CPC/technology-class enrichment or citation-weighting** — a richer materiality read; future slice.
- **AI patent-abstract reading** (beyond the count) — a future `Microsoft.Extensions.AI` slice.
- **Extracting the shared feed-token splitter** across the parsers if not already done by the spec-103 follow-up.

---

## Acceptance criteria

- [ ] No Domain enum change — `EvidenceSourceType.Patent` and `SignalType.PatentActivity` (both pre-reserved) are
      wired; no exhaustive `SignalType`/`EvidenceSourceType` switch broken.
- [ ] One `KeywordSignalExtractor` rule maps the verbatim phrase `patent activity (recent grants)` →
      `PatentActivity` **Neutral** (3/5/0.45); no materiality metadata read; the phrase does not trip another
      rule; `RuleSetVersion` bumped `radar-keyword-rules-v3` → `v4`.
- [ ] `PatentActivityCollector` (kind `"patents"`, `CollectorName "patents"`, `SourceType Patent`) reads
      `FeedsOfType("patents")`, parses the `assignee=…` token, reads recent grants via `IPatentSearchReader`
      (PatentsView), and emits **one** `Patent` evidence per company with the fixed phrase in Title/RawText (no
      raw titles) + rich metadata (assignee, count, window, sample titles, retrievedAtUtc, `quality=High`) +
      feed-bound hint.
- [ ] Reader returns typed non-throwing outcomes (empty result = `Success` 0 grants; blank API key =
      `MissingApiKey` with no HTTP call); malformed token / missing key / reader failure degrade to
      `SourceFailure` + zero evidence; caller cancellation propagates. API key read from the config-named env
      var, never committed/logged.
- [ ] `PatentFeedTarget` parses `assignee=…` (`null` on malformed); routed through the shared token splitter if
      extracted, else the extraction recorded as a follow-up in the PR.
- [ ] Additively registered via `AddPatentActivityCollector`; enable-able by `Radar:Collectors` containing
      `"patents"`; **default `Radar:Collectors` and `scripts/run-profiles/default.json` unchanged (opt-in OFF)**;
      the verified `patents` feeds seeded in `data/companies.json` with the exact assignee tokens, each with a
      distinct feed Id (spec 97).
- [ ] A default (patents-off) run/composition registers the collector set **without** `patents` and produces
      **no** `feeds-lost-before-collection` warning for the declared `patents` feeds (spec-98 interaction; no
      validator change).
- [ ] **No `_formula.Version`/weight/tier change; only `RuleSetVersion` v3→v4.** The default fingerprint(s)
      re-stamp automatically; `ScoringConfigFingerprintTests` (and the other v3-pinning tests) re-pinned to the
      value(s) obtained by **running** the test; `default.json` `_comment` + `architecture-decisions.md` AD-10
      lineage updated; the efficacy-segment boundary is noted as score-neutral.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
