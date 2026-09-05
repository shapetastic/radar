# Insider-flow audit — what the Form 4 channel can currently see (spec 209 §1)

**Read-only, from persisted data only.** Source: `data/evidence/raw/filing/**/*.json` filtered to
`metadata.form == "4"` (1,847 files; 1,847 distinct evidence ids; 1,847 distinct content hashes — no
duplicates). Window: the current 60-day scoring window of the latest full run,
`(2026-07-05T21:45:47Z, 2026-09-03T21:45:47Z]` (the `windowStartUtc`/`windowEndUtc` stamped on all 102
snapshots of that run), applied to evidence `publishedAt` (the filing acceptance instant). Company = first
`companyHints` ticker. Measured 2026-09-05 with a PowerShell pass over the store; no code path was run.
**Store instant:** the pass covered the store as of the 2026-09-03T21:40:59Z collection (the newest
`collectedAt` included). A later collection on 2026-09-05 (16:55–16:57Z) added 20 Form 4 files, all with
`publishedAt` after the window end (earliest 2026-09-03T22:30:44Z), so the all-time totals below are
stale by those 20 while every in-window figure and both case studies are unaffected; a re-run today
counts 1,867 all-time.

**The constraint every number below is built around** (verified in `HttpSecForm4Reader.Classify`): a
10b5-1 plan filing is classified `plan-10b5-1` BEFORE any transaction code, share count or price is read,
and the durable evidence for such a filing carries only the plan token — no transaction value. Discretionary
filings persist ONE magnitude (`insiderNetValue`); for `mixed-buy-sell` that figure is
`Math.Max(purchaseValue, saleValue)`, neither a net nor a total, so it is never summed into a value column
here. Legacy evidence collected before spec 156 (merged 2026-07-28) carries no classification token at
all and is bucketed **unknown**; its `insiderDirection`/`insiderNetValue` are reported as context only.
There is no separable "excluded" class: grants, holdings-only and empty filings all land in
`no-discretionary-transactions` and cannot be told apart after the fact.

## Universe distribution (the headline)

| Classification token (persisted) | In-window filings | Share | All-time filings | Share |
|---|---:|---:|---:|---:|
| `no-discretionary-transactions` | 197 | 41.3% | 777 | 42.1% |
| `discretionary-sale` | 136 | 28.5% | 226 | 12.2% |
| `plan-10b5-1` | 64 | 13.4% | 118 | 6.4% |
| *(unknown — legacy, no token)* | 52 | 10.9% | 682 | 36.9% |
| `discretionary-buy` | 28 | 5.9% | 44 | 2.4% |
| `mixed-buy-sell` | 0 | 0.0% | 0 | 0.0% |
| **Total** | **477** | | **1,847** | |

In-window: 477 Form 4 filings across 79 of the 102 companies (23 companies have no Form 4 in the window).

| Captured value, in-window | Filings | With a persisted value | Sum of captured values |
|---|---:|---:|---:|
| `discretionary-buy` purchase value | 28 | 28 | $2,099,372 |
| `discretionary-sale` sale value | 136 | 136 | $514,093,090 |
| `mixed-buy-sell` | 0 | 0 | *(never totalled — Max(purchase, sale) is not a total)* |
| *(unknown)* | 52 | 6 | *(not totalled — branch unknown; the 6 legacy values sum to $7,932,855 as context only)* |

**Distribution rule verdict: plan filings do NOT dominate everywhere.** In-window, 20 of 79 companies
carry at least one `plan-10b5-1` filing and in 13 of those the plan bucket is ≥ 50% of the company's
filings (NWPX is 11 of 11). The universe-level headline is different: 41% of what the channel sees is
`no-discretionary-transactions` (grants/exercises/withholding/holdings — indistinguishable after the
fact), 28.5% is discretionary sale, and discretionary purchases are 5.9% (28 filings, $2.1M in total
across 10 companies). Per company: 46 have ≥ 1 discretionary sale, 10 have ≥ 1 discretionary purchase, 17
carry legacy unknown-token evidence inside the window, 0 carry a mixed filing.

## Per-company, in-window

Columns: ticker | total | plan | discretionary purchase count (captured sum) | discretionary sale count
(captured sum) | mixed | no-discretionary | unknown. `dbuy`/`dsale` are the persisted `discretionary-buy` /
`discretionary-sale` tokens; sums are the captured `insiderNetValue` for those two tokens only.

