# Task: Expand the watch universe — batch 3: add 23 verified companies, raise the report entry cap

> **UNIVERSE EXPANSION (data + config only).** Approved 2026-07-29 after a leaderboard-power dig on the first
> 10-strategy run. This is a **data change to `data/companies.json`** plus a **one-key config change** to
> `scripts/run-profiles/default.json`. **The company universe is NOT a scoring input**, so there is **no
> fingerprint impact and no formula/RuleSetVersion bump** (the live baseline stamps stay AI-OFF
> `radar-scoring-fp-4eb2fe5d3cdf` / AI-ON `radar-scoring-fp-4da4b5ff6ec9`). Every CIK below was verified
> 2026-07-29 against SEC's authoritative `company_tickers.json` **and** its `data.sec.gov` submissions record;
> every RSS URL below was live-verified the same day with **no User-Agent** (what the collector actually sends).

## Overview

The 2026-07-29 strategy leaderboard ranked **1 of 10** strategies, and even that one's headline number is
statistically empty: out-of-sample Spearman ρ 0.0579 with a 95% CI of **−0.18 to +0.29** over 72 observations —
wide enough to contain "works", "does nothing" and "actively inverted". The binding constraint on the whole
136–157 measurement arc is now **cross-sectional power**, and companies are the one lever that adds observations
without waiting for calendar dates to accrue one per day.

The dig that motivated this (2026-07-29) also established there is **no dead weight to fix first**: all 43
current tickers have full price coverage through 2026-07-27, and the leaderboard's "18 companies" is purely
add-wave timing — the universe grew 7 → 18 → 43 across 07-03/07-07/07-22 waves, so the 25 companies added
2026-07-22/23 simply have no complete 21-day forward windows yet (they enter the join ~2026-08-12+). Companies
added **now** accrue evidence, scores and price history immediately and their forward windows complete on the
same late-August/September schedule the existing arms are already waiting on. Adding them in September instead
would restart a 3–4 week clock.

This batch adds **23 verified companies** concentrated in the thinnest sectors (Utilities 1 → 4, Basic
Materials 2 → 5, Energy 2 → 5), broadens small-cap Financial Services / Healthcare / Technology, and adds
**zero new Industrials** (already 10 of 43). All are `small` or `mid` `followingTier` — mega/large names are
structurally suppressed by the notedness discount, and the five existing mega-caps already serve as the
discrimination controls. Mix: 13 improving-candidates + 10 steady controls, consistent with the sample's
purpose of testing **discrimination**, not hand-picked bullish theses.

Result: universe 43 → **66**. **No companies are removed.**

Selection was **neutral (AD-14 preserved)**: names were chosen by sector fill, size/following profile and
filing-cadence liveness — never by recent price action, and price history was not consulted.

## Assignment

Worktree: any
Dependencies: current main (post 157). Pure data + config change — independent of scoring; **no fingerprint
impact**.
Estimated time: ~1–2 hours (mostly per-company seed-entry authoring; CIK + RSS verification is already done,
below).

## The batch (all CIKs verified 2026-07-29 against SEC `company_tickers.json` + submissions API)

