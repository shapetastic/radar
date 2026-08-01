# Spec 165 findings — cmpscan-v2 candidate rules, measured on the raw exhibit bodies

> **MEASUREMENT ONLY — no production change.** `EarningsComparabilityScan` (`cmpscan-v1`) and its two phrase
> tables are untouched. No `ScoringConfigVersion` input moved, no `RuleSetVersion` bump, no
> `_formula.Version` bump: every spec-148/160 pin stands. Promoting any phrase into production is a
> SEPARATE cmpscan-v2 spec, which bumps `EarningsComparabilityScan.Version` and therefore moves the AI-ON
> scoring pins via the descriptor's `cmpscan=` segment.

## What this measures, and why it was needed

Spec 162 Phase B counted comparability concepts (acquisition/perimeter, discrete tax) by running regexes over
the second reader's **curated `comparabilityItems` strings**. That establishes candidate *concepts*; it says
nothing about how a *phrase* behaves on a raw filing body. The candidate phrases named in the Phase B
findings had never been run against a filing. This spec runs them.

## Provenance and reproduction

- **Corpus**: the 298 archived FULL normalized exhibit texts (`data/calibration-audit/exhibits-full/`,
  untracked, read-only). **All 298 were hash-verified** — SHA-256 of the raw file bytes against
  `docs/162-exhibit-manifest.csv`'s `fullTextSha256` — before being read. All 298 manifest rows are
  `outcome = success` with a non-empty hash, so 0 rows were excluded by the admission rule.
- **Concept reference**: `docs/165-comparability-item-mapping-all235.csv`, regenerated over **all 235 labeled
  filings** with the generator's new cohort switch. The spec-162 artifact
  (`docs/162-comparability-item-mapping.csv`, 145 directional filings, 497 item rows) is **byte-untouched** and
  the default cohort still reproduces it byte-for-byte (pinned by a test). Using the 145-filing mapping over
  the 235-filing population would have read every no-signal filing as concept-negative and manufactured false
  positives; the measurement script refuses that input.
- **Any-break reference**: `label.comparisonClean = false` in `docs/162-calibration-labels-full.jsonl`
  (186 break / 49 clean / 0 unrecorded over the 235).
- **Artifacts**: `docs/165-cmpscan-candidate-hits.csv` (1,287-row long-form hit matrix, one row per
  (candidate, filing) where the candidate fired).
- **Reproduce**:

```
powershell -NoProfile -File scripts/calibration-audit/categorize-comparability.ps1 `
    -LabelsPath docs/162-calibration-labels-full.jsonl `
    -WorksheetPath docs/162-study-worksheet.csv `
    -Cohort all-labeled -OutFile docs/165-comparability-item-mapping-all235.csv

powershell -NoProfile -File scripts/calibration-audit/measure-cmpscan-candidates.ps1 `
    -ExhibitsDir <path-to>/data/calibration-audit/exhibits-full `
    -ManifestPath docs/162-exhibit-manifest.csv `
    -LabelsPath docs/162-calibration-labels-full.jsonl `
    -MappingPath docs/165-comparability-item-mapping-all235.csv `
    -OutCsv docs/165-cmpscan-candidate-hits.csv
```

## Headline

**One of the fifteen precommitted primary candidates passes the promotion rule: `acquisitions`**
(concept precision 0.822, Wilson 95% 0.743–0.881; recall 0.776, 0.695–0.840; 21 labeled filings where
`cmpscan-v1` does not fire). Everything else fails at least one leg of the rule.

The four discrete-tax candidates fail on **precision**, not on rarity: `discrete tax` 0.391, `tax benefit`
0.409, `uncertain tax position` 0.417, `valuation allowance` 0.222. As a phrase rule over raw bodies, the
discrete-tax concept is **not supported by this measurement** — which is the opposite of what the Phase B
curated-item counts (25/145 directional filings) would have suggested if read as phrase evidence. That gap is
exactly why this spec exists.

The precise candidates are precise and rare: `pro forma`, `deconsolidation` and `completed acquisition` all
score precision 1.000, with recall 0.152 / 0.032 / 0.056 — none reaches the 0.30 recall floor, and `pro forma`
adds **zero** novel labeled coverage (`cmpscan-v1` already fires on every filing it fires on).

`divestiture` and `divestitures` also add **zero** novel labeled coverage. `divestiture` is already a v1
cap-triggering phrase, and `divestitures` contains it as a substring, so v1 fires wherever they do — measured,
not assumed. `same store` (unhyphenated) fires on **0 of 298** filings.

### Three things the numbers say that the rule does not

These are DESCRIPTIVE. They are not part of the promotion rule and do not change any verdict.

1. **The cap is already close to universal.** `cmpscan-v1` fires on **227/298 (76.2 %)** of the corpus. Adding
   `acquisitions` takes the union to **251/298 (84.2 %)**. A cmpscan-v2 spec must weigh that against spec 160's
   own stated reason for excluding `non-GAAP`/`adjusted`: *"those phrases would cap everything and turn the cap
   into a constant"*. `acquisitions` is not that extreme — but it is on the same axis, and 84 % is a number a
   production spec has to defend rather than inherit.
2. **Any-break precision must be read against its base rate, not against zero.** 186 of the 235 labeled filings
   are `comparisonClean = false`, so the base rate is **0.791**. `cmpscan-v1` scores 0.840, `acquisitions`
   0.856, `acquisition` 0.830. The lift over "fire on everything" is real but small; any-break precision
   discriminates weakly here and should not be used on its own to justify a rule.
3. **The winning candidate's false positives are dominated by forward-looking-statement boilerplate.** Of the
   15 listed FPs for `acquisitions` (of 21), **13 are risk-factor / forward-looking recitals** — *"failure to
   realize … the anticipated benefits of our acquisitions, joint ventures or divestitures"*, *"other
   acquisitions, joint ventures or strategic investments"* — and the remaining two are generic strategy
   (`0000950170-25-064564`) or capital-allocation (`0001104659-26-012356`) prose. **None of the 15 is a
   disclosed comparability break.** Literal substring containment cannot distinguish a boilerplate recital
   from a disclosed break, and a cmpscan-v2 spec that promotes `acquisitions` unmodified inherits that. All
   six of `divestiture`'s FPs are the same class. **This is a finding for the maintainer, not an
   adjudication**: some of these may equally be label omissions, which is why the ±80-character context is
   printed below rather than a bare count.

## Recommendation carried into the cmpscan-v2 spec

Per the precommitted rule, applied verbatim and with no post-hoc adjustment: **`acquisitions` is the only
recommended candidate.** Nothing else is recommended, full stop.

Two items are recorded as *re-measure in a future round*, and are explicitly **not** recommendations: a
boilerplate-suppressed variant of `acquisitions` (the FP class above is highly stereotyped), and a
narrower discrete-tax formulation. Both would need their own precommitted list and their own measurement —
a rule chosen after seeing these results is tuning, and this document must not be cited as evidence for one.
The exploratory regex rows below are ineligible for promotion by construction and are printed for
orientation only; note that `\bacquisitions?\b` reproduces `acquisition`'s numbers exactly, so word-boundary
anchoring buys nothing here.

---

# Measurement output (verbatim from `measure-cmpscan-candidates.ps1`)

Read-only measurement. `EarningsComparabilityScan` (cmpscan-v1) is NOT touched: no production code,
no fingerprint input, no pin move. Every number below is descriptive except the precommitted
promotion rule at the end.

## Inputs and denominators

- Manifest rows: 298; admitted (outcome=success and non-empty fullTextSha256): 298; excluded: 0.
- Every admitted exhibit was hash-verified: raw file bytes SHA-256 == manifest `fullTextSha256` (298/298).
- Labeled filings (concept + any-break reference): 235 of the 298 scanned. Unlabeled (hit rates ONLY): 63.
- Concept mapping: 854 item rows over 211 labeled filing(s).
  - concept reference `acquisition-divestiture-perimeter`: 125/235 labeled filings positive.
  - concept reference `discrete-tax`: 39/235 labeled filings positive.
  - ANY-BREAK reference (`label.comparisonClean = false`): 186 break / 49 clean / 0 not recorded.

## Primary candidates (FROZEN before the run - literal, case-insensitive substring, cmpscan-v1 semantics)

