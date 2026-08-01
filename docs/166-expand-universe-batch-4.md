# Task: Expand the watch universe — batch 4: add 8 verified companies (66 → 74)

> **UNIVERSE EXPANSION (data-only; spec-159 pattern).** Proposed by the external reviewer 2026-07-31 and
> endorsed by the maintainer alongside the standing constraint: **more companies yes, more strategies no.**
> The company universe is NOT a scoring input — no fingerprint impact, no formula/RuleSetVersion bump, no
> `ReportMaxItems` change needed (74 < the spec-159 cap of 90). **All CIKs were verified 2026-07-31 against
> SEC's authoritative `company_tickers.json`** (single paced request, compliant UA). Unlike spec 159, **RSS
> feeds and filing cadence are NOT pre-verified** — both are implementation steps below, with the exact
> method. **CHEF (Chefs' Warehouse) was in the original nine-name proposal and is DROPPED** — see the
> exclusion note — so this batch is eight.

## Cohort classification — read this before citing this batch in any efficacy result

**This batch is an EVENT-ENRICHED EXPLORATORY COHORT, not a neutral sample.** The review that caught it was
right: several names were proposed partly *because of* known 2026 events (CCOI's data-centre sale, THRM's
pending Modine combination, CARS's restructuring disclosures, PSTL's raised guidance) — that selects on
current manifestations of the very predictor Radar scores, and one rationale ("third-party-attention
potential") brushes the AD-16 attention outcome itself. Price action was still never consulted (AD-14
holds), but "no price selection" is not "neutral". Consequences, recorded here so they bind:

- The batch carries **no candidate/control bucket labels** — none would be honest.
- **AD-16 §7 is AMENDED (2026-07-31, committed with this spec) to exclude this cohort from the binding
  primary screen** — separate reporting alone would not have changed §7's "same eligible companies at each
  date" rule, so these eight names would otherwise have entered the screen as their outcomes accrued. The
  exclusion is machine-readable at **`docs/cohorts/event-enriched-2026-07.json`** (the evaluator reads the
  file, never git history; the minimum-20 eligibility count is taken AFTER the exclusion). The cohort may
  appear only in a separately labelled exploratory rerun reported beside the primary result.
- Any other efficacy/leaderboard analysis that segments by cohort must likewise report this batch
  **separately** from the spec-125/159 waves, and pooled results that include it must carry an "includes an
  event-enriched cohort" note.
- Cross-sectional validation passing *because* of this cohort proves enrichment, not discrimination — the
  discrimination claim rests on the earlier neutral waves.

## Overview

Spec 159's rationale stands: cross-sectional power is the lever, and companies added now become measurable
in the same September window everything else is waiting on. This batch fills genuine coverage gaps — two
REIT models (the calibration study showed REIT prints need FFO/AFFO framing; the universe holds only 3),
housing-cycle exposure, communications infrastructure, auto-supplier economics — and adds **zero new
Industrials and zero semiconductors**. Result: universe 66 → **74**. **No companies are removed.**

## Assignment

Worktree: any
Dependencies: current main (post #167). Pure data change — independent of scoring; no fingerprint impact.
Estimated time: ~1–2 hours (seed authoring + the two verification passes below).

## The batch (CIKs verified 2026-07-31 against `company_tickers.json`; registrant titles as returned by SEC)

| Ticker | Registrant name (SEC) | CIK (10-digit) | followingTier* | Sector / Industry |
|---|---|---|---|---|
| PSTL | Postal Realty Trust, Inc. | 0001759774 | small | Real Estate / REIT—Office |
| FR | FIRST INDUSTRIAL REALTY TRUST INC | 0000921825 | mid | Real Estate / REIT—Industrial |
| CCOI | COGENT COMMUNICATIONS HOLDINGS, INC. | 0001158324 | mid | Communication Services / Telecom Services |
| ATNI | ATN International, Inc. | 0000879585 | small | Communication Services / Telecom Services |
| CARS | Cars.com Inc. | 0001683606 | small | Communication Services / Internet Content & Information |
| MHO | M/I HOMES, INC. | 0000799292 | mid | Consumer Cyclical / Residential Construction |
| THRM | Gentherm Inc | 0000903129 | small | Consumer Cyclical / Auto Parts |
| BKE | BUCKLE INC | 0000885245 | small | Consumer Cyclical / Apparel Retail |

\* **`followingTier` rubric — following/coverage ONLY, never market cap (AD-14; the `FollowingTier` domain
contract states it is NEVER derived from price, market cap, or volume).** Assess each tier from observable
FOLLOWING signals exclusively: count of covering sell-side analysts (IR "analyst coverage" pages /
earnings-call Q&A participation), financial-press mention density, and index-membership-driven coverage —
and record in the PR body which signal grounded each tier. The proposals above are prior beliefs from
coverage impressions; the implementer re-evaluates all eight under this rubric and changes any tier the
evidence contradicts (the tier directly drives the notedness discount, so a cap-inferred tier would smuggle
market cap into scoring). Prior reasoning per name: PSTL/ATNI — minimal sell-side coverage, rarely in
national financial press ⇒ small; CARS/THRM/BKE — modest coverage; BKE in particular is famously
thinly covered for its size ⇒ small; FR/CCOI/MHO — solidly covered mid-tier names with regular analyst
participation ⇒ mid. None approaches the large/mega following of the existing benchmark names.

Resulting sector fill: Real Estate 3 → 5, Communication Services 4 → 7, Consumer Cyclical 5 → 8.
Industrials stay 10; no new semiconductors; Consumer Defensive unchanged (CHEF dropped).

Coverage rationale per name (research-sample framing; the event-enrichment caveat above applies): PSTL — a
fourth REIT model (USPS-leased, acquisition-driven, AFFO guidance). FR — industrial REIT with clean
occupancy/same-store disclosures. CCOI — communications infrastructure; its 2026 data-centre sale is a
ready-made comparability stress case for the cmpscan work. ATNI — rural/international telecom + towers.
CARS — digital marketplace with recurring-revenue disclosures. MHO — housing cycle via
orders/backlog/cancellations/margins, a dimension the universe lacks. THRM — automotive supplier with
FX/volume/transaction comparability. BKE — steady, thinly-covered retail with frequent uncomplicated
disclosures.

## ⚠ CHEF exclusion — recorded, with the reopening condition

`NewsAttentionCollector.IsRelevant` is an exact-punctuation, unanchored, case-insensitive `Contains`.
"Chefs' Warehouse" headlines split between straight (`'`) and curly (`’`) apostrophes, ticker `CHEF` is a
common-word substring that must be omitted, and no apostrophe-free distinctive phrase exists — so every
curly-quoted headline would be a **false zero-attention observation**, corrupting the AD-16 third-party
attention outcome for that name (SEC/RSS coverage does not repair the attention series). CHEF may join a
future batch **only after** a punctuation-normalization change to the collector's matching (its own spec —
it touches relevance semantics for every company, so it needs its own tests and a look at whether existing
seeds relied on exact punctuation).

## ⚠ Ticker substring collisions (`IsRelevant` is an unanchored case-insensitive `Contains`)

**Two tickers MUST omit the `ticker=` token** (the established V/DEA/KGS/PLUS treatment):

| Ticker | Collides with |
|---|---|
| FR | "**fr**om", "**fr**ee", "**Fr**iday", "**Fr**ance" — near-universal bigram |
| CARS | "**cars**", "used **cars**", "**cars** recalled" |

The other six (PSTL, CCOI, ATNI, MHO, THRM, BKE) are distinctive letter-runs — keep `ticker=`.

## `newssearch` `url` tokens (no `&` inside any value — the `TwoKeyFeedToken` contract, spec 159's JJSF trap)

| Ticker | `url` |
|---|---|
| PSTL | `query=Postal Realty&ticker=PSTL` |
| FR | `query=First Industrial Realty` |
| CCOI | `query=Cogent Communications&ticker=CCOI` |
| ATNI | `query=ATN International&ticker=ATNI` |
| CARS | `query=Cars.com` |
| MHO | `query=M/I Homes&ticker=MHO` |
| THRM | `query=Gentherm&ticker=THRM` |
| BKE | `query=The Buckle&ticker=BKE` |

Notes: **MHO** — the `/` in `M/I Homes` is fine inside a token value (only `&` is forbidden); verify the
composed Google News URL encodes it. **BKE** — `The Buckle` (not bare `Buckle`, which `Contains`-matches
"buckle up"); ticker `BKE` carries the finance-styled headlines.

## RSS feeds — NOT pre-verified; verify during implementation (this differs from spec 159)

For each of the eight, probe the standard patterns (`/rss/news-releases.xml` on the IR host,
`/news-events/press-releases/rss`, site `/feed/`) using **`curl -H "User-Agent:" <url>`** — the collector
sends NO User-Agent, so verifying with one (spec 125's original mistake) admits feeds that 403 in
production. Include a feed only on HTTP 200 **plus** ≥10 real `<item>`s **plus** a content check that items
are press releases, not marketing/culture blogs (spec 159's ASIX/MMSI trap). Record the per-ticker verdict
(URL or named failure) in the PR body. Omitted feeds are fine — SEC (CIK) + `newssearch` fully cover every
name.

## Filing-cadence check — during implementation, paced

Fetch each CIK's `data.sec.gov` submissions record (sequential, ~3/sec max, compliant UA from the
environment — never committed): confirm 2026 8-Ks and Form 4s exist and note the most-recent filing date in
the PR body. A name with no 2026 filings would sit inert in the sample — flag it rather than silently seed
it. (The proposal states all names have active 2026 filings; verify rather than trust.)

## Changes

### `data/companies.json` — add 8 entries (the ONLY file change)

Exact spec-159 shape per entry: fresh unique GUID `id`; `name` natural-case (e.g. "Postal Realty Trust",
"First Industrial Realty", "Cogent Communications", "ATN International", "Cars.com", "M/I Homes",
"Gentherm", "The Buckle") with `legalName` faithful to the SEC registrant title above; `ticker`,
`countryCode: "US"`, sector/industry/tier from the batch table; `sec`/`secform4`/`sec13dg` all three on
`https://data.sec.gov/submissions/CIK<10-digit>.json`; `newssearch` exactly per the token table; `rss` only
where verified above; all other collectors omitted. All 66 existing entries byte-untouched.

`aliases` / `themes`:

| Ticker | aliases | themes |
|---|---|---|
| PSTL | "Postal Realty", "Postal Realty Trust" | "USPS-leased properties", "net lease REIT", "postal logistics real estate" |
| FR | "First Industrial", "First Industrial Realty" | "industrial REIT", "logistics real estate", "warehouse development" |
| CCOI | "Cogent", "Cogent Communications" | "internet transit", "fiber network", "data center leasing", "IPv4 leasing" |
| ATNI | "ATN International", "ATN" | "rural broadband", "international telecom", "tower infrastructure" |
| CARS | "Cars.com", "Cars Commerce" | "auto marketplace", "dealer software", "digital advertising" |
| MHO | "M/I Homes", "M/I" | "homebuilding", "orders and backlog", "housing cycle" |
| THRM | "Gentherm" | "thermal management", "automotive seating comfort", "medical thermal devices" |
| BKE | "Buckle", "The Buckle" | "apparel retail", "denim", "mall retail" |

## Tests

- Universe-size/membership tests: 66 → 74.
- Extend the ticker-collision regression test: FR and CARS carry no `ticker=` token; BKE's phrase is exactly
  `The Buckle`; a representative distinctive newcomer (e.g. CCOI) does carry `ticker=`; the JJSF
  `query=JJSF` pin stays untouched.
- `companies.json` round-trips through `LocalFileCompanySeedSource` (GUID ids, tiers, feeds parse).

## Constraints

- Data only; **no fingerprint moves** (current live-baseline stamps: AI-OFF `radar-scoring-fp-4eb2fe5d3cdf`,
  AI-ON 60d `radar-scoring-fp-5ffa8c9e25f0` post-160); `StrategyIdentityGuard` must not trip on the next run.
- CIKs exactly as tabled (verified 2026-07-31); do not invent or alter. No `&` inside any token value. No
  SEC UA committed; all SEC checks paced and sequential.
- **`followingTier` is assessed from following/coverage evidence only** — never market cap, price or volume
  (AD-14 + the `FollowingTier` domain contract); the grounding per name is recorded in the PR body.
- **No new strategies** (standing constraint, 2026-07-31): this batch adds observations for the EXISTING
  ten arms, nothing else.
- **CHEF stays out** until the punctuation-normalization collector spec exists and merges.

## Expected operational consequences (record, don't fix)

Per-run cost 660 → **740 scorings**; source feeds ≈ 302 active → **≈ 334 active** (8 × 4 SEC/newssearch
feeds = 32, plus any RSS that passes verification; ≈ 355 total seeded); temporary DeepInfra 8-K backlog over ~1–2 runs
(bounded by `Ai:MaxFilingsPerRun`); 1-year price backfill per new ticker on first run; first rankable
observations ≈ 21 days after first snapshots (≈ **2026-08-21+** if merged promptly). If the spec-161
filtered-collect affordance has merged, a `-Companies` collect pass can backfill the eight cheaply before
the next scheduled full run.

## Acceptance criteria

- [ ] 8 entries added with the exact CIKs; tiers re-evaluated under the coverage-only rubric with the
      grounding recorded in the PR body; all-three SEC feeds + `newssearch` tokens exactly as tabled; RSS
      only where the no-UA verification passed, with per-ticker verdicts recorded; filing cadence confirmed
      per name.
- [ ] The event-enriched-cohort classification is preserved verbatim in this spec; no candidate/control
      labels anywhere; `docs/cohorts/event-enriched-2026-07.json` and the AD-16 §7 amendment (both already
      committed) list exactly this batch's eight tickers — assert the seed additions match the cohort file.
- [ ] Collision/regression tests extended (FR/CARS no-ticker, BKE phrase, JJSF pin untouched); universe
      tests at 74; all 66 existing entries byte-untouched; file valid JSON.
- [ ] No fingerprint moves; `dotnet build Radar.sln -c Release` and
      `dotnet test Radar.sln -c Release --no-build` pass.