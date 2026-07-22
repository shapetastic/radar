# Task: Expand the watch universe — add 14 gap-sector companies, raise the report entry cap

> **UNIVERSE EXPANSION (data + config only).** Approved 2026-07-22 after the first live `radar-formula-v8`
> baseline run confirmed the spec-120 batch collected and scored cleanly (29/29 companies scored, 2528 evidence
> items, 136 sources, 1 pre-existing failure). This is a **data change to `data/companies.json`** plus a **one-key
> config change** to `scripts/run-profiles/default.json`. **The company universe is NOT a scoring input**, so there
> is **no fingerprint impact and no formula/RuleSetVersion bump** (unlike adding a *collector*, which would).
> Motivation: the 2026-07-22 run showed the universe still concentrated in Industrials (10 of 29) with **zero**
> coverage of Financial Services below mega, Real Estate, Consumer Cyclical, and Communication Services. Every CIK
> below was verified against SEC's authoritative `company_tickers.json` **and** its `data.sec.gov` submissions
> record.

## Overview

The watch universe (`data/companies.json`) is a curated, diversified efficacy sample — deliberately spanning
improving-candidates + steady controls + large-cap benchmarks so the spec-101/108 score-vs-price efficacy layer can
test **discrimination**, not just measure hand-picked bullish theses.

Two problems the 2026-07-22 baseline run exposed:

1. **Sector gaps.** Current distribution: Industrials 10, Technology 6, Healthcare 5, Consumer Defensive 4,
   Utilities 1, Basic Materials 1, Financial Services 1 (mega-only: V), Energy 1 (mega-only: CVX). Real Estate,
   Consumer Cyclical and Communication Services are entirely absent.
2. **Silent report truncation.** The run scored 29 companies but the report rendered only 25 — TR, WTRG, CVX and
   CAT were scored and then dropped on the floor, invisible to the reader.

This slice adds **14 verified companies** concentrated in the zero-coverage sectors, all **small or mid**
`followingTier` (mega/large names are structurally suppressed by the spec-117/122 notedness discount, so adding
more would spend collection budget on predictably-suppressed scores), and raises the report cap.

Result: universe 29 → **43**. **No companies are removed** — the five mega-caps (AAPL, CAT, JNJ, V, CVX) are
retained deliberately as the efficacy sample's discrimination controls; JNJ in particular is the spec-117 notedness
worked-example, and dropping any of them would both remove that reference point and break the accrued score-vs-price
history the spec-108 segmentation depends on.

## Assignment

Worktree: any
Dependencies: current main (post 122 / `radar-formula-v8`). Pure data + config change — independent of scoring;
**no fingerprint impact**.
Estimated time: ~1–2 hours (mostly per-company seed-entry authoring; RSS verification is already done, below).

## The batch (all CIKs verified against SEC `company_tickers.json` + submissions API)

| Ticker | Registrant name (SEC) | CIK (10-digit) | Exch* | followingTier | Bucket | Sector / Industry |
|---|---|---|---|---|---|---|
| SFBS | ServisFirst Bancshares, Inc. | 0001430723 | NYSE | mid | control | Financial Services / Banks—Regional |
| OFG | OFG BANCORP | 0001030469 | NYSE | mid | candidate | Financial Services / Banks—Regional |
| SKWD | Skyward Specialty Insurance Group, Inc. | 0001519449 | NASDAQ | mid | candidate | Financial Services / Insurance—Property & Casualty |
| GTY | GETTY REALTY CORP /MD/ | 0001052752 | NYSE | small | control | Real Estate / REIT—Retail |
| UMH | UMH PROPERTIES, INC. | 0000752642 | NYSE | small | candidate | Real Estate / REIT—Residential |
| DEA | Easterly Government Properties, Inc. | 0001622194 | NYSE | small | control | Real Estate / REIT—Office |
| LZB | LA-Z-BOY INC | 0000057131 | NYSE | small | control | Consumer Cyclical / Furnishings, Fixtures & Appliances |
| HZO | MARINEMAX INC | 0001057060 | NYSE | small | candidate | Consumer Cyclical / Specialty Retail |
| SHOO | STEVEN MADDEN, LTD. | 0000913241 | NASDAQ | mid | control | Consumer Cyclical / Footwear & Accessories |
| SHEN | SHENANDOAH TELECOMMUNICATIONS CO/VA/ | 0000354963 | NASDAQ | small | candidate | Communication Services / Telecom Services |
| ATEX | Anterix Inc. | 0001304492 | NASDAQ | small | candidate | Communication Services / Telecom Services |
| IMAX | IMAX CORP | 0000921582 | NYSE | mid | control | Communication Services / Entertainment |
| HWKN | HAWKINS INC | 0000046250 | NASDAQ | mid | candidate | Basic Materials / Specialty Chemicals |
| WTTR | Select Water Solutions, Inc. | 0001693256 | NYSE | small | candidate | Energy / Oil & Gas Equipment & Services |

