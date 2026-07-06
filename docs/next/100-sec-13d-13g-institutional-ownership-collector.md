# Task: SEC 13D/13G institutional & activist ownership collector

> **DIRECTED FEATURE — second of two slices (read first).** This is the **infrastructure half** of the new
> institutional/activist ownership axis. Spec **99**
> (`docs/99-institutional-ownership-signal-type-and-extractor-rules.md`) lands the
> `SignalType.InstitutionalOwnership` enum value and the extractor rule group that maps three fixed ownership
> phrases; **this slice must be sequenced AFTER it**. This slice adds a deterministic `sec13dg` collector that
> fetches each watch company's **Schedule 13D / 13G beneficial-ownership filings**, classifies each by form
> type, and emits `Filing` evidence carrying **exactly the fixed phrases spec 99 defined** — so the extractor
> already maps them to `InstitutionalOwnership` Positive/Neutral. This is the "smart money / activist
> ownership" axis, independent from insider (`secform4`), filing (`sec`), gov-demand (`usaspending`), and news.

> **⚠️ VERIFICATION GATE — do this FIRST, before writing any collector code.** This slice assumes 13D/13G
> filings **filed about a watch company appear in that company's own `submissions/CIK{cik}.json`
> `filings.recent` list** (the same endpoint the `sec` and `secform4` collectors already read), filtered by
> form type. **Confirm this against a real subject CIK before building** (e.g. with a compliant User-Agent,
> `GET https://data.sec.gov/submissions/CIK0001049521.json` for Mercury Systems and check whether
> `filings.recent.form` contains any `SC 13D` / `SC 13G` / `SC 13D/A` / `SC 13G/A` values). EDGAR indexes
> 13D/13G under the **subject** company in the browse-EDGAR company view, but the `data.sec.gov` submissions
> JSON historically lists filings by **filer** — and a 13D/13G is filed by the *beneficial owner*, not the
> subject. **If the subject's submissions JSON does NOT list inbound 13D/13G filings, STOP and escalate:** the
> collector must instead source them via EDGAR **full-text search** (`https://efts.sec.gov/LATEST/search-index`
> filtered by subject entity + `forms=SC 13D,SC 13G`) — a larger, separate endpoint/design that changes this
> spec's shape. Do **not** silently build against the wrong endpoint. Record the verification outcome in the PR
> description. (Everything below is written for the submissions-endpoint path, which is the intended design if
> the gate passes.)
>
> **PRE-VERIFIED (2026-07-06, maintainer session):** the submissions endpoint was confirmed live against four
> subject CIKs — `filings.recent.form` DOES contain `SC 13D` / `SC 13D/A` / `SC 13G` / `SC 13G/A` (counts:
> AGYS 81, MRCY 70, HLIO 66, CYRX 50 SC-13* filings; 13G/13G-A dominate, 13D is rare — 0–2 per name). **The
> gate passes** — the coder should cheaply re-confirm against one CIK and proceed on the submissions-endpoint
> path (no `efts.sec.gov` escalation needed). Note the exact form strings observed (`SC 13D`, `SC 13D/A`,
> `SC 13G`, `SC 13G/A`) match the classifier's `StartsWith`/`EndsWith("/A")` matcher.

## Overview

A new **SEC 13D/13G collector** (`IEvidenceCollector`, kind `"sec13dg"`, `CollectorName "sec-13dg"`) reads each
watch company's EDGAR submissions JSON, filters `filings.recent` to the beneficial-ownership form types
(`SC 13D`, `SC 13D/A`, `SC 13G`, `SC 13G/A`), classifies each by form, and maps it to one
`EvidenceSourceType.Filing` evidence item carrying a **deterministically-chosen fixed phrase** (spec 99's
contract): activist 13D → `activist beneficial-ownership stake (13d)`; passive 13G →
`passive beneficial-ownership stake (13g)`; any `/A` amendment → `beneficial-ownership amendment (routine)`.
The extractor (spec 99) already turns those into `InstitutionalOwnership` Positive (13D) / Neutral (13G) /
Neutral (amendment) signals; the formula folds them into TrajectoryScore as directional corroboration (like
`InsiderBuying`). Only the rare activist 13D is bullish in v1; passive 13G is Neutral (maintainer decision — see
spec 99 rationale).

