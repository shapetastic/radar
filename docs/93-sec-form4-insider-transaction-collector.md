# Task: SEC Form 4 (insider-transaction) collector — directional insider-activity signals

> **DIRECTED + NOT ARCH-GATED (read first).** This is a **directed** task the maintainer asked for — NOT
> the generic planner loop and NOT architecture-gated. It plugs the exact gap the EOSE live run exposed:
> insider **selling** (a "CAO sells shares" event) was invisible to Radar, surfacing only as a Neutral
> `MediaAttention` echo. Insider buying/selling becomes a first-class **corroborating** signal
> (`SignalType.InsiderBuying`) and adds a distinct evidence source type, strengthening
> `EvidenceConfidenceScore` diversity (the corroboration thesis). Numbered **93**: `docs/` ends at 91 and a
> separate price-collector slice is being planned concurrently as **92** — 93 avoids that collision.

## Overview

Radar's `SignalType.InsiderBuying` enum value is **defined but unfed** — no collector or extractor produces
it today (verified: `InsiderBuying` appears only in `src/Radar.Domain/Signals/SignalType.cs` and the master
docs; no production code path emits it). This slice makes it a real, directional signal.

A new **SEC Form 4 collector** (`IEvidenceCollector`, kind `"secform4"`) fetches each watch-universe
company's recent Form 4 filings from the SEC submissions API, fetches and parses each Form 4's **structured
XML ownership document**, and turns the aggregated insider transactions into one `Filing`-type evidence item
per filing carrying a **deterministically-determined insider-activity direction** (Positive for open-market
purchases, Negative for discretionary open-market sales, Neutral/excluded for grants, exercises,
tax-withholding, gifts, and anything under a 10b5-1 pre-arranged plan). The existing deterministic
`KeywordSignalExtractor` then turns that evidence into an `InsiderBuying` signal via a small new
phrase→direction rule group (mirroring how the USASpending collector emits a "federal contract award" phrase
that the extractor maps to a `GovernmentContract` Positive signal, and how spec 66 scales its Strength from
an `awardAmount` metadata key). Strength is scaled by transaction $ value and multi-insider clustering via a
materiality tier table (the spec 66 precedent).

This is **deterministic-first** (rule-based on SEC transaction codes; NO AI). It threads the normal
`collect → map → resolve → review → store` path, so provenance is intact end-to-end (Form 4 XML → `Filing`
evidence with the filing index URL → `InsiderBuying` signal → score). Opt-in via `Radar:Collectors`; the
default `["rss"]` is byte-for-byte unchanged.

All external facts below were **verified live** against SEC endpoints on 2026-07-04 with a compliant
User-Agent (`Radar Research <email>`). Do NOT re-research — use these.

---

## Assignment

Worktree: any — mostly new Infrastructure files under `Sec/` + additive DI + one enable-able collector kind
+ a small additive extractor rule group + seed data. It **edits `KeywordSignalExtractor.cs`** (new rule
group + one materiality read), so it **must NOT run in parallel** with any other extractor / scoring /
directional-filing slice, and it edits `RadarWorkerServices.cs` / `RadarWorkerOptions.cs` /
`InfrastructureServiceCollectionExtensions.cs` / `appsettings.json` / `data/companies.json` — sequence
rather than parallelize against spec 92 (price) and any slice touching Worker composition / DI.
Dependencies: 56 (SEC EDGAR filing collector — merged; supplies `SecEdgarUrls`/`SecHttpFetch`/the reader
pattern to reuse), 66 (GovernmentContract materiality tiers — merged; the $-value tiering precedent),
54/55 (multi-collector composition + feed-type seam — merged).
Conflicts with: spec 92 (both touch Worker composition / DI / `appsettings.json`); any extractor/scoring
slice.
Estimated time: ~2 h

---

## Verified Form 4 source facts (do NOT re-research; use these)

- **Submissions API (same endpoint the spec-56 reader already uses):**
  `https://data.sec.gov/submissions/CIK##########.json` (CIK zero-padded to 10). The columnar
  `filings.recent` arrays carry `form` — **filter `form == "4"`**. Each Form 4 row exposes
  `accessionNumber` (e.g. `0001040470-26-000197`), `filingDate`, `acceptanceDateTime` (full UTC, `…Z`), and
  `primaryDocument`. **Keyless**; reuses the mandatory SEC `User-Agent` (403 without it) and the 10 req/s
  ceiling. Form 4s are numerous (AEHR filed 4 on a single day; Agilysys has dozens) — **cap per company**
  (`MaxFilingsPerCompany`) and stay sequential/polite.
