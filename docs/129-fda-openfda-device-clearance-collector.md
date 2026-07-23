# Task: FDA device clearance/approval collector (leading medtech go-to-market signal)

> **COLLECTOR-EXPANSION SLICE #3 — the medtech axis, and Radar's first directional-Positive count-based
> collector.** Follows the documented expansion arc in `docs/radar-full-pipeline-spec.md`
> (*Patent (127) → FCC (128) → FDA → USPTO Trademark*, sequenced because each bumps `RuleSetVersion` and
> re-pins the same scoring fingerprint). An FDA **510(k) clearance** or **PMA approval** is the regulatory
> gate a medical device must clear **before** it may be marketed in the US — a discrete, unambiguous,
> market-relevant *positive* event that **leads** commercial launch. High leading value for the medtech
> names in the universe (AxoGen, TransMedics) and future health names. This is a **single cohesive slice**
> (~2–3 h): the extractor rule + Infrastructure collector + reader seam + feed-token parser + seed + wiring
> land together because the collector's emitted phrase and the extractor rule are a **verbatim contract**
> and must ship as one unit (the exact pattern of specs 103/127/128). It is **opt-in / OFF by default** —
> the baseline run does not enable it.

## Overview

A new deterministic collector (`IEvidenceCollector`, config kind `"fda"`, `CollectorName "fda"`,
`EvidenceSourceType.RegulatoryApproval`) reads each seeded company's openFDA feed (a token carrying that
company's **applicant/sponsor organization name** to query), counts recently-issued device clearances/
approvals (510(k) + PMA) in a bounded lookback window, and emits **one** `RegulatoryApproval`
`CollectedEvidence` per company carrying a **fixed clearance-activity phrase** in Title/RawText plus rich
provenance metadata (applicant query, clearance-count, window, a few sample submission numbers + device
names, retrieved timestamp). A new `KeywordSignalExtractor` rule maps that phrase to a **Positive**
`SignalType.RegulatoryApproval` signal at routine strength.

**Two new append-only Domain slots** are added at the end of their enums:
`EvidenceSourceType.RegulatoryApproval` and `SignalType.RegulatoryApproval` (never reorder/remove;
persisted by name). `RegulatoryApproval` is **first-party** (a company's own regulatory clearance is not
third-party market attention) — it is **not** added to
`EvidenceSourceTypes.IsThirdPartyAttentionSource`, so it contributes nothing to Attention reach — correct.

### The direction decision (departs from patents/FCC Neutral — flagged for maintainer sign-off)

Unlike patents (127) and FCC (128), which are **Neutral** in v1 because a rolling COUNT can't distinguish
genuine acceleration from an always-prolific filer, **FDA v1 emits a POSITIVE signal at ROUTINE strength**.
Rationale (state in code comments and the PR; **the maintainer should confirm this at review** — it is the
one place this slice deliberately introduces a bullish direction):

- An FDA **clearance/approval is a discrete, binary, market-relevant gate** — a specific device cleared for
  sale — with **clear positive valence** (the roadmap: "approval = Positive"). It is materially more
  event-like than a rolling patent/authorization count.
- **The misfire risk is bounded structurally, not by tuning.** The extractor emits **one** signal per
  company per run at a **fixed routine strength** (mirroring `hiring activity`/`13g`/`patent activity`) —
  **not** proportional to the count — so an always-prolific medtech incumbent produces the *same* single
  routine-strength signal as a first-time clearer, and cannot dominate. The count lives in metadata for
  provenance and for the deferred surge slice, not in the score magnitude.
- **Routine (not high) strength** keeps it a corroborating, breadth-lifting positive rather than a
  standalone thesis driver: a clearance nudges Trajectory but does not by itself flip a label
  (consistent with the corroboration-aware discipline of specs 111/121).
- If the maintainer prefers strict consistency with the patents/FCC Neutral posture, the fallback is to ship
  **Neutral** v1 and defer the Positive valence to slice B (the code change is a single `SignalDirection`
  literal + the fixed strength). The spec ships **Positive** unless the reviewer/maintainer directs
  otherwise.