| Ticker | Registrant name (SEC) | CIK (10-digit) | Exch* | followingTier | Bucket | Sector / Industry |
|---|---|---|---|---|---|---|
| MSEX | MIDDLESEX WATER CO | 0000066004 | Nasdaq | small | control | Utilities / Utilities—Regulated Water |
| YORW | YORK WATER CO | 0000108985 | Nasdaq | small | control | Utilities / Utilities—Regulated Water |
| OTTR | Otter Tail Corp | 0001466593 | Nasdaq | mid | candidate | Utilities / Utilities—Diversified |
| ASIX | AdvanSix Inc. | 0001673985 | NYSE | small | candidate | Basic Materials / Chemicals |
| IOSP | INNOSPEC INC. | 0001054905 | Nasdaq | mid | control | Basic Materials / Specialty Chemicals |
| KWR | QUAKER CHEMICAL CORP | 0000081362 | NYSE | mid | candidate | Basic Materials / Specialty Chemicals |
| KGS | Kodiak Gas Services, Inc. | 0001767042 | NYSE | mid | candidate | Energy / Oil & Gas Equipment & Services |
| LBRT | Liberty Energy Inc. | 0001694028 | NYSE | mid | control | Energy / Oil & Gas Equipment & Services |
| PUMP | ProPetro Holding Corp. | 0001680247 | NYSE | small | candidate | Energy / Oil & Gas Equipment & Services |
| CASS | CASS INFORMATION SYSTEMS INC | 0000708781 | Nasdaq | small | control | Financial Services / Specialty Business Services |
| TBBK | Bancorp, Inc. | 0001295401 | Nasdaq | mid | candidate | Financial Services / Banks—Regional |
| PLMR | Palomar Holdings, Inc. | 0001761312 | Nasdaq | mid | candidate | Financial Services / Insurance—Specialty |
| IRMD | IRADIMED CORP | 0001325618 | Nasdaq | small | candidate | Healthcare / Medical Devices |
| MMSI | MERIT MEDICAL SYSTEMS INC | 0000856982 | Nasdaq | mid | control | Healthcare / Medical Instruments & Supplies |
| ANIP | ANI PHARMACEUTICALS INC | 0001023024 | Nasdaq | mid | candidate | Healthcare / Drug Manufacturers—Specialty & Generic |
| DGII | DIGI INTERNATIONAL INC | 0000854775 | Nasdaq | small | candidate | Technology / Communication Equipment |
| CLFD | Clearfield, Inc. | 0000796505 | Nasdaq | small | candidate | Technology / Communication Equipment |
| PLUS | EPLUS INC | 0001022408 | Nasdaq | small | control | Technology / Information Technology Services |
| JJSF | J&J SNACK FOODS CORP | 0000785956 | Nasdaq | mid | control | Consumer Defensive / Packaged Foods |
| CALM | CAL-MAINE FOODS INC | 0000016160 | Nasdaq | mid | control | Consumer Defensive / Farm Products |
| WINA | WINMARK CORP | 0000908315 | Nasdaq | small | control | Consumer Cyclical / Specialty Retail |
| MNRO | MONRO, INC. | 0000876427 | Nasdaq | small | candidate | Consumer Cyclical / Auto Parts |
| IDT | IDT CORP | 0001005731 | NYSE | small | candidate | Communication Services / Telecom Services |

\* Exchange is metadata (not load-bearing — SEC collection keys on CIK). Values are as reported by the SEC
submissions `exchanges` field.

Resulting sector fill: Utilities 1 → 4, Basic Materials 2 → 5, Energy 2 → 5, Financial Services 4 → 7,
Healthcare 5 → 8, Technology 6 → 9, Consumer Defensive 4 → 6, Consumer Cyclical 3 → 5, Communication
Services 3 → 4. **No new Industrials** (stays 10), no new Real Estate (stays 3).

**Filing cadence verified for all 23** (from the submissions records, fetched paced 2026-07-29): every name has
2026 8-Ks **and** 2026 Form 4s; most recent filing May–July 2026 for every name (e.g. MNRO filed 2026-07-29,
CLFD/IRMD/PLMR/PLUS 2026-07-28). None will sit inert in the sample.

## ⚠ Ticker substring collisions — MUST be handled (7 companies omit `ticker=`)

`NewsAttentionCollector.IsRelevant` matches the ticker with an **unanchored, case-insensitive `Contains`** on
the (publisher-suffix-stripped, whitespace-normalised) headline — no word-boundary check. Seven tickers in this
batch are substrings of common headline words and MUST omit the `ticker=` token from the `newssearch` feed URL
(the established V / DEA / SHOO / ATEX / SHEN treatment):

| Ticker | Collides with |
|---|---|
| KGS | "500 **kgs**" — the kilograms abbreviation, common in commodity/trade headlines |
| PUMP | "**pump**", "**pump**ed", "**pump**kin", "gas **pump** prices" |
| CASS | "**cass**ette", "**Cass**andra", "**cass**ava", "Pi**cass**o" |
| ANIP | "m**anip**ulate", "m**anip**ulation" |
| PLUS | "**plus**", "sur**plus**" |
| CALM | "**calm**", "**calm**ing" |
| IDT | "m**idt**erm", "m**idt**own" |

