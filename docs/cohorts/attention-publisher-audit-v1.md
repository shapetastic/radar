# Attention publisher tier audit — `attention-publisher-audit-v1`

**This file is the authority for publisher MEMBERSHIP in `AttentionSourceTierOptions.Default`** (spec 196 §2).
It records what each hard-coded publisher actually published in the live corpus, so the tier assignment is
auditable rather than asserted. It determines **membership only** — never a tier's weight, which is a spec
decision (§2) and lives in code beside the tier definitions.

## The tier policy (defined BEFORE any publisher was assigned)

| tier | weight | definition |
| --- | ---: | --- |
| `Wire` | 0.05 | Paid or company-originated distribution. Confers visibility, **not independent notice**, because the company controls whether the item exists at all. |
| `Mill` | 0.1 | Automated, templated or republished material with no demonstrated independent selection. The test is *selection*: does this outlet decide which companies to cover, or does it publish on every ticker by construction? |
| `Platform` | 0.3 | Investor-content platforms carrying a mixture of contributor analysis and syndication — a human chose to write about *this* company, but the outlet exercises little editorial gatekeeping. |
| `Genuine` | 1.0 | Independent reporting or editorial selection. |

An unrecognised publisher is **not** in this table and resolves to the configured `UnknownWeight`, whose
default spec 196 §1 inverted from `0.25` to `0.1` — the `Mill` weight. Enumerating the long tail is
unwinnable (256 publishers / 367 observations remain unclassified after this audit, 194 of them singletons),
so an explicit entry is now required to count as *notice* rather than to be *discounted*.

## Corpus and sampling rule (fully reproducible)