**Recalls / enforcement (the NEGATIVE counterpart) are explicitly deferred** to a separate future slice
(see Out of scope) — that is the pipeline's first Negative-direction collector and deserves its own slice
and its own signal decision. This slice is clearances/approvals only.

This threads the normal `collect → map → resolve → review → store → score → report` path, so provenance is
intact end-to-end. Opt-in via `Radar:Collectors`; the default baseline run is byte-for-byte unchanged **in
scoring math** (see the Fingerprint section for the one automatic re-stamp).

---

## Assignment

Worktree: any — mostly new Infrastructure files under a new `Fda/` folder + one additive extractor rule +
two append-only Domain enum members + additive DI + one enable-able collector kind + seed data. It edits
shared surfaces (`SignalType.cs`, `EvidenceSourceType.cs`, `KeywordSignalExtractor.cs`,
`RadarWorkerServices.cs`, `RadarWorkerOptions.cs`, `appsettings.json`,
`InfrastructureServiceCollectionExtensions.cs`, `data/companies.json`, and the fingerprint/descriptor
tests), so **sequence** it rather than parallelizing against any slice that touches the extractor rule
table, `SignalType`/`EvidenceSourceType`, the scoring fingerprint, Worker composition/DI, or the seed.
Dependencies: **128 (FCC — must be MERGED first)** because the fingerprint re-pin is computed from the
post-128 tree (`RuleSetVersion` v5 → v6) and this slice routes its feed-token parser through the shared
`SingleKeyFeedToken` **extracted by spec 128**. Also 127 (patents — merged), 103 (the opt-in-off collector
this mirrors — merged), 95 (`SignalSourceDescriptor` folds `RuleSetVersion` into the fingerprint — merged),
97 (feed-Id folds feed type — merged), 98 (`SeedFeedInventoryValidator` — verify no false warning; merged).
Conflicts with: **specs 127, 128, 130** (all bump `RuleSetVersion` and re-pin the same fingerprint) and any
slice touching `KeywordSignalExtractor` rules / `RuleSetVersion`, `SignalType`/`EvidenceSourceType`,
`ScoringConfigFingerprint`/its tests, Worker composition/DI, or the seed. **Do NOT dispatch in parallel
with any other `RuleSetVersion`-bumping collector.**
Estimated time: ~2–3 h

---

## Grounding facts to VERIFY (a short reachability spike) before dispatch

A new **external** source — confirm before/at implementation (exactly as specs 103/127/128 required a
maintainer-verified reachability spike). The collector + reader + parser + wiring + tests are **fully
offline** (fake `IFdaClearanceReader`, JSON fixtures), so the coder can complete the whole slice against
fixtures; only the live **endpoint/response schema** and the **seed applicant names** are the live gate.

- **Endpoint (openFDA).** `GET https://api.fda.gov/device/510k.json` and
  `GET https://api.fda.gov/device/pma.json` — free, structured JSON, **no API key required** for low
  request volume (an optional key raises rate limits; do NOT require one). **Confirm the current base URLs,
  the `search`/`count`/`limit` query params, and the response envelope** (historically
  `{ "meta": { "results": { "total": N } }, "results": [ { … } ] }`). Capture per record: the **submission/
  approval number** (`k_number` for 510(k), `pma_number` for PMA), the **device name** (`device_name`), and
  the **decision date** (`decision_date`, `YYYYMMDD`). Pin field names as named constants; if the schema
  differs, adjust the reader parse + fixtures to the **observed** shape — the collector/extractor/wiring do
  not change.
- **Query.** Filter by applicant/sponsor organization name and a decision-date floor, e.g.
  `search=applicant:"<name>"+AND+decision_date:[<floor>+TO+<today>]` for 510(k) (the PMA endpoint uses
  `applicant` similarly — **confirm the exact field names per endpoint in the spike**; openFDA field names
  differ between device sub-endpoints). Request a bounded page (`limit`, default 100). Prefer the endpoint's
  `meta.results.total` as the authoritative total, but keep the **parsed page count** as `ClearanceCount`
  (deterministic from the payload) and record the reported total in metadata as a cross-check.
