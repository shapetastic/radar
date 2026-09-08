# Task: A reference value must be PRIOR, DURABLE and VERIFIED before it can support a judgment — the five post-merge findings on spec 215

## Overview

Spec 215 merged (`9823e7c`) and the post-merge review found five defects, all confirmed against the code
on 2026-09-08. `Radar:Ai:ReportedMetrics:Enabled` was set to `false` in the default profile the same day
(`766c925`) so the ledger stays empty until this spec lands — the flag is not a fingerprint input, and a
disabled ledger is indistinguishable to the judge from an empty one. `data/reported-metrics/` holds zero
files, so nothing is polluted. Re-enable is the last acceptance line of this spec.

1. **[P1] A current value can validate itself as a "prior".** `CollectionPass` writes the newest filing's
   metrics to the ledger BEFORE the judge runs in the same pass, and `ReferenceValueProjector` selects every
   ledger row whose metric a supplied statement names, ordered by filing date, with NO temporal check
   (`ReferenceValueProjector.cs` L139–140). The validator then requires only that a cited ReferenceId
   exists in the projected set. So on the very first read, a news fact "backlog $2.5B" is handed the same
   $2.5B from the same release as a reference and can grade `ReferenceSupported` on its own value; and a
   NEWER filing can be "compared" against an OLDER news fact, backwards in time.
2. **[P1] A failed ledger write is never retried.** `DirectionalFilingSignalSource` stamps the analyzed-filing
   cache record with `reportedMetricsPolicy = reported-metrics-v1` at analysis time (L346/L378); the ledger
   write happens later, in `CollectionPass` (L472, `WriteReportedMetricsAsync`). If that write returns
   `NotPersisted`, or the company cannot be resolved, the cache already says the extraction is done — the
   next run is a hit and the metric is lost permanently. It is COUNTED on the aggregated line, but counting a
   loss is not the same as not losing it.
3. **[P1] "Verbatim verified" verifies less than it claims.** `ReportedMetricVerifier` (L82–105): the
   metric label is trusted — nothing checks the quote NAMES that metric (a cash figure labelled Backlog
   passes); the current period only has to be non-blank (an invented period passes); `quote.Contains(value)`
   is a substring test, so "384" verifies inside "384.0" (a numeric fragment passes). Metric and period
   drive projection matching, so all three reach the judge as facts.
4. **[P2] A partial ledger file can become permanent.** `FileReportedMetricStore.WriteIfNewAsync` writes
   with `FileMode.CreateNew` straight to the final path; an I/O failure after creation leaves a partial file,
   and `catch (IOException) when (File.Exists(path))` (L116) then reports it as `AlreadyOnDisk` — a
   concurrent-writer success — so every later run treats the fragment as the record.
5. **[P2] The policy-version migration cannot work.** A re-analysis under `reported-metrics-v2` would try to
   write the same `{companyId}/{accession}.json`, hit `CreateNew` → `AlreadyOnDisk`, and the v1 file would
   stand forever; record identity and the judgment's reference hashing exclude the policy. Not broken
   today (only v1 exists), but the documented heal-forward path is a promise the code cannot keep.

None of this touches a score, weight, formula or pin: the ledger is not a scoring input, and the judge's
cohort key already carries `references=reference-projection-v1`; §1 bumps it to `v2` (a projection whose
selection rule changed IS a different input to the model), which moves the AI-ON pins once more — the
operator step (spec 214 §5) is performed once after merge, and the AI-OFF pins must not move.

## Assignment

Worktree: any. Dependencies: main at `766c925` or later. Use `run-next.ps1 -Spec 216`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. A reference is PRIOR to the fact it supports — `reference-projection-v1 → v2`

- Thread the family's `EarliestObservedAtUtc` (already on `FactFamilyRecord`) into `NewsJudgmentInputFamily`
  as `ObservedAtUtc` (additive, non-null — every family has one).