Precision/recall/F1 are FILING-LEVEL over the labeled cohort, against the concept reference. Wilson
95% intervals are reported for precision and recall; **F1 is a point estimate with no interval**
(it is a function of two dependent proportions - a Wilson interval on it would not mean what it looks
like). `v1 overlap` counts scanned filings where the candidate AND cmpscan-v1 both fire; `novel
(labeled)` counts LABELED filings where the candidate fires and cmpscan-v1 does NOT.

| id | literal | concept | hit | hit rate | TP | FP | FN | precision (Wilson 95%) | recall (Wilson 95%) | F1 | any-break precision | v1 overlap | novel (labeled) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| acq-01 | `acquisition` | acquisition-divestiture-perimeter | 216/298 | 0.725 | 117 | 54 | 8 | 117/171 = 0.684 (Wilson 95%: 0.611-0.749) | 117/125 = 0.936 (Wilson 95%: 0.879-0.967) | 0.791 | 142/171 = 0.830 (Wilson 95%: 0.767-0.879) | 182 | 29 |
| acq-02 | `acquisitions` | acquisition-divestiture-perimeter | 150/298 | 0.503 | 97 | 21 | 28 | 97/118 = 0.822 (Wilson 95%: 0.743-0.881) | 97/125 = 0.776 (Wilson 95%: 0.695-0.840) | 0.798 | 101/118 = 0.856 (Wilson 95%: 0.781-0.908) | 126 | 21 |
| acq-03 | `completed acquisition` | acquisition-divestiture-perimeter | 9/298 | 0.030 | 7 | 0 | 118 | 7/7 = 1.000 (Wilson 95%: 0.646-1.000) | 7/125 = 0.056 (Wilson 95%: 0.027-0.111) | 0.106 | 7/7 = 1.000 (Wilson 95%: 0.646-1.000) | 4 | 5 |
| acq-04 | `recent acquisition` | acquisition-divestiture-perimeter | 22/298 | 0.074 | 17 | 2 | 108 | 17/19 = 0.895 (Wilson 95%: 0.686-0.971) | 17/125 = 0.136 (Wilson 95%: 0.087-0.207) | 0.236 | 17/19 = 0.895 (Wilson 95%: 0.686-0.971) | 17 | 5 |
| acq-05 | `pro forma` | acquisition-divestiture-perimeter | 23/298 | 0.077 | 19 | 0 | 106 | 19/19 = 1.000 (Wilson 95%: 0.832-1.000) | 19/125 = 0.152 (Wilson 95%: 0.100-0.225) | 0.264 | 18/19 = 0.947 (Wilson 95%: 0.754-0.991) | 23 | 0 |
| acq-06 | `deconsolidation` | acquisition-divestiture-perimeter | 4/298 | 0.013 | 4 | 0 | 121 | 4/4 = 1.000 (Wilson 95%: 0.510-1.000) | 4/125 = 0.032 (Wilson 95%: 0.013-0.079) | 0.062 | 4/4 = 1.000 (Wilson 95%: 0.510-1.000) | 1 | 3 |
| acq-07 | `divestiture` | acquisition-divestiture-perimeter | 58/298 | 0.195 | 41 | 6 | 84 | 41/47 = 0.872 (Wilson 95%: 0.748-0.940) | 41/125 = 0.328 (Wilson 95%: 0.252-0.414) | 0.477 | 44/47 = 0.936 (Wilson 95%: 0.828-0.978) | 58 | 0 |
| acq-08 | `divestitures` | acquisition-divestiture-perimeter | 47/298 | 0.158 | 31 | 6 | 94 | 31/37 = 0.838 (Wilson 95%: 0.689-0.923) | 31/125 = 0.248 (Wilson 95%: 0.181-0.330) | 0.383 | 35/37 = 0.946 (Wilson 95%: 0.823-0.985) | 47 | 0 |
| acq-09 | `held for sale` | acquisition-divestiture-perimeter | 38/298 | 0.128 | 24 | 11 | 101 | 24/35 = 0.686 (Wilson 95%: 0.520-0.814) | 24/125 = 0.192 (Wilson 95%: 0.133-0.270) | 0.300 | 31/35 = 0.886 (Wilson 95%: 0.740-0.955) | 33 | 4 |
| acq-10 | `same-store` | acquisition-divestiture-perimeter | 15/298 | 0.050 | 8 | 2 | 117 | 8/10 = 0.800 (Wilson 95%: 0.490-0.943) | 8/125 = 0.064 (Wilson 95%: 0.033-0.121) | 0.119 | 9/10 = 0.900 (Wilson 95%: 0.596-0.982) | 13 | 1 |
| acq-11 | `same store` | acquisition-divestiture-perimeter | 0/298 | 0.000 | 0 | 0 | 125 | n/a (n=0) | 0/125 = 0.000 (Wilson 95%: 0.000-0.030) | n/a | n/a (n=0) | 0 | 0 |
| tax-01 | `discrete tax` | discrete-tax | 28/298 | 0.094 | 9 | 14 | 30 | 9/23 = 0.391 (Wilson 95%: 0.222-0.592) | 9/39 = 0.231 (Wilson 95%: 0.126-0.383) | 0.290 | 17/23 = 0.739 (Wilson 95%: 0.535-0.875) | 28 | 0 |
| tax-02 | `tax benefit` | discrete-tax | 81/298 | 0.272 | 27 | 39 | 12 | 27/66 = 0.409 (Wilson 95%: 0.299-0.530) | 27/39 = 0.692 (Wilson 95%: 0.536-0.814) | 0.514 | 55/66 = 0.833 (Wilson 95%: 0.726-0.904) | 69 | 9 |
| tax-03 | `valuation allowance` | discrete-tax | 19/298 | 0.064 | 4 | 14 | 35 | 4/18 = 0.222 (Wilson 95%: 0.090-0.452) | 4/39 = 0.103 (Wilson 95%: 0.041-0.236) | 0.140 | 13/18 = 0.722 (Wilson 95%: 0.491-0.875) | 18 | 0 |
| tax-04 | `uncertain tax position` | discrete-tax | 12/298 | 0.040 | 5 | 7 | 34 | 5/12 = 0.417 (Wilson 95%: 0.193-0.680) | 5/39 = 0.128 (Wilson 95%: 0.056-0.267) | 0.196 | 9/12 = 0.750 (Wilson 95%: 0.468-0.911) | 12 | 0 |

Rationales (recorded with the frozen list, before any result was seen):

- `acquisition` (acq-01) - The broadest perimeter word; expected to over-match ("acquisition of customers", "talent acquisition") - measuring exactly how much is the point.
- `acquisitions` (acq-02) - Plural form; separates programme/pipeline language from a single completed deal.
- `completed acquisition` (acq-03) - A completed deal is what actually breaks a year-over-year comparison; narrower than the bare word.
- `recent acquisition` (acq-04) - Recency wording usually accompanies a perimeter change inside the compared periods.
- `pro forma` (acq-05) - Pro-forma presentation is the standard tell that reported periods are not comparable as reported.
- `deconsolidation` (acq-06) - Technical term with essentially one meaning; expected precise, expected rare.
- `divestiture` (acq-07) - Perimeter reduction; note cmpscan-v1 ALREADY caps on this phrase - measured here for overlap, not novelty.
- `divestitures` (acq-08) - Plural form, which v1 does not carry verbatim (v1 has the singular only).
- `held for sale` (acq-09) - Accounting classification announcing a pending perimeter change before it closes.
- `same-store` (acq-10) - Its presence implies management itself is normalising away a perimeter change.
- `same store` (acq-11) - Unhyphenated variant; both spellings occur in filings.
- `discrete tax` (tax-01) - The explicit name for a one-off tax item distorting the effective rate.
- `tax benefit` (tax-02) - Common but ambiguous - also matches routine stock-compensation and deferred-tax prose; the noise test.
- `valuation allowance` (tax-03) - A release/establishment swings net income without operating change; technical and fairly unambiguous.
- `uncertain tax position` (tax-04) - Reserve releases are a classic discrete tax item; formal phrasing, expected rare and precise.

## cmpscan-v1 baseline (hit rate + ANY-BREAK precision + overlap ONLY)

v1's 15 cap-triggering phrases legitimately detect impairments, litigation, settlements and
asset-sale effects - concepts NEITHER candidate reference covers. Scoring v1 against the
acquisition/tax references would count its legitimate hits as false positives, so **no concept
precision or recall is computed for v1** (structurally: the baseline is not a candidate row and
never enters the candidate metric function).

