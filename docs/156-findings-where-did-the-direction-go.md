# Findings: where did the direction go? (spec 156 audit)

## 1. What this audit establishes — and what it does not

This audit establishes **data provenance and extraction coverage** over Radar's accrued signal store:
what was collected, what direction each signal was assigned, and how much of that assignment is
explainable — at three independent levels — from what is on disk.

It establishes **nothing whatsoever about business efficacy**. It does not show that any signal predicts
anything, and no number in this document may be cited in support of a strategy claim. AD-15's
positive-claim suspension (amended 2026-07-28) is unaffected by anything found here.

**Store snapshot:** the live store at `data/` (main repo), scanned read-only on **2026-07-28**
(16:24 UTC) by `scripts/audit-signal-directions.ps1`. Totals: **49,969** signal files (0 unreadable),
**6,567** raw evidence files (0 unreadable, 0 duplicate ids). Signal `observedAt` spans
2006-02-07 → 2026-07-28 (SEC filings backfill their historical filing instants); signal `createdAt`
(the knowledge date) spans 2026-06-30 → 2026-07-28. Nothing in the store was modified.

**Direction totals (N = 49,969 signals):** Neutral **43,787 (87.63 %)**, Positive **4,033 (8.07 %)**,
Negative **2,149 (4.30 %)** — confirming spec 156's premise that ~7 of 8 signals carry no trajectory.

## 2. The three dimensions, independently, each with its own denominator

Per spec 156 §1, these are three *independent* dimensions — none is conditional on another, and each has
its own denominator.

| # | Dimension | Denominator | Coverage |
|---|-----------|-------------|----------|
| 1a | Evidence-source resolution (signals whose `evidenceId` resolves to a stored raw item) | 49,969 signals | 5,717 — **11.44 %** |
| 1b | Evidence-source resolution (distinct referenced `evidenceId`s that resolve) | 48,797 distinct referenced evidence ids | 5,657 — **11.59 %** |
| 2 | Persisted extraction `Reason` on the signal record itself (non-blank) | 49,969 signals | 49,969 — **100.00 %** |
| 3 | Upstream producer/classification reason (InsiderBuying scope: evidence resolves AND carries `insiderClassificationReason`) | 9,020 InsiderBuying signals | 0 — **0.00 %** (100.00 % Unknown) |

**The headline structural fact is dimension 2 recovering where dimension 1 fails.** Only 11.44 %
(of 49,969 signals) have resolvable evidence — the spec-142/145 measurement, cause diagnosed in spec 145
(per-run minted evidence ids vs `contentHash`-keyed files), healed forward only. But `FileSignalStore`
persists `Reason` **on the signal record itself**, beside `Direction` and `Type`, and that coverage is
**total**: 100.00 % of 49,969 signals carry a non-blank reason. The extraction rule that fired is
therefore recoverable for *every* signal in the store, including the ~88.5 % whose evidence is
unresolvable. The by-design vs by-default split in §3 is answerable at rule level for the whole corpus —
it is only the *upstream* classification branch (dimension 3) that is gone.

A corroborating detail: the persisted reasons include phrases that no longer exist in the current rule
table (`radar-keyword-rules-v6`) — e.g. 21 `CustomerWin` signals matched a bare `deployment` and
6 `GovernmentContract` signals a bare `awarded`, phrases since tightened. The `Reason` field is a faithful
record of the rule that fired *at extraction time*, under the rule-set version then current — which is
exactly what makes it auditable history rather than a re-derivation.

## 3. Direction × reason classification (measured, N = 49,969 signals)

Reason-classes: `keyword` = `Matched phrase '<phrase>'` (the deterministic extractor);
`news-branch` = the fixed spec-70 news reason; `ai-read` = free-prose rationale from the spec-119
AI earnings read; `unknown` = blank/missing (measured count: **0**).

