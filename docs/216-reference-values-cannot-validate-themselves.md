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

None of this touches a score, weight or formula. The AI-ON pins move once, to values carrying
`references=reference-projection-v2` and `rm=reported-metrics-v2` (§6 re-enables the ledger in this PR, so
the shipped pin is the enabled one; `rm=disabled` is a test/profile state, not a regime). The AI-OFF pins do
NOT move (the `ai=` descriptor folds only with an AI filing read) and that non-move is an asserted
deliverable. The
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

## 2. A real outbox: the complete, ready-to-write ledger payload is persisted in `CollectionPass` after company resolution

Marking the cache "unacknowledged" and re-analyzing would re-fetch from SEC and re-run the model, which can
return a DIFFERENT direction (review round 1). And `ReportedMetricExtraction` (`ReportedMetric.cs` L81)
carries the metrics, accession, form and reader identity but NOT the resolved `CompanyId`, `EvidenceId` or
`FilingDateUtc` a ledger record needs, while `IAnalyzedFilingCache` (L145) has no way to enumerate pending
entries — those values exist only transiently in `CollectionPass` today (review round 3). So the outbox is
its own durable store, written at the ONE point where every routing field is known:

- **`IReportedMetricOutbox`** (Application) with a file implementation under
  `data/reported-metrics/outbox/` (`RadarFileStoreJson`/`GracefulFileWriter`, atomic temp+move as §4):
  `EnqueueAsync(envelope)`, `EnumeratePendingAsync(ct)` (deterministic order: companyId, accession),
  `MarkAttemptAsync(id)` and `AcknowledgeAsync(id)`. An envelope is the COMPLETE ready-to-write payload:
  `OutboxId` (content-derived from policy + accession), `Policy`, `CompanyId` (nullable — see unresolved
  below), `EvidenceId`, `Accession`, `Form`, `FilingDateUtc`, reader identity, the verified
  `ReportedMetricReading[]` plus the per-class drop counts, `Attempts` (int, missing ⇒ 0), `CreatedAtUtc`,
  `LastAttemptAtUtc`. Nothing in it needs re-derivation: replay is `WriteIfNewAsync(policy, records)` per
  envelope, no fetch, no model call — asserted by a test with a throwing analyzer.
- **Ordering, so nothing is ever held only in memory when the cache says "done":** analysis (transient
  extraction) → `CollectionPass` resolves the company → **outbox envelope written (durable)** → only on
  `Succeeded`/`AlreadyOnDisk` of THAT write is the analyzed-filing record stamped `reportedMetricsPolicy =
  <policy>` → ledger write attempted from the envelope → `AcknowledgeAsync`. If the process dies before the
  envelope is durable, the cache is unstamped and the filing re-analyzes next run exactly as any uncached
  read does today (nothing was ever persisted to lose; the direction of THAT re-read is the direction, as
  for every first read). If it dies after, the envelope replays. The analyzed-filing cache therefore gains
  NO payload and NO enumeration — only the policy stamp it already has, whose meaning becomes "an outbox
  envelope exists for this accession under this policy".
- **Replay each run:** `CollectionPass` enumerates pending envelopes BEFORE the fresh reads (so a stuck
  envelope is retried before new work is added), replays each into the ledger, acknowledges on success,
  otherwise `MarkAttemptAsync`. Acknowledged envelopes are moved to `outbox/acknowledged/` (append-only;
  never deleted) so the ledger's provenance chain stays walkable. Aggregated line:
  `outbox pending {n} / replayed {n} / acknowledged {n} / attempts-exhausted {n}`.
- **Unresolved company is a routable state, not a loss:** an envelope whose company could not be resolved
  at enqueue time is written with `CompanyId = null` under `outbox/unresolved/{accession}.{policy}.json`
  and is re-run through company resolution on every replay (resolution may succeed later — a universe
  addition, a hint fix); once resolved it is re-enqueued under the company and the unresolved copy is
  acknowledged. Counted (`outboxUnresolvedCompany`) and, at `Attempts ≥ 3`, logged once per accession per
  run as a durable defect — counted, named, never dropped, never retried silently forever.
- **The three-run warning reads the PERSISTED `Attempts`** on the envelope (survives restarts); missing
  ⇒ 0. Null `reportedMetricsPolicy` on a cache record (pre-215) stays the hit it is today. A cache record
  WITH a policy stamp but NO matching outbox envelope (acknowledged or pending) is the 215-era shape:
  NOT acknowledged as empty (review round 2) — counted (`policyStampWithoutEnvelope`), logged once per
  accession, and recoverable only by a conscious maintainer re-analysis. Measured 2026-09-08: the live
  cache holds ZERO such records; the implementer asserts that count in the PR body.
- Tests: a `NotPersisted` ledger write leaves the envelope pending with `Attempts` 1 and the next pass
  replays the byte-identical payload without invoking the analyzer; a `Succeeded` write acknowledges and
  moves the envelope; an unresolved-company envelope is retried and re-enqueued once resolution succeeds;
  `Attempts` 3 triggers exactly one warning; a policy stamp without an envelope fails closed.

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
  the `ai=` descriptor folds only when the AI filing read is registered, so the **AI-ON pins move** — and
  because §6 re-enables the ledger IN THIS SAME PR, the pins this slice ships carry
  `rm=reported-metrics-v2` (with `references=reference-projection-v2`); `rm=disabled` is an intermediate
  state that exists only in the mutation tests and in a profile that switches the ledger off, never a
  separate later regime — and the **AI-OFF pins do NOT move** (no AI read ⇒ no extraction to hash; an AI-OFF move here would be scope leakage — the spec-197
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
- The PR flips `Radar:Ai:ReportedMetrics:Enabled` back to `true` in `default.json` and amends its
  `_comment` in place (drop the DISABLED sentence, cite this spec); the live AI-ON pins asserted by
  `ScoringConfigFingerprintTests` are computed WITH the flag on (`rm=reported-metrics-v2`). If the
  implementer is not confident all of §1–§4 hold, the flag stays `false`, the asserted pins carry
  `rm=disabled`, and the PR body says so — a disabled ledger is honest; a self-validating one is not.

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
- [ ] `IReportedMetricOutbox` persists the COMPLETE ledger payload (company, evidence, accession, form,
      filing date, reader, policy, verified records, drop counts, attempts) in `CollectionPass` after
      company resolution and BEFORE the cache is stamped; pending envelopes are enumerated and replayed
      each run (no fetch, no model call — asserted) until acknowledged; acknowledged envelopes are kept;
      unresolved-company envelopes are retried through resolution; the persisted `Attempts` drives the
      warning at 3; missing state reads `Pending`/0; null cache policy stays a hit; a policy stamp with no
      envelope fails closed and the PR body asserts the live count of such records is zero.
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