| rule | filings hit | hit rate | labeled hits | any-break precision |
| --- | --- | --- | --- | --- |
| cmpscan-v1 (15 cap-triggering phrases) | 227/298 | 0.762 | 175 | 147/175 = 0.840 (Wilson 95%: 0.778-0.887) |

## False positives and false negatives (examples, not just counts)

A "false positive" here means the candidate fired on a labeled filing whose concept reference is
negative. That may be a genuine over-match OR a label omission - only the example lets a human tell,
which is why the context is printed. Listings are capped at 15 per candidate per list, sorted by
accession (ordinal ascending), with the overflow counted.

### acq-01 - `acquisition` (acquisition-divestiture-perimeter)

False positives: 54.
- 0000018230-25-000043: ...ure to realize, or a delay in realizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relatio...
- 0000018230-26-000017: ...ure to realize, or a delay in realizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relatio...
- 0000056978-25-000078: ..., costs associated with restructuring and severance, equity-based compensation, acquisition and integration costs, impairment relating to assets acquired through business ...
- 0000056978-26-000010: ..., costs associated with restructuring and severance, equity-based compensation, acquisition and integration costs, impairment relating to assets acquired through business ...
- 0000056978-26-000018: ..., costs associated with restructuring and severance, equity-based compensation, acquisition and integration costs, impairment relating to assets acquired through business ...
- 0000093410-25-000016: ...mmon stock, reflecting continuing confidence in the consummation of the pending acquisition of Hess. • Started production from the Ballymore field in the deepwater Gulf of...
- 0000320193-25-000055: ...87 27,462 Proceeds from sales of marketable securities 5,210 4,314 Payments for acquisition of property, plant and equipment (6,011) (4,388) Other (635) (729) Cash generat...
- 0000320193-25-000071: ...6 39,838 Proceeds from sales of marketable securities 10,785 7,382 Payments for acquisition of property, plant and equipment (9,473) (6,539) Other (975) (1,117) Cash gener...
- 0000320193-25-000077: ... 51,211 Proceeds from sales of marketable securities 12,890 11,135 Payments for acquisition of property, plant and equipment (12,715) (9,447) Other (1,480) (1,308) Cash ge...
- 0000320193-26-000005: ...10 15,967 Proceeds from sales of marketable securities 2,824 3,492 Payments for acquisition of property, plant and equipment (2,373) (2,940) Other (154) (603) Cash generat...
- 0000320193-26-000011: ...91 26,587 Proceeds from sales of marketable securities 8,615 5,210 Payments for acquisition of property, plant and equipment (4,344) (6,011) Other (1,584) (635) Cash gener...
- 0000708781-26-000020: ... will depend on market conditions, earnings, balance sheet growth and potential acquisition opportunities. Asset Quality - Non-performing loans totaled $3.1 million at Mar...
- 0000708781-26-000028: ... will depend on market conditions, earnings, balance sheet growth and potential acquisition opportunities. Asset Quality - Non-performing loans totaled $1.6 million at Jun...
- 0000921582-25-000005: ...nvestment in equipment for joint revenue sharing arrangements (24,341) (18,000) Acquisition of other intangible assets (8,447) (8,344) Proceeds from sale of equity securit...
- 0000921582-25-000022: ...Investment in equipment for joint revenue sharing arrangements (11,746) (4,442) Acquisition of other intangible assets (1,233) (1,594) Net cash used in investing activitie...
- ... and 39 more (listing capped at 15).

False negatives: 8.
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000105132-26-000006: UK homecare/cleaning portfolio divested in Q4 FY2025; ~$1.6M of prior-year sales not recurring (HCCP -31%) // FY2026 guidance presented on pro forma basis excluding assets expected to be divested in FY2026
- 0000105132-26-000026: UK homecare/cleaning portfolio divestiture (Q4 FY2025) removes ~$1.5M of prior-year sales, depressing HCCP and EIMEA comparisons // FY2026 guidance presented on pro-forma basis excluding assets expected to be divested in fiscal 2026
- 0000105132-26-000059: Prior-year quarter included $1.1M of UK homecare sales divested in Q4 FY2025 // Guidance perimeter changed: outlook now re-includes Americas homecare brands (~$12M sales, $2.9M operating income, $0.17 EPS) and is compared to 'pro forma' FY2025 net sales
- 0000950170-25-097225: Book4Time acquisition contributes to the 44.3% subscription revenue growth (explicitly 'including Book4Time'), so growth is not fully organic // Amortization of internal-use software and intangibles rose $0.25M to $1.46M (acquisition-related step-up)
- 0001193125-25-251570: Book4Time acquisition included in FY26 Q2 subscription revenue ("33% growth... including Book4Time") but not in the prior-year quarter
- 0001193125-26-228898: Company notes 'record' claims apply only to the post-FY2014 hospitality-focused perimeter (definitional caveat, not a period distortion)
- 0001805077-25-000152: loss on repurchase of 2026 convertible notes and loss on Delayed Draw Term Loan prepayment in net loss

### acq-02 - `acquisitions` (acquisition-divestiture-perimeter)

False positives: 21.
- 0000018230-25-000043: ...ure to realize, or a delay in realizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relation...
- 0000018230-26-000017: ...ure to realize, or a delay in realizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relation...
- 0000093410-25-000016: ...d or will not be realized within the expected time period; the company’s future acquisitions or dispositions of assets or shares or the delay or failure of such transaction...
- 0000950170-25-029071: ...cement of fixed-wing aircraft for our aviation transportation services or other acquisitions, joint ventures or strategic investments; our ability to maintain Federal Aviat...
- 0000950170-25-064564: ... with premier products and solutions through innovative product development and acquisitions. The Company has paid a cash dividend to its shareholders every quarter since b...
- 0000950170-25-066916: ...cement of fixed-wing aircraft for our aviation transportation services or other acquisitions, joint ventures or strategic investments; our ability to maintain Federal Aviat...
- 0000950170-25-100228: ...cement of fixed-wing aircraft for our aviation transportation services or other acquisitions, joint ventures or strategic investments; our ability to maintain Federal Aviat...
- 0001023024-26-000049: ...Company’s ability to complete or achieve any or all of the intended benefits of acquisitions and investments, in a timely manner or at all; delays and disruptions in the pr...
- 0001049521-25-000014: ...on customer satisfaction, inability to fully realize the expected benefits from acquisitions, restructurings, and operational efficiency initiatives or delays in realizing ...
- 0001049521-25-000022: ...on customer satisfaction, inability to fully realize the expected benefits from acquisitions, restructurings, and operational efficiency initiatives or delays in realizing ...
- 0001049521-26-000004: ...on customer satisfaction, inability to fully realize the expected benefits from acquisitions, restructurings, and operational efficiency initiatives or delays in realizing ...
- 0001049521-26-000021: ...on customer satisfaction, inability to fully realize the expected benefits from acquisitions, restructurings, and operational efficiency initiatives or delays in realizing ...
- 0001104659-26-012356: ... strategic opportunities, including investing in our business, making strategic acquisitions, strengthening our balance sheet and returning cash to our stockholders through...
- 0001193125-25-256326: ...cement of fixed-wing aircraft for our aviation transportation services or other acquisitions, joint ventures or strategic investments; our ability to maintain Federal Aviat...
- 0001193125-26-066953: ...cement of fixed-wing aircraft for our aviation transportation services or other acquisitions, joint ventures or strategic investments; our ability to maintain Federal Aviat...
- ... and 6 more (listing capped at 15).