| Count | Type | Direction | Reason-class | Bucket (with citation) |
|------:|------|-----------|--------------|------------------------|
| 15,656 | MediaAttention | Neutral | news-branch | **Neutral by design** — spec 70; thesis-consistent under AD-16 (news is the attention *arriving*) |
| 12,688 | InstitutionalOwnership | Neutral | keyword | **Neutral by design** — spec 99 (10,339 `beneficial-ownership amendment (routine)` + 2,349 `passive beneficial-ownership stake (13g)`; "never misfire bullish") |
| 7,026 | InsiderBuying | Neutral | keyword | **Neutral by design** (reader rules: 10b5-1 forced Neutral at `HttpSecForm4Reader`; mixed buy+sell; grants/exercises excluded) — but *which* branch is **Unknown** (§4) |
| 5,018 | GuidanceChange | Neutral | keyword | **Neutral by design / data limitation** — 8-K item 2.02 `results of operations` carries no valence in the item title (rule-table comment); see §5 for the spec-119 relationship |
| 2,888 | ExecutiveHire | Neutral | keyword | **Neutral by design / data limitation** — 8-K item 5.02 `appointment of certain officers` covers both departures and appointments (rule-table comment) |
| 1,837 | InsiderBuying | Negative | keyword | Directional — reader branch: discretionary open-market sale |
| 1,565 | StrategicPartnership | Positive | keyword | Directional — keyword rules (`material definitive agreement` 827, `partnership` 607, …) |
| 612 | ProductLaunch | Positive | keyword | Directional — keyword rules |
| 511 | CapitalRaise | Neutral | keyword | **Neutral by design** — debt/hybrid events whose valence the code cannot read (convertible/credit-facility/8-K 2.03/3.02; rule-table comment) |
| 433 | GuidanceChange | Positive | ai-read | Directional — spec-119 AI earnings read (validated structured output) |
| 426 | GovernmentContract | Positive | keyword | Directional — keyword rules |
| 412 | CustomerWin | Positive | keyword | Directional — keyword rules |
| 306 | CapitalRaise | Negative | keyword | Directional — dilution/distress phrases |
| 157 | InsiderBuying | Positive | keyword | Directional — reader branch: discretionary open-market purchase |
| 156 | InstitutionalOwnership | Positive | keyword | Directional — spec 99: activist 13D |
| 128 | ExecutiveHire | Positive | keyword | Directional — keyword rules |
| 102 | GuidanceChange | Positive | keyword | Directional — press-release guidance phrases |
| 42 | CapitalRaise | Positive | keyword | Directional — funding-round phrases |
| 6 | GuidanceChange | Negative | ai-read | Directional — spec-119 AI earnings read |

(The 19 rows sum to 49,969. A full per-(type, direction, phrase) frequency table is emitted by
`scripts/audit-signal-directions.ps1`.)

**Bucket totals (N = 49,969 signals):**

- **Directional** — 6,182 (12.37 %): 5,743 by keyword/reader rule, 439 by the AI read.
- **Neutral by design, with citation** — 43,787 (87.63 %). Every Neutral signal in the store traces to an
  explicit, cited decision or stated data limitation: spec 70 (news, 15,656), spec 99 (13G/amendments,
  12,688), the `HttpSecForm4Reader` rules incl. 10b5-1 (7,026), the no-valence 8-K item-title phrases
  (5,018 + 2,888), and the no-directional-read CapitalRaise phrases (511).
- **Neutral by default** (matched a rule that merely happens to lack a direction, with no stated
  rationale) — **0**. This bucket is empty: every Neutral rule carries design commentary in
  `KeywordSignalExtractor` or `HttpSecForm4Reader`.
- **Unknown** (blank/missing reason) — **0** at the extraction-rule level. (At the *upstream-branch* level
  the InsiderBuying picture is 100 % Unknown — §4 — but that is dimension 3, not this table.)

Types with designed-Neutral rules but **zero accrued signals**: HiringActivity (spec 103), PatentActivity
(spec 127), RegulatoryApproval (spec 129 — designed Positive), TrademarkActivity (spec 130). Their
collectors are not in the live enabled set, so they contribute nothing to any figure here.

## 4. The Form 4 classification reason: UNKNOWN — the persistence gap, named

**N = 9,020 InsiderBuying signals** (7,026 Neutral / 1,837 Negative / 157 Positive). Attributable to a
specific reader branch (10b5-1 plan vs mixed buy+sell vs no-discretionary-transactions):
**0 (0.00 %). 9,020 (100.00 %) are Unknown.**

The gap is a *persistence* gap, not a classification gap: `HttpSecForm4Reader.Classify` computes
`Is10b5Plan` and distinguishes the `NeutralExcluded`-codes-only case from a mixed same-filing buy+sell —
deterministically, at collection time — but `SecForm4Collector.MapToEvidence` persisted only
`insiderDirection` and `insiderNetValue`. The branch never reached disk. Under this spec's
read-only/no-refetch constraint the historical attribution is reported as **Unknown — not estimated, not
inferred from the phrase or the net value, and not backfilled by re-fetching filings.**

Two compounding facts, stated for honesty: even had the branch been persisted on evidence metadata,
dimension 1 shows only 11.44 % (of 49,969) of signals' evidence resolves, so most accrued attribution
would still be unreachable through the evidence link; and both halves of that chain (content-derived
evidence identity, spec 145; the classification token, spec 156 §4 below) heal **forward only**.

## 5. Per-source classification: design decision / data limitation / unfilled gap

For every predominantly-Neutral source (denominators are the per-type totals):

- **News / MediaAttention** (15,656 of 15,656 Neutral — 100 % of the type; 31.33 % of all 49,969
  signals): **design decision** (spec 70), and under AD-16 thesis-consistent — news is the attention
  *arriving*, the thing the stealth thesis predicts, not an input to it. Implemented as designed; not
  empirically validated, and this audit does not validate it. Per AD-16, no recommendation to make news
  directional is made here.