\* Exchange is metadata (not load-bearing — SEC collection keys on CIK). Values are as reported by the SEC
submissions `exchanges` field. **Note SFBS:** SEC reports `NYSE` even though some third-party pages still describe
it as Nasdaq-listed; the SEC value is recorded and is the one to use.

Resulting sector fill: Financial Services 1 → 4, Real Estate 0 → 3, Consumer Cyclical 0 → 3, Communication
Services 0 → 3, Basic Materials 1 → 2, Energy 1 → 2. No new Industrials, no new semiconductor names.

All 14 were confirmed to have current filing cadence (2026 8-Ks *and* 2026 Form 4s; most recent filing May–July 2026
for every name), so each will actually produce signals rather than sitting inert in the sample.

## ⚠ Ticker substring collisions — MUST be handled (4 companies)

`NewsAttentionCollector.IsRelevant` (`src/Radar.Infrastructure/News/NewsAttentionCollector.cs:204-206`) matches the
ticker with an **unanchored, case-insensitive `Contains`** on the headline — there is no word-boundary check. This
is why spec 120 had to special-case `V` (Visa). Four tickers in this batch are substrings of common English words
and will pull in large volumes of false-positive MediaAttention evidence if the `ticker=` token is included:

| Ticker | Collides with |
|---|---|
| DEA | "**dea**l", "**dea**ler", "i**dea**s", "**dea**dline" |
| SHOO | "**shoo**t", "**shoo**ting" |
| ATEX | "l**atex**" |
| SHEN | "**Shen**zhen" |

**For these four, omit the `ticker=` token from the `newssearch` feed URL** — use `url: "query=<Registrant Name>"`
alone, exactly as shipped for V. Relevance is then driven by the query phrase only.

This matters more than it did before spec 122: false-positive media evidence inflates **Attention**, and
`radar-formula-v8` now credits collapsed distinct-publisher breadth into the reach term — so junk headlines from
unrelated outlets would inflate the breadth credit and distort the notedness discount, corrupting the exact
component the 117→124 calibration arc just settled.

The other ten tickers (SFBS, OFG, SKWD, GTY, UMH, LZB, HZO, IMAX, HWKN, WTTR) are distinctive enough to keep the
`ticker=` token.

## IR RSS feeds — already live-verified (do NOT re-verify with a User-Agent)

**Correction to spec 120's method:** spec 120 instructed verifying IR RSS "with the collector User-Agent". That is
wrong — Radar's RSS collector sends **no User-Agent header at all**. A feed that returns 200 to a curl carrying a
browser/SEC UA can still 403 in production. All verification below was done with **no UA**
(`curl -H "User-Agent:" …`), which is what the collector actually does.

**Include the `rss` feed for these 8** (verified HTTP 200 with valid items, no UA):