False negatives: 28.
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000105132-25-000013: FY2025 guidance is pro forma, excluding homecare/cleaning products classified as held for sale (~$23M sales, ~$6M operating income, ~$0.33 EPS if divestiture fails)
- 0000105132-25-000023: FY2025 guidance on pro forma basis excluding HCCP assets held for sale (~$20M sales, ~$6M operating income, ~$0.33 EPS if not divested)
- 0000105132-25-000061: Assets held for sale (homecare/cleaning divestiture intent) — FY gross margin 55.1% reported vs 55.6% excluding held-for-sale impact; FY26 guidance is pro forma excluding assets to be divested
- 0000105132-26-000006: UK homecare/cleaning portfolio divested in Q4 FY2025; ~$1.6M of prior-year sales not recurring (HCCP -31%) // FY2026 guidance presented on pro forma basis excluding assets expected to be divested in FY2026
- 0000105132-26-000026: UK homecare/cleaning portfolio divestiture (Q4 FY2025) removes ~$1.5M of prior-year sales, depressing HCCP and EIMEA comparisons // FY2026 guidance presented on pro-forma basis excluding assets expected to be divested in fiscal 2026
- 0000105132-26-000059: Prior-year quarter included $1.1M of UK homecare sales divested in Q4 FY2025 // Guidance perimeter changed: outlook now re-includes Americas homecare brands (~$12M sales, $2.9M operating income, $0.17 EPS) and is compared to 'pro forma' FY2025 net sales
- 0000700923-25-000005: $3.1M decrease in contingent compensation expense from a prior acquisition flattered Q4 SG&A/C&I margin
- 0000700923-25-000021: Prior-year Q1 2024 SG&A included $3.2M contingent compensation expense related to a prior acquisition
- 0000700923-25-000028: $5.0M contingent compensation expense (prior acquisition) in Q2 2024 SG&A ($8.2M in 1H 2024) flatters the SG&A comparison
- 0000700923-25-000042: $1.1M Q3 2024 (and $9.3M 9M 2024) contingent compensation expense from a prior acquisition did not recur
- 0000700923-26-000006: $10.3M contingent compensation expense from a prior acquisition recognized in FY2024 did not recur in 2025
- 0000950170-25-074348: Book4Time acquisition completed during FY2025 inflates revenue and subscription growth (CEO cites Q4 subscription growth of 42.7% 'including Book4Time') // Ending cash fell $144.9M to $73.0M year-over-year (likely acquisition-related; cause not stated in provided text)
- 0000950170-25-097225: Book4Time acquisition contributes to the 44.3% subscription revenue growth (explicitly 'including Book4Time'), so growth is not fully organic // Amortization of internal-use software and intangibles rose $0.25M to $1.46M (acquisition-related step-up)
- 0001030469-26-000004: $16.8M discrete tax benefits in 4Q25 ($12.9M expiration of 2019 Scotiabank PR/USVI acquisition tax agreement + $3.9M deferred-tax valuation allowance release) swung tax to an $8.5M benefit vs $2.4M expense in 4Q24
- ... and 13 more (listing capped at 15).

### acq-03 - `completed acquisition` (acquisition-divestiture-perimeter)

No false positives.

False negatives: 118.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000057131-26-000006: 15-store southeast U.S. acquisition (17 acquired stores in last 12 months) inflates Retail sales growth; written same-store sales actually -4% // Kincaid upholstery sale completed subsequent to quarter close; LOI signed to sell American Drew/Kincaid casegoods businesses (upcoming perimeter change)
- 0000057131-26-000018: ~100 bps of Q4 consolidated adjusted margin improvement (150 bps in Wholesale) from favorable casegoods inventory adjustments and pricing ahead of the divestiture — a one-off // 15-store independent-dealer acquisition plus 15 new stores inflate Retail written (+11%) and delivered (+9%) sales; written same-store sales were down 2%
- ... and 103 more (listing capped at 15).

### acq-04 - `recent acquisition` (acquisition-divestiture-perimeter)

False positives: 2.
- 0001694028-26-000021: ...amended (the “Exchange Act”), including, among others, our expected growth from recent acquisitions, expected performance, expectations regarding the success of our distributed p...
- 0001694028-26-000036: ...amended (the “Exchange Act”), including, among others, our expected growth from recent acquisitions, expected performance, expectations regarding the success of our distributed p...

False negatives: 108.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-26-000006: 15-store southeast U.S. acquisition (17 acquired stores in last 12 months) inflates Retail sales growth; written same-store sales actually -4% // Kincaid upholstery sale completed subsequent to quarter close; LOI signed to sell American Drew/Kincaid casegoods businesses (upcoming perimeter change)
- 0000057131-26-000018: ~100 bps of Q4 consolidated adjusted margin improvement (150 bps in Wholesale) from favorable casegoods inventory adjustments and pricing ahead of the divestiture — a one-off // 15-store independent-dealer acquisition plus 15 new stores inflate Retail written (+11%) and delivered (+9%) sales; written same-store sales were down 2%
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- ... and 93 more (listing capped at 15).

### acq-05 - `pro forma` (acquisition-divestiture-perimeter)

No false positives.

False negatives: 106.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000057131-26-000006: 15-store southeast U.S. acquisition (17 acquired stores in last 12 months) inflates Retail sales growth; written same-store sales actually -4% // Kincaid upholstery sale completed subsequent to quarter close; LOI signed to sell American Drew/Kincaid casegoods businesses (upcoming perimeter change)
- 0000057131-26-000018: ~100 bps of Q4 consolidated adjusted margin improvement (150 bps in Wholesale) from favorable casegoods inventory adjustments and pricing ahead of the divestiture — a one-off // 15-store independent-dealer acquisition plus 15 new stores inflate Retail written (+11%) and delivered (+9%) sales; written same-store sales were down 2%
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- 0000093410-25-000058: Hess Corporation acquisition completed July 2025 (post-quarter, affects go-forward perimeter) // Lithium acreage acquisition (~125,000 net acres, Smackover Formation) adds inorganic capex
- ... and 91 more (listing capped at 15).

### acq-06 - `deconsolidation` (acquisition-divestiture-perimeter)

No false positives.

False negatives: 121.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000057131-26-000006: 15-store southeast U.S. acquisition (17 acquired stores in last 12 months) inflates Retail sales growth; written same-store sales actually -4% // Kincaid upholstery sale completed subsequent to quarter close; LOI signed to sell American Drew/Kincaid casegoods businesses (upcoming perimeter change)
- 0000057131-26-000018: ~100 bps of Q4 consolidated adjusted margin improvement (150 bps in Wholesale) from favorable casegoods inventory adjustments and pricing ahead of the divestiture — a one-off // 15-store independent-dealer acquisition plus 15 new stores inflate Retail written (+11%) and delivered (+9%) sales; written same-store sales were down 2%
- ... and 106 more (listing capped at 15).

### acq-07 - `divestiture` (acquisition-divestiture-perimeter)

False positives: 6.
- 0000018230-25-000043: ...alizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relations issues; (xii) adverse effects...
- 0000018230-26-000017: ...alizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relations issues; (xii) adverse effects...
- 0000056978-25-000078: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signifi...
- 0000056978-26-000010: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signifi...
- 0000056978-26-000018: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signifi...
- 0000093410-25-000016: ...s and losses from asset dispositions or impairments; government mandated sales, divestitures, recapitalizations, taxes and tax audits, tariffs, sanctions, changes in fisca...

False negatives: 84.
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- 0000108985-26-000040: $470K acquisition of two wastewater systems (CMV Sewage Co., Pine Run Retirement Community) adds minor inorganic growth
- 0000700923-25-000005: $3.1M decrease in contingent compensation expense from a prior acquisition flattered Q4 SG&A/C&I margin
- 0000700923-25-000021: Prior-year Q1 2024 SG&A included $3.2M contingent compensation expense related to a prior acquisition
- 0000700923-25-000028: $5.0M contingent compensation expense (prior acquisition) in Q2 2024 SG&A ($8.2M in 1H 2024) flatters the SG&A comparison
- 0000700923-25-000042: $1.1M Q3 2024 (and $9.3M 9M 2024) contingent compensation expense from a prior acquisition did not recur
- ... and 69 more (listing capped at 15).

### acq-08 - `divestitures` (acquisition-divestiture-perimeter)

False positives: 6.
- 0000018230-25-000043: ...alizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relations issues; (xii) adverse effects ...
- 0000018230-26-000017: ...alizing, all of the anticipated benefits of our acquisitions, joint ventures or divestitures; (xi) union disputes or other employee relations issues; (xii) adverse effects ...
- 0000056978-25-000078: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signific...
- 0000056978-26-000010: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signific...
- 0000056978-26-000018: ...uidance does not incorporate the impact of any potential business combinations, divestitures, unannounced restructuring activities, strategic investments and other signific...
- 0000093410-25-000016: ...s and losses from asset dispositions or impairments; government mandated sales, divestitures, recapitalizations, taxes and tax audits, tariffs, sanctions, changes in fiscal...