**Deterministic-first (AD: deterministic before AI).** v1 emits from **submissions/filing METADATA ONLY** —
form type, filing date, accession, index URL. It does **not** fetch or parse the free-form 13D/13G filing
body, so it does **not** extract the **% of class**, the **filer/reporting-person name**, or the 13D **Item 4
"Purpose of Transaction" intent**. Those live in unstructured HTML (no reliable structured XML like Form 4's
ownership document), so a cheap deterministic parse is not available — each is an explicit deferred follow-up
(see Out of scope). Direction/strength therefore come from **form type alone**, which is exactly what the
metadata reliably carries.

This threads the normal `collect → map → resolve → review → store` path, so provenance is intact end-to-end
(13D/13G submissions row → `Filing` evidence with the filing index URL + accession → `InstitutionalOwnership`
signal → score). Opt-in via `Radar:Collectors`; the default is byte-for-byte unchanged.

---

## Assignment

Worktree: any — mostly new Infrastructure files under `Sec/` + additive DI + one enable-able collector kind +
seed data. It **extracts a shared columnar submissions-row helper and routes `HttpSecForm4Reader` through it**
(reuse-over-copy — see below), so it **must NOT run in parallel** with any SEC-reader / Form 4 slice; it also
edits `RadarWorkerServices.cs` / `RadarWorkerOptions.cs` / `InfrastructureServiceCollectionExtensions.cs` /
`appsettings.json` / `data/companies.json`, so **sequence** rather than parallelize against any slice touching
Worker composition / DI / the seed.
Dependencies: **99** (the `InstitutionalOwnership` type + the extractor phrase contract — MUST be merged
first), 93 (the `secform4` collector this mirrors — merged), 56 (`SecEdgarUrls`/`SecHttpFetch`/reader pattern —
merged), 97 (feed-Id folds feed type, so a third feed sharing the submissions URL no longer collides — merged).
Conflicts with: spec 99 (sequence AFTER); any Form 4 / SEC-reader / Worker-composition / seed slice.
Estimated time: ~1.5 h

---

## SignalType: no Domain change here

`SignalType.InstitutionalOwnership` is added by spec **99**. This slice **only feeds** it (via the fixed
phrases the extractor already maps) — **no Domain enum change, no formula change, no `RuleSetVersion` bump**
here. Enabling this collector re-stamps `ScoringConfigVersion` **automatically** via spec 95's
`SignalSourceDescriptor` (the enabled collector-name set is hashed) — so **no manual fingerprint work** in this
slice. Confirm the fingerprint tests pin a **representative** descriptor literal (they do not resolve the live
Worker collector set), so adding `sec-13dg` does not break them; if any test resolves the full Worker graph and
asserts the collector set, extend it to include `sec-13dg`.

---

## Form-type → category table (deterministic)

Mirror `SecForm4TransactionCode`: a small pure classifier keyed on the EDGAR form string (case-insensitive,
trimmed). Only the four beneficial-ownership forms are in scope; anything else is `NotApplicable` (filtered
out upstream).

| Form (starts-with, case-insensitive) | `/A`? | Category | Emitted phrase | Direction (via spec 99) |
|---|---|---|---|---|
| `SC 13D` | no | `Activist13D` | `activist beneficial-ownership stake (13d)` | Positive |
| `SC 13G` | no | `Passive13G` | `passive beneficial-ownership stake (13g)` | Neutral |
| `SC 13D` / `SC 13G` | **`/A`** | `Amendment` | `beneficial-ownership amendment (routine)` | Neutral |
| anything else | — | `NotApplicable` | (filtered — not collected) | — |

- **13D = activist with declared intent** (a forward catalyst) → activist phrase (Positive via spec 99). The
  only bullish ownership read in v1.
- **13G = passive > 5 % accumulation** → passive phrase, but **Neutral via spec 99** (not Positive): 13G is
  dominated by passive index filers v1 can't distinguish from conviction buyers, so it's kept Neutral to avoid
  mechanical-flow noise. The phrase the collector emits is unchanged; only spec 99's direction is Neutral.
- **`/A` amendment → routine/Neutral in v1.** Submissions metadata alone **cannot tell** an increase from a
  reduction or an **exit to 0** — a Neutral read is the conservative deterministic choice (it still lifts
  source diversity; Neutral contributes 0 to Trajectory, so it never misfires as bullish/bearish). Making
  amendments directional needs the % -of-class body parse — deferred (see Out of scope).