- **InstitutionalOwnership / sec-13dg** (12,688 of 12,844 Neutral — 98.79 % of the type): **design
  decision** (spec 99: a passive 13G and any /A amendment are Neutral so they never misfire bullish),
  with a named **deferral that deserves calling out**: the 13G/13D % -of-class and the amendment *delta*
  (increasing vs exiting stake) are not parsed — spec 100 explicitly deferred both (v1 reads form type
  only, never the filing body). This is the largest single cohort where direction is plausibly present in
  the source and deliberately not read — 10,339 routine amendments whose deltas could in principle be
  directional. It is deferred-by-design (cited), not an unnoticed gap; whether to fund that parse is an
  efficacy-motivated next-spec decision, not a defect finding.
- **InsiderBuying / sec-form4** (7,026 of 9,020 Neutral — 77.89 % of the type): the neutrality itself is
  **design decision** (10b5-1 plans forced Neutral; mixed buy+sell deliberately not net-signed;
  grants/exercises/withholding/gifts excluded — all rule-table/reader commentary). The **unfilled gap**
  was the branch attribution (§4) — now fixed forward (§6). The direction of every historical insider
  signal *is* recoverable (`insiderDirection` was always persisted); only the reason was not.
- **GuidanceChange, the `results of operations` phrase** (5,018 of 5,559 Neutral — 90.27 % of the type):
  **data limitation at the item-title level** — 8-K item 2.02 says "earnings were released", not whether
  they were good, so the keyword rule is Neutral by design (rule-table comment). The relationship worth
  stating: the **spec-119 AI earnings read produces directional reads for exactly this filing class** and
  contributed 439 directional GuidanceChange signals (433 Positive, 6 Negative — dimension-2 `ai-read`
  bucket) in the same store. The designed remedy for this limitation already exists and runs; its coverage
  (confidence-gated, one read per filing, model-dependent) is simply much smaller than the item-title
  stream it sits beside.
- **ExecutiveHire, the `appointment of certain officers` phrase** (2,888 of 3,016 Neutral — 95.76 % of
  the type): **data limitation** — 8-K item 5.02 covers both departures and appointments and the item
  title cannot tell which (rule-table comment). Reading the 8-K body (or an AI read) could recover
  valence; that is deferred, not overlooked.
- **CapitalRaise Neutral phrases** (511 of 859 Neutral — 59.49 % of the type): **design decision** — a
  convertible note or credit facility is genuinely ambiguous at the keyword level (accretive vs death
  spiral; rule-table comment). Note the type is *majority directional-capable*: 306 Negative + 42 Positive
  signals show the directional phrases fire when the text supports them.
- **Hiring / patents / trademarks** (0 signals each): designed Neutral pending slice-B surge detection
  (specs 103/127/130), but with their collectors not enabled there is no accrued data to classify. Their
  neutrality is a design fact with no store footprint.

## 6. Recommendation

**What the audit justifies — the spec §4 fix, implemented in this slice.** The one thing the audit shows
is unambiguously missing *and* cheap to persist is the Form 4 classification branch. Implemented, forward
only:

- `SecForm4Filing` gains a trailing `ClassificationReason`, computed in `HttpSecForm4Reader.Classify` as
  one of five stable tokens (consts on `SecForm4ClassificationReasons`): `plan-10b5-1` (checked first —
  a plan skips every transaction and would otherwise be indistinguishable from the no-discretionary
  bucket), `discretionary-buy`, `discretionary-sale`, `mixed-buy-sell`,
  `no-discretionary-transactions`.
- `SecForm4Collector.MapToEvidence` persists it as `metadata["insiderClassificationReason"]` — **additive
  metadata only**, never in Title/RawText. Evidence identity is the normalized title+body hash alone
  (spec 145), so `ContentHash`, the evidence id and every `AddIfNewAsync` decision are provably unmoved
  (asserted by test through the real mapper). No backfill, no rewrite of accrued files (AD-1/AD-8), no
  scoring change, no fingerprint input, no `RuleSetVersion` or formula bump; the spec-148 pins stand.
- The audit script's dimension 3 is written against this key, so the same audit becomes answerable for
  evidence collected from now on.

**The honest conclusion: the extractor is not where the direction went — the collector mix is the
constraint.** The extraction layer is fully accounted for: 100.00 % of 49,969 signals carry a persisted
rule-level reason, the "Neutral by default" and "unknown reason" buckets are both empty, and every
Neutral rule traces to a cited design decision or a stated data limitation of the source itself. The
87.63 % neutrality (of 49,969 signals) is the arithmetic of what Radar collects: the three
highest-volume streams are structurally directionless by design — third-party news (31.33 % of the
store, Neutral under the accepted thesis), 13G/amendment ownership filings (25.4 %, Neutral so passive
stakes never misfire bullish), and routine insider filings (14.1 % Neutral). Making any of these
directional by fiat would be the failure mode the constraints forbid ("do not fix neutrality by making
uncertain things directional"); collecting *sources that carry valence* — or funding the cited deferrals
(13G % -of-class/amendment deltas per spec 100; 8-K body/AI reads for items 2.02/5.02; slice-B surge
detection) — is where directional evidence would actually come from. Per
the collector-expansion discipline, any such addition must be efficacy-motivated and is a next spec, not
this one. Under AD-16, news stays Neutral: it is the outcome the stealth thesis predicts, and no
recommendation to make it directional is made.