- **No API key required (confirm).** Default assumption: **no key**. If the spike shows the maintainer wants
  the higher-rate keyed tier, add the env-var posture from spec 127 (`Radar:Fda:ApiKeyEnvVar`, missing key ⇒
  degrade, never committed) — otherwise do **not** add key plumbing.
- **Applicant names (seed).** The per-company token is the company's **applicant/sponsor organization name**
  as it appears on FDA submissions (often the legal entity or a subsidiary). Verify **2–3** names that return
  non-empty results and seed only those; partial coverage is normal and expected (exactly like `usaspending`
  covering 3/43). First candidates to verify: **AXGN** (AxoGen, Inc.), **TMDX** (TransMedics, Inc.).
- **Politeness.** Generic `User-Agent` on the named `HttpClient`; small bounded page size (the count matters,
  not full enumeration). No pagination beyond the first bounded page; cap at the reader's page size and note
  "≥ N" if `meta.results.total` reports a larger total.

---

## Design

### 1. Domain — two append-only enum members (minimal)

- `EvidenceSourceType.RegulatoryApproval` appended at the **end** of `EvidenceSourceType` (append-only;
  persisted by name; never reorder/remove). Do **not** add it to
  `EvidenceSourceTypes.IsThirdPartyAttentionSource` (first-party regulatory clearance, not market attention).
- `SignalType.RegulatoryApproval` appended at the **end** of `SignalType`.
- Confirm no exhaustive `switch` over `SignalType`/`EvidenceSourceType` needs a new arm (the extractor maps
  by string and the formula folds by direction).

### 2. Extractor rule — one Positive phrase (verbatim contract)

Add **one** rule group to the `KeywordSignalExtractor` `Rules` table (after the `EquipmentAuthorization` row
from spec 128), mapping the single fixed phrase the collector emits to `RegulatoryApproval` **Positive** at
**routine** strength. Mirror the routine-Positive rows (e.g. the routine-strength customer/partnership rows)
— NOT the high-conviction strengths; a clearance corroborates, it does not by itself flip a label:

```csharp
// RegulatoryApproval (openFDA 510(k)/PMA; spec 129). The FdaClearanceCollector synthesizes exactly this
// fixed phrase into the RegulatoryApproval evidence Title/RawText; the extractor only maps phrase -> fixed
// type+direction+strength (never re-derives valence — the PatentActivity/EquipmentAuthorization precedent).
// v1 is POSITIVE (an FDA clearance/approval is a discrete, clear-valence, market-relevant gate) but at
// ROUTINE strength and ONE signal per run (NOT count-proportional) so an always-prolific medtech incumbent
// cannot dominate — it corroborates, it does not flip a label alone (specs 111/121 discipline). NO raw
// device names in searchable text (metadata only). Recalls (Negative) are a separate future slice.
new("fda clearance or approval (recent)", SignalType.RegulatoryApproval, SignalDirection.Positive, 4, 5, 0.5m),
```

- **ONE phrase** (simplest; keeps the verbatim contract tiny). Confirm the exact `(magnitude, quality,
  confidence)` triple against the existing routine-Positive rows when implementing — the values above are the
  intended routine tier (corroborating, not thesis-driving); match a real routine row rather than inventing a
  new magnitude.
- Bump **`KeywordSignalExtractor.RuleSetVersion`** `"radar-keyword-rules-v5"` → **`"radar-keyword-rules-v6"`**
  and update the surrounding comment.
- **No-contamination rule:** the collector embeds **only** the fixed phrase + numeric count + applicant/
  window in Title/RawText — **never raw device names** (a device name like "cardiac partnership system"
  could otherwise trip the `partnership` rule). Sample submission numbers + device names live in **metadata
  only** (the extractor does not scan metadata for phrases). State this in both the collector and the rule
  comment.