- **The raw structured XML document name is NOT constant — derive it from `primaryDocument`.** For a Form 4,
  `primaryDocument` is the **XSL-rendered** path `xslF345XNN/<file>.xml` (verified variants:
  `xslF345X06/primary_01.xml`, `xslF345X06/form4.xml`, `xslF345X05/ownership.xml`). The **raw ownership XML**
  is that same `<file>.xml` at the filing-folder root — i.e. **strip the leading `xslF345XNN/` path
  segment** from `primaryDocument`. Build the URL as `{BuildArchiveBaseUrl(cik, accession)}/{rawFile}` using
  the existing `SecEdgarUrls` (verified: `https://www.sec.gov/Archives/edgar/data/1040470/000104047026000197/primary_01.xml`
  → 200, valid XML). Defensive rule: if `primaryDocument` contains a `/`, take the last path segment;
  otherwise use it as-is. If it does not end `.xml`, skip that filing (typed no-op, not a throw).
- **XML shape (verified — root `<ownershipDocument>`, NO XML namespace, plain elements):**
  - `<documentType>` = `4`; `<issuer>/<issuerCik>`, `<issuerName>`, `<issuerTradingSymbol>`;
    `<periodOfReport>`; `<reportingOwner>/<reportingOwnerId>/<rptOwnerName>` and
    `<reportingOwnerRelationship>` (`<isOfficer>`, `<isDirector>`, `<officerTitle>`).
  - **Document-level 10b5-1 flag:** `<aff10b5One>` — value is a boolean string with **both** casings/forms
    observed: `false` / `true` (AEHR) and `0` / `1` (Mercury). Treat `true`/`1` (case-insensitive) as
    "10b5-1 pre-arranged plan"; `false`/`0`/empty/absent as "not a plan". A `<footnote>` mentioning
    `10b5-1` is a **secondary** plan indicator (belt-and-braces).
  - **Transactions:** `<nonDerivativeTable>` contains zero or more `<nonDerivativeTransaction>` (and possibly
    `<nonDerivativeHolding>` — **skip holdings**, they carry no `transactionCoding`/`transactionAmounts`).
    `<derivativeTable>` likewise (usually empty for plain buys/sells). Each transaction carries:
    - `<transactionCoding>/<transactionCode>` — the **single-letter code** (verified real values: `A`, `S`;
      also `P`, `M`, `F`, `G`, `D`, `X`, `C`, `J` per the SEC code table below).
    - `<transactionAmounts>/<transactionShares>/<value>` (integer share count),
      `<transactionPricePerShare>/<value>` (decimal; `0` for grants), and
      `<transactionAcquiredDisposedCode>/<value>` = `A` (acquired) or `D` (disposed).
    - `<ownershipNature>/<directOrIndirectOwnership>/<value>` = `D`/`I`.
  - `<transactionTimeliness>` is present but usually **empty** — it is NOT the 10b5-1 flag (that is
    `<aff10b5One>`); do not use it for plan detection.
- **Verified real examples (fixtures should mirror these):**
  - AEHR `0001040470-26-000197` (`primary_01.xml`): one `<nonDerivativeTransaction>`, code `A` (grant,
    A/acquired, price 0), `<aff10b5One>false</aff10b5One>` → **Neutral / excluded** (RSU grant).
  - Mercury `0001049521-26-000030` (`form4.xml`): two `<nonDerivativeTransaction>` code `S` (D/disposed,
    ~$99/sh, 8000 + 1250 shares) + a `<nonDerivativeHolding>` (skip), `<aff10b5One>0</aff10b5One>` →
    **Negative** (discretionary open-market sale, ≈$923k).
- **SEC Form 4 transaction-code table (cite in code comments; source: SEC Form 4 general instructions /
  the EDGAR ownership XSL legend):**
  `P` open-market/private **purchase**; `S` open-market/private **sale**; `A` grant/award/other
  **acquisition** from the issuer; `M` **exercise/conversion** of a derivative security; `F` payment of
  exercise price or tax by **withholding** securities; `G` bona-fide **gift**; `D` disposition to the issuer
  (e.g. forfeiture); `X` **exercise** of in-the-money/at-the-money derivative; `C` **conversion** of a
  derivative; `J` **other** (footnote-described).

