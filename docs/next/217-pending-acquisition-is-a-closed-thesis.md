# Task: A pending acquisition is a closed thesis — recognise it from the 8-K, say so on the report, and keep it out of the forward efficacy series

## Overview

On 2026-08-10 Blackstone-owned Safe Harbor Marinas agreed to buy MarineMax (HZO) for $53.00 a share in
cash (~$1.5B). The stock gapped +46.1% (adjusted close $35.68 → $52.12) and has sat under the bid since.
Reconstructed from the store on 2026-09-08 (`data/evidence/raw`, `data/reports/weekly`, `data/prices/hzo.json`):

- Radar collected the 8-K (items 1.01, 7.01, 9.01 — "Entry into a Material Definitive Agreement") and
  ~50 articles THAT DAY. The keyword extractor's rule for "material definitive agreement"
  (`KeywordSignalExtractor.cs` L131) minted `StrategicPartnership (Positive)`, strength 4; trajectory rose
  56 → 62; the 2026-08-10 report labelled MarineMax **Thesis improving** at rank 43 — a $1.5B all-cash sale
  of the whole company, read as a partnership. By 2026-08-14 it was `Ignore` again (the improving label is
  a delta against the prior snapshot). No judgment was ever made (rank 43 is never a judge candidate),
  and news at the time reached scoring only as Neutral attention volume (pre-194).
- The filing reader analyses ONLY earnings 8-Ks (`DirectionalFilingSignalSource.EarningsItemCode = "2.02"`),
  so the merger terms were never read. Nothing predictive existed before the Reuters "sources say" exclusive
  on the morning of the 10th — Radar could not have front-run a private negotiation and is not meant to.
- **The efficacy series is now contaminated.** The +46% day sits inside the 21-day forward window of every
  MarineMax score from 2026-07-20 to 2026-08-07 (all `Ignore`, rank ~46), so every arm's rank correlation
  carries a large "miss" that no trajectory read could have earned; and from 08-10 onward the price is
  pinned at the bid — zero variance — while Radar keeps scoring the company and keeps it in the
  equal-weight peer mean of `excess-vs-universe-v1` (MarineMax is a `benchmark-universe-v1` member), which
  biases every other company's excess return. Over the period 2026-06-30 → 2026-09-08 MarineMax is the
  best price performer in the universe (+42.5%); it is a takeover, not a thesis.

**Measured:** the store holds **178** 8-K filings carrying item 1.01 — a title-only rule fires on credit
agreements, leases and supply contracts alike, so recognition must read the filing. MarineMax is the ONLY
announced takeover in the universe today (title scan of every collected article for takeover shapes).

This spec adds ONE company state — `PendingAcquisition` — recognised deterministically from the filing,
rendered honestly, and excluded from the forward efficacy series with the exclusion counted. It adds NO new
report label (the six allowed labels are unchanged — AD-9 / the philosophy file): the state is a banner and
a rationale, and the label it forces is `Ignore`.

## Assignment

Worktree: any. Dependencies: main at `2058fcb` or later (spec 216 may land before or after — no overlap).
Use `run-next.ps1 -Spec 217`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Recognition: a deterministic scan of the item 1.01 filing — fail-closed, no AI