The other sixteen (MSEX, YORW, OTTR, ASIX, IOSP, KWR, LBRT, TBBK, PLMR, IRMD, MMSI, DGII, CLFD, JJSF, WINA,
MNRO) are distinctive enough to keep the `ticker=` token — **except JJSF, which needs the special treatment
below**.

## ⚠ Query-phrase precision — the phrase is ALSO a `Contains` match (4 special cases)

`IsRelevant` passes when the headline contains the **query phrase** too, so an ambiguous phrase admits junk just
like an ambiguous ticker (junk media inflates the v8 breadth credit and distorts the notedness discount):

- **JJSF — the `&` trap, do NOT put the registrant name in the token.** `TwoKeyFeedToken.TrySplit` splits
  `query=…&ticker=…` on the **first `&` after the value start**, and its contract states "our seeds never put
  `&` inside a value". `query=J&J Snack Foods&ticker=JJSF` would silently parse the phrase as **"J"**, whose
  `Contains` relevance admits nearly every headline in existence. Use **`url: "query=JJSF"`** (phrase = ticker,
  no `ticker=` token). Google News finds ticker-bearing coverage fine; relevance is then exact.
- **MNRO** — "Monro" is a substring of "Monroe" (Monroe County, Marilyn Monroe). Use
  **`url: "query=Monro, Inc&ticker=MNRO"`** — the comma-form phrase matches the registrant's own styling, and
  ticker `MNRO` carries the finance headlines.
- **CLFD** — "Clearfield" alone is a Pennsylvania county and a Utah city (local-news junk). Use
  **`url: "query=Clearfield, Inc&ticker=CLFD"`**.
- **IDT** — use **`url: "query=IDT Corp"`** (no ticker, per the collision table). "IDT Corp" is a prefix
  substring of "IDT Corporation", so the one phrase `Contains`-matches both headline stylings.

Full `newssearch` `url` token per company (name convention `"<Name> — News attention (Google News)"`):

| Ticker | `url` |
|---|---|
| MSEX | `query=Middlesex Water&ticker=MSEX` |
| YORW | `query=York Water&ticker=YORW` |
| OTTR | `query=Otter Tail&ticker=OTTR` |
| ASIX | `query=AdvanSix&ticker=ASIX` |
| IOSP | `query=Innospec&ticker=IOSP` |
| KWR | `query=Quaker Houghton&ticker=KWR` |
| KGS | `query=Kodiak Gas` |
| LBRT | `query=Liberty Energy&ticker=LBRT` |
| PUMP | `query=ProPetro` |
| CASS | `query=Cass Information Systems` |
| TBBK | `query=The Bancorp&ticker=TBBK` |
| PLMR | `query=Palomar Holdings&ticker=PLMR` |
| IRMD | `query=iRadimed&ticker=IRMD` |
| MMSI | `query=Merit Medical&ticker=MMSI` |
| ANIP | `query=ANI Pharmaceuticals` |
| DGII | `query=Digi International&ticker=DGII` |
| CLFD | `query=Clearfield, Inc&ticker=CLFD` |
| PLUS | `query=ePlus` |
| JJSF | `query=JJSF` |
| CALM | `query=Cal-Maine` |
| WINA | `query=Winmark&ticker=WINA` |
| MNRO | `query=Monro, Inc&ticker=MNRO` |
| IDT | `query=IDT Corp` |

(KWR's phrase is "Quaker Houghton" — the operating/dba name every headline uses — not the SEC registrant
"Quaker Chemical".)

## IR RSS feeds — already live-verified with NO User-Agent (do not re-verify with one)