```
AAPL     4 | plan   3 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  0
AEHR    33 | plan   0 | dbuy  0 () | dsale 12 (17,304,581) | mixed  0 | nodisc   5 | unk 16
AGX      1 | plan   0 | dbuy  0 () | dsale  1 (3,313,222) | mixed  0 | nodisc   0 | unk  0
AGYS     5 | plan   0 | dbuy  0 () | dsale  4 (23,046,537) | mixed  0 | nodisc   1 | unk  0
ALNT     6 | plan   0 | dbuy  0 () | dsale  1 (7,950,789) | mixed  0 | nodisc   5 | unk  0
AMBA     1 | plan   0 | dbuy  0 () | dsale  1 (468,656) | mixed  0 | nodisc   0 | unk  0
ANIP     9 | plan   6 | dbuy  0 () | dsale  1 (243,474) | mixed  0 | nodisc   2 | unk  0
ATEX    13 | plan   0 | dbuy  0 () | dsale  4 (10,528,719) | mixed  0 | nodisc   7 | unk  2
ATNI     9 | plan   0 | dbuy  0 () | dsale  9 (1,631,399) | mixed  0 | nodisc   0 | unk  0
AXGN     3 | plan   3 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  0
BELFB    1 | plan   0 | dbuy  0 () | dsale  1 (146,060) | mixed  0 | nodisc   0 | unk  0
BKE      1 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  0
CARS     2 | plan   0 | dbuy  0 () | dsale  2 (790,281) | mixed  0 | nodisc   0 | unk  0
CAT      7 | plan   0 | dbuy  0 () | dsale  1 (26,211,764) | mixed  0 | nodisc   3 | unk  3
CCOI     1 | plan   0 | dbuy  0 () | dsale  1 (45,445) | mixed  0 | nodisc   0 | unk  0
CLFD     1 | plan   1 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  0
CLMB     5 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   5 | unk  0
CMCO    15 | plan   0 | dbuy  1 (47,135) | dsale  0 () | mixed  0 | nodisc  14 | unk  0
CVLT    12 | plan   5 | dbuy  0 () | dsale  1 (42,590) | mixed  0 | nodisc   5 | unk  1
CVX      9 | plan   0 | dbuy  0 () | dsale  6 (228,595,179) | mixed  0 | nodisc   3 | unk  0
CYRX     1 | plan   1 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  0
EOSE    24 | plan   2 | dbuy  0 () | dsale  1 (574,546) | mixed  0 | nodisc  14 | unk  7
EPM      4 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   4 | unk  0
ERII     3 | plan   2 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  1
ESQ      6 | plan   0 | dbuy  0 () | dsale  1 (388,792) | mixed  0 | nodisc   5 | unk  0
FLXS    13 | plan   0 | dbuy  0 () | dsale  7 (1,752,895) | mixed  0 | nodisc   6 | unk  0
FR       1 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  0
GHM      2 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   2 | unk  0
HRL      1 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  1
HWKN    10 | plan   0 | dbuy  2 (261,576) | dsale  0 () | mixed  0 | nodisc   8 | unk  0
IDT      3 | plan   0 | dbuy  0 () | dsale  2 (757,633) | mixed  0 | nodisc   1 | unk  0
IMAX     6 | plan   1 | dbuy  0 () | dsale  5 (3,974,180) | mixed  0 | nodisc   0 | unk  0
ITIC     2 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   2 | unk  0
JBSS     5 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   5 | unk  0
JNJ      5 | plan   0 | dbuy  0 () | dsale  4 (52,756,297) | mixed  0 | nodisc   0 | unk  1
JOUT     1 | plan   0 | dbuy  0 () | dsale  1 (299,125) | mixed  0 | nodisc   0 | unk  0
KGS     11 | plan   5 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   6 | unk  0
KLIC     8 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   2 | unk  6
KOP      6 | plan   0 | dbuy  0 () | dsale  6 (4,857,572) | mixed  0 | nodisc   0 | unk  0
KWR      3 | plan   0 | dbuy  0 () | dsale  1 (100,986) | mixed  0 | nodisc   2 | unk  0
LBRT     3 | plan   2 | dbuy  1 (250,009) | dsale  0 () | mixed  0 | nodisc   0 | unk  0
LZB     11 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   9 | unk  2
MHO      3 | plan   0 | dbuy  0 () | dsale  2 (7,484,050) | mixed  0 | nodisc   1 | unk  0
MLAB     9 | plan   0 | dbuy  2 (902,411) | dsale  1 (800,090) | mixed  0 | nodisc   6 | unk  0
MMSI     3 | plan   0 | dbuy  0 () | dsale  2 (2,501,525) | mixed  0 | nodisc   1 | unk  0
MNRO     8 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   8 | unk  0
MRCY    19 | plan   1 | dbuy  0 () | dsale 13 (41,336,731) | mixed  0 | nodisc   4 | unk  1
MSEX     2 | plan   0 | dbuy  0 () | dsale  1 (35,430) | mixed  0 | nodisc   1 | unk  0
NOVT     1 | plan   1 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  0
NSSC     2 | plan   0 | dbuy  0 () | dsale  2 (18,257,168) | mixed  0 | nodisc   0 | unk  0
NWPX    11 | plan  11 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  0
OFG      5 | plan   0 | dbuy  0 () | dsale  3 (3,491,253) | mixed  0 | nodisc   2 | unk  0
OLP     13 | plan   0 | dbuy  0 () | dsale  1 (108,270) | mixed  0 | nodisc  12 | unk  0
OOMA     6 | plan   3 | dbuy  0 () | dsale  3 (765,226) | mixed  0 | nodisc   0 | unk  0
OTTR     2 | plan   0 | dbuy  0 () | dsale  2 (394,044) | mixed  0 | nodisc   0 | unk  0
OUST     5 | plan   3 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   2 | unk  0
PDYN     3 | plan   0 | dbuy  0 () | dsale  3 (167,719) | mixed  0 | nodisc   0 | unk  0
PLMR     9 | plan   4 | dbuy  0 () | dsale  5 (8,366,833) | mixed  0 | nodisc   0 | unk  0
PLUS     7 | plan   5 | dbuy  0 () | dsale  1 (44,043) | mixed  0 | nodisc   1 | unk  0
POWL     4 | plan   2 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   0 | unk  2
PRCT     5 | plan   0 | dbuy  1 (498,315) | dsale  0 () | mixed  0 | nodisc   4 | unk  0
PUMP     4 | plan   0 | dbuy  0 () | dsale  2 (1,189,548) | mixed  0 | nodisc   2 | unk  0
RGCO    16 | plan   0 | dbuy  6 (39,030) | dsale  0 () | mixed  0 | nodisc  10 | unk  0
SENEA    6 | plan   0 | dbuy  1 (3,806) | dsale  0 () | mixed  0 | nodisc   5 | unk  0
SFBS     1 | plan   0 | dbuy  0 () | dsale  1 (1,028,445) | mixed  0 | nodisc   0 | unk  0
SHEN     8 | plan   0 | dbuy  2 (74,280) | dsale  0 () | mixed  0 | nodisc   6 | unk  0
SHOO     6 | plan   0 | dbuy  0 () | dsale  5 (1,194,381) | mixed  0 | nodisc   1 | unk  0
SKWD     1 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  0
STRL     2 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  1
TBBK     1 | plan   0 | dbuy  0 () | dsale  1 (220,195) | mixed  0 | nodisc   0 | unk  0
THRM     2 | plan   0 | dbuy  0 () | dsale  2 (243,056) | mixed  0 | nodisc   0 | unk  0
TMDX     4 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  3
UFPT     7 | plan   0 | dbuy  0 () | dsale  7 (12,634,943) | mixed  0 | nodisc   0 | unk  0
UMH     10 | plan   0 | dbuy  3 (10,230) | dsale  0 () | mixed  0 | nodisc   5 | unk  2
V        8 | plan   3 | dbuy  0 () | dsale  2 (27,553,200) | mixed  0 | nodisc   2 | unk  1
WDFC     2 | plan   0 | dbuy  0 () | dsale  2 (117,063) | mixed  0 | nodisc   0 | unk  0
WTRG     1 | plan   0 | dbuy  0 () | dsale  1 (112,915) | mixed  0 | nodisc   0 | unk  0
WTTR     3 | plan   0 | dbuy  0 () | dsale  0 () | mixed  0 | nodisc   1 | unk  2
YORW    11 | plan   0 | dbuy  9 (12,581) | dsale  1 (266,240) | mixed  0 | nodisc   1 | unk  0
```