False negatives: 94.
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- 0000105132-25-000013: FY2025 guidance is pro forma, excluding homecare/cleaning products classified as held for sale (~$23M sales, ~$6M operating income, ~$0.33 EPS if divestiture fails)
- 0000105132-25-000023: FY2025 guidance on pro forma basis excluding HCCP assets held for sale (~$20M sales, ~$6M operating income, ~$0.33 EPS if not divested)
- 0000108985-26-000040: $470K acquisition of two wastewater systems (CMV Sewage Co., Pine Run Retirement Community) adds minor inorganic growth
- 0000700923-25-000005: $3.1M decrease in contingent compensation expense from a prior acquisition flattered Q4 SG&A/C&I margin
- 0000700923-25-000021: Prior-year Q1 2024 SG&A included $3.2M contingent compensation expense related to a prior acquisition
- ... and 79 more (listing capped at 15).

### acq-09 - `held for sale` (acquisition-divestiture-perimeter)

False positives: 11.
- 0001030469-25-000033: ...for investment 7,990,647 7,671,470 7,616,099 7,589,076 7,482,406 Mortgage loans held for sale 14,590 12,439 13,286 10,908 8,375 Other loans held for sale 4,362 4,362 4,446 4...
- 0001030469-25-000047: ...for investment 7,919,485 7,990,647 7,671,470 7,616,099 7,589,076 Mortgage loans held for sale 9,680 14,590 12,439 13,286 10,908 Other loans held for sale 6,248 4,362 4,362 4...
- 0001030469-26-000022: ...for investment 8,031,107 7,998,701 7,919,485 7,990,647 7,671,470 Mortgage loans held for sale 8,967 12,483 9,680 14,590 12,439 Other loans held for sale — 3,062 6,248 4,362 ...
- 0001030469-26-000034: ...for investment 8,109,199 8,031,107 7,998,701 7,919,485 7,990,647 Mortgage loans held for sale 7,822 8,967 12,483 9,680 14,590 Other loans held for sale — — 3,062 6,248 4,362...
- 0001169561-26-000003: ...nts $ 1,026,346 $ 302,103 Trade accounts receivable, net 361,846 251,995 Assets held for sale — 34,770 Other current assets 56,869 46,189 Total current assets 1,445,061 635,...
- 0001171843-25-002338: ...13 867,696 (19 )% Restricted equity securities 12,156 11,300 8 % Mortgage loans held for sale 11,386 7,592 50 % Loans 12,886,831 11,880,696 8 % Less allowance for credit los...
- 0001171843-25-004580: ...52 767,255 (11 )% Restricted equity securities 12,156 11,300 8 % Mortgage loans held for sale 22,131 11,174 98 % Loans 13,232,560 12,332,780 7 % Less allowance for credit lo...
- 0001171843-25-006529: ...595 728,580 (8 )% Restricted equity securities 12,203 11,300 8 % Mortgage loans held for sale 9,433 8,453 12 % Loans 13,311,967 12,338,226 8 % Less allowance for credit loss...
- 0001171843-26-000344: ...76 714,853 (8 ) % Restricted equity securities 12,203 11,300 8 % Mortgage loans held for sale 11,744 9,211 27 % Loans 13,696,912 12,605,836 9 % Less allowance for credit los...
- 0001171843-26-002569: ...70 701,713 (8 ) % Restricted equity securities 12,466 12,156 3 % Mortgage loans held for sale 12,893 11,386 13 % Loans 13,945,913 12,886,831 8 % Less allowance for credit lo...
- 0001171843-26-004772: ...80 686,652 (7 ) % Restricted equity securities 12,475 12,156 3 % Mortgage loans held for sale 14,886 22,131 (33 ) % Loans 14,478,489 13,232,560 9 % Less allowance for credit...

False negatives: 101.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- 0000093410-25-000058: Hess Corporation acquisition completed July 2025 (post-quarter, affects go-forward perimeter) // Lithium acreage acquisition (~125,000 net acres, Smackover Formation) adds inorganic capex
- ... and 86 more (listing capped at 15).

### acq-10 - `same-store` (acquisition-divestiture-perimeter)

False positives: 2.
- 0001193125-26-313363: ...ses • Gross profit increased by 9.2% to $218.1 million, despite a 7% decline in same-store sales, reflecting the strength of MarineMax’s diversified business model and ex...
- 0001493152-26-009545: ...sed customers’ utilization of credit and debit cards versus cash, and increased same-store sales. ● NRS’ ‘Rule of 40’ score was 46 in 2Q26, indicating a productive balanc...

False negatives: 117.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000080420-25-000095: Remsdaq acquisition announced subsequent to quarter end - does not affect reported Q3 figures
- 0000080420-25-000145: Remsdaq acquisition completed in Q4 FY2025 (adds $112K intangible amortization; revenue contribution not quantified, appears immaterial)
- 0000093410-25-000058: Hess Corporation acquisition completed July 2025 (post-quarter, affects go-forward perimeter) // Lithium acreage acquisition (~125,000 net acres, Smackover Formation) adds inorganic capex
- 0000093410-26-000019: Hess acquisition (completed 2025) adds 261 MBOED and inflates yoy production, earnings and cash flow comparisons // Divestitures reduce production comparability: Republic of Congo, Malaysia-Thailand JDA, Canada asset sales; $1.8B asset sale proceeds included in adjusted FCF
- 0000093410-26-000110: Hess Corporation acquisition inflates production (+388 MBOED U.S.), capex, and DD&A vs prior year
- 0000105132-25-000013: FY2025 guidance is pro forma, excluding homecare/cleaning products classified as held for sale (~$23M sales, ~$6M operating income, ~$0.33 EPS if divestiture fails)
- ... and 102 more (listing capped at 15).

### acq-11 - `same store` (acquisition-divestiture-perimeter)

No false positives.

False negatives: 125.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000046250-25-000024: Four FY25 acquisitions added ~$72M of the $83.2M Water Treatment sales growth ($13.0M in Q4) — inorganic revenue inflates YoY comparison // SG&A includes $10.4M of added acquired-business costs incl. $4.2M intangible amortization // Post year-end WaterSurplus acquisition pushed leverage above 1.0x adjusted EBITDA
- 0000046250-25-000041: WaterSurplus acquisition adds ~$29M of Water Treatment sales, inflating 15% revenue growth // $2.0M intangibles amortization + $0.9M acquisition costs from acquired business in SG&A
- 0000046250-25-000056: WaterSurplus acquisition adds ~$23M of Water Treatment sales (inorganic) and depresses EPS via ~$5M added amortization/interest // $5.6M added SG&A from acquired business incl. $2.5M intangible amortization and $0.5M earnout fair-value accretion // Interest expense, net rose $1.4M -> $3.8M on acquisition borrowing; leverage 0.86x -> 1.53x
- 0000046250-26-000004: Six FY2026 acquisitions (incl. WaterSurplus) add ~$19M of Water Treatment sales and ~$5M/quarter of amortization, earnout accretion and interest expense
- 0000048465-25-000005: Effective tax rate 21.8% vs 23.4% prior year, driven by purchase of federal transferable energy credits
- 0000048465-25-000035: Organic vs reported net sales distinction (organic +1% vs reported flat) implies perimeter/currency effects not detailed in provided text
- 0000048465-26-000024: Whole-bird turkey divestiture removes ~$50M from FY26 reported net sales // SG&A % of net sales 10.7% vs 8.7% GAAP driven by divestiture-related items (adjusted flat at 8.2%)
- 0000057131-25-000016: Acquired independent La-Z-Boy Furniture Galleries stores and new stores inflate Retail growth (delivered +11% vs written same-store +7%) // Non-GAAP excludes purchase accounting charges from prior acquisitions (~$0.25M operating income difference, minor)
- 0000057131-25-000028: Acquisitions (7 independent stores) and 11 new stores inflate Retail delivered sales vs written same-store sales down 5%
- 0000057131-25-000080: New and acquired stores drive Retail written +5% while written same-store sales are down 4% // $0.2M (FY26 Q1) and $0.4M (FY25 Q1) purchase accounting charges from prior acquisitions // Announced 15-store acquisition (closing late October) will affect future-period comparability
- 0000057131-25-000096: $0.2M pre-tax purchase accounting charges from prior acquisitions excluded from adjusted results // Retail written sales +4% driven by new and acquired stores while written same-store sales were -2%
- 0000057131-26-000006: 15-store southeast U.S. acquisition (17 acquired stores in last 12 months) inflates Retail sales growth; written same-store sales actually -4% // Kincaid upholstery sale completed subsequent to quarter close; LOI signed to sell American Drew/Kincaid casegoods businesses (upcoming perimeter change)
- 0000057131-26-000018: ~100 bps of Q4 consolidated adjusted margin improvement (150 bps in Wholesale) from favorable casegoods inventory adjustments and pricing ahead of the divestiture — a one-off // 15-store independent-dealer acquisition plus 15 new stores inflate Retail written (+11%) and delivered (+9%) sales; written same-store sales were down 2%
- ... and 110 more (listing capped at 15).