| Ticker | RSS URL |
|---|---|
| SFBS | `https://www.servisfirstbancshares.com/news-events/press-releases/rss` |
| SKWD | `https://investors.skywardinsurance.com/rss/news-releases.xml` |
| DEA | `https://ir.easterlyreit.com/rss/news-releases.xml` |
| SHOO | `https://investor.stevemadden.com/rss/news-releases.xml` |
| SHEN | `https://investor.shentel.com/rss/news-releases.xml` |
| ATEX | `https://investors.anterix.com/rss/news-releases.xml` |
| IMAX | `https://investors.imax.com/rss/news-releases.xml` |
| HWKN | `https://hawkinsinc.gcs-web.com/rss/news-releases.xml` |
| WTTR | `https://investors.selectwater.com/news-events/press-releases/rss` |

**Omit the `rss` feed for these 5** (no usable feed — do not re-litigate, these were checked):

| Ticker | Finding |
|---|---|
| OFG | 403 (Cloudflare on www.ofgbancorp.com); `ir.`/`investor.`/`gcs-web` hosts do not resolve |
| GTY | `ir.gettyrealty.com` 403; `www.gettyrealty.com/feed/` returns 200 but with **zero items** → unusable, do not add it |
| UMH | 403 (Cloudflare) |
| LZB | 403 (Cloudflare error 1014) |
| HZO | 403 (Cloudflare) |

These five are still fully collected via SEC (CIK) + newssearch — RSS is additive, not required.

## Changes

### 1. `data/companies.json` — add 14 entries

For **each** company, add a seed entry matching the exact shape of the existing entries (see MRCY at the top of the
file). Required/known fields:

- `id`: a fresh **GUID** (generate one per company; unique).
- `name`, `legalName`: the registrant name above. `ticker`, `exchange`, `countryCode: "US"`, `sector`, `industry`.
- `followingTier`: the tier above (case-insensitive string; the seed parser defaults absent → Small).
- `aliases` / `themes`: see the table at the end of this spec.
- `sourceFeeds` (mirror the existing per-company pattern):
  - `sec`, `secform4`, `sec13dg` — **all three required**, each `url:
    "https://data.sec.gov/submissions/CIK<10-digit>.json"` using the CIK above (same URL for all three types, as
    every existing entry does). Names follow the existing convention (`"<Name> — SEC filings (EDGAR)"`,
    `"… — SEC Form 4 insider filings (EDGAR)"`, `"… — SEC 13D/13G ownership filings (EDGAR)"`).
  - `newssearch` — **required**, name `"<Name> — News attention (Google News)"`.
    `url: "query=<Registrant Name>&ticker=<TICKER>"` — **except for DEA, SHOO, ATEX, SHEN, which use
    `url: "query=<Registrant Name>"` with no `ticker=` token** (see the collision section above).
  - `rss` — include for the 8 verified above, omit for the 5 listed as unusable.
  - `usaspending` — **omit** for this batch (adding a recipient requires a per-company USASpending recipient-ID
    lookup — out of scope; DEA is the obvious later candidate given its federal-agency tenant base).
  - `news` (GDELT) — **omit**, consistent with the spec-120 batch.

Keep the file valid JSON (trailing-comma-free), and keep all 29 existing companies untouched.

### 2. `scripts/run-profiles/default.json` — raise the report entry cap

The weekly report renders at most `WeeklyReportOptions.MaxItems`, fed from `RadarWorkerOptions.ReportMaxItems`
(default **25**, `src/Radar.Worker/RadarWorkerOptions.cs:139`), bound from config key `Radar:ReportMaxItems`
(`src/Radar.Worker/RadarWorkerServices.cs:27,44`). With 29 companies the 2026-07-22 run silently dropped 4; with 43
it would drop 18.

Add `"ReportMaxItems": 60` to the `Radar` object in `default.json`. **60, not 43** — deliberate headroom so the next
universe expansion does not silently truncate again. Extend the profile's `_comment` to record why.