- `ReferenceValueProjector.Select` takes the supplied families and selects, per named metric, only ledger
  records whose `FilingDateUtc` is STRICTLY EARLIER than the EARLIEST `ObservedAtUtc` among the families
  that name that metric — a reference must have been published before the news that quotes the level.
  Records from the same run, the same filing date, or later are excluded and COUNTED
  (`referencesExcludedNotPrior`). The same accession as any evidence the judged families cite is excluded
  on the same axis (a release cannot be its own prior).
- The validator enforces the same rule on the OUTPUT: every cited `TrajectoryReferenceId` /
  finding `ReferenceId` must resolve to a projected record whose filing date precedes the earliest
  `ObservedAtUtc` of the facts it is cited beside; a violation is dropped with a named reason
  (`reference-not-prior`) and the basis falls back to what the remaining citations support.
- Pin with tests: same-run value → not projected, basis stays `LevelOnly`; a January reference beside a
  September fact → projected, `ReferenceSupported`; a September filing beside an August fact → excluded and
  counted.

## 2. The cache is stamped only when the ledger ACKNOWLEDGES the write — an outbox, not a hope

- `DirectionalFilingSignalSource` no longer stamps `reportedMetricsPolicy = reported-metrics-v1` at analysis
  time. It stamps **`reported-metrics-v1;unacknowledged`** — a bounded automatic MISS for extraction on the
  next run (the direction/confidence/rationale of the cached read are still a HIT; only the metric
  extraction re-runs, under `MaxFilingsPerRun` and the 429 breaker like any miss). Null stays the pre-215
  hit exactly as today.
- `CollectionPass`, after `WriteIfNewAsync` returns `Succeeded` OR `AlreadyOnDisk` (a complete file — see §4)
  for EVERY record of that accession, calls `IAnalyzedFilingCache.AcknowledgeReportedMetricsAsync(accession)`
  which rewrites the stamp to `reported-metrics-v1`. `NotPersisted` or no-resolved-company leaves it
  unacknowledged; the aggregated log line gains `unacknowledged {n}` and `acknowledged {n}`.
- Retry is bounded and visible: a filing that stays unacknowledged for ≥ 3 runs is logged once per company
  as a durable defect (not silently retried forever) — counted, named, never dropped.
- Test: a `NotPersisted` write leaves the cache unacknowledged and the next pass re-extracts; a `Succeeded`
  write acknowledges and the next pass is a hit.

## 3. Verification verifies the metric, the period and the WHOLE token

`ReportedMetricVerifier` (still `reported-metrics-v1` → the verification rule is part of the policy token,
so this is **`reported-metrics-v2`**; there are no v1 files to migrate, and §5 makes future migrations real):

- **Metric named in the quote:** a closed synonym table per `ReportedMetric` (Backlog: backlog / order
  backlog / backlog of; Revenue: revenue / revenues / net sales / sales; NetIncome: net income / net
  earnings; DilutedEps: diluted earnings per share / diluted EPS / per diluted share; GrossMargin: gross
  margin; OperatingIncome: operating income / income from operations; CashAndInvestments: cash / cash and
  cash equivalents / cash and investments; TotalDebt: debt / borrowings; FreeCashFlow: free cash flow) —
  the quote must contain a synonym of the LABELLED metric, whole-word, case-insensitive. Failing entries
  are dropped and counted (`metricsDroppedMetricNotInQuote`). The table is policy identity.
- **Period in the quote:** `Period` must appear verbatim in the quote, exactly as `PriorPeriod` already must
  after the Copilot fix (`2815e68`) — "as the release words it" is satisfied by requiring the release's own
  words. Dropped and counted (`metricsDroppedPeriodNotInQuote`).
- **Whole numeric token:** `value` and `priorValue` must match as complete tokens — bounded by
  non-digit/non-separator characters — so "384" does NOT verify inside "384.0" or "3,384"; "384.0" verifies
  only against "384.0". Same for the unit token. Dropped and counted (`metricsDroppedFragment`).
- Quote-in-body stays as is. Tests pin every drop class with the exact reviewer examples: a cash figure
  labelled Backlog; an invented period; "384" against "384.0".