## NWPX case study — what the durable data can say

- **In-window: 11 Form 4 filings, all 11 `plan-10b5-1`; transaction value not captured for any of them.**
  Filing dates 2026-08-05, 08-11 (×2), 08-18 (×2), 08-25 (×2), 08-28, 09-02 (×2), 09-03 — **11
  planned-disposition filings across 29 days** (`(2026-09-03 − 2026-08-05).Days`). All-time NWPX: 11
  plan + 7 `no-discretionary-transactions`, 0 discretionary.
- **Per-filing transaction codes, share counts and prices for these plan filings were never persisted** —
  the reader skips every transaction of a plan filing before reading its code (`HttpSecForm4Reader.Classify`,
  "a 10b5-1 plan forces every transaction Neutral"), and the collector writes `insiderNetValue` only when
  the discretionary value is positive. That absence IS the finding: the store cannot say whether these were
  dispositions or acquisitions, by whom, or for how much. The web-verified "weekly planned dispositions by
  the CEO and a director" is external context, not a Radar measurement. The `PrimaryOwnerName` in the
  synthesized title is the only owner fact retained.
- What the report can honestly render (spec 209 §3): "11 planned-disposition filings across 29 days;
  transaction value not captured".

## AGX case study — reconciling against the externally reported ~$119M H1-2026 insider sales