This is an operational display parameter, **not** a scoring weight — it is not a `ScoringWeights` knob, is not
hashed into the fingerprint, and needs no version bump.

## Tests

- Update any test that pins the **universe size or membership** (e.g. an assertion of "29 companies") to the new set
  (29 + 14 = **43**). Search tests for a hard-coded company count.
- If `LocalFileCompanySeedSource`/`LocalFileCompanySeedDocument` has parse tests over a fixture, they need no change
  unless they load the production `companies.json`; ensure the production file still round-trips (all new entries
  parse — GUID `id`, tier string, feeds).
- **Add a regression test for the ticker-collision rule**: assert that the `newssearch` feed URL for DEA, SHOO,
  ATEX and SHEN contains no `ticker=` token (and, to keep it honest, that a representative distinctive ticker such
  as HWKN *does*). This is the one piece of the spec most likely to be silently undone by a later well-meaning
  "consistency" edit.
- No new production code path is expected — this is seed data + one config key.

## Constraints

- **Data + config only.** **No** `_formula.Version` / `KeywordSignalExtractor.RuleSetVersion` bump; the company
  universe is not a `ScoringConfig` input, so the fingerprint does **not** move (AI-OFF `cb80a5809882` / AI-ON
  `c908f03a554a` both unchanged — do not let the fp move).
- Every CIK must be exactly as listed (verified). Do not invent or alter CIKs.
- `followingTier` values only from {mega, large, mid, small} — curated, non-price (**AD-14** preserved: never
  market-cap/price/volume-derived).
- **No companies are removed.** All 29 existing entries, including the five mega-caps, stay.
- SEC User-Agent for any live SEC verification must be a real contact (not the placeholder that 403s); the coder
  does not commit any UA into the repo. **SEC rate-limit discipline:** fetch `company_tickers.json` once and reuse
  it locally; pace `data.sec.gov` requests to ~3-5/sec and sequentially — an unpaced burst has previously
  self-blocked this machine from `www.sec.gov` and starved the production AI earnings path.

## Known traps — do not re-tread

Carried forward from spec 120 plus this slice's research:

- **Do NOT use XOM.** SEC `company_tickers.json` maps `XOM` to CIK `2115436` "ExxonMobil Holdings Corp", a
  non-trading shell. (The real registrant is CIK `0000034088`.)
- **Do NOT use ARIS for Aris Water Solutions.** Ticker `ARIS` maps to **Aris Mining Corp** (CIK 1964504) — a
  different, Canada-domiciled company. Same class of trap as XOM.
- **BELFB** (Class B) and BELFA (Class A) share one registrant/CIK `0000729580`.
- Tickers investigated and found **absent from `company_tickers.json`** (unverifiable, rejected): ALEX
  (Alexander & Baldwin), MCW (Mister Car Wash), HIFS (Hingham Institution for Savings — likely reports to a banking
  regulator rather than filing an SEC submissions record), NR (Newpark Resources), PLYM (Plymouth Industrial REIT),
  VTLE (Vital Energy).

**Verified alternates** (drop-in replacements if any name above has to be pulled during implementation):

- **CASS** — Cass Information Systems, CIK `0000708781`, NASDAQ, RSS `https://ir.cassinfo.com/rss/news-releases.xml`
  verified **200** with real items. Excluded only because its mixed freight-payments/commercial-bank model makes the
  Sector string ambiguous.
- **ASIX** — AdvanSix, CIK `0001673985`, NYSE, Basic Materials / Specialty Chemicals. RSS not tested.
- **KGS** — Kodiak Gas Services, CIK `0001767042`, NYSE, Energy / Oil & Gas Equipment & Services. RSS not tested.

## Caveats to sanity-check during implementation

These came from research judgement, not an authoritative field lookup — check them rather than trusting them:

- **`followingTier` values are proposals**, inferred from market-cap scale and observable coverage density. There is
  no measured analyst-count field. Sanity-check each before committing; the tier directly drives the spec-117
  following discount.