## 4. The store writes atomically and never mistakes a fragment for a record

`FileReportedMetricStore.WriteIfNewAsync`: serialize to `{path}.tmp-{guid}` in the same directory, flush,
then `File.Move(tmp, path, overwrite: false)` — the move is the no-overwrite commit point. On any failure
before the move, delete the temp file and return `NotPersisted`. Separate the two collision cases: a move
that fails because `path` now exists is `AlreadyOnDisk` ONLY if that file parses as a complete record
(read it back); an unparseable existing file is reported `NotPersisted` with reason `corrupt-existing` and
logged once per path — never `AlreadyOnDisk`. Same treatment for the sibling `GracefulFileWriter` users is
NOT in scope (spec 201 owns that seam) — note the precedent in the PR body if the pattern is reusable.

## 5. Policy version is part of the record's identity and path

- Ledger path becomes `data/reported-metrics/{companyId}/{accession}.{policy}.json`
  (`…/0001104659-26-104735.reported-metrics-v2.json`); the record carries `policy` and its content-derived
  id includes it. A re-analysis under a later policy writes a NEW file beside the old one; the projector
  reads ONLY the current policy's files and counts superseded-policy files it skips
  (`referencesSkippedSupersededPolicy`). Nothing is deleted (append-only).
- The judgment record persists the policy token of the references it was handed (already persists ids),
  so a judgment can be reconciled to the policy that produced its references.
- There are no v1 files (measured 2026-09-08: zero) — no migration is needed for this bump; the mechanism
  is proven by a test that writes v1 and v2 for one accession and shows the projector picks v2 only.

## 6. Live verification and re-enable

- PR body: the unit-level distribution of the new verifier over a fixture set is not a live distribution;
  the LIVE one is owed from the first post-merge run with the flag ON: records written / dropped per class
  (unverified, metric-not-in-quote, period-not-in-quote, fragment, unrecognised) / acknowledged /
  unacknowledged; judgments handed ≥ 1 reference; `ReferenceSupported` count; `referencesExcludedNotPrior`.
  **A verifier that drops > 50% of what the model returns is a prompt/table defect to investigate, not a
  finding** (spec 215 §3's bound, kept).
- The LAST change in the PR flips `Radar:Ai:ReportedMetrics:Enabled` back to `true` in `default.json` and
  amends its `_comment` in place (drop the DISABLED sentence, cite this spec). If the implementer is not
  confident all of §1–§4 hold, the flag stays `false` and the PR body says so — a disabled ledger is
  honest; a self-validating one is not.

## Non-goals

- Widening the metric enum or reading 10-Q/10-K bodies (spec 215 non-goal, unchanged).
- Backfilling; touching accrued judgments or signals; any scoring input.

## Acceptance criteria

- [ ] `reference-projection-v2`: a reference is projected only when its filing date strictly precedes the
      earliest observation of the facts naming that metric, never from the same accession the facts cite;
      exclusions counted; validator drops `reference-not-prior` citations; the three pinned cases pass.
- [ ] Cache stamp `reported-metrics-vN;unacknowledged` at analysis, acknowledged only after every record of
      the accession is `Succeeded`/`AlreadyOnDisk`; unacknowledged filings re-extract (bounded) and are
      logged once per company after 3 runs; null stays a hit.
- [ ] `reported-metrics-v2` verifier: metric synonym in quote, period in quote, whole-token value/unit/prior
      value; each drop class counted; the reviewer's three examples are negative tests.
- [ ] Atomic temp-file + no-overwrite move; fragment never reported `AlreadyOnDisk`; `corrupt-existing`
      counted and logged once.
- [ ] Policy in the ledger path and record id; projector reads the current policy only and counts skipped
      superseded files; v1/v2 side-by-side test.
- [ ] AI-ON pins moved once (projection v2) and asserted by `ScoringConfigFingerprintTests`; AI-OFF proven
      unchanged; operator step stated in the PR body.
- [ ] `Enabled` back to `true` with the comment amended in place — or left `false` with the reason stated.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.