Radar's AGX Form 4 evidence: 16 filings all-time, filing dates 2026-06-12 → 2026-08-04. Fifteen were
collected in one pass on 2026-07-22 (before spec 156 landed on 2026-07-28, so none carries a
classification token) and one on 2026-08-05. **Fifteen is exactly the collector's `MaxFilingsPerCompany`
depth (15, `SecForm4CollectorOptions` default; `default.json` does not override it)**, so the first
collection captured the 15 newest filings and nothing earlier.

| Bucket | What the store establishes | Value |
|---|---|---|
| **Captured, in current window** | 1 filing, `discretionary-sale`, 2026-08-04 | $3,313,222 sale value (captured) |
| **Outside the window, captured** | 7 legacy filings 2026-06-16 → 06-23, `insiderDirection = Negative`, legacy `insiderNetValue` present | $84,241,605 as context: these carry NO classification token (bucketed *unknown*), so this figure is not adopted into any Radar value column. (The reader's direction rule, unchanged since spec 93, only yields Negative on the sale-only branch, so a reader may infer discretionary sale — this audit records the inference and does not act on it.) |
| **Outside the window, plan-classified (value not captured)** | **Not established.** 8 legacy Neutral filings (7 on 2026-06-12, 1 on 2026-07-02) carry no token: each could be a plan filing, a grant/exercise or a holdings-only filing — indistinguishable after the fact. | — |
| **Never collected** | Everything filed before 2026-06-12 (the 15-deep first fetch's oldest filing). H1 2026 runs 2026-01-01 → 06-30, so five and a half months of AGX Form 4s were never fetched. | Not established (Radar holds nothing for them) |

Arithmetic against the external figure, as context only: $3.3M (captured, in window) + $84.2M (legacy
Negative, outside window) = $87.6M of the ~$119M is visible in the store in some form; the remaining
~$31M is some mix of the never-collected pre-2026-06-12 filings and the 8 untokened Neutral filings, and
**cannot be split from durable data**. The external ~$119M is not adopted as a Radar measurement.

So for AGX the "single ~$3.3M discretionary-sale signal" the skeptic saw is the window (the June cluster
sits before 2026-07-05) plus fetch depth (nothing before 2026-06-12), not a classification failure.

## What this says about the channel (measured, no tuning here)

- The channel currently sees discretionary sales far more than purchases (136 vs 28 filings in-window;
  $514M vs $2.1M captured) — a materiality asymmetry the spec-110 tiers already assume.
- 13.4% of in-window filings are plan filings whose transaction detail is permanently invisible in the
  store; forward capture of per-filing codes/values is deferred to its own slice (spec 209 §4).
- 10.9% of in-window (and 36.9% of all-time) filings predate the classification token and stay *unknown*
  forever (heal-forward only, AD-8).
- 15-deep first-fetch depth bounds how far back a newly added company's insider history reaches — a
  collection-depth question for a separate measured spec (spec 209 §5), not tuned here.