### 3. Infrastructure — reader seam + collector (new `Radar.Infrastructure/Fda/` folder)

**Reader seam** (`internal`, mirrors `HttpUsaSpendingAwardReader`/`HttpPatentSearchReader`/`HttpFccAuthReader`):

- `FdaClearance(string SubmissionNumber, string DeviceName, DateOnly DecisionDate, string Track)` — the
  normalized per-clearance shape (`Track` = `"510(k)"` or `"PMA"`).
- `FdaClearanceResult(int ClearanceCount, IReadOnlyList<FdaClearance> Clearances)` — `ClearanceCount` is the
  count of parsed clearances across the queried endpoints in the returned pages (deterministic from the
  payload); the endpoints' reported totals are recorded in metadata as a cross-check only.
- `FdaReadOutcome` enum: `Success, Unreachable, HttpError, Timeout, Malformed` (mirror
  `UsaSpendingReadOutcome`).
- `FdaClearanceReadResult(FdaReadOutcome Outcome, FdaClearanceResult? Result, string? Detail)` with
  `IsSuccess` + `Success(...)`/`Failure(...)` factories.
- `IFdaClearanceReader { Task<FdaClearanceReadResult> ReadAsync(string applicantName, DateOnly decisionFloor, CancellationToken ct); }`.
- `HttpFdaClearanceReader : IFdaClearanceReader`: injected typed `HttpClient` + `System.Text.Json`, queries
  the 510(k) and PMA endpoints (verified in the spike, field/operator names pinned as constants), merges the
  parsed results, `HttpCompletionOption.ResponseHeadersRead`, materialize body before dispose, then the
  standard ladder: `HttpRequestException`→`Unreachable`, `TaskCanceledException`(non-ct)→`Timeout`,
  caller-cancellation re-throw (`catch (OperationCanceledException) when (ct.IsCancellationRequested)
  { throw; }`), non-success status→`HttpError`, `JsonException` / root-not-object / missing `results`
  array→`Malformed`, **openFDA's documented empty-search `404` (`"No matches found"`) → `Success` with 0
  clearances** (a valid no-recent-clearances result, not an error — confirm this behaviour in the spike and
  handle it explicitly). All HTTP/JSON stays in `Radar.Infrastructure` (AD-5); all new types `internal`.

**Feed-token parser (reuse the shared single-key splitter — HARD RULE).**
`FdaFeedTarget(string ApplicantName)` (`internal sealed record`, in `Radar.Infrastructure.Fda`):
`Parse(string?) -> FdaFeedTarget?` for the token `applicant=<name>`, routed through the shared
`SingleKeyFeedToken.TrySplit` **extracted by spec 128** (do NOT paste a divergent copy). Trimming/blank-null
discipline as an explicit per-caller hook; malformed/blank ⇒ `null` so the collector degrades to a
`SourceFailure`.

**Collector** `FdaClearanceCollector : IEvidenceCollector`:

- `CollectorName => "fda"`, `SourceType => EvidenceSourceType.RegulatoryApproval`.
- Injected: `IFdaClearanceReader`, `ILogger`, `TimeProvider`, `FdaCollectorOptions`.
- Iterate `context.FeedsOfType("fda")` (deterministic order). Build `companiesById`. Per feed:
  `FdaFeedTarget.Parse(feed.Url)`; `null` ⇒ `SourceFailure(feed.Name, feed.Url, "malformed fda feed token")`
  + Warning + continue. Compute `decisionFloor = today - options.LookbackDays` (default **365** — device
  clearances are lower-frequency than patents/press, so a longer window avoids mostly-empty snapshots) via
  the injected `TimeProvider`. `ReadAsync(target.ApplicantName, decisionFloor, ct)`; `!IsSuccess` ⇒
  `SourceFailure` (+ `result.Detail`) + continue.