Radar's RSS collector sends **no User-Agent header**; all verification below was done with
`curl -H "User-Agent:" …` on 2026-07-29 (spec 125's corrected method). 200 + ≥10 real `<item>`s for every
included URL.

**Include the `rss` feed for these 12:**

| Ticker | RSS URL |
|---|---|
| MSEX | `https://investors.middlesexwater.com/rss/news-releases.xml` |
| YORW | `https://www.yorkwater.com/feed/` |
| IOSP | `https://investors.innospec.com/rss/news-releases.xml` |
| KWR | `https://investors.quakerhoughton.com/rss/news-releases.xml` |
| PUMP | `https://ir.propetroservices.com/news-events/press-releases/rss` |
| CASS | `https://ir.cassinfo.com/rss/news-releases.xml` |
| PLMR | `https://ir.palomarspecialty.com/rss/news-releases.xml` |
| ANIP | `https://anipharmaceuticals.gcs-web.com/rss/news-releases.xml` |
| DGII | `https://digi.gcs-web.com/rss/news-releases.xml` |
| JJSF | `https://investors.jjsnack.com/rss/news-releases.xml` |
| CALM | `https://calmainefoods.gcs-web.com/rss/news-releases.xml` |
| IDT | `https://www.idt.net/feed/` |

Two of these are site-wide WordPress feeds rather than IR-platform feeds, included after a content check:
**YORW** (`/feed/` carries earnings releases and company announcements — "Reports Three Months Earnings",
customer-program launches — alongside occasional operational notices) and **IDT** (carries genuine press
releases: product awards, NRSInsights monthly retail reports, investor-conference announcements). This is the
opposite of spec 125's GTY finding (a zero-item feed); both verified with 10 live items.

**Omit the `rss` feed for these 11** (do not re-litigate, these were checked 2026-07-29):

| Ticker | Finding |
|---|---|
| OTTR | No feed found (Q4 `/rss/news-releases.xml`, `/news-events/press-releases/rss` and `/feed/` all dead) |
| KGS | No feed found (same patterns dead) |
| LBRT | No feed found |
| TBBK | No feed found |
| IRMD | No feed found |
| CLFD | No feed found |
| PLUS | No feed found |
| WINA | No feed found |
| MNRO | No feed found |
| ASIX | `https://www.advansix.com/feed/` returns 200 with items but is an **agronomy marketing blog** ("Topdress for Your Crops' Bottom Line") — not press releases; do NOT add it |
| MMSI | `https://www.merit.com/feed/` returns 200 with items but is a **corporate-culture blog** (employee highlights) — not press releases; do NOT add it |

These 11 are still fully collected via SEC (CIK) + newssearch — RSS is additive, not required.

## Changes

### 1. `data/companies.json` — add 23 entries

For **each** company, add a seed entry matching the exact shape of the existing entries (see MRCY at the top of
the file). Required/known fields:

- `id`: a fresh **GUID** (generate one per company; unique).
- `name`, `legalName`: the registrant name (natural-case is fine for `name`, e.g. "Middlesex Water Company" /
  ticker `MSEX`; keep `legalName` faithful to the SEC registrant). `ticker`, `exchange`, `countryCode: "US"`,
  `sector`, `industry` from the batch table.
- `followingTier`: the tier from the batch table (values only from {mega, large, mid, small}).
- `aliases` / `themes`: see the table at the end of this spec.
- `sourceFeeds` (mirror the existing per-company pattern):
  - `sec`, `secform4`, `sec13dg` — **all three required**, each
    `url: "https://data.sec.gov/submissions/CIK<10-digit>.json"` using the CIK above (same URL for all three
    types, as every existing entry does). Names follow the existing convention
    (`"<Name> — SEC filings (EDGAR)"`, `"… — SEC Form 4 insider filings (EDGAR)"`,
    `"… — SEC 13D/13G ownership filings (EDGAR)"`).
  - `newssearch` — **required**, name `"<Name> — News attention (Google News)"`, `url` **exactly** as given in
    the token table above (collision + precision analysis is already folded in — do not "normalise" them).
  - `rss` — include for the 12 verified above, omit for the 11 listed as unusable/absent.
  - `usaspending`, `news` (GDELT), `hiringats`, `patents`, `fda`, `trademarks` — **omit**, consistent with the
    spec-125 batch (each requires per-company verification that is out of scope here).

Keep the file valid JSON (trailing-comma-free, UTF-8 like the existing file), and keep all 43 existing
companies untouched.

### 2. `scripts/run-profiles/default.json` — raise the report entry cap

`ReportMaxItems` is currently **60** (set by spec 125 with headroom); with 66 companies the weekly report would
silently drop up to 6 scored companies — the exact spec-125 failure. Set `"ReportMaxItems": 90` — deliberate
headroom again so the *next* expansion does not silently truncate — and extend the profile's `_comment` to
record the change (60 → 90, spec 159, universe 43 → 66). This is an operational display parameter, **not** a
scoring weight — not hashed, no version bump.

## Tests

- Update any test that pins the **universe size or membership** (search tests for a hard-coded 43) to the new
  66-company set.
- **Extend the ticker-collision regression test** (the spec-125 one that pins DEA/SHOO/ATEX/SHEN) to also
  assert: the seven no-`ticker=` names above (KGS, PUMP, CASS, ANIP, PLUS, CALM, IDT) carry no `ticker=` token;
  **JJSF's `newssearch` url is exactly `query=JJSF`** (the `&` trap — the single most likely entry for a later
  well-meaning "consistency" edit to silently break); and a representative distinctive newcomer (e.g. DGII)
  *does* carry `ticker=`.