### tax-01 - `discrete tax` (discrete-tax)

False positives: 14.
- 0000056978-26-000010: ...airment relating to equity investments, income tax expense/benefit arising from discrete tax items triggered by acquisition, disposal of business (both via a sale or an aba...
- 0000854775-26-000016: ...uted share, respectively, exclusive of such items as reversals of tax reserves, discrete tax benefits, restructuring charges and reversals, intangible amortization, stock-b...
- 0001049521-25-000014: ...ome from operations before income taxes. The recalculation also adjusts for any discrete tax expense or benefit related to the items. (2) Adjusted earnings per share is cal...
- 0001049521-25-000022: ...ome from operations before income taxes. The recalculation also adjusts for any discrete tax expense or benefit related to the items. (2) Adjusted earnings per share is cal...
- 0001049521-26-000004: ...ome from operations before income taxes. The recalculation also adjusts for any discrete tax expense or benefit related to the items. (2) Adjusted earnings per share is cal...
- 0001049521-26-000021: ...ome from operations before income taxes. The recalculation also adjusts for any discrete tax provision or benefit related to the items. (2) Adjusted earnings per share is c...
- 0001193125-26-085812: ...nt and gain on the sale of CFP. The 2024 period included an overall increase in discrete tax benefits driven by the previous CEO's termination in July 2024. Net income, dil...
- 0001421517-25-000047: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted net income per share is a non-GAAP financial measure that the ...
- 0001421517-25-000076: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted net loss per share is a non-GAAP financial measure that the Co...
- 0001421517-25-000116: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted net income (loss) per share is a non-GAAP financial measure th...
- 0001421517-25-000133: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted net income per share is a non-GAAP financial measure that the ...
- 0001421517-26-000021: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted net income per share is a non-GAAP financial measure that the ...
- 0001421517-26-000040: ...licable tax effect of the excluded items including the stock-based compensation discrete tax item. • Adjusted loss per share is a non-GAAP financial measure that the Compan...
- 0001628280-26-010689: ...er adjustments — — (0.14) — Other charges 0.04 0.04 0.08 0.07 Impact of certain discrete tax items (c) (0.17) 0.06 (0.06) 0.17 Non-GAAP earnings per diluted share $ 1.65 $ ...

False negatives: 30.
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000057131-25-000028: $0.10/share unfavorable foreign tax discrete items in both GAAP and adjusted EPS
- 0000105132-25-000013: $11.9M one-time uncertain tax position release inflates net income (+92% headline vs +15% excluding) and adds $0.87 to diluted EPS
- 0000105132-25-000023: Guidance excludes previously disclosed uncertain tax position benefit
- 0000105132-25-000061: $11.9M discrete tax release (uncertain tax position) inflates FY25 net income +31% vs +14% adjusted; $0.87 favorable EPS impact
- 0000105132-26-000026: Prior-year $11.9M ($0.87/share) one-time favorable tax adjustment (uncertain tax position release) makes GAAP net income/EPS fall 32% despite underlying growth
- 0000700923-25-000021: Effective tax rate swung 18.0% (Q1 2024) to 28.9% (Q1 2025) due to no stock-compensation excess tax benefits this year, distorting the net income/EPS comparison (understates underlying improvement)
- 0000700923-25-000028: Tax swing: Q2 2025 income tax expense of $10.9M vs Q2 2024 tax benefit of $6.9M (prior-year pretax loss); 1H 2024 effective rate was negative 281.9%
- 0000700923-25-000042: Q3 2024 had an income tax BENEFIT of $7.9M (42.5% effective rate) vs $12.6M tax expense (28.3%) in Q3 2025 - tax swing distorts net income comparison
- 0000700923-26-000006: Effective tax rate fell sharply (Q4: 40.9% to 21.2%; FY: 34.9% to 26.6%) from state deferred tax remeasurement and permanent items, amplifying net income growth
- 0000700923-26-000026: Effective tax rate fell 28.9% -> 26.9% partly from stock-compensation excess tax benefits
- 0000950170-25-097225: Prior-year quarter included a $6.7M income tax benefit, inflating the prior-year net income comparison ($14.1M vs $4.9M)
- 0001030469-25-000033: $1.7M discrete tax benefit lowered 2Q25 ETR to 21.37% vs 24.90% anticipated full-year rate // YoY tax swing: income tax $14.1M vs $20.1M in 2Q24 — diluted EPS rose 6.5% YoY while pretax income fell ~7.6% ($65.9M vs $71.3M)
- 0001030469-25-000047: $2.3M discrete tax benefit lowered 3Q25 ETR to 15.53% vs anticipated 23.06% annual rate, flattering YoY EPS
- 0001030469-26-000022: 4Q25 comparison distorted by $16.8M tax benefits (4Q25 tax was an $8.5M benefit vs $14.9M expense) and net $6.8M expense items
- ... and 15 more (listing capped at 15).

### tax-02 - `tax benefit` (discrete-tax)

False positives: 39.
- 0000056978-26-000010: ... significant changes in tax laws, gain/loss on disposal of business, as well as tax benefits or expenses associated with the foregoing non-GAAP items. The non-GAAP adjustm...
- 0000105132-26-000059: ...oud computing implementation costs 1,319 1,265 Deferred income taxes (665) (86) Tax benefit from release of uncertain tax position — (11,929) Stock-based compensation 6,06...
- 0000320193-25-000077: ...set by a U.S. foreign tax credit of $4.8 billion and a decrease in unrecognized tax benefits of $823 million. For additional information, refer to Note 7, “Income Taxes” o...
- 0000700923-25-000005: ...nized benefit of deferred tax assets, offset by lower stock compensation excess tax benefits. The increase in permanent difference items primarily related to deductibility...
- 0000854775-26-000016: ...e, respectively, exclusive of such items as reversals of tax reserves, discrete tax benefits, restructuring charges and reversals, intangible amortization, stock-based com...
- 0000856982-26-000007: ...erred income tax liabilities ​ 19,665 ​ 240 Liabilities related to unrecognized tax benefits ​ 2,248 ​ 2,118 Deferred compensation payable ​ 17,542 ​ 19,197 Deferred credi...
- 0000856982-26-000023: ...ed income tax liabilities ​ 19,664 ​ 19,665 Liabilities related to unrecognized tax benefits ​ 2,248 ​ 2,248 Deferred compensation payable ​ 17,373 ​ 17,542 Deferred credi...
- 0000908315-26-000004: ...00) ​ Operating lease right of use asset amortization ​ ​ 347,300 ​ ​ 317,100 ​ Tax benefits on exercised stock options ​ 1,619,000 ​ 1,307,700 ​ Change in operating asset...
- 0000908315-26-000016: ...6,600 ​ Operating lease right of use asset amortization ​ ​ 90,800 ​ ​ 82,200 ​ Tax benefits on exercised stock options ​ 302,800 ​ — ​ Change in operating assets and liab...
- 0000908315-26-000027: ...400 ​ Operating lease right of use asset amortization ​ ​ 183,800 ​ ​ 166,400 ​ Tax benefits on exercised stock options ​ 742,800 ​ 971,200 ​ Change in operating assets an...
- 0000921582-25-000005: ...973) 1,759 Write-downs, including asset impairments 3,973 1,884 Deferred income tax benefit (5,631) (1,447) Share-based and other non-cash compensation 23,209 24,230 Unrea...
- 0000950170-25-074348: ...arnings per share (c) $ 0.54 $ 0.32 $ 1.55 $ 1.10 (a) Tax events include excess tax benefits or expense related to share-based compensation, release of valuation allowance...
- 0001022408-26-000017: ...495 4,363 3,788 Other (income) expense, net [2] (568 ) (930 ) (2,243 ) (1,498 ) Tax benefit (expense) on restricted stock 12 21 101 513 Non-GAAP: Provision for income taxe...
- 0001049521-25-000014: ...6) Other income (expense), net 2,304 (2,784) (2,900) (5,706) Loss before income tax benefit (21,818) (57,217) (69,241) (170,674) Income tax benefit (2,648) (12,643) (14,96...
- 0001049521-26-000004: ...5) (17,336) Other expense, net (440) (3,865) (2,520) (5,204) Loss before income tax benefit (17,459) (24,304) (33,995) (47,423) Income tax benefit (2,364) (6,725) (6,385) ...
- ... and 24 more (listing capped at 15).