- On success compute `count = result.Result.ClearanceCount`,
  `sample = result.Result.Clearances.Take(options.MaxSampleClearances)`, and emit **one** `CollectedEvidence`
  (`MapToEvidence`):
  - **Title** embeds the verbatim phrase + count, e.g.
    `"FDA clearance or approval (recent) — {count} device clearances/approvals for '{applicant}' in the last {lookbackDays} days"`.
  - **RawText** embeds the phrase + count + applicant + window + retrieved timestamp for hash distinctness
    (NO raw device names), e.g. `"Applicant '{applicant}': {count} FDA device clearances/approvals since
    {decisionFloor:o}, as of {retrievedAtUtc:o}. Signal: fda clearance or approval (recent)."`.
  - `SourceUrl` = a stable human-viewable openFDA/FDA link if one is available from the spike, else the query
    URL; `PublishedAt` = `CollectedAt` = `TimeProvider.GetUtcNow()` (a snapshot window has no single publish
    date). **Each run produces a distinct timestamped snapshot** (the RawText timestamp makes the ContentHash
    distinct run-to-run) — this **is** the accrued clearance history slice B reads; state this explicitly.
  - **Metadata** (`Dictionary<string,string>`, Ordinal): `quality = "High"` (FDA decisions are an
    authoritative public-record source, on par with SEC/USASpending `High`); `fdaFeedUrl` (= `feed.Url`);
    `applicant`; `clearanceCount`; `lookbackDays`; `decisionFloor` (`o`/`yyyy-MM-dd`); `sampleClearances`
    (the first N `"{submissionNumber} [{track}]: {deviceName}"` joined with `" | "` — provenance/debug only,
    **not** scanned by the extractor); `reportedTotal510k` / `reportedTotalPma` (optional cross-checks);
    `retrievedAtUtc` (`o`).
  - `CompanyHints = CollectorCompanyHints.For(feed.CompanyId, companiesById)` — never invent a ticker.
- Populate `CollectionSummary` exactly like `SecForm4Collector`/`UsaSpendingContractCollector`/
  `PatentActivityCollector`/`FccEquipmentAuthorizationCollector` (checked, ok, failed, count, failures); log
  per-feed + aggregate.
- `FdaCollectorOptions`: `LookbackDays` (default 365), `MaxSampleClearances` (default 5), `MaxPageSize`
  (default 100). (Add `ApiKeyEnvVar` only if the spike proves the keyed tier is wanted.)

### 4. Feed-token seed (additive, `data/companies.json`)

Add one `fda` feed to the **2–3** companies verified in the reachability spike (record the exact applicant
names here as the seed of record; leave the other names out — partial coverage is normal, exactly like
`usaspending`/`hiringats`/`patents`/`fccauth`). Token form `applicant=<verified applicant organization
name>`; feed `name` e.g. `"AxoGen — Recent FDA device clearances (openFDA)"`. Additive edit only — leave
every existing feed untouched. Per spec 97 the feed-Id folds the feed **type**, so these do not collide with
any existing feed.

### 5. Wiring (opt-in, OFF by default)

- `AddFdaClearanceCollector(this IServiceCollection, FdaCollectorOptions options)` in
  `InfrastructureServiceCollectionExtensions.cs`: register a named typed `HttpClient`
  (`ConfigurePrimaryHttpMessageHandler` with gzip/deflate `AutomaticDecompression`, generic `User-Agent`,
  mirroring `AddUsaSpendingContractCollector`/`AddPatentActivityCollector`), register `IFdaClearanceReader`,
  `AddSingleton(options)`, `TryAddSingleton(TimeProvider.System)`, and
  `AddSingleton<IEvidenceCollector, FdaClearanceCollector>()`.
- `RadarWorkerServices.cs`: add a `"fda"` enable-able kind branch (register via `AddFdaClearanceCollector`
  from a new `RadarWorkerOptions.Fda` block); extend **all three** valid-kinds messages (the empty-list, the
  null/blank-entry, and the unknown-kind messages) to include `"fda"`.
- `RadarWorkerOptions.cs`: add `FdaWorkerOptions Fda { get; init; } = new();` (bound from `Radar:Fda`) with
  `LookbackDays` (365), `MaxSampleClearances` (5), `MaxPageSize` (100), mirroring the other `*WorkerOptions`
  blocks.