- Ensure the production `companies.json` still round-trips through
  `LocalFileCompanySeedSource`/`LocalFileCompanySeedDocument` (all new entries parse — GUID `id`, tier string,
  feeds).
- No new production code path is expected — this is seed data + one config key.

## Constraints

- **Data + config only.** **No** `_formula.Version` / `KeywordSignalExtractor.RuleSetVersion` bump; the company
  universe is not a `ScoringConfig` input, so **no fingerprint moves** — the live baseline stamps stay AI-OFF
  `radar-scoring-fp-4eb2fe5d3cdf` / AI-ON `radar-scoring-fp-4da4b5ff6ec9` (60-day window), the unit-test pins
  stay `radar-scoring-fp-0c46e07b94db` / `radar-scoring-fp-28226897f97b` (30-day), and `StrategyIdentityGuard`
  must NOT trip on the next run.
- Every CIK must be exactly as listed (verified 2026-07-29). Do not invent or alter CIKs.
- **No `&` inside any feed-token value** (`TwoKeyFeedToken` contract). JJSF is the case in point.
- **No companies are removed.** All 43 existing entries, including the five mega-caps, stay.
- The coder does not commit any SEC User-Agent into the repo. **SEC rate-limit discipline** for any re-checks:
  reuse the already-fetched data locally; pace `data.sec.gov` requests to ~3/sec, sequentially — an unpaced
  burst has previously self-blocked this machine from `www.sec.gov`.

## Expected operational consequences (record, don't fix)

- **AI-read backlog:** 23 new companies bring an uncached 8-K backlog to the DeepSeek directional read.
  `Ai:MaxFilingsPerRun=50` bounds new analyses per run, so the backlog drains over the first ~1–2 baseline
  runs — a temporary DeepInfra cost bump, expected.
- **Price backfill:** `Prices.Range="1y"` fetches a year of daily history per new ticker automatically on the
  next run, so per-company efficacy SVGs work immediately.
- **Leaderboard lag:** the new names' first rankable observations arrive ~21 days after their first scores
  (≈ 2026-08-19+), joining the same window in which the v9/v10/v11 arms become rankable. Until then the
  leaderboard's observation counts simply grow more slowly — that is the honest answer, not a regression.
- **Run duration and source count:** sources checked grows from 203 to ~300; collection wall-clock and SEC
  request volume rise accordingly (all paced by the global `SecRequestPacer`).

## Caveats to sanity-check during implementation

- **`followingTier` values are proposals**, inferred from coverage density and market-cap scale (no measured
  analyst-count field exists). Sanity-check each before committing; the tier directly drives the notedness
  discount.
- **Sector/Industry strings are Yahoo-convention classifications** chosen to match the existing file. Two
  judgement calls to flag rather than hide: **CASS** is a mixed freight-payments/commercial-bank model (SEC SIC
  "Services-Business Services") recorded here as Financial Services / Specialty Business Services — spec 125
  excluded it for exactly this ambiguity; it is included now because the batch needs steady small-cap financial
  controls and the ambiguity is metadata-only. **OTTR** (SEC SIC "Electric Services") is a utility holding
  company with a large plastics/manufacturing segment; Utilities / Utilities—Diversified is the better-fit
  label.
- **KWR** trades and reports as "Quaker Houghton" (dba); the SEC registrant remains QUAKER CHEMICAL CORP. The
  seed `name` should be "Quaker Houghton" (what headlines and the newssearch phrase use), `legalName` the
  registrant.