False negatives: 12.
- 0000057131-25-000028: $0.10/share unfavorable foreign tax discrete items in both GAAP and adjusted EPS
- 0000057131-26-000018: $0.16/share favorable discrete tax items boost both GAAP and adjusted Q4 EPS
- 0001030469-25-000033: $1.7M discrete tax benefit lowered 2Q25 ETR to 21.37% vs 24.90% anticipated full-year rate // YoY tax swing: income tax $14.1M vs $20.1M in 2Q24 — diluted EPS rose 6.5% YoY while pretax income fell ~7.6% ($65.9M vs $71.3M)
- 0001030469-25-000047: $2.3M discrete tax benefit lowered 3Q25 ETR to 15.53% vs anticipated 23.06% annual rate, flattering YoY EPS
- 0001030469-26-000034: 2Q26 ETR includes benefit of unspecified discrete tax items
- 0001104659-26-014044: Prior-year Q4 2024 write-off of deferred tax assets drove 48.8% effective tax rate vs 27.1% in Q4 2025, flattering headline YoY earnings growth
- 0001140361-25-005021: FY2023 net income benefited from $61.9M valuation allowance release on deferred tax assets, partially offset by $38.2M tax receivable agreements expense — distorts the $35.5M vs $79.2M net income comparison
- 0001169561-25-000030: Prior-year Q4'24 income tax benefit of $103.1M (FY'24 benefit $85.3M) inflates prior-year GAAP net income/EPS ($126M / $2.89 vs $31M / $0.69), making the net-income comparison meaningless
- 0001193125-25-181264: Simple Mills dilutive: net loss $2.1M and ($0.01) diluted EPS, plus acquisition-related costs ($0.01/share incl. $0.01 non-deductible tax item from prior period)
- 0001304492-25-000095: Tax swing: $2.3M benefit vs $1.2M expense prior year
- 0001558370-25-000885: $11,010,000 nonrecurring non-cash write-off of deferred tax assets in Q4 2024 (deferred comp tax deductibility revoked), inflating tax expense and depressing reported net earnings
- 0001628280-26-009722: Income tax swing: FY taxes fell $6.9M to $4.8M, cushioning a $3.6M pre-tax income decline

### tax-03 - `valuation allowance` (discrete-tax)

False positives: 14.
- 0000921582-25-000005: ...n 240,133 243,299 Other assets 22,441 20,879 Deferred income tax assets, net of valuation allowance 14,499 7,988 Goodwill 52,815 52,815 Other intangible assets, net of accumulated...
- 0000921582-25-000022: ...n 245,073 240,133 Other assets 22,107 22,441 Deferred income tax assets, net of valuation allowance 14,394 14,499 Goodwill 52,815 52,815 Other intangible assets, net of accumulate...
- 0000921582-25-000064: ...n 243,672 240,133 Other assets 23,356 22,441 Deferred income tax assets, net of valuation allowance 13,630 14,499 Goodwill 52,815 52,815 Other intangible assets, net of accumulate...
- 0000950170-25-074348: ... excess tax benefits or expense related to share-based compensation, release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adju...
- 0001023024-26-000013: ...ent, net 62,476 56,863 Deferred tax assets, net of deferred tax liabilities and valuation allowance 69,072 85,106 Intangible assets, net 479,526 541,834 Goodwill 62,480 59,990 Der...
- 0001193125-25-251570: ... excess tax benefits or expense related to share-based compensation, release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adju...
- 0001193125-26-022562: ... excess tax benefits or expense related to share-based compensation, release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adju...
- 0001193125-26-228898: ... excess tax benefits or expense related to share-based compensation, release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adju...
- 0001193125-26-318055: ... excess tax benefits or expense related to share-based compensation, release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adju...
- 0001628280-25-045977: ...n 243,836 240,133 Other assets 24,002 22,441 Deferred income tax assets, net of valuation allowance 12,728 14,499 Goodwill 52,815 52,815 Other intangible assets, net of accumulate...
- 0001628280-26-010689: ...pacts of certain discrete tax items include certain impacts of tax law changes, valuation allowance adjustments, uncertain tax positions, provision to return and other adjustments...
- 0001628280-26-011693: ...n 242,910 240,133 Other assets 24,820 22,441 Deferred income tax assets, net of valuation allowance 12,577 14,499 Goodwill 45,815 52,815 Other intangible assets, net of accumulate...
- 0001628280-26-028907: ...se incentives and other assets 27,240 24,820 Deferred income tax assets, net of valuation allowance 12,675 12,577 Goodwill 45,815 45,815 Other intangible assets, net of accumulate...
- 0001628280-26-049290: ...se incentives and other assets 28,613 24,820 Deferred income tax assets, net of valuation allowance 12,465 12,577 Goodwill 45,815 45,815 Other intangible assets, net of accumulate...

False negatives: 35.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit // Discrete stock-based-compensation tax benefit $17M in 1Q25 vs $38M in 1Q24
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000018230-25-000043: $41M discrete tax charge in 3Q25 vs $47M discrete tax benefit in 3Q24 (~$88M unfavorable swing on prior-year estimates)
- 0000018230-26-000017: Discrete tax benefit $68M in 1Q26 vs $17M in 1Q25 (stock-comp settlements); effective rate 20.9% vs 22.3%
- 0000056978-25-000078: Q4 FY24 had a $2.0M income tax benefit vs Q4 FY25 $0.3M tax expense, distorting the GAAP EPS comparison // FY25 tax provision of $20.3M against only $20.5M pretax income implies large discrete tax items compressing FY GAAP net income to $0.2M
- 0000056978-26-000018: Non-GAAP adjustments (amortization, restructuring/severance, equity comp, impairments, discrete tax items) bridge GAAP $0.66 to non-GAAP $0.79; reconciliation tables are cut off in the provided text
- 0000057131-25-000028: $0.10/share unfavorable foreign tax discrete items in both GAAP and adjusted EPS
- 0000057131-26-000018: $0.16/share favorable discrete tax items boost both GAAP and adjusted Q4 EPS
- 0000105132-25-000013: $11.9M one-time uncertain tax position release inflates net income (+92% headline vs +15% excluding) and adds $0.87 to diluted EPS
- 0000105132-25-000023: Guidance excludes previously disclosed uncertain tax position benefit
- 0000105132-25-000061: $11.9M discrete tax release (uncertain tax position) inflates FY25 net income +31% vs +14% adjusted; $0.87 favorable EPS impact
- 0000105132-26-000026: Prior-year $11.9M ($0.87/share) one-time favorable tax adjustment (uncertain tax position release) makes GAAP net income/EPS fall 32% despite underlying growth
- 0000700923-25-000021: Effective tax rate swung 18.0% (Q1 2024) to 28.9% (Q1 2025) due to no stock-compensation excess tax benefits this year, distorting the net income/EPS comparison (understates underlying improvement)
- 0000700923-25-000028: Tax swing: Q2 2025 income tax expense of $10.9M vs Q2 2024 tax benefit of $6.9M (prior-year pretax loss); 1H 2024 effective rate was negative 281.9%
- 0000700923-25-000042: Q3 2024 had an income tax BENEFIT of $7.9M (42.5% effective rate) vs $12.6M tax expense (28.3%) in Q3 2025 - tax swing distorts net income comparison
- ... and 20 more (listing capped at 15).

### tax-04 - `uncertain tax position` (discrete-tax)

False positives: 7.
- 0000105132-26-000059: ... costs 1,319 1,265 Deferred income taxes (665) (86) Tax benefit from release of uncertain tax position — (11,929) Stock-based compensation 6,067 5,716 Unrealized foreign currency exc...
- 0000950170-25-074348: ..., release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adjusted net income, a non-GAAP financial measure, is defined as net incom...
- 0001193125-25-251570: ..., release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adjusted net income, a non-GAAP financial measure, is defined as net incom...
- 0001193125-26-022562: ..., release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adjusted net income, a non-GAAP financial measure, is defined as net incom...
- 0001193125-26-228898: ..., release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adjusted net income, a non-GAAP financial measure, is defined as net incom...
- 0001193125-26-318055: ..., release of valuation allowances against deferred income taxes, and changes in uncertain tax positions (b) Adjusted net income, a non-GAAP financial measure, is defined as net incom...
- 0001628280-26-010689: ...ms include certain impacts of tax law changes, valuation allowance adjustments, uncertain tax positions, provision to return and other adjustments, and the impact on certain intercom...