- Detect `/A` by a case-insensitive `EndsWith("/A")` (trimmed) test on the form string; detect 13D vs 13G by
  `StartsWith("SC 13D")` / `StartsWith("SC 13G")`. **Verify the exact form strings during the verification
  gate** and adjust the matcher if EDGAR uses variants (keep the classifier the single owner of the mapping).

---

## Reuse-over-copy: shared columnar `filings.recent` flattener

`HttpSecForm4Reader.ParseForm4Rows` already flattens the columnar `filings.recent` parallel arrays
(`form` / `filingDate` / `acceptanceDateTime` / `accessionNumber` / `primaryDocument`), filtered by form and
capped. This new reader needs the **same** flattening filtered by the 13D/13G form set. That is a **second
copy** of the columnar parse — the recurring 76/77/83 MEDIUM the `radar-architecture-reviewer` flags. **Extract
it, do not paste it:**

- Add `SecRecentFilings` (static, `Radar.Infrastructure.Sec`) with a method that takes the parsed
  `filings.recent` `JsonElement`, a **form-type predicate** (`Func<string, bool>` — the per-caller hook), a
  `maxRows` cap, and a `CancellationToken`, and returns aligned rows
  (`accession`, `filingDate`, `acceptanceDateTimeUtc`, `primaryDocument`, `form`) newest-first — folding the
  existing `GetArray` / `At` / `TryParseAcceptance` scaffolding and the "skip rows with no accession /
  unparseable acceptance" rules.
- **Route both call sites through it:** the new `HttpSec13DGReader` (predicate = the 13D/13G form set) **and**
  `HttpSecForm4Reader.ParseForm4Rows` (predicate = `form == "4"`). Keep each reader's genuinely-per-source
  behaviour as its own hook — Form 4's subsequent **raw-ownership-XML fetch + classify** stays in
  `HttpSecForm4Reader`; this reader's **form-type classify** stays in `HttpSec13DGReader`. Share the columnar
  core, not the divergent edges.