## `aliases` and `themes`

| Ticker | aliases | themes |
|---|---|---|
| MSEX | "Middlesex Water", "Middlesex" | "regulated water utility", "rate base growth", "New Jersey water infrastructure" |
| YORW | "York Water", "The York Water Company" | "regulated water utility", "Pennsylvania water", "dividend consistency" |
| OTTR | "Otter Tail", "Otter Tail Power" | "diversified utility", "electric utility", "plastics manufacturing" |
| ASIX | "AdvanSix" | "nylon chemistry", "ammonium sulfate", "chemical intermediates" |
| IOSP | "Innospec" | "specialty chemicals", "fuel additives", "performance chemicals" |
| KWR | "Quaker Houghton", "Quaker Chemical" | "industrial process fluids", "metalworking fluids", "specialty chemicals" |
| KGS | "Kodiak Gas Services", "Kodiak" | "gas compression", "Permian infrastructure", "contract compression" |
| LBRT | "Liberty Energy", "Liberty" | "hydraulic fracturing", "oilfield services", "power generation services" |
| PUMP | "ProPetro", "ProPetro Holding" | "pressure pumping", "Permian oilfield services", "electric frac fleets" |
| CASS | "Cass Information Systems", "Cass" | "freight audit and payment", "utility expense management", "payment processing" |
| TBBK | "The Bancorp", "Bancorp Bank" | "fintech partner banking", "banking-as-a-service", "specialty lending" |
| PLMR | "Palomar", "Palomar Holdings" | "specialty insurance", "earthquake insurance", "fronting" |
| IRMD | "iRadimed", "IRADIMED" | "MRI-compatible medical devices", "infusion pumps", "patient monitoring" |
| MMSI | "Merit Medical", "Merit" | "interventional medical devices", "cardiovascular devices", "medical consumables" |
| ANIP | "ANI Pharmaceuticals", "ANI" | "specialty generics", "rare disease", "Cortrophin" |
| DGII | "Digi International", "Digi" | "industrial IoT", "embedded connectivity", "device networking" |
| CLFD | "Clearfield", "Clearfield Inc" | "fiber connectivity", "fiber to the home", "broadband buildout" |
| PLUS | "ePlus", "ePlus inc" | "IT solutions", "technology financing", "security and cloud services" |
| JJSF | "J&J Snack Foods", "J and J Snack Foods" | "snack foods", "soft pretzels", "frozen beverages" |
| CALM | "Cal-Maine", "Cal-Maine Foods" | "shell eggs", "egg production", "cage-free transition" |
| WINA | "Winmark", "Winmark Corporation" | "resale franchising", "Play It Again Sports", "Plato's Closet" |
| MNRO | "Monro", "Monro Auto Service" | "auto service", "tire retail", "automotive aftermarket" |
| IDT | "IDT Corporation", "IDT Corp" | "fintech", "NRS point-of-sale", "BOSS Money remittances" |

## Acceptance criteria

- [ ] All 23 companies added to `data/companies.json` with the exact CIKs above, correct `followingTier`, and
      `sec`/`secform4`/`sec13dg`/`newssearch` feeds; each `id` a unique GUID; file is valid JSON; all 43
      existing companies byte-untouched.
- [ ] The seven collision tickers (KGS, PUMP, CASS, ANIP, PLUS, CALM, IDT) carry **no** `ticker=` token; JJSF's
      newssearch url is exactly `query=JJSF`; the four precision phrases (MNRO/CLFD/IDT/KWR) are as specified.
- [ ] The 12 verified `rss` feeds are present; the 11 unusable/absent ones are omitted (including ASIX's and
      MMSI's blog feeds).
- [ ] `"ReportMaxItems": 90` in `scripts/run-profiles/default.json`, with the `_comment` extended to explain it.
- [ ] Universe-size/membership tests updated to 66; the collision regression test extended per the Tests
      section.
- [ ] Fingerprints unchanged (live AI-OFF `radar-scoring-fp-4eb2fe5d3cdf` / AI-ON
      `radar-scoring-fp-4da4b5ff6ec9`; unit pins `radar-scoring-fp-0c46e07b94db` /
      `radar-scoring-fp-28226897f97b`); no formula/RuleSetVersion bump.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