- **Sector/Industry strings are Yahoo-convention classifications**, chosen to match the existing file, not fetched
  from a Yahoo endpoint. Most are corroborated by the registrant's SEC SIC description (SFBS/OFG "State Commercial
  Banks", SKWD "Fire, Marine & Casualty Insurance", UMH/DEA "Real Estate Investment Trusts", WTTR "Oil & Gas Field
  Services", LZB "Household Furniture", HZO "Retail-Auto & Home Supply Stores", SHOO "Footwear (No Rubber)",
  SHEN/ATEX "Telephone Communications", HWKN "Wholesale-Chemicals"). Two do not line up neatly: **GTY**'s SEC SIC is
  the generic "Real Estate", and **IMAX**'s is "Photographic Equipment & Supplies", which does not match the
  Communication Services label assigned here. Both are judgement calls — the Yahoo-convention label is the better
  fit for the sample's sector-balance purpose, but flag if you disagree.

## `aliases` and `themes`

| Ticker | aliases | themes |
|---|---|---|
| SFBS | "ServisFirst", "ServisFirst Bancshares" | "regional banking", "commercial lending", "deposit growth" |
| OFG | "OFG Bancorp", "Oriental Bank" | "regional banking", "Puerto Rico banking", "consumer lending" |
| SKWD | "Skyward Specialty", "Skyward" | "specialty insurance", "excess and surplus lines", "underwriting discipline" |
| GTY | "Getty Realty", "Getty" | "net lease REIT", "convenience and automotive retail", "single-tenant real estate" |
| UMH | "UMH Properties", "UMH" | "manufactured housing", "residential REIT", "rental communities" |
| DEA | "Easterly Government Properties", "Easterly" | "government-leased real estate", "office REIT", "federal agency tenants" |
| LZB | "La-Z-Boy", "La-Z-Boy Incorporated" | "residential furniture", "consumer discretionary retail", "manufacturing and retail vertical" |
| HZO | "MarineMax", "MarineMax Inc" | "recreational boating retail", "marina services", "discretionary consumer demand" |
| SHOO | "Steve Madden", "Steven Madden" | "footwear and accessories", "branded consumer", "wholesale and direct-to-consumer" |
| SHEN | "Shentel", "Shenandoah Telecommunications" | "fiber broadband", "Glo Fiber", "rural telecom" |
| ATEX | "Anterix", "Anterix Inc" | "private wireless networks", "900 MHz spectrum", "utility communications" |
| IMAX | "IMAX", "IMAX Corporation" | "premium cinema", "entertainment technology", "box office exhibition" |
| HWKN | "Hawkins", "Hawkins Inc" | "specialty chemicals", "water treatment", "industrial distribution" |
| WTTR | "Select Water Solutions", "Select Water" | "water infrastructure", "produced water recycling", "oilfield services" |

## Acceptance criteria

- [ ] All 14 companies added to `data/companies.json` with the exact CIKs above, correct `followingTier`, and
      `sec`/`secform4`/`sec13dg`/`newssearch` feeds; each `id` a unique GUID; file is valid JSON.
- [ ] **DEA, SHOO, ATEX and SHEN carry no `ticker=` token** in their `newssearch` feed URL; the other ten do.
- [ ] The 8 verified `rss` feeds are present; the 5 unusable ones are omitted (including GTY's zero-item feed).
- [ ] All 29 existing companies unchanged; nothing removed.
- [ ] `"ReportMaxItems": 60` added to the `Radar` object in `scripts/run-profiles/default.json`, with the
      `_comment` extended to explain it.
- [ ] Universe-size/membership test updated to the new 43-company set; a regression test pins the four
      no-`ticker=` newssearch URLs.
- [ ] Fingerprint unchanged (AI-OFF `cb80a5809882`, AI-ON `c908f03a554a`); no formula/RuleSetVersion bump.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