- `appsettings.json`: add a documented, disabled-by-default `Radar:Fda` section; **leave `Radar:Collectors`
  default unchanged** (fda stays opt-in).
- **`scripts/run-profiles/default.json`: do NOT add `"fda"` to `Collectors`.** It stays opt-in/off.
  Promoting it into the baseline is a later deliberate fingerprint re-stamp after a live measurement
  validates the axis — out of scope.

---

## Fingerprint + sequencing (load-bearing)

Shipping this bumps `KeywordSignalExtractor.RuleSetVersion` **v5 → v6**, which spec 95's
`SignalSourceDescriptor` folds into the scoring-config fingerprint **independently of which collectors are
enabled**. Therefore:

- **The DEFAULT/baseline fingerprint re-stamps the moment this merges — even though `fda` is opt-in-OFF.**
  The default descriptor's enabled-collector CSV is **unchanged** (still the 6-collector set); the
  fingerprint moves **solely** because `rules=radar-keyword-rules-v5` → `…-v6`. There is no fingerprint-safe
  way to add a new scoring-affecting signal type — spec 95 working as intended (as specs 103/127/128
  re-stamped).
- **No `_formula.Version` bump** (`radar-formula-v8` stays). Only `RuleSetVersion` bumps. No weight /
  attention-tier / insider-tier change.
- **Efficacy interaction (spec 101/108) — this re-stamp is SCORE-NEUTRAL, a cosmetic boundary.** The
  collector is opt-in-OFF and the new rule matches only the FDA phrase (which no existing evidence contains),
  so **every company scores byte-identical before and after** — only the fingerprint *string* changes. Spec
  108's continuity-aware segmentation connects the score line across a score-continuous re-stamp; state
  plainly that this boundary is an input-hash artifact, not a measurement break.
- **Re-pin the fingerprint by RUNNING the test, not by hand** (same procedure as specs 103/127/128): update
  the `RuleSetVersion` literal to v6, run `dotnet test`, read the **actual** default fingerprints the failing
  pins report, and paste those exact values in. Do **not** hand-compute a SHA. The re-pin surface
  (grep-verify against the post-128 tree before editing):
  - `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — the `RuleSetVersion` const (+ comment).
  - `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — the `…rules-v5…` descriptor
    literal → `…rules-v6…`; the `ChangedSignalSourceDescriptor` variant; the pinned **AI-OFF** default
    fingerprint → the NEW value from the run; the lineage comment.
  - `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs` — `…rules-v5…` → `…rules-v6…`.
  - `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs` — any `…rules-v5…` literal.
  - `tests/Radar.Infrastructure.Tests/FileSystem/FileScoringConfigStoreTests.cs` — the fixture descriptor
    `…rules-v5…` → `…rules-v6…` **if** it fails.
  - `scripts/run-profiles/default.json` — the `_comment`'s `radar-keyword-rules-v5` → `v6` reference and the
    AI-OFF / AI-ON default-fingerprint references (re-pin from the RUN, never by hand; note the AI-ON
    transition in the comment if no test pins it live).
  - `docs/architecture-decisions.md` — add an AD-10 lineage line recording `RuleSetVersion` v5→v6 (new
    `RegulatoryApproval` rule group) and the default fingerprint transition(s) (old → new), noting scoring
    math is byte-identical and the collector is opt-in-off (mirror the spec-103/127/128 lineage entries).
- **Spec-98 collection-health check:** a declared-but-disabled `fda` feed type has **declared == reached** ⇒
  **no** `feeds-lost-before-collection` warning. **No `SeedFeedInventoryValidator` change.** Prove it with a
  test; introduce no spurious warning on a default (fda-off) run.

---

## Tests

