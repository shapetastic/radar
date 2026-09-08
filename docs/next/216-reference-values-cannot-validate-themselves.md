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

None of this touches a score, weight or formula. The AI-ON pins move once: §1 bumps `references=` to v2 in
`news=`, and §5 adds the `rm=` field to the directional-filing `ai=` descriptor. The AI-OFF pins do NOT move
(the `ai=` descriptor folds only with an AI filing read) and that non-move is an asserted deliverable. The
operator step (spec 214 §5) is performed once after merge; the boundary is 214–216.

## Assignment

Worktree: any. Dependencies: main at `766c925` or later. Use `run-next.ps1 -Spec 216`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. A reference is PRIOR to the fact it supports — a STRUCTURAL rule, not a timestamp — `reference-projection-v1 → v2`

A timestamp test is not enough (review round 1): a release routinely precedes its own coverage by hours or
a day, so "filing date strictly earlier than the article" still admits the current value. And a
same-accession exclusion is generally impossible — stage-1 news facts carry observation ids and excerpts,
never the SEC accession behind a company release. So the rule is structural, on the LEDGER's own order:

- **The newest accession for a metric is never a reference.** Per (company, metric), ledger records are
  ordered by filing date, then — deterministically, for same-day filings — by accession number (ordinal
  string order, which is chronological within a filer's sequence), then by record id; the record from the
  LATEST accession under that order is the "current" value and is EXCLUDED from projection. A record becomes eligible as a reference only once a LATER accession has reported the same
  metric. A metric reported once is therefore never a reference — that is the honest state of a young
  ledger, counted as `referencesExcludedNewest`.
- **A complete prior pair from the newest filing IS projectable, separately.** When the newest record
  carries a verified `PriorValue` + `PriorPeriod` (the release itself stated the comparison), that pair is
  projected as a reference of kind `StatedPrior` with its own ReferenceId — it is the company's own prior
  statement, and it is by construction not the current value. The judge is told which kind it sees.
- Thread the family's `EarliestObservedAtUtc` (already on `FactFamilyRecord`) into `NewsJudgmentInputFamily`
  as `ObservedAtUtc` and PERSIST it on the judgment record (§5) — not as the eligibility test, but so the
  reconciliation "which reference could this fact have seen" is answerable from the record. A secondary
  guard still applies on top of the structural rule: a reference whose filing date is LATER than the
  fact's `ObservedAtUtc` is excluded and counted (`referencesExcludedLaterThanFact`) — a newer filing may
  never be compared backwards against older news.
- The validator enforces both on the OUTPUT with the SAME strictness FactIds already have
  (`NewsJudgmentValidator` ~L248): an unprojected `TrajectoryReferenceId` FAILS THE WHOLE JUDGMENT
  (validation failure, named reason `reference-not-projected`, counted) — never a silent fallback to a
  weaker basis, because a hallucinated trajectory reference is exactly the evidence-gate failure the
  validator exists to catch (review round 2 corrected the earlier "drop and fall back" wording). An
  unprojected reference on a FINDING drops that finding only, as an unresolved finding FactId does today.
- Pin with tests: (a) one accession only → nothing projected, basis `LevelOnly`; (b) **the article appears
  the DAY AFTER the filing** whose value it quotes, with no earlier accession → nothing projected (the
  timestamp rule alone would have admitted it — this is the case the round-1 review named); (c) two
  accessions → only the older one projects; (d) the newest filing states a prior pair → `StatedPrior`
  projects, basis `ReferenceSupported`; (e) a filing dated after the fact's observation → excluded and
  counted.

## 2. The outbox CONTAINS the extraction — the cache replays it until the ledger acknowledges

Marking the cache "unacknowledged" and re-analyzing would re-fetch from SEC and re-run the model, which can
return a DIFFERENT direction — contradicting "direction stays a hit" (review round 1). So the payload is
persisted, not re-derived:

- `AnalyzedFilingRecord` gains `reportedMetricsExtraction` (nullable): the VERIFIED extraction exactly as
  returned by the verifier (records + per-class drop counts), plus `reportedMetricsLedgerState`
  (`Acknowledged | Pending`) and `reportedMetricsLedgerAttempts` (int). Written at analysis time with
  state `Pending`, attempts 0. The direction/confidence/rationale of the read are never re-derived.
- On every run, for each cached record with state `Pending`, `CollectionPass` REPLAYS the persisted
  extraction into `IReportedMetricStore.WriteIfNewAsync(policy, …)` — no fetch, no model call — and on
  `Succeeded`/`AlreadyOnDisk` for every record of the accession calls
  `IAnalyzedFilingCache.AcknowledgeReportedMetricsAsync(accession)` (state → `Acknowledged`). On
  `NotPersisted` or no-resolved-company it increments `reportedMetricsLedgerAttempts` and leaves the state
  `Pending`. The aggregated line gains `pending {n} / acknowledged {n} / replayed {n}`.
- The three-run warning reads the PERSISTED attempt count (it survives process restarts): a record with
  `attempts ≥ 3` still `Pending` is logged once per company per run as a durable defect — counted, named,
  never dropped, never retried silently forever (the replay continues, bounded only by the count of pending
  records, which is cheap: no I/O beyond the ledger write).
- Null `reportedMetricsPolicy` (pre-215) stays the hit it is today. A record WITH a policy but NO
  extraction payload (the 215-era shape) is NOT acknowledged as empty — such an entry may have produced
  metrics that were never persisted, and treating it as an empty extraction would silently lose them
  (review round 2). Measured 2026-09-08: the live cache holds ZERO such records (the ledger was disabled
  before any post-215 run). The implementer ASSERTS that precondition over the store at implementation
  time (count reported in the PR body) and the code FAILS CLOSED if one ever appears: the record is
  counted on its own axis (`legacyPolicyWithoutPayload`), logged once per accession as a durable defect,
  left `Pending` with attempts frozen, and never acknowledged — the only honest recovery is a conscious
  re-analysis under the current policy, which is a maintainer step, not an automatic one.
- **Missing persisted state reads as `Pending` with attempts 0** (a v7-era record written without the
  fields, or a partially migrated one) — never as `Acknowledged`.
- Test: a `NotPersisted` write leaves state `Pending` with attempts 1 and the next pass replays the SAME
  payload (asserted byte-identical) without invoking the analyzer; a `Succeeded` write acknowledges; a
  persisted attempts count of 3 triggers exactly one warning.

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
- **Association, not co-presence (review round 1):** "cash was $100m and backlog was $2.5bn" must not let a
  Backlog entry with value 100 pass. The metric synonym, the value (with unit) and the period must all lie
  within ONE bounded fragment of the quote. Fragment boundaries are, in order of preference: a newline, a
  `|`, a `;`, a run of two-or-more spaces (table cells), and a SENTENCE-ENDING period — defined as `.`
  followed by whitespace-then-uppercase or end-of-text, so a decimal point inside `384.0`, `$0.79` or
  `2.5B` is NEVER a boundary (review round 2: splitting on every `.` breaks the very values being
  verified). Within that fragment the value must be the NEAREST numeric token to the metric synonym (no
  other verified-shape number between them). Failing entries are dropped and counted
  (`metricsDroppedNotAssociated`). Pinned with the reviewer's example, a two-row table where each row
  carries its own metric, and a sentence containing `$0.79` and `384.0` that must stay one fragment.
- **Negative synonym tests** are mandatory, one per metric at minimum: "cash flow" must NOT verify
  `CashAndInvestments` (it is `FreeCashFlow`'s territory), "cost of sales" must NOT verify `Revenue`,
  "gross profit" must NOT verify `GrossMargin`, "net debt" must NOT verify `TotalDebt` — whole-phrase
  matching with the longest synonym winning, never a substring of a longer phrase.
- Quote-in-body stays as is. Tests pin every drop class
## 4. The store writes atomically and never mistakes a fragment for a record

`FileReportedMetricStore.WriteIfNewAsync`: serialize to `{path}.tmp-{guid}` in the same directory, flush,
then `File.Move(tmp, path, overwrite: false)` — the move is the no-overwrite commit point. On any failure
before the move, delete the temp file and return `NotPersisted`. Separate the two collision cases: a move
that fails because `path` now exists is `AlreadyOnDisk` ONLY if that file parses as a complete record
(read it back); an unparseable existing file is reported `NotPersisted` with reason `corrupt-existing` and
logged once per path — never `AlreadyOnDisk`. Same treatment for the sibling `GracefulFileWriter` users is
NOT in scope (spec 201 owns that seam) — note the precedent in the PR body if the pattern is reusable.

## 5. Persistence and identity, pinned down

- **Policy is an explicit store-write argument.** `IReportedMetricStore.WriteIfNewAsync(string policy, …)`
  — an EMPTY verified metric list has no record from which the policy could be inferred, yet the empty
  outcome must still be acknowledged under a policy. The ledger path becomes
  `data/reported-metrics/{companyId}/{accession}.{policy}.json`
  (`…/0001104659-26-104735.reported-metrics-v2.json`), the record carries `policy`, and its content-derived
  id includes it. A re-analysis under a later policy writes a NEW file beside the old one; the projector
  reads ONLY the current policy's files and counts superseded-policy files it skips
  (`referencesSkippedSupersededPolicy`). Nothing is deleted (append-only). There are no v1 files (measured
  2026-09-08: zero) — the mechanism is proven by a test that writes v1 and v2 for one accession and shows
  the projector picks v2 only.
- **Durable judgment record `news-judgment-v6 → v7`** (`NewsJudgmentRecord.CurrentSchemaVersion`, L269):
  persists, per consumed family, `ObservedAtUtc` (§1); per judgment, the reference POLICY token and the
  reference KIND (`Prior` / `StatedPrior`) of every projected and every cited reference; and the two new
  exclusion counts. v6 records stay readable (new fields null = not recorded).
- **Enablement and verification policy enter scoring identity (review round 1 — P1; placement corrected
  in round 2).** Enabling metric extraction changes the FILING-ANALYSIS prompt itself — the model is asked
  for more — even when the news judgment is disabled, so the token belongs where the filing read's other
  hashed-by-value inputs live: the spec-106/160 DIRECTIONAL-FILING `ai=` descriptor (`str/nov/minconf/
  model/cmpscan/cmpcap`), which gains a trailing `rm=disabled` or `rm=reported-metrics-v2` field (the
  VERIFICATION policy token, since it decides which values exist). `news=` keeps ONLY the projection
  identity (`references=reference-projection-v2`). Consequences, stated so the pins can be reconciled:
  the `ai=` descriptor folds only when the AI filing read is registered, so the **AI-ON pins move** (this
  slice: projection v2 + `rm=disabled`; later, re-enabling flips it to `rm=reported-metrics-v2` and moves
  them again by construction — the regime with references is a different series) and the **AI-OFF pins do
  NOT move** (no AI read ⇒ no extraction to hash; an AI-OFF move here would be scope leakage — the spec-197
  proof pattern, reversing this spec's round-1 wording). **Identity state matrix, pinned by mutation
  tests in `ScoringConfigFingerprintTests`:** (i) judgment OFF, `ReportedMetrics.Enabled` toggled ⇒ the
  AI-ON descriptor CHANGES (the case the round-2 review named — extraction changes the filing prompt even
  with no judge); (ii) judgment ON, flag toggled ⇒ changes; (iii) AI read OFF, flag toggled ⇒ descriptor
  UNCHANGED (nothing to extract); (iv) policy token v2 → a fake v3 with the flag on ⇒ changes.
- **Regime boundary:** the documented boundary "214+215" becomes **"214–216"** everywhere it is written
  (CLAUDE.md, `docs/architecture-history.md`, the operator guide) — one discontinuity spanning the three
  slices, with the **precommitted 2026-09-29 claim date unchanged** (moving it after outcomes exist would
  invalidate the claim family; the boundary describes comparability, not the claim).

## 6. Live verification and re-enable

- PR body: the unit-level distribution of the new verifier over a fixture set is not a live distribution;
  the LIVE one is owed from the first post-merge run with the flag ON: records written / dropped per class
  (unverified, metric-not-in-quote, period-not-in-quote, fragment, unrecognised) / acknowledged /
  unacknowledged; judgments handed ≥ 1 reference (by kind: Prior / StatedPrior); `ReferenceSupported` count; `referencesExcludedNewest` / `referencesExcludedLaterThanFact`; `pending` / `acknowledged` / `replayed`.
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

- [ ] `reference-projection-v2`: the newest accession per (company, metric) is never a reference; a
      verified prior pair from the newest filing projects as `StatedPrior`; a filing later than the fact's
      observation is excluded; both exclusions counted; validator drops `reference-not-projected`
      trajectory citations by FAILING the judgment (a finding's drops the finding only); same-day
      accessions ordered by accession then record id; the five pinned cases (incl.
      article-the-day-after-the-filing) pass.
- [ ] The verified extraction, ledger state and attempt count are PERSISTED on the analyzed-filing record;
      `Pending` records replay the same payload (no fetch, no model call — asserted) until acknowledged; the
      persisted attempt count drives the once-per-company warning at 3; null policy stays a hit; missing
      state reads `Pending`/0; a policy-without-payload record fails closed (counted, logged, never
      acknowledged) and the PR body asserts the live count of such records is zero.
- [ ] `reported-metrics-v2` verifier: metric synonym in quote, period in quote, whole-token value/unit/prior
      value, AND metric/value/period associated within one bounded fragment with the value nearest the
      metric, with decimal points never a fragment boundary; each drop class counted; the reviewer's four
      examples (incl. "cash $100m and backlog $2.5bn"), the `$0.79`/`384.0` single-fragment case, and the
      per-metric negative synonym cases (cash flow ≠ CashAndInvestments, cost of sales ≠ Revenue) are
      tests.
- [ ] Atomic temp-file + no-overwrite move; fragment never reported `AlreadyOnDisk`; `corrupt-existing`
      counted and logged once.
- [ ] Policy is an explicit `WriteIfNewAsync` argument, in the ledger path and the record id; projector
      reads the current policy only and counts skipped superseded files; v1/v2 side-by-side test; durable
      judgment record `news-judgment-v7` with v6 readable, persisting `ObservedAtUtc`, reference policy and
      reference kinds.
- [ ] The directional-filing `ai=` descriptor carries `rm=disabled|<policy>`; `news=` carries
      `references=reference-projection-v2`; the four-case identity matrix (judgment off + flag toggled ⇒
      changes; judgment on + toggled ⇒ changes; AI read off + toggled ⇒ unchanged; policy token changed ⇒
      changes) is pinned by mutation tests; AI-ON pins moved once and asserted, AI-OFF proven unchanged;
      the regime boundary reads "214–216" with the 2026-09-29 claim date unchanged; operator step stated in
      the PR body.
- [ ] `Enabled` back to `true` with the comment amended in place — or left `false` with the reason stated.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.