- The general `HttpSecFilingReader` also parses `filings.recent` but does more per-row work (item titles); a
  parallel migration is a reasonable **follow-up** — note it, do NOT do it here (keeps the slice scoped and
  avoids touching the `sec` collector's reader).

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  SecRecentFilings.cs              # NEW: shared columnar filings.recent flattener (form predicate + cap hook)
  ISec13DGReader.cs                # NEW: ReadAsync(submissionsUrl, ct) -> Sec13DGReadResult
  Sec13DGReadResult.cs             # NEW: outcome enum (mirror SecForm4ReadOutcome) + parsed Sec13DGFiling items
  Sec13DGFiling.cs                 # NEW: accession, filingDate, acceptanceUtc, indexUrl, form, category
  Sec13DGFormType.cs               # NEW: form string -> {Activist13D, Passive13G, Amendment, NotApplicable}
  HttpSec13DGReader.cs             # NEW: submissions JSON -> SecRecentFilings(13D/G predicate) -> classify (metadata only, NO body fetch)
  Sec13DGCollectorOptions.cs       # NEW: required UserAgent, MaxFilingsPerCompany (default e.g. 20)
  Sec13DGCollector.cs              # NEW: IEvidenceCollector; CollectorName "sec-13dg"; SourceType Filing; FeedsOfType("sec13dg")
  HttpSecForm4Reader.cs            # MODIFIED: route ParseForm4Rows through SecRecentFilings (reuse-over-copy)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddSec13DGCollector(...) additive registration + named HttpClient (UA + gzip)

src/Radar.Worker/
  RadarWorkerServices.cs           # MODIFIED: add "sec13dg" enable-able kind; extend valid-kinds messages
  RadarWorkerOptions.cs            # MODIFIED: Sec13DGWorkerOptions (UserAgent, MaxFilingsPerCompany) bound from Radar:Sec13DG
  appsettings.json                 # MODIFIED: leave Collectors default unchanged; add a documented Sec13DG section

data/companies.json                # MODIFIED (data): add per-company "sec13dg" feed (same CIK submissions URL as "sec"/"secform4"); omit any delisted issuer

tests/Radar.Infrastructure.Tests/Sec/
  SecRecentFilingsTests.cs         # NEW: columnar flatten + predicate filter + cap + skip-bad-rows; Form4-parity
  HttpSec13DGReaderTests.cs        # NEW: offline (fake HttpMessageHandler + fixture submissions JSON)
  Sec13DGCollectorTests.cs         # NEW: fake reader -> Filing evidence with phrase/direction/hints/summary; degrade
  SecForm4CollectorTests.cs        # (unchanged) — assert Form4 still green after the ParseForm4Rows migration
```

---

## Implementation details

### Reader (`HttpSec13DGReader : ISec13DGReader`)

- Named `HttpClient` configured exactly like `HttpSecForm4Reader` (UA via
  `TryAddWithoutValidation("User-Agent", options.UserAgent)`, `Accept-Encoding: gzip, deflate`, automatic
  decompression). **Reuse `SecHttpFetch.GetAsync`** for the submissions fetch + outcome-mapping ladder
  (Success / Unreachable / HttpError / Forbidden / Timeout) and **`SecEdgarUrls`** for URL/CIK building — NO
  duplicated SEC URL/HTTP logic.
- Parse the submissions JSON exactly like `HttpSecForm4Reader` (root object, non-blank `cik`, `filings.recent`
  object — the same `Malformed` guards; a blank cik / absent `filings.recent` is `Malformed`, not a silent
  zero-item Success). Then call `SecRecentFilings` with the **13D/13G form predicate** and
  `MaxFilingsPerCompany`.
- For each flattened row, classify via `Sec13DGFormType` and build a `Sec13DGFiling`
  (`IndexUrl = SecEdgarUrls.BuildIndexUrl(cik, accession, ".htm")`). **No second HTTP fetch** — v1 is
  metadata-only (this is the key simplification vs the Form 4 reader, which additionally fetches ownership XML).
  A row that classifies `NotApplicable` is skipped (defensive — the predicate already excluded it).
- **Never throw** on a bad feed: reuse the mirrored outcome set; a submissions-level failure degrades the whole
  feed to the typed outcome + empty items (logged Warning). Re-throw only genuine caller cancellation
  (`OperationCanceledException when ct.IsCancellationRequested`). All HTTP/JSON/SEC code stays in
  `Radar.Infrastructure` (AD-5); all new types `internal` (the test project already has `InternalsVisibleTo`).

### Collector (`Sec13DGCollector : IEvidenceCollector`)

- `CollectorName = "sec-13dg"`, `SourceType = EvidenceSourceType.Filing`. Iterate `context.FeedsOfType("sec13dg")`
  (deterministic order), read via `ISec13DGReader`, cap at `MaxFilingsPerCompany`, dedupe within a feed by
  accession (mirror `SecForm4Collector` exactly).
- Map each `Sec13DGFiling` to a `CollectedEvidence` (`SourceType = Filing`) whose synthesized **Title/RawText
  embed the fixed phrase** the extractor matches (factual, advice-free — only real metadata: form, filing date,
  accession):
  - `Activist13D` → e.g. `"Schedule 13D — activist beneficial-ownership stake (13d) filed {filingDate} (accession {accession})"`.
  - `Passive13G` → `"… passive beneficial-ownership stake (13g) …"`.
  - `Amendment` → `"… beneficial-ownership amendment (routine) …"`. Include accession + date in the hashed text
    so distinct filings hash distinctly under the mapper's Title+RawText `ContentHash`.
  - **Provenance:** `SourceUrl` = the filing **index URL**; `PublishedAt` = `acceptanceDateTimeUtc` (UTC —
    windowing/recency); `CollectedAt` = `TimeProvider.GetUtcNow()`.
  - **Metadata:** `quality = "High"` (SEC primary source, matching the spec-56/93 collectors); `secFeedUrl`,
    `accessionNumber`, `form` (the real form string, e.g. `"SC 13D"`), `filingDate`, and `ownershipCategory`
    (`Activist13D`/`Passive13G`/`Amendment` — debug/traceability, NOT read by the extractor). **No % -of-class
    materiality metadata in v1** (deferred), so — unlike InsiderBuying — the extractor uses the fixed rule
    strengths.
  - **CompanyHints:** `CollectorCompanyHints.For(feed.CompanyId, companiesById)` — feed-bound ticker; never
    invent one.
- Populate `CollectionSummary` (checked/failed + `SourceFailure` list) exactly like `SecForm4Collector` so the
  merged run summary reflects it. Log per-feed + aggregate outcomes.

### Config, DI & seed

- `Sec13DGCollectorOptions`: `UserAgent` (**required** — fail fast, mirror `SecForm4CollectorOptions`),
  `MaxFilingsPerCompany` (default e.g. **20** — 13D/13G are far less frequent than Form 4, but keep the fetch
  bounded).
- `AddSec13DGCollector` registers the reader + collector additively
  (`AddSingleton<IEvidenceCollector, Sec13DGCollector>()`) and the named `HttpClient`; fail fast on a blank
  `UserAgent` and a non-positive `MaxFilingsPerCompany` with `Radar:Sec13DG:*` messages (mirror
  `AddSecForm4Collector`). `TryAddSingleton(TimeProvider.System)`.
- `RadarWorkerServices` gains `"sec13dg"` as an enable-able kind and threads `RadarWorkerOptions.Sec13DG`;
  extend every valid-kinds message to include `"sec13dg"`. Default `Radar:Collectors` unchanged.
- **Seed:** add a `sec13dg` feed to each seeded company in `data/companies.json` (the **same** verified CIK
  submissions URL as the `sec`/`secform4` feeds), omitting any delisted issuer per the existing seed. Per spec
  97, the feed-Id folds the feed **type**, so a third feed sharing the submissions URL (`sec` + `secform4` +
  `sec13dg`) has a **distinct** feed Id and does **not** collide — verify a seed-source test still yields three
  distinct feeds for that shared URL.

---

## Tests

- `SecRecentFilingsTests` (NEW): a fixture `filings.recent` with a mix of forms + a supplied predicate returns
  only matching rows, newest-first, capped; rows with a blank accession or unparseable acceptance are skipped;
  culture-invariant acceptance parse. **Form 4 parity:** the `form == "4"` predicate returns the same rows the
  old `ParseForm4Rows` did (guards the migration).
- `HttpSec13DGReaderTests` (offline, fake `HttpMessageHandler`): a fixture submissions JSON with a mix of forms
  parses only `SC 13D` / `SC 13D/A` / `SC 13G` / `SC 13G/A`; each maps to the correct `Sec13DGFiling` category
  and index URL. `SC 13D` → `Activist13D`, `SC 13G` → `Passive13G`, `SC 13D/A` / `SC 13G/A` → `Amendment`; an
  unrelated form (`8-K`, `4`) is excluded. Malformed/empty submissions JSON → `Malformed`; a blank cik →
  `Malformed`; 403 → `Forbidden`; a thrown `TaskCanceledException` (timeout) → `Timeout`; caller cancellation
  re-throws. **No network.** (No per-filing body fetch to test — v1 is metadata-only.)
- `Sec13DGCollectorTests` (fake `ISec13DGReader`): parsed filings map to `Filing` `CollectedEvidence` with the
  correct **fixed phrase** in Title/RawText for each category, `High` quality, index-URL provenance, accession
  + form + `ownershipCategory` metadata, UTC observed instant from `acceptanceDateTime`, the feed's CompanyId
  as hint; `MaxFilingsPerCompany` honoured; dedupe by accession; a `Forbidden`/empty feed degrades to a
  `SourceFailure`/no evidence without throwing; `CollectionSummary` counts correct; deterministic order.
- `SecForm4CollectorTests` / `HttpSecForm4ReaderTests` stay green after routing `ParseForm4Rows` through
  `SecRecentFilings` (the migration is behaviour-preserving).
- Provenance end-to-end (cheap): a 13D evidence resolves and (with spec 99's extractor) produces a stored
  `InstitutionalOwnership` Positive signal whose `EvidenceId` traces to the 13D evidence — extend an existing
  integration/runner test only if cheap; otherwise the collector + spec-99 extractor unit coverage suffices.
- Existing tests (RSS, SEC, Form 4, USASpending, News, runner merge, DI list, Worker DI) stay green; the
  multi-collector runner now composes the enabled set + `sec-13dg` when `"sec13dg"` is enabled.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based classification; **NO AI**.
- All SEC/HTTP/JSON confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8); reuse
  `SecEdgarUrls` / `SecHttpFetch` / `CollectorCompanyHints` and the extracted `SecRecentFilings` — **no
  duplicated SEC URL/HTTP/columnar logic** ("reuse over copy").
- Graceful degradation: typed non-throwing outcomes; a delisted/quiet issuer or a bad feed yields zero
  evidence, not an error; only genuine caller cancellation propagates.
- Provenance preserved (13D/13G submissions row → `Filing` evidence with index URL + accession →
  `InstitutionalOwnership` signal → score). No advice language (AD-9): the factual "activist/passive
  beneficial-ownership stake" phrasing carries no recommendation; direction is internal to scoring.
- Store timestamps in UTC; IDs `Guid`. Honour the SEC User-Agent + polite/sequential request rules; keep
  `MaxFilingsPerCompany` bounded.
- **No formula/weight/attention-tier/`RuleSetVersion` change** here (spec 99 owns the type + rules); enabling
  the collector re-stamps `ScoringConfigVersion` automatically via `SignalSourceDescriptor` — do NOT bump any
  version constant.

## Out of scope (note explicitly)

- **% of class / stake size** (a materiality-tier strength read like InsiderBuying's `insiderNetValue`) — needs
  parsing the unstructured 13D/13G filing body (no reliable structured XML); v1 uses fixed rule strengths.
  Deferred.
- **Filer / reporting-person name** — not a `filings.recent` column; needs the filing body. Deferred.
- **AI read of the 13D Item 4 "Purpose of Transaction" intent** — the classic activist-catalyst signal; a
  future AI slice behind `Microsoft.Extensions.AI`, out of scope (deterministic-first).
- **Directional amendments** (increase → Positive, exit/reduction → Negative) — needs the % -of-class delta
  from the body; v1 amendments are Neutral.
- **Form 13F** (institutional quarterly holdings) — filed BY the institution, not on the subject, so mapping a
  13F back to a watch company needs a **reverse index** (institution → holdings → subject); a much bigger,
  separate slice. **Deferred entirely.**
- **Any non-SEC ownership source** (13F aggregators, proxy/DEF 14A ownership tables, exchange disclosures) —
  out of scope; SEC 13D/13G only.
- **Migrating `HttpSecFilingReader` to `SecRecentFilings`** — a reasonable follow-up; not done here to keep the
  `sec` collector's reader untouched.

## Acceptance criteria

- [ ] **Verification gate resolved and recorded in the PR:** confirmed 13D/13G appear in the subject's
      `submissions/CIK{cik}.json` `filings.recent` (or escalated to the full-text-search design if not).
- [ ] `Sec13DGCollector` (kind `"sec13dg"`, `CollectorName "sec-13dg"`) fetches each feed's submissions JSON
      with a compliant, configurable User-Agent, filters to `SC 13D`/`SC 13D/A`/`SC 13G`/`SC 13G/A`, and maps
      each to a `Filing` evidence item with an index-URL provenance link, accession/form/`ownershipCategory`
      metadata, UTC observed instant, feed-bound hint, and `High` quality.
- [ ] Form classification is exactly the table: original `SC 13D` → activist phrase (Positive via spec 99),
      original `SC 13G` → passive phrase (Neutral via spec 99), any `/A` → routine amendment phrase (Neutral);
      non-13D/G forms excluded. The emitted phrases are **verbatim** the spec-99 contract, so the extractor produces
      `InstitutionalOwnership` signals with no further extractor change.
- [ ] `SecRecentFilings` is extracted and **both** `HttpSec13DGReader` and `HttpSecForm4Reader` route through
      it (no second copy of the columnar `filings.recent` parse); Form 4 behaviour unchanged and green.
- [ ] The reader returns typed outcomes (incl. distinct `Forbidden` for 403), never throws on a bad feed;
      caller cancellation propagates; a blank `UserAgent` / non-positive `MaxFilingsPerCompany` is a fail-fast
      config error.
- [ ] Additively registered and enable-able via `Radar:Collectors` containing `"sec13dg"`; default config
      unchanged; seed carries a per-company `sec13dg` feed (verified CIK URL), distinct feed-Id from the
      `sec`/`secform4` feeds (spec 97); merged `CollectionSummary` reflects it.
- [ ] No formula/weight/`RuleSetVersion` change; `ScoringConfigVersion` re-stamps automatically via
      `SignalSourceDescriptor` when enabled; no version constant bumped.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