- **`HttpFdaClearanceReaderTests`** (offline, fake `HttpMessageHandler`): a `{"results":[…]}` fixture for
  510(k) and PMA → correct merged `ClearanceCount` + submission numbers + device names + decision dates +
  track; openFDA empty-search `404` "No matches found" → `Success` with 0 clearances (assert this explicit
  handling); missing `results` / root-not-object → `Malformed`; non-success status (other than the empty-404)
  → `HttpError`; thrown `TaskCanceledException` (timeout) → `Timeout`; `HttpRequestException` →
  `Unreachable`; caller cancellation re-throws. **No network.**
- **`FdaFeedTargetTests`**: `applicant=AxoGen, Inc.` parses (value may contain spaces/commas); missing key /
  blank value / blank token → `null`; whitespace trimmed; routed through the shared `SingleKeyFeedToken`.
- **`FdaClearanceCollectorTests`** (fake `IFdaClearanceReader`): a successful search → **one**
  `RegulatoryApproval` `CollectedEvidence` whose Title/RawText contain the verbatim
  `fda clearance or approval (recent)` phrase and the count (and **no** raw device names); metadata carries
  `applicant`/`clearanceCount`/`lookbackDays`/`decisionFloor`/`sampleClearances`/`retrievedAtUtc`/
  `quality=High`; `CompanyHints` = feed-bound ticker; UTC instants from the injected `TimeProvider`;
  `decisionFloor` = now − `LookbackDays`; a malformed token and a reader failure each degrade to a
  `SourceFailure`/no-evidence without throwing; `CollectionSummary` counts correct; deterministic order.
- **Extractor mapping** (extend `KeywordSignalExtractorTests`): `RegulatoryApproval` evidence whose text
  contains `fda clearance or approval (recent)` → exactly one `RegulatoryApproval` **Positive** signal at the
  chosen routine strength, with a verbatim excerpt; and a guard that the phrase does **not** also fire an
  unrelated rule (and that a sample device name placed only in metadata does not leak into matching).
- **Fingerprint re-pin**: `ScoringConfigFingerprintTests` green with the `RuleSetVersion`-v6 descriptor and
  the NEW pinned default fingerprint(s) (obtained by running); `SignalSourceDescriptorTests`,
  `ScoringEngineTests`, `FileScoringConfigStoreTests` updated to v6 where they pin it.
- **Default (fda-off) composition / no false warning**: a Worker-DI or runner test with the default
  `Collectors` (no `fda`) but the seed containing the `fda` feeds registers the collector set **without**
  `fda` and produces **no** `feeds-lost-before-collection` warning for the `fda` feed type. If a Worker-DI
  test asserts the enable-able kind list, extend it to include `"fda"`.
- **Seed**: a `LocalFileCompanySeedSource` test asserts the verified companies carry an `fda` feed with the
  exact applicant tokens, each with a distinct feed Id (spec 97).