- New `AcquisitionAgreementScan` (`acqscan-v1`, Application, pure) over the 8-K's primary document and its
  EX-99.1 (fetched through the existing `SecEdgarUrls`/`SecHttpFetch` seam under the global pacer — one
  bounded fetch per item-1.01 filing, cached like the earnings read). It recognises a pending acquisition
  of THE COMPANY only when BOTH hold within the text: (a) an "Agreement and Plan of Merger" (or "merger
  agreement" / "definitive agreement … to be acquired") in which the SUBJECT company is the target — the
  company's own name (or alias) within the same sentence as "acquired by" / "to be acquired" / "merger
  sub" / "will merge with and into"; AND (b) a stated per-share cash and/or stock consideration
  ("$53.00 per share in cash", "each share … converted into the right to receive"). Either missing ⇒ no
  state, counted (`item101NotRecognised`, split by which leg failed). A filing where the company is the
  ACQUIRER (Stereotaxis buying Robocath) fails leg (a) by construction and is counted separately
  (`item101CompanyIsAcquirer`).
- Output, persisted append-only under `data/acquisitions/{companyId}/{accession}.json` (typed durable
  write): announcement date (filing date), acquirer name, consideration (per-share amount + currency +
  kind cash/stock/mixed, verbatim quote, verified verbatim in the text — the spec-215/216 rule), the
  evidence id, the scan version, and `verification: Verbatim`. Content-derived id.
- **Live distribution BEFORE it ships:** a read-only harness runs `acqscan-v1` over all 178 item-1.01
  filings in the store (paced live fetch; env-gated like `NewsRecencyWindowLiveMeasurementTests`) and the
  PR body reports: recognised / not-recognised by leg / company-is-acquirer / fetch-failed. Expected: 1
  recognised (HZO) and 177 not. **Any second recognition is investigated by hand and named in the PR
  body** — a false positive here closes a live thesis, which is worse than missing one.

## 2. The state, and what it does to scoring, labels and the report

- `CompanyStatus` gains `PendingAcquisition` (Domain; today `Active / Delisted / WatchOnly /
  Unresolved`). It is NOT set in `data/companies.json` by hand: the Worker derives it at run time from the
  acquisitions store (a recognised, unexpired agreement ⇒ pending) and stamps it on every score snapshot
  the company receives from the announcement date onward (`companyStatusAtScoring`, nullable pre-217).
  The seed file stays the curated source of the OTHER statuses; a maintainer moves a closed deal to
  `Delisted` when it delists (a conscious step, journaled) — automation of that is out of scope.
- **Scoring continues** (append-only; the universe is unchanged; nothing is regenerated) — but the
  snapshot carries the status, and:
- **The action policy** gains rule 0, ahead of `Needs more evidence`: a `PendingAcquisition` company is
  labelled `Ignore` with the rationale `Acquisition pending: <acquirer> at <consideration> announced
  <date>; thesis closed — trajectory and opportunity are not the driver of this price.` The label is one of
  the six; the state is the rationale. `Thesis improving` / `deteriorating` cannot fire for it (rule 0 is
  first), so the 2026-08-10 shape cannot recur. Policy `weekly-report-action-v5 → v6`.
- **The report** renders the entry with a one-line banner under the label (`⏸ Acquisition pending — …`),
  lists it in a new `## Acquisitions pending` section (company, acquirer, consideration, announced, days
  pending) placed after `## Ignore / Low signal`, and drops it from every strategy's ranked table into a
  one-line footer per strategy (`1 company excluded: pending acquisition — see Acquisitions pending`), so a
  pinned price never sits inside a ranking a reader compares. Counted, never silent.
- **The keyword rule** for "material definitive agreement" is NOT removed (it is a scoring input; changing
  it is `RuleSetVersion` territory) — but when `acqscan-v1` recognises the filing, the extractor's
  `StrategicPartnership` from THAT evidence is superseded at scoring assembly by the same mechanism the
  judgment signal uses (`news-judgment-supersede-v1` precedent): a `CorporateAction (Neutral)` signal from
  the same evidence, strength 0 contribution, reason naming the acquisition — counted per company. This is
  a scoring-assembly rule, so it is hashed: a new `acq=acqscan-v1;supersede=acq-supersede-v1` field in the
  `rules=`-adjacent descriptor (the coder places it beside the extractor rule-set identity), moving BOTH
  AI-ON and AI-OFF pins once (not AI-gated). Operator step per spec 214 §5.

## 3. Efficacy: an outcome no strategy can earn is excluded and counted — `observation-eligibility-v2`

Today's exclusion axes are `WithoutForwardPrice`, `PartialForwardWindow`, `BenchmarkUnavailable`,
`NotInBenchmarkUniverse`. Add **`CorporateActionInWindow`**:

- An observation at as-of D for company C is excluded when a recognised acquisition of C was announced in
  `(D − 0, D + h]` — i.e. the announcement falls inside the forward window (the jump is not attributable to
  the score at D) — OR when D is on/after the announcement date (the price is pinned; the outcome is
  the deal, not the business). Excluded observations are COUNTED on their own column in
  `strategy-leaderboard.{md,csv}` and `strategy-paired-comparison.{md,csv}`, per strategy, beside the
  existing exclusion columns.
- **Benchmark:** a pinned member must leave the peer mean from the announcement date, or every other
  company's excess return is biased. That is a benchmark-rule change: declare **`excess-vs-universe-v2`**
  (same frozen `benchmark-universe-v1` membership; a member under `PendingAcquisition` is excluded from the
  equal-weight peer mean from its announcement date onward, counted on the coverage line), stamped on the
  artifacts, with the v1 series preserved as `strategy-leaderboard-excess-v1.{md,csv}` exactly as the raw
  series was preserved when v1 replaced it (spec 183 precedent). **Declared prospectively, before any
  eligible claim date exists** — the precommitted 2026-09-29 AD-15 boundary is UNCHANGED, and the paired
  comparison's claim interval starts after it, so no outcome that has entered the claim family is
  re-scored. The PR body states this explicitly.
- Rule identity `observation-eligibility-v2` is stamped on both artifacts and on the Lead-call evidence
  lines the weekly report prints (the reviewer checks the operating-call table cites the new version).
- **Report the effect, not just the rule:** the PR body carries the leaderboard BEFORE and AFTER for the
  same store (the artifact is a read-side recomputation; nothing accrued is rewritten): per strategy,
  in-sample/oos ρ and CI, observations excluded as `CorporateActionInWindow` (expect ~14 dates × 1
  company per arm), and the Lead vs comparators ordering. If the ordering changes, say so plainly; it is a
  finding about how much one takeover was worth in a 13-date sample, not a result.

## 4. Docs

- `docs/reading-radar-output.md`: what "Acquisition pending" means, that the label is `Ignore` by rule 0,
  why the company leaves the rankings and the peer mean, and that the maintainer retires it on delisting.
- `docs/strategy-lifecycle.md` header: one sentence — a `PendingAcquisition` recognition is journaled as an
  event (`corporate-action`) with the accession, and so is the later `Delisted` move.
- `docs/architecture-history.md`: spec-217 bullet with the MarineMax reconstruction and the before/after
  leaderboard; amend the spec-183 benchmark bullet IN PLACE for v2; amend the spec-140 leaderboard bullet
  for the new exclusion axis.
- CLAUDE.md standing facts: amend the universe bullet in place — "pooled efficacy is benchmark-adjusted
  (`excess-vs-universe-v2` since spec 217: pending-acquisition members leave the peer mean)".

## Non-goals

- Predicting takeovers, or any M&A-arbitrage reading; reading item 1.01 filings with AI; retiring
  companies automatically on delisting; reading 13D activism as a takeover precursor (a separate,
  evidence-led question — Levin Capital's "value-maximising sale" letter surfaced only on 08-11).
- Changing the six labels, the keyword rule table, or any weight.

## Acceptance criteria

- [ ] `acqscan-v1` recognises a pending acquisition only with both legs (target + consideration) verbatim
      in the filing text; acquirer-side filings and credit/lease agreements are counted, not recognised;
      the live harness over all item-1.01 filings in the store is in the PR body with exactly the
      recognised set named (expected HZO alone; any other investigated by hand).
- [ ] `data/acquisitions/{companyId}/{accession}.json` append-only, verified verbatim, typed durable write.
- [ ] `CompanyStatus.PendingAcquisition` derived at run time, stamped on snapshots from the announcement
      date; seed file untouched; scoring continues.
- [ ] Policy rule 0 (`weekly-report-action-v6`) forces `Ignore` with the acquisition rationale; the improving
      / deteriorating rules cannot fire; banner, `## Acquisitions pending` section, per-strategy footer;
      the 2026-08-10 shape is pinned as a regression test (an 8-K 1.01 merger ⇒ Ignore, not Thesis
      improving).
- [ ] `CorporateAction (Neutral)` supersedes the extractor's `StrategicPartnership` from a recognised
      filing at scoring assembly, counted per company; the `acq=` field is hashed; both pin sides move
      once and are asserted; operator step in the PR body.
- [ ] `CorporateActionInWindow` exclusion on both efficacy artifacts, counted per strategy;
      `excess-vs-universe-v2` declared prospectively with v1 preserved; `observation-eligibility-v2`
      stamped; 2026-09-29 unchanged; before/after leaderboard in the PR body with the ordering change
      stated if any.
- [ ] Operator guide, lifecycle header, history (new + 183/140 in-place), CLAUDE.md bullet amended.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.