False negatives: 34.
- 0000018230-25-000013: Prior-year (1Q24) $64M nontaxable gain on divestiture of a non-U.S. mining entity plus related $54M tax benefit // Discrete stock-based-compensation tax benefit $17M in 1Q25 vs $38M in 1Q24
- 0000018230-25-000037: Prior-year 2Q24 included $228M losses on divestiture of two non-U.S. entities with no related tax benefit, distorting GAAP YoY comparison
- 0000018230-25-000043: $41M discrete tax charge in 3Q25 vs $47M discrete tax benefit in 3Q24 (~$88M unfavorable swing on prior-year estimates)
- 0000018230-26-000017: Discrete tax benefit $68M in 1Q26 vs $17M in 1Q25 (stock-comp settlements); effective rate 20.9% vs 22.3%
- 0000056978-25-000078: Q4 FY24 had a $2.0M income tax benefit vs Q4 FY25 $0.3M tax expense, distorting the GAAP EPS comparison // FY25 tax provision of $20.3M against only $20.5M pretax income implies large discrete tax items compressing FY GAAP net income to $0.2M
- 0000056978-26-000018: Non-GAAP adjustments (amortization, restructuring/severance, equity comp, impairments, discrete tax items) bridge GAAP $0.66 to non-GAAP $0.79; reconciliation tables are cut off in the provided text
- 0000057131-25-000028: $0.10/share unfavorable foreign tax discrete items in both GAAP and adjusted EPS
- 0000057131-26-000018: $0.16/share favorable discrete tax items boost both GAAP and adjusted Q4 EPS
- 0000700923-25-000021: Effective tax rate swung 18.0% (Q1 2024) to 28.9% (Q1 2025) due to no stock-compensation excess tax benefits this year, distorting the net income/EPS comparison (understates underlying improvement)
- 0000700923-25-000028: Tax swing: Q2 2025 income tax expense of $10.9M vs Q2 2024 tax benefit of $6.9M (prior-year pretax loss); 1H 2024 effective rate was negative 281.9%
- 0000700923-25-000042: Q3 2024 had an income tax BENEFIT of $7.9M (42.5% effective rate) vs $12.6M tax expense (28.3%) in Q3 2025 - tax swing distorts net income comparison
- 0000700923-26-000006: Effective tax rate fell sharply (Q4: 40.9% to 21.2%; FY: 34.9% to 26.6%) from state deferred tax remeasurement and permanent items, amplifying net income growth
- 0000700923-26-000026: Effective tax rate fell 28.9% -> 26.9% partly from stock-compensation excess tax benefits
- 0000854775-26-000004: Cash flow from operations increase driven primarily by $5.1M decrease in deferred income tax benefit vs $0.5M increase in prior year
- 0001030469-25-000033: $1.7M discrete tax benefit lowered 2Q25 ETR to 21.37% vs 24.90% anticipated full-year rate // YoY tax swing: income tax $14.1M vs $20.1M in 2Q24 — diluted EPS rose 6.5% YoY while pretax income fell ~7.6% ($65.9M vs $71.3M)
- ... and 19 more (listing capped at 15).

## EXPLORATORY rows (regex variants) - DESCRIPTIVE ONLY

**These rows are NOT eligible for the promotion rule and no production recommendation may cite
them.** They were not frozen with the primary list and exist only to indicate whether a narrower
anchored form is worth precommitting in a FUTURE measurement round.

| id | regex | concept | hit | hit rate | TP | FP | FN | precision | recall | F1 | any-break precision | v1 overlap | novel (labeled) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| x-01 | `\bacquisitions?\b` | acquisition-divestiture-perimeter | 216/298 | 0.725 | 117 | 54 | 8 | 0.684 | 0.936 | 0.791 | 142/171 = 0.830 (Wilson 95%: 0.767-0.879) | 182 | 29 |
| x-02 | `\bdivestitures?\b` | acquisition-divestiture-perimeter | 58/298 | 0.195 | 41 | 6 | 84 | 0.872 | 0.328 | 0.477 | 44/47 = 0.936 (Wilson 95%: 0.828-0.978) | 58 | 0 |
| x-03 | `\bpro forma\b` | acquisition-divestiture-perimeter | 23/298 | 0.077 | 19 | 0 | 106 | 1.000 | 0.152 | 0.264 | 18/19 = 0.947 (Wilson 95%: 0.754-0.991) | 23 | 0 |
| x-04 | `\bdiscrete tax\b` | discrete-tax | 28/298 | 0.094 | 9 | 14 | 30 | 0.391 | 0.231 | 0.290 | 17/23 = 0.739 (Wilson 95%: 0.535-0.875) | 28 | 0 |
| x-05 | `\bvaluation allowance\b` | discrete-tax | 13/298 | 0.044 | 3 | 9 | 36 | 0.250 | 0.077 | 0.118 | 9/12 = 0.750 (Wilson 95%: 0.468-0.911) | 12 | 0 |

## Decisions - the PRECOMMITTED promotion rule, applied verbatim

Frozen in spec 165 before this measurement ran: a PRIMARY (literal) candidate is RECOMMENDED for a
production cmpscan-v2 spec iff **concept precision >= 0.80 AND concept recall >= 0.30 AND it fires on
>= 5 labeled filings where cmpscan-v1 did not** - all three, over the 235-filing labeled reference.
Candidates failing it are NOT recommended, full stop. Exploratory rows are ineligible.

| id | literal | precision | >= threshold | recall | >= threshold | novel (labeled) | >= threshold | VERDICT |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| acq-01 | `acquisition` | 0.684 | no | 0.936 | yes | 29 | yes | NOT RECOMMENDED |
| acq-02 | `acquisitions` | 0.822 | yes | 0.776 | yes | 21 | yes | RECOMMENDED |
| acq-03 | `completed acquisition` | 1.000 | yes | 0.056 | no | 5 | yes | NOT RECOMMENDED |
| acq-04 | `recent acquisition` | 0.895 | yes | 0.136 | no | 5 | yes | NOT RECOMMENDED |
| acq-05 | `pro forma` | 1.000 | yes | 0.152 | no | 0 | no | NOT RECOMMENDED |
| acq-06 | `deconsolidation` | 1.000 | yes | 0.032 | no | 3 | no | NOT RECOMMENDED |
| acq-07 | `divestiture` | 0.872 | yes | 0.328 | yes | 0 | no | NOT RECOMMENDED |
| acq-08 | `divestitures` | 0.838 | yes | 0.248 | no | 0 | no | NOT RECOMMENDED |
| acq-09 | `held for sale` | 0.686 | no | 0.192 | no | 4 | no | NOT RECOMMENDED |
| acq-10 | `same-store` | 0.800 | yes | 0.064 | no | 1 | no | NOT RECOMMENDED |
| acq-11 | `same store` | n/a | no | 0.000 | no | 0 | no | NOT RECOMMENDED |
| tax-01 | `discrete tax` | 0.391 | no | 0.231 | no | 0 | no | NOT RECOMMENDED |
| tax-02 | `tax benefit` | 0.409 | no | 0.692 | yes | 9 | yes | NOT RECOMMENDED |
| tax-03 | `valuation allowance` | 0.222 | no | 0.103 | no | 0 | no | NOT RECOMMENDED |
| tax-04 | `uncertain tax position` | 0.417 | no | 0.128 | no | 0 | no | NOT RECOMMENDED |

RESULT: 1 candidate(s) pass the precommitted rule: `acquisitions`.

## Standing caveats (they apply to EVERY number above)

1. The concept reference derives from EXPLORATORY ratified labels (spec 162 status), not ground truth.
2. The taxonomy is REGEX-CODED (`categorize-comparability.ps1`) with a long uncategorized tail; a
   concept-negative filing may simply be a filing whose item text the taxonomy did not catch.
3. 63 of the 298 scanned filings have no labels at all - they contribute HIT RATES ONLY and never
   enter any precision, recall, F1 or any-break number.
4. Filings cluster within tickers, so observations are not independent and the Wilson intervals are
   somewhat narrower than the truth.
5. Nothing here changes production. A promoted phrase becomes real only via a cmpscan-v2 spec, which
   bumps `EarningsComparabilityScan.Version` and moves the AI-ON scoring pins.