---

## The transaction-code → direction table (deterministic; get this right)

The collector aggregates the transactions **within one filing**, classifies each by code, and produces a
single **filing-level direction**. Only two codes are directional; everything else is Neutral/excluded, and
**anything under a 10b5-1 plan is forced Neutral** (a planned sale is not a discretionary bearish signal —
this is precisely why EOSE's "10b5-1 plan" / "tax-driven RSU vesting" sales must NOT read as bearish).

| Code | Meaning | A/D | Classification | Contributes to |
|------|---------|-----|----------------|----------------|
| `P` | Open-market / private **purchase** | A | **Bullish** — insider buying with own money | Positive $ |
| `S` | Open-market / private **sale** (discretionary only) | D | **Bearish** — discretionary sale | Negative $ |
| `A` | Grant / award from issuer | A | Neutral (compensation, not a market signal) | — |
| `M` | Derivative exercise / conversion | A/D | Neutral (mechanical) | — |
| `F` | Tax-withholding on vest/exercise | D | Neutral (tax, not discretionary) | — |
| `G` | Gift | A/D | Neutral | — |
| `D` | Disposition to issuer (forfeiture) | D | Neutral | — |
| `X`,`C` | Exercise / conversion of derivative | A/D | Neutral (mechanical) | — |
| `J` | Other (footnote-described) | A/D | Neutral (ambiguous → conservative) | — |
| *any unknown/blank code* | — | — | **Neutral** (conservative default) | — |
| *any code with `<aff10b5One>` true/1, OR a 10b5-1 footnote* | pre-arranged plan | — | **Neutral (override)** — planned, not discretionary | — |

**Filing-level direction** (deterministic, from the per-transaction classification):
- Compute `buyValue = Σ(shares × price)` over **discretionary** `P` transactions and
  `sellValue = Σ(shares × price)` over **discretionary** `S` transactions (both exclude any 10b5-1-plan
  transaction and any grant price of 0).
- If `buyValue > 0` and `sellValue == 0` → **Positive**, `netValue = buyValue`.
- If `sellValue > 0` and `buyValue == 0` → **Negative**, `netValue = sellValue`.
- If both > 0 → **Neutral** (`SignalDirection.Neutral`; a mixed same-filing buy+sell is genuinely
  ambiguous — do NOT net-sign it), `netValue = max(buyValue, sellValue)`. (Rare in practice.)
- If neither > 0 (all grants/exercises/withholding/gifts/plan) → **Neutral**, `netValue = 0` — this is the
  EOSE / AEHR-grant case: the evidence is still stored (source-diversity), but it carries a Neutral
  insider-activity phrase so it does not misfire as bearish.

The collector encodes this by choosing the **synthesized evidence phrase** (below) and writing an
`insiderNetValue` metadata key (invariant-culture decimal) for the extractor's materiality scaling. **The
direction is decided in the collector (parsing codes); the extractor only maps a fixed phrase → a fixed
direction and scales Strength by `insiderNetValue`.** This keeps direction deterministic and out of the
keyword heuristics, exactly mirroring the GovernmentContract precedent.

---

## Materiality tiers (Proposed — maintainer judgment call)

Scale the `InsiderBuying` signal's **Strength** by `netValue` (the discretionary buy-or-sell $), tiered like
the spec-66 `GovernmentContract` amount tiers (descending, inclusive-lower / exclusive-upper). **Proposed**
values (a product judgment call — flag for maintainer sign-off):

```
>= $5,000,000   -> 8   very large, thesis-moving insider trade
>= $1,000,000   -> 7   large, clearly material insider trade
>= $250,000     -> 6   solidly material (≈ the fixed baseline)
>= $50,000      -> 4   modest but real
<  $50,000      -> 2   small/routine; <= 2 so the existing DeterministicSignalReviewer
                        (MinMaterialStrength = 3, strict < 3) flags it NeedsMoreEvidence — reuse
                        that guardrail, do not add a drop path.
```

Insider $ magnitudes are naturally smaller than federal-contract awards, so the tier boundaries are lower
than spec 66's. **Multi-insider clustering:** when a single filing has multiple discretionary `P` (or `S`)
transactions they already sum into `netValue`; additionally, if a filing reports **≥ 2 distinct
`<reportingOwner>`s** transacting the same direction (cluster), add **+1** to the tier Strength (capped at
domain max 10) — several insiders acting together is a stronger read than one. (Per-company cross-filing
clustering across a window is a possible future refinement — OUT OF SCOPE here; state so.) All Strengths stay
within the domain range 1–10 so mapped signals pass `SignalValidation`.

---

## Domain: reuse `InsiderBuying` (do NOT add a new SignalType) — decision + justification

**Decision: reuse the existing `SignalType.InsiderBuying` as the direction-carrying category** — the
Positive/Negative/Neutral valence rides on `SignalDirection`, exactly as `GuidanceChange` and (per spec 86)
`CapitalRaise` already carry direction. **No Domain enum change.**

Justification (smallest correct option):
- The alternative — adding `SignalType.InsiderActivity` / `InsiderTransaction` — is a Domain enum change
  that would ripple into the mapper, tests, and any future exhaustiveness handling, for a naming nicety.
  (Verified: there is **no exhaustive `switch` on `SignalType`** in production code today, and `InsiderBuying`
  is currently unfed, so reuse costs nothing and breaks nothing.)
- The precedent is settled: spec 86 reused `CapitalRaise` with `SignalDirection.Negative` rather than
  minting a "CapitalDilution" type; `GuidanceChange` carries Positive/Negative/Neutral. `InsiderBuying`
  becomes the insider-activity *category*; direction expresses buy vs sell.
- The `"InsiderBuying (Negative)"` label reads slightly oddly, but it never surfaces in the report
  (direction is internal to scoring; the report emits only the AD-9 allowed action labels). The factual
  evidence text ("insider sold $X") stays advice-free (AD-9).
- **Revisit** (record in `architecture-decisions.md` if chosen later): if a cleaner name is wanted, rename
  the enum value to `InsiderActivity` in a dedicated Domain slice — but not in this feature slice.

---

## Fingerprint / cross-run comparability assessment (FLAG, do NOT solve here)

Two related nuances, both **NOTE-and-accept** for this slice:

1. **Enabling this collector changes scoring OUTPUT but NOT `ScoringConfigVersion`.** Post-89 (AD-10 as
   amended), `ScoringConfigVersion` is a **content fingerprint of the effective scoring config — the formula
   structure + every `ScoringWeights` value + the attention tier-map descriptor ONLY**. It does **not**
   include the enabled-collector set. So enabling `"secform4"` (new directional `InsiderBuying` signals that
   move `TrajectoryScore`) will **not** re-stamp the fingerprint. Two runs across the enable-transition would
   be judged "comparable" by the spec-69 gate and could show a **one-off thesis delta** from newly-appearing
   insider signals.
2. **The new extractor rules + the InsiderBuying materiality tier are "extractor-rule-like"** — AD-10's
   original prose says extractor-rule changes must bump the stamp — but the amended derived fingerprint
   **does not hash extractor rules either** (only formula/weights/tier-map). So the InsiderBuying vocabulary
   likewise cannot re-stamp. Note that this slice makes **no formula/weight/attention-tier change**, so the
   fingerprint value is genuinely unchanged.

**Assessment: acceptable for this slice; NOTE-and-accept.** The collector is opt-in — a human enabling it
knows the transition run is not a clean before/after comparison, and steady-state comparisons (both runs with
`"secform4"` enabled) are unaffected. **Do NOT expand the fingerprint here.** Folding the enabled-collector
set (and/or the extractor-rule identity) into the fingerprint is a **bigger, separate decision affecting ALL
collectors** — flag it as a follow-up (candidate `architecture-decisions.md` entry) for maintainer
consideration, out of scope for this feature. Do **not** bump any version constant in this slice (there is no
manual `ScoringConfigVersion` to bump post-89, and `_formula.Version` is unchanged — the formula shape does
not move).

---

## Project structure changes

```text
src/Radar.Infrastructure/Sec/
  ISecForm4Reader.cs               # NEW: ReadAsync(submissionsUrl, ct) -> typed SecForm4ReadResult (Form 4 filings + parsed txns)
  SecForm4ReadResult.cs            # NEW: outcome enum (reuse the SecFilingReadOutcome value set) + parsed Form4Filing items
  SecForm4Filing.cs                # NEW: parsed filing (accession, filingDate, acceptanceUtc, indexUrl, issuerTicker, distinct owner count, aggregated txns, direction, netValue, hasCluster, is10b5Plan)
  SecForm4TransactionCode.cs       # NEW: the code -> classification table (Buy/Sell/Neutral) + the cited SEC code legend
  HttpSecForm4Reader.cs            # NEW: submissions JSON (form==4) -> per filing fetch raw ownership XML (xslF345XNN/ strip) -> parse -> classify; reuses SecEdgarUrls + SecHttpFetch
  SecForm4CollectorOptions.cs      # NEW: required UserAgent, MaxFilingsPerCompany (default e.g. 15), materiality tiers are extractor-side
  SecForm4Collector.cs             # NEW: IEvidenceCollector; CollectorName "sec-form4"; SourceType Filing; FeedsOfType("secform4")

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs        # MODIFIED: add an InsiderBuying rule group (phrase->direction) + read insiderNetValue for Strength (mirror the GovernmentContract materiality read)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: AddSecForm4Collector(...) additive registration + named HttpClient (UA + gzip)

src/Radar.Worker/
  RadarWorkerServices.cs           # MODIFIED: add "secform4" as an enable-able kind; extend the valid-kinds messages
  RadarWorkerOptions.cs            # MODIFIED: SecForm4WorkerOptions (UserAgent, MaxFilingsPerCompany) bound from Radar:SecForm4
  appsettings.json                 # MODIFIED: leave Collectors=["rss"]; add a documented SecForm4 section

data/companies.json                # MODIFIED (data): add per-company "secform4" feeds (same CIK submissions URL as "sec"); omit SPNS (delisted)

tests/Radar.Infrastructure.Tests/Sec/
  HttpSecForm4ReaderTests.cs       # NEW: offline (fake HttpMessageHandler + fixture submissions JSON + fixture Form 4 XML)
  SecForm4CollectorTests.cs        # NEW: fake reader -> Filing evidence with direction/netValue/hints/summary; degrade
tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs   # MODIFIED: InsiderBuying phrase->direction + materiality tier cases
```

## Implementation details

### Reader (`HttpSecForm4Reader : ISecForm4Reader`)
- Named `HttpClient` configured exactly like `HttpSecFilingReader` (UA via
  `DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent)`, `Accept-Encoding:
  gzip, deflate`, automatic decompression). **Reuse `SecEdgarUrls`** (`StripLeadingZeros`,
  `BuildArchiveBaseUrl`) and **`SecHttpFetch.GetAsync`** for every request + outcome mapping — NO duplicated
  SEC URL/HTTP logic.
- Step 1 — fetch the company's submissions JSON (the feed `Url`), parse `filings.recent` columnar arrays,
  filter `form == "4"`, take the most-recent `MaxFilingsPerCompany`. (You may reuse the spec-56 columnar
  parse idiom; a small internal helper is fine — do not change `HttpSecFilingReader`.)
- Step 2 — for each Form 4, derive the raw ownership-XML URL: take `primaryDocument`, drop any leading path
  segment (`xslF345XNN/`), and if the remainder ends `.xml`, fetch
  `{SecEdgarUrls.BuildArchiveBaseUrl(cik, accession)}/{rawFile}`. Parse with `System.Text.Xml`/
  `XDocument` (root `<ownershipDocument>`, **no namespace**). Read `<aff10b5One>` (true/1 → plan), the
  distinct `<reportingOwner>` count, and each `<nonDerivativeTransaction>`/`<derivativeTransaction>`
  (**skip `<nonDerivativeHolding>`/`<derivativeHolding>`**): `transactionCode`, `transactionShares/value`,
  `transactionPricePerShare/value`, `transactionAcquiredDisposedCode/value`. Classify per the code table,
  apply the 10b5-1 override, compute filing-level `Direction` + `netValue` + `hasCluster`.
- **Never throw** on a bad feed/filing: reuse the `SecFilingReadOutcome` value set (Success / Unreachable /
  HttpError / Forbidden / Timeout / Malformed) — a per-filing XML fetch/parse failure skips that ONE filing
  (logged, no throw) while other filings in the same feed still count; a submissions-level failure degrades
  the whole feed to the typed outcome + empty items. Re-throw only genuine caller cancellation
  (`OperationCanceledException when ct.IsCancellationRequested`), exactly as the existing readers do.
- All HTTP/XML/SEC code stays in `Radar.Infrastructure` (AD-5); all new types `internal`
  (`InternalsVisibleTo` for the test project is already present).

### Collector (`SecForm4Collector : IEvidenceCollector`)
- `CollectorName = "sec-form4"`, `SourceType = EvidenceSourceType.Filing`. Iterate
  `context.FeedsOfType("secform4")` (deterministic `(CompanyId, Id)` order), read via `ISecForm4Reader`,
  cap at `MaxFilingsPerCompany`, dedupe within a feed by accession (mirror `SecEdgarFilingCollector`).
- Map each parsed Form 4 to a `CollectedEvidence` (`SourceType = Filing`) whose synthesized **Title/RawText
  encode the direction as a fixed phrase** the extractor matches (no filing body text fabricated — only real
  metadata: issuer, owner name(s), code(s), share count, $ value, filing date, accession):
  - Positive → include the phrase **`"insider open-market purchase"`** (e.g. `"Form 4 — insider open-market
    purchase: {owner} bought {shares} shares (~${netValue}) ({filingDate})"`).
  - Negative → **`"insider open-market sale"`** (e.g. `"… insider open-market sale: {owner} sold {shares}
    shares (~${netValue}) …"`). Factual, advice-free (AD-9).
  - Neutral (grants/plan/mixed/none) → **`"insider stock transaction (routine)"`** — a Neutral phrase so the
    evidence stays source-diversity-positive without misfiring directionally.
  - **Provenance:** `SourceUrl` = the filing **index URL** (`SecEdgarUrls.BuildIndexUrl(cik, acc, ".htm")`);
    carry `accessionNumber` in metadata so distinct filings hash distinctly under the mapper's
    Title+RawText `ContentHash`. Include the accession + date in the hashed text.
  - **Metadata:** `quality` = `"High"` (SEC primary source, matching the spec-56 collector);
    `insiderNetValue` = invariant-culture decimal `netValue` (the extractor's Strength key); plus
    `accessionNumber`, `form` = `"4"`, `filingDate`, and `insiderDirection` (for debugging/traceability —
    NOT read by the extractor). Write `insiderNetValue` only when `> 0` (a Neutral no-value filing omits it,
    so the InsiderBuying signal keeps its baseline Strength).
  - **Timestamps (UTC):** `PublishedAt` = `acceptanceDateTimeUtc` (windowing/recency).
  - **CompanyHints:** `CollectorCompanyHints.For(feed.CompanyId, companiesById)` — feed-bound ticker; never
    invent one.
- Populate `CollectionSummary` (checked/failed + `SourceFailure` list) exactly like `SecEdgarFilingCollector`
  so the merged run summary reflects it. Log per-feed + aggregate outcomes.

### Extractor (`KeywordSignalExtractor`) — additive InsiderBuying rule group + materiality read
- Add a **new ordered rule group** for `SignalType.InsiderBuying`, placed as its own block (Negative first
  so a mixed-phrase filing — should never occur, but defensive — resolves conservatively; each phrase maps a
  fixed direction):
  - `"insider open-market purchase"` → `InsiderBuying` **Positive** (Strength 6, Novelty 5, Confidence 0.6).
  - `"insider open-market sale"` → `InsiderBuying` **Negative** (Strength 6, Novelty 5, Confidence 0.6).
  - `"insider stock transaction (routine)"` → `InsiderBuying` **Neutral** (Strength 3, Novelty 4,
    Confidence 0.45) — Neutral contributes 0 to Trajectory (radar-formula-v2, AD-6) yet still lifts
    source-type diversity.
- Extend the **materiality read** (the spec-66 pattern): parse `insiderNetValue` once per evidence and, for
  a fired `InsiderBuying` **Positive or Negative** signal, scale its Strength via the InsiderBuying tier
  table above (a Neutral routine phrase and any absent/zero value keep the fixed rule Strength). Reuse the
  existing `TryGetAwardAmount`/`StrengthForAmount` shape (a sibling method reading the `insiderNetValue`
  key + an InsiderBuying tier array — do not overload the GovernmentContract path).
- **Refactor note (WATCH-ITEM in the extractor's own doc-comment):** the extractor's comment warns that a
  *third* source-type special-case should trigger a refactor to explicit per-`SignalType`/`EvidenceSourceType`
  dispatch rather than a third inline branch. This InsiderBuying materiality read is a **second metadata-aware
  read** (GovernmentContract was the first); it is metadata-driven, not `EvidenceSourceType`-driven, so it
  extends the existing generic materiality mechanism rather than adding a new inline source-type branch —
  keep it structured as a small generic "materiality for (SignalType, metadataKey, tiers)" helper so a third
  such case does not force a rewrite. If the reviewer judges this the tipping point, a tiny extract-to-helper
  refactor is in scope; a new `ISignalExtractor` is NOT.

### Config, DI & seed
- `SecForm4CollectorOptions`: `UserAgent` (**required** — fail fast, mirror `SecCollectorOptions`),
  `MaxFilingsPerCompany` (default e.g. **15** — Form 4s are numerous; keep the per-run fetch bounded). Tiers
  live in the extractor, not options.
- `AddSecForm4Collector` registers the reader + collector additively
  (`AddSingleton<IEvidenceCollector, SecForm4Collector>()`) and the named `HttpClient`; fail fast on a
  blank `UserAgent` and a non-positive `MaxFilingsPerCompany` with `Radar:SecForm4:*` messages (mirror
  `AddSecEdgarCollector`). `TryAddSingleton(TimeProvider.System)`.
- `RadarWorkerServices` gains `"secform4"` as an enable-able kind and threads
  `RadarWorkerOptions.SecForm4`; extend every valid-kinds message to include `"secform4"`. Default
  `Radar:Collectors` stays `["rss"]`.
- **Seed:** add a `secform4` feed to each seeded company in `data/companies.json` (the same verified CIK
  submissions URL as the `sec` feed), OMIT SPNS (delisted, per spec 56). This is data — include it so the
  follow-up live run can enable `["rss","sec","secform4"]`.

## Tests

- `HttpSecForm4ReaderTests` (offline, fake `HttpMessageHandler`): a fixture submissions JSON with a mix of
  forms parses only `form == "4"`; for each Form 4 the reader fetches the raw XML at the
  `xslF345XNN/`-stripped path (assert the requested URL). Using fixture Form 4 XML mirroring the verified
  shapes:
  - **`P` (purchase, A/acquired)** → Positive, `netValue = shares×price`.
  - **`S` (sale, D/disposed), `<aff10b5One>0</aff10b5One>`** → Negative (the Mercury shape).
  - **`S` under `<aff10b5One>true</aff10b5One>`** (or a `10b5-1` footnote) → **Neutral** (plan override).
  - **`A` grant (price 0)**, **`M`**, **`F`**, **`G`** → Neutral/excluded (the AEHR grant shape).
  - **Mixed buy+sell in one filing** → Neutral (ambiguous), `netValue = max`.
  - **Multi-owner cluster** (≥2 `<reportingOwner>` same direction) → `hasCluster` true.
  - **`<nonDerivativeHolding>`** rows are ignored (no txn).
  - Malformed/empty submissions JSON → `Malformed`; a per-filing XML 404/malformed XML → that filing is
    skipped, others still parse; 403 → `Forbidden`; a thrown `TaskCanceledException` (timeout) → `Timeout`;
    caller cancellation re-throws. No network.
- `SecForm4CollectorTests` (fake `ISecForm4Reader`): parsed Form 4s map to `Filing` `CollectedEvidence` with
  the correct **direction phrase** in Title/RawText, `insiderNetValue` metadata (only when > 0), index-URL
  provenance, accession in metadata, UTC observed instant from `acceptanceDateTime`, the feed's CompanyId as
  hint, and `High` quality; `MaxFilingsPerCompany` honoured; a `Forbidden`/empty feed degrades to a
  `SourceFailure`/no evidence without throwing; `CollectionSummary` counts correct; deterministic order.
- `KeywordSignalExtractorTests` (MODIFIED): the three InsiderBuying phrases map to Positive/Negative/Neutral
  respectively; a Positive/Negative signal's Strength scales by `insiderNetValue` across the tier boundaries
  (e.g. $5,000,000→8, $1,000,000→7, $250,000→6, $50,000→4, $10,000→2); a routine-Neutral phrase / absent
  value keeps the fixed Strength; existing GovernmentContract/CapitalRaise/other cases stay green (the new
  group is additive and first-match-per-type is preserved).
- Provenance end-to-end: a Form 4 purchase evidence resolves and produces a stored `InsiderBuying` Positive
  signal whose `EvidenceId` traces to the Form 4 evidence (extend the integration test or a runner-level
  test only if cheap; otherwise the collector+extractor unit coverage suffices).
- Existing tests (RSS, SEC, USASpending, News, runner merge, DI list, Worker DI) stay green; the
  multi-collector runner now composes rss + sec + secform4 when enabled.

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based classification; **NO AI** (an AI-assisted
  insider-sentiment refinement of edge cases is a possible FUTURE slice — out of scope).
- All SEC/HTTP/XML confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8); reuse
  `SecEdgarUrls` / `SecHttpFetch` / `CollectorCompanyHints` — **no duplicated SEC URL/HTTP logic** ("reuse
  over copy").
- Graceful degradation: typed non-throwing outcomes; a delisted/quiet issuer (SPNS) or a bad filing yields
  zero/partial evidence, not an error; only genuine caller cancellation propagates.
- Provenance preserved (Form 4 XML → `Filing` evidence with index URL + accession → `InsiderBuying` signal →
  score). No advice language (AD-9): "insider bought/sold $X" is factual; the direction is internal to
  scoring.
- Store timestamps in UTC; IDs `Guid`.
- **No formula/weight/attention-tier change → the scoring fingerprint (`ScoringConfigVersion`) is unchanged;
  do NOT bump any version constant and do NOT expand the fingerprint** (see the assessment above).
- Honour the SEC User-Agent + rate rules (compliant UA required; sequential/polite requests under 10 rps;
  gzip). Keep `MaxFilingsPerCompany` bounded (Form 4s are numerous).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Out of scope (note explicitly)

- **AI-assisted insider-sentiment refinement** (edge cases, footnote-language reads) — future slice.
- **Folding the enabled-collector set (or extractor-rule identity) into the scoring fingerprint** — a
  bigger, separate cross-collector decision; flag as a follow-up `architecture-decisions.md` candidate, do
  NOT implement here.
- **Per-company cross-filing insider clustering across a window** (only within-filing clustering here).
- **Form 144** (proposed sales) — a different filing type; **Form 4 only** in this slice.
- **The price collector** (separate spec 92).
- **Renaming `InsiderBuying` → `InsiderActivity`** — deferred Domain nicety, not this feature.

## Acceptance criteria

- [ ] `SecForm4Collector` (kind `"secform4"`) fetches each feed's submissions JSON with a compliant,
      configurable User-Agent, filters `form == "4"`, fetches each Form 4's raw ownership XML (deriving the
      URL by stripping the `xslF345XNN/` prefix from `primaryDocument`), and maps each filing to a `Filing`
      evidence item with an index-URL provenance link, accession metadata, UTC observed instant from
      `acceptanceDateTime`, feed-bound hint, and `High` quality.
- [ ] The transaction-code classification is exactly the table above: `P`→bullish, `S`→bearish
      (discretionary only), grants/exercises/withholding/gifts/other→Neutral, unknown→Neutral, and **any
      10b5-1-plan transaction (`<aff10b5One>` true/1 or a 10b5-1 footnote) is forced Neutral**; a mixed
      same-filing buy+sell is Neutral. Filing-level direction + `netValue` computed per the rules.
- [ ] The evidence carries a fixed direction phrase (`insider open-market purchase` / `insider open-market
      sale` / `insider stock transaction (routine)`) and an `insiderNetValue` metadata (when > 0); the
      `KeywordSignalExtractor` maps those to `InsiderBuying` Positive/Negative/Neutral and scales
      Positive/Negative Strength by the InsiderBuying materiality tiers.
- [ ] `SignalType.InsiderBuying` is now fed end-to-end (was unfed); no Domain enum change; provenance
      (evidence → `InsiderBuying` signal) holds.
- [ ] The reader returns typed outcomes (incl. distinct `Forbidden` for 403/UA), never throws on a bad
      feed/filing (a bad single filing is skipped, not fatal); caller cancellation propagates; a blank
      `UserAgent` / non-positive `MaxFilingsPerCompany` is a fail-fast config error.
- [ ] Additively registered and enable-able via `Radar:Collectors` containing `"secform4"`; default config
      unchanged (`["rss"]`); seed carries per-company `secform4` feeds (verified CIK URLs), omitting SPNS;
      merged `CollectionSummary` reflects it.
- [ ] No formula/weight/attention-tier change; `ScoringConfigVersion` fingerprint unchanged; no version
      constant bumped; fingerprint scope explicitly NOT expanded (follow-up flagged).
- [ ] Offline tests cover P→Positive, S→Negative, 10b5-1/M/F/A/G→Neutral-or-excluded, materiality tiers by
      $ value, multi-insider cluster, mixed→Neutral, holdings-ignored, malformed/empty/timeout/429/403→typed
      failure no-throw, opt-in default-unchanged, and the extractor phrase→direction + tier mapping.
      `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