- **Population** — news observations whose `publishedAtUtc` falls in the **60 days preceding the pinned
  as-of instant `2026-08-27T21:42:45.4943606Z`** (the last completed baseline's `windowEndUtc`) **and**
  whose company is in the **74-company universe** of `data/companies.json`. Not file mtime, not "now".
- **Size** — **2,865** observations over **317** distinct normalized publishers.
- **Tier resolution** — the production `ConfiguredAttentionSourceWeights.Normalize`: lowercase, strip one
  trailing TLD from the closed set, remove non-alphanumerics. Not whole-string comparison.
- **Sampling** — for each publisher, the **most-recent in-corpus item per company** (ordered by
  `PublishedAtUtc` then `ObservationId`), then those per-company representatives ordered by
  `PublishedAtUtc` descending then `ObservationId`, **up to ten companies**. Same corpus, same sample,
  every time.

## Measured tier shares at the pinned instant

| tier | before spec 196 | after spec 196 |
| --- | ---: | ---: |
| `Wire` (0.05) | — | 127 (4.4 %) |
| `Mill` (0.1) | 1,415 (49.4 %) | 2,238 (78.1 %) |
| `Platform` (0.3) | — | 105 (3.7 %) |
| `Genuine` (1.0) | 15 (0.5 %) | 28 (1.0 %) |
| unclassified | **1,435 (50.1 %)** | **367 (12.8 %)** |

Per-publisher counts differ by one or two from the figures quoted in the spec body for a few outlets
(Yahoo Finance 479 vs 478, Seeking Alpha 66 vs 64, PR Newswire 42 vs 41, Business Wire 30 vs 29). The
tier totals reproduce the spec exactly; the small differences are grouping — this audit resolves through
the production `Normalize`, which folds domain-form variants (`seekingalpha.com` onto `Seeking Alpha`)
that a display-name grouping keeps apart.

## Two findings this audit recorded and deliberately did NOT fix in the tier map

1. **Company-name collision, not attention.** `Valley News Live` (8), `Perham Focus` (7) and
   `The Mighty 790 KFGO` (6) publish genuine local journalism about **Otter Tail County, Minnesota** —
   matched to **OTTR** (Otter Tail Corporation) by name alone. These are real newsrooms exercising real
   selection, so tagging them `Mill` would misdescribe them; the defect is in company resolution, not in
   the publisher map. They stay unclassified and land on the inverted `0.1` default, which is the right
   practical outcome. A company-resolution fix is a separate spec.
2. **The issuer as its own publisher.** `Aehr Test Systems` (7), `Caterpillar Inc` (4) and `Chevron` (3)
   appear as publisher names — company-originated distribution counted as third-party publisher breadth.
   Issuer names are not added to a global publisher map (they do not generalise across 74 companies);
   they too fall to the inverted default.

## Measured counterfactual — what the corrected policy actually produced (spec 196 §7)

One paired READ-ONLY run (`AttentionPolicyCounterfactualTests`) at the pinned as-of
`2026-08-27T21:42:45.4943606Z`, 60-day window, `default` strategy, 74 companies, nothing persisted. Only the
`IAttentionSourceWeights` instance differs between the two arms, so the delta is the policy's and not the
evidence's. **The old-policy arm reproduces this file's own corpus figures exactly (2,865 / 1,435 / 1,415 /
15), which is what validates the harness.**

**Raw observation coverage** (2,865 in-window observations) — the same population as the tier-share table
above, restated from the harness:

| tier | old policy | new policy |
| --- | ---: | ---: |
| unclassified | 1,435 (50.1 %) | **367 (12.8 %)** |
| `Mill` | 1,415 (49.4 %) | 2,238 (78.1 %) |
| `Platform` | 0 | 105 (3.7 %) |
| `Wire` | 0 | 127 (4.4 %) |
| `Genuine` | 15 (0.5 %) | 28 (1.0 %) |
| distinct unclassified publishers | 302 | 256 (194 singletons) |

Largest remaining unclassified: `Valley News Live` 8, `Aehr Test Systems` 7, `Perham Focus` 7,
`The Mighty 790 KFGO` 6 — exactly the company-name-collision and issuer-as-publisher cases recorded above and
deliberately not tiered.

**Breadth actually consumed by `AttentionReach`** — the *scoring* unit, not the raw volume: 3,107
company-publisher pairs over 833 distinct publishers, counting survivors and collapsed-only publishers alike.

| measure | old policy | new policy |
| --- | ---: | ---: |
| tier-weighted reach per company, min | 2.350 | 1.500 |
| tier-weighted reach per company, mean | **9.165** | **4.949** |
| tier-weighted reach per company, max | 58.900 | 25.700 |
| distinct publishers, unclassified | **808** | **756** |
| distinct publishers, `Mill` | 19 | 55 |
| distinct publishers, `Platform` | 0 | 6 |
| distinct publishers, `Wire` | 0 | 7 |
| distinct publishers, `Genuine` | 6 | 9 |

**This audit classified the VOLUME, not the breadth TAIL.** 756 of 833 distinct publishers are still
unclassified even though only 12.8 % of observations are, because the tail is overwhelmingly singletons —
which is precisely why the inverted default, rather than further enumeration, was the right lever.

**`AttentionScore`** (n = 74): min 52 → 44, mean **74.4 → 64.7**, max 95 → 90, spread 43 → **46**, populated
decades 5 → 6.

| decade | old | new |
| --- | ---: | ---: |
| 40–49 | 0 | 2 |
| 50–59 | 2 | 17 |
| 60–69 | 16 | 40 |
| 70–79 | 43 | 11 |
| 80–89 | 10 | 3 |
| 90–99 | 3 | 1 |

**`OpportunityScore`** (n = 74): min 3 → 3, mean 19.0 → **20.2**, max 38 → **41**, spread 35 → **38**,
populated decades 4 → 5.

| decade | old | new |
| --- | ---: | ---: |
| 0–9 | 6 | 6 |
| 10–19 | 35 | 33 |
| 20–29 | 28 | 25 |
| 30–39 | 5 | 8 |
| 40–49 | 0 | 2 |

### Verdict — PARTIAL, not a clean win

The criterion (attention DISCRIMINATES rather than taxes) is only **partially** met. Real improvement: the
mass moved out of the 70s (43 → 11) into the 60s, a sixth decade opened and the spread widened 43 → 46. But
**40 of 74 companies (54 %) still sit in one decade**, so attention remains substantially a broad discount
rather than a strong discriminator. Per spec 196 §5/§7 that is the recorded evidence for a separate
discount-weight-tuning slice — **no weight was tuned here to manufacture a better-looking spread**, and
`OpportunityAttentionDivisor`, `OpportunityAttentionDiscountWeight` and `FollowingTierDiscountWeight` are
untouched.

## Sampled audit

Each entry below records the publisher's in-corpus volume, its company spread, the audit verdict, and the
sampled items the verdict rests on.

## Tier `Wire`

### PR Newswire

`prnewswire` · **42** in-corpus observations across **17** companies · **verdict: `Wire`**

Company-originated press releases and paid law-firm alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-27 | CCOI Investor Alert: COGENT COMMUNICATIONS HOLDINGS, INC. Securities Class Action Notice - Contact SueWallSt - PR Newswire |
| HRL | 2026-08-27 | HORMEL FOODS REPORTS THIRD QUARTER FISCAL 2026 RESULTS - PR Newswire |
| MHO | 2026-08-25 | Zonda and M/I Homes Partner on the 2027 Virtual Concept Home - PR Newswire |
| CVLT | 2026-08-25 | Commvault Closes the Gap Between Detection and Recovery at Fal.Con 2026 - PR Newswire |
| TMDX | 2026-08-25 | Did TransMedics Group, Inc. Insiders Breach their Fiduciary Duties to Shareholders? - PR Newswire |
| MRCY | 2026-08-25 | Did Mercury Systems, Inc. Insiders Breach their Fiduciary Duties to Shareholders? - PR Newswire |
| PLUS | 2026-08-24 | ePlus Acquires Assets of Daymark Solutions - PR Newswire |
| FLO | 2026-08-20 | FLOWERS FOODS, INC. REPORTS SECOND QUARTER 2026 RESULTS - PR Newswire |
| LZB | 2026-08-18 | La-Z-Boy Incorporated Reports First Quarter Results; Retail Momentum With Positive Written Same-Store Sales - PR Newswire |
| CAT | 2026-08-17 | Caterpillar Expands Workforce Initiative to Arkansas to Strengthen Pathways to Manufacturing Careers - PR Newswire |

### GlobeNewswire

`globenewswire` · **31** in-corpus observations across **13** companies · **verdict: `Wire`**

Company-originated press releases and paid law-firm alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-27 | Cogent Communications Shareholder Alert: ClaimsFiler - GlobeNewswire |
| SFBS | 2026-08-24 | ServisFirst Bancshares, Inc. Ranks Sixth Among Top-Performing Banks with between $10 Billion to $50 Billion in Assets - GlobeNewswire |
| ANIP | 2026-08-24 | ANI Pharmaceuticals Appoints Henry Gosebruch to Board of Directors - GlobeNewswire |
| TMDX | 2026-08-17 | TransMedics Group, Inc. Investor News: Rosen Law Firm - GlobeNewswire |
| ATNI | 2026-08-17 | ATN International, Inc. to Present and Host 1x1 Investor - GlobeNewswire |
| SHEN | 2026-08-12 | Shentel Employees Fight Hunger Through 2026 Shentel Cares Initiatives - GlobeNewswire |
| ATEX | 2026-08-11 | Anterix Issues Statement in Support of SpaceX Proposal - GlobeNewswire |
| PSTL | 2026-08-04 | Postal Realty Trust, Inc. Reports Second Quarter 2026 Results - GlobeNewswire |
| BELFB | 2026-08-03 | Bel Fuse Inc. Announces Regular Quarterly Cash Dividend on its Class A and Class B Shares - GlobeNewswire |
| POWL | 2026-08-03 | Powell Industries Declares Quarterly Cash Dividend - GlobeNewswire |

### Business Wire

`businesswire` · **30** in-corpus observations across **19** companies · **verdict: `Wire`**

Company-originated press releases and paid law-firm alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| AGYS | 2026-08-27 | 40% of Golfweek Top 100 U.S. Resort Golf Courses Use Agilysys Golf - Business Wire |
| CCOI | 2026-08-17 | Cogent Communications Holdings, Inc. Class Action Reminder – Robbins LLP Encourages CCOI Investors to Contact the Firm for Information About Their Rig |
| WDFC | 2026-08-17 | WD-40 Company to Participate in Water Tower Research Fireside Chat Series - Business Wire |
| TMDX | 2026-08-17 | TransMedics Group Investigation Initiated: Kahn Swick & Foti, LLC Investigates the Officers and Directors of TransMedics Group, Inc. - TMDX - Business |
| MNRO | 2026-08-13 | Monro, Inc. Declares Quarterly Cash Dividend - Business Wire |
| HZO | 2026-08-10 | MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Tran |
| HLIO | 2026-08-10 | Helios Technologies Reports Second Quarter 2026 Results; Profitable Sales Growth Momentum Continues, Raising Full Year 2026 Outlook - Business Wire |
| ASIX | 2026-08-07 | AdvanSix Announces Second Quarter 2026 Financial Results - Business Wire |
| BKE | 2026-08-06 | The Buckle, Inc. Reports July 2026 Net Sales - Business Wire |
| KGS | 2026-08-05 | Kodiak Gas Services Announces Quarterly Dividend - Business Wire |

### TMX Newsfile

`tmxnewsfile` · **12** in-corpus observations across **2** companies · **verdict: `Wire`**

Newswire distribution; the sampled items are paid law-firm investor alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-27 | Hagens Berman Alerts Cogent Communications (CCOI) Investors to Recent Q2 2026 Financial Results Amid Ongoing Securities Class Action and September 21  |
| CVLT | 2026-08-26 | COMMVAULT SYSTEMS, INC. (CVLT) SHAREHOLDER INVESTIGATION ALERT: Bernstein Liebhard Investigates Potential Breaches of Fiduciary Duty - TMX Newsfile |

### ACCESS Newswire

`accessnewswire` · **9** in-corpus observations across **3** companies · **verdict: `Wire`**

Newswire distribution; sampled items are paid law-firm investor alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CVLT | 2026-08-25 | CommVault Systems, Inc. (CVLT) Investigation: Bronstein, Gewirtz & Grossman, LLC Encourages Shareholders to Contact the Firm to Learn More About the I |
| EOSE | 2026-08-23 | Bronstein, Gewirtz & Grossman, LLC Announces an Investigation Against Eos Energy Enterprises, Inc. (EOSE) and Encourages Shareholders to Learn More Ab |
| CCOI | 2026-08-22 | Securities Lawsuit Alert: Cogent Communications Holdings, Inc. (CCOI) - Contact Levi & Korsinsky Before September 21, 2026 - ACCESS Newswire |

### NewMediaWire

`newmediawire` · **3** in-corpus observations across **1** companies · **verdict: `Wire`**

Newswire distribution.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-27 | Kaplan Fox Continues to Alert Investors of Cogent Communications Holdings, Inc. (NASDAQ: CCOI) to a Class Action Deadline on September 21, 2026 - NewM |

## Tier `Mill`

### Yahoo Finance

`yahoofinance` · **479** in-corpus observations across **73** companies · **verdict: `Mill`**

Aggregator republishing Zacks / Insider Monkey / Simply Wall St templated pieces across every ticker; no independent selection.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| FLO | 2026-08-27 | Flowers Foods (FLO) Lost Volume as Price/Mix Rose. Is the Bread Aisle Breaking its Business Model? - Yahoo Finance |
| CVLT | 2026-08-27 | Why Is Commvault (CVLT) Up 11.6% Since Last Earnings Report? - Yahoo Finance |
| HRL | 2026-08-27 | Hormel Foods Trims Full-Year Sales Guidance Amid Weak Consumer Demand - Yahoo Finance |
| HLIO | 2026-08-27 | Wall Street Analysts See a 26.12% Upside in Helios Technologies (HLIO): Can the Stock Really Move This High? - Yahoo Finance |
| KLIC | 2026-08-27 | How Much Upside is Left in Kulicke and Soffa (KLIC)? Wall Street Analysts Think 25.67% - Yahoo Finance |
| CVX | 2026-08-27 | What Does Chevron (CVX) Want From Its Nuclear Fusion Push? - Yahoo Finance |
| MRCY | 2026-08-27 | Mercury Systems (MRCY) Is Sitting On A Record Pile Of Orders - Yahoo Finance |
| MHO | 2026-08-27 | Zacks Industry Outlook PulteGroup, M/I Homes and Century - Yahoo Finance |
| MYRG | 2026-08-27 | How Oversold Technicals and Rising Earnings Estimates Will Impact MYR Group (MYRG) Investors - Yahoo Finance |
| JNJ | 2026-08-26 | Johnson & Johnson (JNJ) Sees a More Significant Dip Than Broader Market: Some Facts to Know - Yahoo Finance |

### Quiver Quantitative

`quiverquantitative` · **51** in-corpus observations across **36** companies · **verdict: `Mill`**

Templated per-ticker `Financials - Revenue Breakdown` pages plus PR restatements.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| EOSE | 2026-08-27 | Eos Energy Enterprises to Consolidate Battery Manufacturing at Thorn Hill, Expanding Pittsburgh Operations - Quiver Quantitative |
| HRL | 2026-08-27 | Hormel Foods Reports $2.96 Billion Third-Quarter Sales and 11-Cent EPS - Quiver Quantitative |
| IDT | 2026-08-24 | IDT Corporation to Present at Midwest IDEAS Investor Conference - Quiver Quantitative |
| CAT | 2026-08-24 | Caterpillar Inc. Stock (CAT) Opinions on Record Quarterly Earnings - Quiver Quantitative |
| NSSC | 2026-08-24 | NAPCO Security NSSC Q4 Revenue Rises 10% to Record $55.8 Million - Quiver Quantitative |
| MYRG | 2026-08-24 | MYRG \| MYR Group, Inc. Financials - Revenue Breakdown - Quiver Quantitative |
| KGS | 2026-08-24 | KGS \| Kodiak Gas Services, Inc. Financials - Revenue Breakdown - Quiver Quantitative |
| PUMP | 2026-08-23 | PUMP \| ProPetro Holding Corp. Financials - Revenue Breakdown - Quiver Quantitative |
| PSTL | 2026-08-23 | PSTL \| Postal Realty Trust, Inc Financials - Revenue Breakdown - Quiver Quantitative |
| SKWD | 2026-08-23 | SKWD \| Skyward Specialty Insurance Group, Financials - Revenue Breakdown - Quiver Quantitative |

### Sahm

`sahm` · **31** in-corpus observations across **21** companies · **verdict: `Mill`**

Templated question-form headlines generated across the ticker universe.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| PUMP | 2026-08-25 | ProPetro Stock And 2 Energy Picks Linked To Oil Supply Risk - Sahm |
| GTY | 2026-08-25 | How Investors May Respond To Getty Realty (GTY) Analyst Upgrade Amid Auto Exposure And Environmental Risks - Sahm |
| ANIP | 2026-08-25 | Is ANI Pharmaceuticals (ANIP) Fairly Priced After Its Board Change? - Sahm |
| LBRT | 2026-08-24 | Does Liberty Energy’s (LBRT) Data Center Power Pivot Redefine Its Core Competitive Edge? - Sahm |
| CVLT | 2026-08-24 | Have Insiders Sold Commvault Systems Shares Recently? - Sahm |
| SFBS | 2026-08-23 | What Is Drawing Attention To ServisFirst Bancshares (SFBS) Today? - Sahm |
| ATEX | 2026-08-23 | The Bull Case For Anterix (ATEX) Could Change Following ESOP Share Offering And Profitability Shifts - Sahm |
| WDFC | 2026-08-22 | Is WD-40 (WDFC) Using Thailand Expansion And Buybacks To Reinforce Or Stretch Its Premium Story? - Sahm |
| HRL | 2026-08-20 | Hormel Foods (HRL) Just Gave Investors Something To Think About - Sahm |
| JJSF | 2026-08-20 | J&J Snack Foods (JJSF) Could Be 20% Undervalued As Dividend News Puts Valuation Back Focus - Sahm |

### vinanet.vn

`vinanetvn` · **31** in-corpus observations across **20** companies · **verdict: `Mill`**

Machine-generated price/earnings boilerplate; support/resistance templates.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| WINA | 2026-08-25 | Winmark (WINA) Trading: Gains 1.90% to Reach $350.81 for the Current Session; Chart View: Price Remains Between $333.27 Support and $368.35 Resistance |
| AGYS | 2026-08-24 | Agilysys (AGYS) Quarterly Results: The Bottom-Line Figure Outpaces Its Comparison on the Day; Session Move - vinanet.vn |
| ASIX | 2026-08-24 | AdvanSix (ASIX) Quarterly Results: The EPS Result Is Below Expectation for This Review; Price Move: Shares Close 0.87% Lower at the Close - Revenue Re |
| CLFD | 2026-08-23 | Clearfield (CLFD) Quarterly Figures: Profit Per Share Surpasses Its Benchmark in the Current Update; Price Move: Shares End With Little Net Change - A |
| HLIO | 2026-08-23 | Helios (HLIO) Levels: Ends Lower by 0.17% at $76.18 in the Latest Reading; Chart Watch: Price Remains Between $72.37 Support and $79.99 Resistance on  |
| PLUS | 2026-08-23 | ePlus (PLUS) Shares: Closes with Little Net Change at $86.85 on the Day; Key Range: the Near-Term Reference Band Stretches From $82.51 to $91.19 in th |
| AXGN | 2026-08-23 | Axogen (AXGN) Results Breakdown: The Available Earnings Metric Delivers a Miss for This Review; Market Response: Shares Move Higher 8.53% in the Lates |
| THRM | 2026-08-23 | Gentherm (THRM) Earnings Report: The EPS Result Is Above Expectation on the Day; Closing Move: Shares Finish 0.49% Lower for the Current Session - Pre |
| JJSF | 2026-08-22 | Snack (JJSF) Session: Posts a 0.96% Loss to $87.59 in the Latest Reading; Price Levels: Price Remains Between $83.21 Support and $91.97 Resistance - U |
| DGII | 2026-08-22 | Digi (DGII) Momentum: Finishes 1.77% Up at $74.91 in the Current Update; Market Levels: $71.16 and $78.66 Define the Nearby Reference Levels in the La |

### Kalkine Media

`kalkinemedia` · **27** in-corpus observations across **22** companies · **verdict: `Mill`**

Templated `(EXCH:TICK): Can X Drive Growth?` headlines emitted per ticker.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| ATNI | 2026-08-27 | ATN International (NASDAQ:ATNI) Puts Network Strategy Back In Focus - Kalkine Media |
| KLIC | 2026-08-27 | Kulicke & Soffa (NASDAQ:KLIC): Can Chip Packaging Drive Growth? - Kalkine Media |
| WTRG | 2026-08-27 | Essential Utilities (NYSE:WTRG): Is Fresh Momentum Building? - Kalkine Media |
| CVLT | 2026-08-27 | Commvault (NASDAQ:CVLT): Why Is It in Focus After It launched new capabilities linking cyber detection with recovery? - Kalkine Media |
| MRCY | 2026-08-26 | Mercury Systems (NASDAQ:MRCY): Can Backlog Fuel Progress? - Kalkine Media |
| HLIO | 2026-08-26 | Helios Technologies (NYSE:HLIO): What Could Reignite Momentum? - Kalkine Media |
| EOSE | 2026-08-26 | Eos Energy Enterprises (NASDAQ:EOSE): Why Is It in Focus After It appointed a new commercial leader for its storage business? - Kalkine Media |
| MMSI | 2026-08-25 | Merit Medical CHRO Donates 650 Shares to Charity, Reports Stock and Option Holdings - Kalkine Media |
| CVX | 2026-08-25 | Chevron (NYSE:CVX): Can Strong Execution Offset Crude Pressure? - Kalkine Media |
| SHOO | 2026-08-25 | Steven Madden (NASDAQ:SHOO): Is Its Brand Strategy Working? - Kalkine Media |

### The Globe and Mail

`theglobeandmail` · **19** in-corpus observations across **14** companies · **verdict: `Mill`**

In this corpus, entirely syndicated: Motley Fool transcripts, TipRanks blurbs, law-firm alerts. No Globe original reporting appeared.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| LZB | 2026-08-25 | La-Z-Boy (LZB) Q1 2027 Earnings Call Transcript - The Globe and Mail |
| MRCY | 2026-08-25 | Mercury Systems (MRCY) Q4 2026 Earnings Call Transcript - The Globe and Mail |
| V | 2026-08-25 | Bullish on Visa Inc. - The Globe and Mail |
| NSSC | 2026-08-25 | Analysts Offer Insights on Industrial Goods Companies: Napco Security Technologies (NSSC) and Norfolk Southern (NSC) - The Globe and Mail |
| CCOI | 2026-08-25 | Kaplan Fox Encourages Investors of Cogent Communications Holdings, Inc. (NASDAQ: CCOI) to Contact the Firm Before Lead Plaintiff Deadline on September |
| LBRT | 2026-08-24 | Green Plains, Liberty Energy, Nabors Industries, Centrus Energy, and Oceaneering Shares Are Falling, What You Need To Know - The Globe and Mail |
| CVX | 2026-08-24 | Chevron vs. Shell: Which Energy Major Offers a Better Bet? - The Globe and Mail |
| HRL | 2026-08-24 | Hormel Foods Q3 Earnings Coming Up: Key Insights for Investors - The Globe and Mail |
| AEHR | 2026-08-24 | Aehr Test Systems Soars Again on Latest Orders, Jefferies Eyes Big-Time Upside Ahead - The Globe and Mail |
| ATEX | 2026-08-18 | Anterix (ATEX) Q1 2027 Earnings Call Transcript - The Globe and Mail |

### Revelio Labs

`reveliolabs` · **17** in-corpus observations across **17** companies · **verdict: `Mill`**

One templated `Number of Employees | Headcount Data` page per company; a database row, not coverage.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| AXGN | 2026-08-06 | AxoGen Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| PLUS | 2026-08-01 | ePlus Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| NPK | 2026-08-01 | National Presto Inds Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| BELFB | 2026-08-01 | Bel Fuse Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| ATNI | 2026-07-31 | ATN International Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| NSSC | 2026-07-31 | Napco Security Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| HZO | 2026-07-31 | MarineMax Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| DGII | 2026-07-31 | Digi International Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| WTRG | 2026-07-30 | Essential Utilities Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |
| THRM | 2026-07-30 | Gentherm Number of Employees 2026 \| Employee Count & Headcount Data - Revelio Labs |

### TradingKey

`tradingkey` · **17** in-corpus observations across **10** companies · **verdict: `Mill`**

Templated technical-analysis and price-move pages per ticker.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| HRL | 2026-08-27 | Hormel Foods (HRL) Fiscal Q3 2026 Earnings Call: EPS Outlook Raised - TradingKey |
| EOSE | 2026-08-27 | Eos Energy Enterprises Inc (EOSE) Technical Analysis: Support, Resistance, Indicators & Moving Averages - TradingKey |
| MRCY | 2026-08-25 | Conferencia de resultados del T4 del FY2026 de Mercury Systems (MRCY): cartera de pedidos récord y perspectivas para el FY2027 - TradingKey |
| V | 2026-08-24 | Visa Inc Stock (V) Closed Up by 3.06% on Aug 24: Facts Behind the Movement - TradingKey |
| AAPL | 2026-08-23 | Apple Stock Outlook: AAPL Faces CEO Change, iPhone 18 Launch and EU Risks - TradingKey |
| MSEX | 2026-08-21 | MSEX\|Middlesex Water Co\|Price:58.260\| - TradingKey |
| SFBS | 2026-08-21 | ServisFirst Bancshares Inc (SFBS) Technical Analysis: Support, Resistance, Indicators & Moving Averages - TradingKey |
| CAT | 2026-08-19 | Caterpillar Inc Stock (CAT) Moved Down by 3.26% on Aug 19: What Investors Need To Know - TradingKey |
| CLFD | 2026-08-07 | Clearfield Inc (CLFD) Stock News Today - TradingKey |
| IDT | 2026-08-07 | IDT Corp (IDT) Stock News Today - TradingKey |

### Investing.com Nigeria

`investingcomnigeria` · **13** in-corpus observations across **12** companies · **verdict: `Mill`**

Regional edition of the already-classified Investing.com; identical syndicated content.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| ATNI | 2026-08-27 | Atn International director Prior sells $313,389 of ATNI stock By Investing.com - Investing.com Nigeria |
| UFPT | 2026-08-26 | UFP Technologies at Midwest IDEAS: growth plan rests on scale By Investing.com - Investing.com Nigeria |
| POWL | 2026-08-26 | Powell Industries at Midwest IDEAS: data centers drive growth By Investing.com - Investing.com Nigeria |
| IOSP | 2026-08-26 | Innospec stock hits 52-week high at 95.07 USD By Investing.com - Investing.com Nigeria |
| ATEX | 2026-08-21 | Why is Anterix stock surging today? By Investing.com - Investing.com Nigeria |
| NSSC | 2026-08-21 | NAPCO Security earnings in focus as recurring revenue push continues By Investing.com - Investing.com Nigeria |
| NPK | 2026-08-20 | National Presto Industries stock hits all-time high at 149.88 USD - Investing.com Nigeria |
| PUMP | 2026-08-20 | ProPetro adds dual listing on NYSE Texas exchange By Investing.com - Investing.com Nigeria |
| IDT | 2026-08-18 | Form 8K IDT Corp For: 18 August By Investing.com - Investing.com Nigeria |
| IRMD | 2026-08-18 | Form 8K Iradimed Co For: 18 August By Investing.com - Investing.com Nigeria |

### Eastern Progress

`easternprogress` · **11** in-corpus observations across **11** companies · **verdict: `Mill`**

Wholesale Zacks republication.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| FR | 2026-08-24 | First Industrial Realty Trust To Host Second Quarter 2026 Results Conference Call On July 23 - Eastern Progress |
| BELFB | 2026-08-22 | Bel Fuse (BELFB) Q1 Earnings and Revenues Beat Estimates - Eastern Progress |
| AXGN | 2026-08-22 | AxoGen (AXGN) Tops Q2 Earnings and Revenue Estimates - Eastern Progress |
| PLMR | 2026-08-22 | Palomar (PLMR) Beats Q2 Earnings and Revenue Estimates - Eastern Progress |
| AGX | 2026-08-20 | Is Argan (AGX) Stock Outpacing Its Construction Peers This Year? - Eastern Progress |
| ERII | 2026-08-20 | Zacks Industry Outlook Highlights Donaldson, CECO Environmental, Energy Recovery and Fuel Tech - Eastern Progress |
| IOSP | 2026-08-19 | Zacks Industry Outlook Highlights Air Products and Chemicals, DuPont de Nemours, Avient and Innospec - Eastern Progress |
| ATEX | 2026-08-19 | Zacks Industry Outlook Highlights Bandwidth and Anterix - Eastern Progress |
| DGII | 2026-08-14 | Surging Earnings Estimates Signal Upside for Digi International (DGII) Stock - Eastern Progress |
| MYRG | 2026-08-12 | Bull of the Day: MYR Group Inc. (MYRG) - Eastern Progress |

### CryptoRank

`cryptorank` · **10** in-corpus observations across **3** companies · **verdict: `Mill`**

Automated tokenized-stock price pages.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| AEHR | 2026-08-26 | Aehr Test Systems Tokenized Stock (Robinhood) to Euro Price Today \| Live AEHR to EUR Converter & Exchange Rate - CryptoRank |
| POWL | 2026-08-25 | Powell Industries Tokenized Stock (Robinhood) - CryptoRank |
| CAT | 2026-08-17 | Caterpillar, Inc. Common Stock (Derivatives) - CryptoRank |

### MarketWatch

`marketwatch` · **10** in-corpus observations across **3** companies · **verdict: `Mill`**

AUDIT OVERRODE REPUTATION: all 10 in-corpus items are the automated `stock outperforms/underperforms competitors` market-wrap template. No selected reporting reached this corpus.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| HRL | 2026-08-27 | Hormel Foods Corp. stock underperforms Thursday when compared to competitors - MarketWatch |
| CAT | 2026-08-26 | Caterpillar Inc. stock outperforms competitors on strong trading day - MarketWatch |
| V | 2026-08-21 | Visa Inc. Cl A stock outperforms competitors on strong trading day - MarketWatch |

### AlphaStreet

`alphastreet` · **9** in-corpus observations across **7** companies · **verdict: `Mill`**

Templated earnings previews and `Jumps 5.3% Amid Sector-Wide Rally` price blurbs.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| MRCY | 2026-08-26 | Mercury Systems (MRCY) Q4 FY2026 Results Show Demand Strength Is Real, but Margin Conversion Still Has to Catch Up - AlphaStreet |
| NSSC | 2026-08-24 | Napco Security Technologies Crushes Q4 2026 Profit Estimates by 31.6% - AlphaStreet |
| CALM | 2026-08-24 | Cal-Maine Foods Jumps 5.3% Amid Sector-Wide Rally - AlphaStreet |
| HRL | 2026-08-24 | Hormel Foods (HRL) Q3 2026 Preview: EPS Est. $0.36, Reports August 27 - AlphaStreet |
| DGII | 2026-08-19 | Digi International Drops 5.8% Amid Sector-Wide Selling - AlphaStreet |
| PLMR | 2026-08-18 | Palomar Holdings Jumps 5.0% Amid Sector-Wide Rally - AlphaStreet |
| YORW | 2026-08-07 | York Water Releases Q2 2026 Financial Results - AlphaStreet |

### Barchart.com

`barchart` · **8** in-corpus observations across **6** companies · **verdict: `Mill`**

Data platform republishing PR plus templated market notes.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-25 | Kaplan Fox Encourages Investors of Cogent Communications Holdings, Inc. (NASDAQ: CCOI) to Contact the Firm Before Lead Plaintiff Deadline on September |
| HRL | 2026-08-24 | Hormel Foods Announces Appointment of Ash Bhumbla as Chief Financial Officer - Barchart.com |
| PUMP | 2026-08-20 | ProPetro Announces Dual Listing on NYSE Texas - Barchart.com |
| WDFC | 2026-08-10 | Unpacking Q2 Earnings: WD-40 (NASDAQ:WDFC) In The Context Of Other Household Products Stocks - Barchart.com |
| CAT | 2026-08-09 | A $72 Billion Reason to Buy Caterpillar Stock Now - Barchart.com |
| MSEX | 2026-07-30 | Middlesex Water: Q2 Earnings Snapshot - Barchart.com |

### StocksToTrade

`stockstotrade` · **8** in-corpus observations across **3** companies · **verdict: `Mill`**

Templated trading blurbs per ticker.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| EOSE | 2026-08-26 | EOSE Stock Slides As Losses Mount And Legal Risks Grow - StocksToTrade |
| NSSC | 2026-08-24 | NSSC Stock Whipsaws As Traders Eye Q4 Earnings Catalyst - StocksToTrade |
| AXGN | 2026-08-22 | Axogen Stock Climbs As Analysts Hike Price Targets After Q2 Beat - StocksToTrade |

### AOL.com

`aol` · **7** in-corpus observations across **7** companies · **verdict: `Mill`**

Aggregator republishing Motley Fool transcripts and AP snapshots.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| HRL | 2026-08-24 | Hormel Foods names former Tyson executive Ash Bhumbla as CFO - AOL.com |
| IMAX | 2026-08-22 | The Difference Between Shot With IMAX And Filmed For IMAX - AOL.com |
| OTTR | 2026-08-21 | One found dead in Otter Tail County house explosion, fire - AOL.com |
| IOSP | 2026-08-19 | Innospec (IOSP) Q2 2026 Earnings Call Transcript - AOL.com |
| ANIP | 2026-08-19 | ANI Pharmaceuticals (ANIP) Q2 2026 Earnings Call Transcript - AOL.com |
| IRMD | 2026-08-08 | IRadimed (IRMD) Q2 2026 Earnings Call Transcript - AOL.com |
| ASIX | 2026-08-07 | AdvanSix: Q2 Earnings Snapshot - AOL.com |

### Investing.com Canada

`investingcomcanada` · **7** in-corpus observations across **6** companies · **verdict: `Mill`**

Regional edition of Investing.com.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| IOSP | 2026-08-26 | Innospec stock hits 52-week high at 95.07 USD By Investing.com - Investing.com Canada |
| POWL | 2026-08-26 | Powell Industries at Midwest IDEAS: data centers drive growth By Investing.com - Investing.com Canada |
| UMH | 2026-08-25 | UMH Properties stock hits 52-week high at 16.77 USD By Investing.com - Investing.com Canada |
| HRL | 2026-08-24 | Hormel Foods names Ash Bhumbla as chief financial officer By Investing.com - Investing.com Canada |
| IDT | 2026-08-18 | Form 8K IDT Corp For: 18 August By Investing.com - Investing.com Canada |
| YORW | 2026-08-18 | Form 8K The York Water Company For: 18 August By Investing.com - Investing.com Canada |

### KING5.com

`king5` · **6** in-corpus observations across **4** companies · **verdict: `Mill`**

Local TV site carrying the AP `Earnings Snapshot` template verbatim; no local selection of these items.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| FLO | 2026-08-20 | Flowers Foods: Q2 Earnings Snapshot - KING5.com |
| LZB | 2026-08-18 | La-Z-Boy: Fiscal Q1 Earnings Snapshot - KING5.com |
| BELFB | 2026-07-29 | Bel Fuse: Q2 Earnings Snapshot - KING5.com |
| AXGN | 2026-07-29 | AxoGen: Q2 Earnings Snapshot - KING5.com |

### Trefis

`trefis` · **6** in-corpus observations across **3** companies · **verdict: `Mill`**

Automated model-driven notes; the sample contains near-duplicate `13-Day`/`14-Day Winning Streak` restatements.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CAT | 2026-08-25 | Before The Surge, Caterpillar Stock’s Order Book Told A Different Story - Trefis |
| MMSI | 2026-08-13 | 14-Day Rally Sends Merit Medical Systems Stock Up 25% - Trefis |
| IOSP | 2026-07-08 | Who Is the Right Buyer For Specialty Chemicals Maker Innospec? - Trefis |

### timothysykes.com

`timothysykes` · **6** in-corpus observations across **3** companies · **verdict: `Mill`**

Templated trading commentary.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| EOSE | 2026-08-26 | EOSE Stock Slides As Earnings Miss And Legal Probes Rattle Traders - timothysykes.com |
| IMAX | 2026-08-24 | AMC Stock Surges As Record Box Office And IMAX Deals Hit - timothysykes.com |
| AXGN | 2026-08-23 | Axogen Inc. Jumps As Analysts Lift AXGN Price Targets - timothysykes.com |

### Yahoo Finance UK

`yahoofinanceuk` · **6** in-corpus observations across **4** companies · **verdict: `Mill`**

Regional edition of Yahoo Finance.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| BKE | 2026-08-27 | Buckle’s (BKE) Women’s Business Is Quietly Outrunning The Rest Of The Store - Yahoo Finance UK |
| CAT | 2026-08-26 | Industrial Giants Pivot to AI Data Centers: Caterpillar (CAT), Cummins (CMI), and Ford (F) Target Power Boom - Yahoo Finance UK |
| MRCY | 2026-08-22 | Mercury Systems (MRCY) Booked $660 Million. Why Did its Shares Fall More than 10% After Hours? - Yahoo Finance UK |
| SKWD | 2026-08-04 | Skyward Specialty Insurance (NASDAQ:SKWD) Reports Upbeat Q2 CY2026 - Yahoo Finance UK |

### Yahoo

`yahoo` · **6** in-corpus observations across **3** companies · **verdict: `Mill`**

Yahoo portal aggregation of other outlets' material.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| IMAX | 2026-08-27 | A look at the rare IMAX projector behind record ticket sales for ‘The Odyssey’ - Yahoo |
| CAT | 2026-08-25 | Non-life-threatening injury at Caterpillar Mapleton - Yahoo |
| BKE | 2026-08-20 | Behind the Buckle: Caldwell Night Rodeo - Yahoo |

### Caledonian Record

`caledonianrecord` · **5** in-corpus observations across **3** companies · **verdict: `Mill`**

A real local paper, but its in-corpus feed is 100% law-firm/PR wire republication.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CCOI | 2026-08-25 | CCOI UPCOMING DEADLINE: SueWallSt Alerts COGENT COMMUNICATIONS HOLDINGS, INC. Stockholders of ... - Caledonian Record |
| ATNI | 2026-08-17 | ATN International, Inc. to Present and Host 1x1 Investor Meetings at the 17th Annual Midwest ... - Caledonian Record |
| YORW | 2026-08-15 | The York Water Company Reports 2nd Quarter and Six Months Earnings - Caledonian Record |

### ChartMill

`chartmill` · **5** in-corpus observations across **5** companies · **verdict: `Mill`**

Pure stock-screener output.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| WTRG | 2026-08-27 | Essential Utilities (NYSE:WTRG) Shows Promising Breakout Setup With Strong Technical and Quality Ratings - ChartMill |
| IMAX | 2026-08-24 | IMAX (NYSE:IMAX) Combines Strong Growth with a Promising Technical Breakout Setup - ChartMill |
| WINA | 2026-08-22 | Winmark (NASDAQ:WINA) Shines as a Quality Stock With Strong Returns and Cash Generation - ChartMill |
| SHOO | 2026-08-07 | Steven Madden (NASDAQ:SHOO) Passes Minervini Trend Template With High-Growth Momentum - ChartMill |
| PSTL | 2026-06-30 | Postal Realty Trust (NYSE:PSTL) Nears 52-Week High, Passes Minervini Trend Template and High Growth Momentum Screen - ChartMill |

### The Manila Times

`themanilatimes` · **5** in-corpus observations across **5** companies · **verdict: `Mill`**

In-corpus items are entirely US small-cap PR republication.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| EOSE | 2026-08-25 | Eos Energy Enterprises Appoints Michelle Buczkowski as Chief Commercial Officer - The Manila Times |
| IDT | 2026-08-24 | IDT Corporation to Present at Midwest IDEAS Investor Conference - The Manila Times |
| SFBS | 2026-08-24 | ServisFirst Bancshares, Inc. Ranks Sixth Among Top-Performing Banks with between $10 Billion to $50 Billion in Assets - The Manila Times |
| MSEX | 2026-07-31 | Middlesex Water Company Reports Second Quarter 2026 Earnings - The Manila Times |
| CASS | 2026-07-23 | Cass Information Systems reports Second Quarter 2026 Results - The Manila Times |

### Investing.com South Africa

`investingcomsouthafrica` · **5** in-corpus observations across **5** companies · **verdict: `Mill`**

Regional edition of Investing.com.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| ATNI | 2026-08-27 | ATN International at 17th Annual Midwest IDEAS Conference: fiber growth, cash returns By Investing.com - Investing.com South Africa |
| AXGN | 2026-08-27 | Axogen stock hits 52-week high at 51.13 USD By Investing.com - Investing.com South Africa |
| UFPT | 2026-08-26 | UFP Technologies at Midwest IDEAS: growth plan rests on scale By Investing.com - Investing.com South Africa |
| YORW | 2026-08-21 | York Water director Douglas Brossman buys $1,000 in shares By Investing.com - Investing.com South Africa |
| NPK | 2026-08-20 | National Presto Industries stock hits all-time high at 149.88 USD - Investing.com South Africa |

### Yahoo Finance Singapore

`yahoofinancesingapore` · **5** in-corpus observations across **5** companies · **verdict: `Mill`**

Regional edition of Yahoo Finance.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| WINA | 2026-08-26 | Winmark - the Resale Company® Extends Sustainability Partnership with Rawlings® and Easton® for Additional Three Years - Yahoo Finance Singapore |
| EOSE | 2026-08-25 | Eos Energy Enterprises Appoints Michelle Buczkowski as Chief Commercial Officer - Yahoo Finance Singapore |
| MHO | 2026-08-25 | Zonda and M/I Homes Partner on the 2027 Virtual Concept Home - Yahoo Finance Singapore |
| LBRT | 2026-08-21 | Liberty Energy (LBRT) Down 1.9% Since Last Earnings Report: Can It Rebound? - Yahoo Finance Singapore |
| MYRG | 2026-07-29 | MYR Group Inc. Announces Second-Quarter and First-Half 2026 Results - Yahoo Finance Singapore |

### Zacks Investment Research

`zacksinvestmentresearch` · **4** in-corpus observations across **4** companies · **verdict: `Mill`**

Name variant of the already-classified Zacks.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CVX | 2026-08-25 | Zacks Market Edge Highlights: ExxonMobil, TotalEnergies, Chevron - Zacks Investment Research |
| HRL | 2026-08-24 | Hormel Foods Q3 Earnings Coming Up: Key Insights for Investors - Zacks Investment Research |
| ATEX | 2026-08-19 | Zacks Industry Outlook Highlights Bandwidth and Anterix - Zacks Investment Research |
| MYRG | 2026-08-12 | Bull of the Day: MYR Group Inc. (MYRG) - Zacks Investment Research |

### Investing.com India

`investingcomindia` · **3** in-corpus observations across **3** companies · **verdict: `Mill`**

Regional edition of Investing.com.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| WTTR | 2026-08-21 | JPMorgan initiates Select Water Solutions stock at Overweight By Investing.com - Investing.com India |
| NPK | 2026-08-20 | National Presto Industries stock hits all-time high at 149.88 USD - Investing.com India |
| MSEX | 2026-08-08 | Middlesex Water VP Lorrie Ginegaw sells $35,430 in shares By Investing.com - Investing.com India |

### Investing.com Australia

`investingcomaustralia` · **2** in-corpus observations across **2** companies · **verdict: `Mill`**

Regional edition of Investing.com.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| WTTR | 2026-08-27 | Select Water Solutions at 17th Annual Midwest IDEAS Conference: growth shifts By Investing.com - Investing.com Australia |
| AXGN | 2026-08-26 | Axogen stock hits 52-week high at 51.13 USD By Investing.com - Investing.com Australia |

### Yahoo! Finance Canada

`yahoofinancecanada` · **2** in-corpus observations across **2** companies · **verdict: `Mill`**

Regional edition of Yahoo Finance.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| KLIC | 2026-08-25 | Should You Include KLIC Stock in Portfolio After Solid Q3 Results? - Yahoo! Finance Canada |
| NSSC | 2026-08-24 | NAPCO Security Technologies, Inc. Reports Fiscal Q4 and Full Year 2026 Results - Yahoo! Finance Canada |

### Yahoo Sports

`yahoosports` · **1** in-corpus observations across **1** companies · **verdict: `Mill`**

Yahoo portal aggregation.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| IMAX | 2026-08-25 | Documentary on Warriors star Steph Curry coming to IMAX theaters in October - Yahoo Sports |

### Yahoo Tech

`yahootech` · **1** in-corpus observations across **1** companies · **verdict: `Mill`**

Yahoo portal aggregation.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| IMAX | 2026-08-22 | The Difference Between Shot With IMAX And Filmed For IMAX - Yahoo Tech |

## Tier `Platform`

### Seeking Alpha

`seekingalpha` · **66** in-corpus observations across **40** companies · **verdict: `Platform`**

Contributor analysis: a human chose to write about this company, but the outlet gatekeeps little.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| PLUS | 2026-08-27 | ePlus: Not Much Has Changed To Convince Me To Start A Position (NASDAQ:PLUS) - Seeking Alpha |
| AAPL | 2026-08-26 | Apple announces product event date (AAPL:NASDAQ) - Seeking Alpha |
| HWKN | 2026-08-25 | Hawkins: A Wasted Opportunity Becomes An Opportunity (NASDAQ:HWKN) - Seeking Alpha |
| GTY | 2026-08-25 | Getty Realty: A Good REIT That Now Trades At Its Own Cap Rate (NYSE:GTY) - Seeking Alpha |
| AGX | 2026-08-24 | Argan extends losing streak with another 6% drop (AGX:NYSE) - Seeking Alpha |
| DGII | 2026-08-24 | Digi International: Record Quarterly Results From Accretive Acquisitions (NASDAQ: DGII) - Seeking Alpha |
| EOSE | 2026-08-24 | Eos Energy: The Golden Dome Can't Protect From GAAP Net Losses And Cash Burn (NASDAQ:EOSE) - Seeking Alpha |
| FLO | 2026-08-24 | Flowers Foods: The Dividend Reset Was A Prudent Move (Rating Upgrade) - Seeking Alpha |
| BKE | 2026-08-22 | The Buckle's Pop Is Only The Beginning - Seeking Alpha |
| AEHR | 2026-08-22 | Aehr Test Systems: The FY2027 Rebound Is Real, But The Price Asks For Too Much - Seeking Alpha |

### The Motley Fool

`themotleyfool` · **16** in-corpus observations across **14** companies · **verdict: `Platform`**

Contributor investor content; same class as Seeking Alpha, hence the same tier.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| POWL | 2026-08-27 | Stock Transaction: Powell Industries Richard Williams Sells 2,250 Shares - The Motley Fool |
| IMAX | 2026-08-25 | Why IMAX Stock Climbed to a New All-Time High Today - The Motley Fool |
| CVX | 2026-08-24 | 3 Dividend Stocks to Buy and Hold for the Next Decade, Starting With Chevron - The Motley Fool |
| MRCY | 2026-08-19 | Why Mercury Systems Stock Is Sinking Today - The Motley Fool |
| HRL | 2026-08-18 | How to Buy Hormel Foods Stock (HRL) in 2026 - The Motley Fool |
| YORW | 2026-08-18 | How to Buy York Water Stock (YORW) in 2026 - The Motley Fool |
| LBRT | 2026-08-10 | Liberty Energy CFO Michael Stock Sells 6,666 Shares - The Motley Fool |
| UFPT | 2026-08-04 | Why UFP Technologies Stock Is Skyrocketing Today - The Motley Fool |
| AEHR | 2026-08-04 | Microsoft vs. Aehr Test Systems: Comparing Revenue Trends Between These Artificial Intelligence Companies - The Motley Fool |
| CVLT | 2026-08-03 | Why Commvault Systems Rocked the Market Today - The Motley Fool |

### 24/7 Wall St.

`247wallst` · **9** in-corpus observations across **5** companies · **verdict: `Platform`**

Human-written comparative stock pieces with weak gatekeeping; concentrated on whichever names moved.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| CVX | 2026-08-24 | ExxonMobil vs. Chevron: We Compared 10 Years of Dividend Growth And Here's the Winner - 24/7 Wall St. |
| FLO | 2026-08-21 | Flowers Foods Misses on Both Lines as Bread Volumes Slide 5.8% - 24/7 Wall St. |
| AEHR | 2026-08-19 | Aehr Test Systems Sinks 10%, Teradyne Falls 5%, FormFactor Drops 6%: What's Hitting These Semiconductor Test Equipment Stocks? - 24/7 Wall St. |
| JJSF | 2026-08-05 | J&J Snack Foods JJSF Q3 2026: Margin Gains Power a 15% EPS Beat - 24/7 Wall St. |
| MSEX | 2026-07-30 | Middlesex Water (MSEX) Q2 2026 Earnings Beat: EPS Tops Estimates by 13% - 24/7 Wall St. |

### Morningstar

`morningstar` · **9** in-corpus observations across **6** companies · **verdict: `Platform`**

Mixed: 3 of 9 are genuine Morningstar analyst commentary, the rest republished PR and law-firm alerts.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| KWR | 2026-08-27 | Quaker Houghton Announces 2026 Investor Day - Morningstar |
| HRL | 2026-08-27 | HORMEL FOODS REPORTS THIRD QUARTER FISCAL 2026 RESULTS - Morningstar |
| CCOI | 2026-08-19 | CCOI DEADLINE: Levi & Korsinsky Reminds COGENT COMMUNICATIONS HOLDINGS, INC. Investors of Upcoming Securities Class Action Deadline - Morningstar |
| TMDX | 2026-08-17 | Did TransMedics Group, Inc. Insiders Breach their Fiduciary Duties to Shareholders? - Morningstar |
| WTRG | 2026-08-07 | American Water on Track to Close Essential Utilities Acquisition in Early 2027 - Morningstar |
| CAT | 2026-08-04 | Caterpillar Earnings: ‘No One Is Slowing Down’ - Morningstar |

### Nareit

`nareit` · **5** in-corpus observations across **4** companies · **verdict: `Platform`**

Trade-association member profiles: a human chose the company, but the outlet exists to promote its members.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| UMH | 2026-08-20 | UMH Rings Opening Bell on Tel Aviv Stock Exchange to Celebrate Dual Listing - Nareit |
| GTY | 2026-07-23 | Getty Realty Expects to Expand and Diversify Portfolio in Next Few Years - Nareit |
| PSTL | 2026-07-14 | Postal Realty Trust Sees Long Runway for Growth in Niche Postal Real Estate Market - Nareit |
| DEA | 2026-07-07 | Easterly Government Properties Sees Long-Term Growth as Federal Leasing Expands - Nareit |

## Tier `Genuine`

### The Business Journals

`thebusinessjournals` · **9** in-corpus observations across **4** companies · **verdict: `Genuine`**

Original local business reporting with clear editorial selection (permits, HQ moves, executive payouts).

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| MHO | 2026-08-27 | M/I Homes, Mitchell Development partner on 68-home community near major hospital - The Business Journals |
| PLUS | 2026-08-25 | EPlus snags Daymark Solutions months after selling off financing subsidiary - The Business Journals |
| CAT | 2026-08-25 | Caterpillar set to begin renovation of Irving office space this month - The Business Journals |
| HZO | 2026-08-24 | MarineMax CEO set for $33M payout from sale to Blackstone - The Business Journals |

### WSJ

`wsj` · **4** in-corpus observations across **4** companies · **verdict: `Genuine`**

Name variant of the already-classified The Wall Street Journal.

| ticker | publishedAtUtc | sampled item |
| --- | --- | --- |
| HRL | 2026-08-27 | Hormel Foods Cuts Sales Outlook Amid Consumer Pullback, Higher Costs - WSJ |
| LZB | 2026-08-18 | La-Z-Boy Swings to First-Quarter Loss From Declining Sales - WSJ |
| CAT | 2026-08-04 | Caterpillar Says the AI Boom Continues to Drive Construction Demand - WSJ |
| CALM | 2026-07-22 | Cal-Maine’s Sales Crack, Hurt by Historically Low Egg Prices - WSJ |