- Existing tests (all collectors, runner merge, DI list, Worker DI, extractor, scoring) stay green. Full
  gate: `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based mapping; **NO AI**.
- All HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8). Reuse
  `CollectorCompanyHints`, `CollectionSummary`/`SourceFailure`, the `CollectedEvidence`/
  `CollectedEvidenceMapper` path, the established non-SEC reader pattern, and the shared
  `SingleKeyFeedToken` (spec 128) — **no duplicated HTTP/parse/token primitives**. Do **not** reuse
  SEC-specific helpers.
- Append-only Domain enum edits (add at the END; never reorder/remove; persisted by name).
- Graceful degradation: typed non-throwing outcomes; a company with zero recent clearances (incl. openFDA's
  empty-search 404) is a valid `Success` (not an error); a bad token / unreachable source yields a
  `SourceFailure` + zero evidence; only genuine caller cancellation propagates.
- **Provenance preserved** (openFDA → `RegulatoryApproval` evidence with applicant + count + window →
  `RegulatoryApproval` signal → score). **No advice language** (AD-9): the factual "fda clearance or approval
  (recent)" phrasing carries no recommendation; direction is internal to scoring.
- Store timestamps in UTC; IDs `Guid`. Keep `MaxSampleClearances`/`MaxPageSize` bounded. Sample device names
  are metadata-only and never enter the extractor's searchable text.
- **Only `RuleSetVersion` bumps** (v5→v6); **no** `_formula.Version` / weight / tier change.
  `ScoringConfigVersion` re-stamps automatically via `SignalSourceDescriptor`; re-pin the tests to the value
  obtained by RUNNING them.

---

## Out of scope / future slices (record, do not build)

- **The NEGATIVE counterpart — FDA recalls / device enforcement collector** (`device/enforcement.json`,
  `recall = Negative`) — the pipeline's **first Negative-direction collector**, feeding "Thesis
  deteriorating" and adding symmetry to a positive-heavy pipeline. Deserves its own slice and its own signal
  decision (a recall is a discrete Negative event with clear valence). This is the highest-value FDA
  follow-up.
- **Slice B — directional surge detection** (a *newly-appearing* clearance / an accelerating count vs the
  accrued clearance history this slice writes). Would raise strength/confidence on a fresh clearance; reads
  the timestamped evidence metadata; no separate history store needed.
- **Drug approvals** (`drug/…`) and **NDA/BLA** tracks — additive endpoints for future biotech names.
- **Promoting `fda` into the baseline `default.json`** — a later deliberate fingerprint re-stamp after a
  live measurement validates the axis.
- **Device-class / product-code enrichment** — a richer materiality read; future slice.

---

## Acceptance criteria

- [ ] Two append-only Domain members added at the END of their enums:
      `EvidenceSourceType.RegulatoryApproval` (NOT in `IsThirdPartyAttentionSource`) and
      `SignalType.RegulatoryApproval`; no exhaustive `SignalType`/`EvidenceSourceType` switch broken.
- [ ] One `KeywordSignalExtractor` rule maps the verbatim phrase `fda clearance or approval (recent)` →
      `RegulatoryApproval` **Positive** at **routine** strength (one signal per run, NOT count-proportional);
      the phrase does not trip another rule; `RuleSetVersion` bumped `radar-keyword-rules-v5` → `v6`.
      **The maintainer/reviewer confirms the Positive direction** (the fallback Neutral posture is recorded).
- [ ] `FdaClearanceCollector` (kind `"fda"`, `CollectorName "fda"`, `SourceType RegulatoryApproval`) reads
      `FeedsOfType("fda")`, parses the `applicant=…` token via the shared `SingleKeyFeedToken`, reads recent
      510(k)/PMA clearances via `IFdaClearanceReader` (openFDA), and emits **one** `RegulatoryApproval`
      evidence per company with the fixed phrase in Title/RawText (no raw device names) + rich metadata
      (applicant, count, window, sample clearances, retrievedAtUtc, `quality=High`) + feed-bound hint.
- [ ] Reader returns typed non-throwing outcomes (empty result / openFDA empty-404 = `Success` 0 clearances);
      malformed token / reader failure degrade to `SourceFailure` + zero evidence; caller cancellation
      propagates.
- [ ] `FdaFeedTarget` parses `applicant=…` via the shared `SingleKeyFeedToken` (spec 128) — no divergent
      copy; `null` on malformed.
- [ ] Additively registered via `AddFdaClearanceCollector`; enable-able by `Radar:Collectors` containing
      `"fda"`; **default `Radar:Collectors` and `scripts/run-profiles/default.json` unchanged (opt-in OFF)**;
      the verified `fda` feeds seeded in `data/companies.json` with the exact applicant tokens, each with a
      distinct feed Id (spec 97).
- [ ] A default (fda-off) run/composition registers the collector set **without** `fda` and produces **no**
      `feeds-lost-before-collection` warning for the declared `fda` feeds (spec-98 interaction; no validator
      change).
- [ ] **No `_formula.Version`/weight/tier change; only `RuleSetVersion` v5→v6.** The default fingerprint(s)
      re-stamp automatically; `ScoringConfigFingerprintTests` (and the other v5-pinning tests) re-pinned to
      the value(s) obtained by **running** the test; `default.json` `_comment` + `architecture-decisions.md`
      AD-10 lineage updated; the efficacy-segment boundary is noted as score-neutral.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
