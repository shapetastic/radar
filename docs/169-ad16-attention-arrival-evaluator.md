# Task: Implement AD-16's precommitted attention-arrival evaluator

> **This is an evaluator, not another strategy and not a chance to retune the thesis.**
>
> AD-16 already fixes the primary arm, outcome, horizon, comparator, cohort exclusion, minimum N and failure
> screen. This slice makes those decisions executable. It may expose missing data; it must not repair a result
> by changing what is measured.

Spec 157 deliberately left this evaluator out of scope while the v11 arm accrued. The remaining blocker is
not the score. It is proving that a zero publisher count is a real zero rather than a failed or truncated
collection window.

## Decisions this spec must not reopen

Use these values verbatim from AD-16:

- primary arm: `disclosure-led-v11`;
- matched formula diagnostic: `disclosure-led-v10-control`;
- primary outcome: distinct third-party publishers with at least one resolving `MediaAttention` signal in
  `(T, T + 21 days]`;
- primary comparator: the same distinct-publisher count in `(T - 21 days, T]`;
- secondary comparator: the stored `AttentionScore` from the v11 snapshot, reported but never screened on;
- no publisher-novelty test and no price input;
- at least 20 eligible companies per as-of date;
- daily `delta(T) = rho_v11(T) - rho_persistence(T)`;
- after at least 20 eligible dates, median delta `<= 0` is `MISS`; median delta `> 0` is only
  `ClearsNecessaryScreen`, never proof of efficacy;
- the eight companies in `docs/cohorts/event-enriched-2026-07.json` are excluded before the primary minimum
  N is calculated and are reported only as a separate exploratory cohort.

Here `T` is the exact UTC `WindowEndUtc` of a score snapshot; its UTC calendar date is only the report
label. Using the whole date would look ahead to articles published later on the scoring day.

## 1. Pin the remaining first-eligible boundary now

AD-16's 2026-07-29 amendment moved the boundary to the later of 2026-09-26 and the first post-spec-160
baseline run plus 60 days, leaving the concrete date to be recorded after the run.

That run now exists:

- `PipelineRunRecord` `7f28ca48-5cb3-4646-8d57-56baf1e482e1`;
- `CreatedAtUtc = 2026-07-30T08:07:19.5804397Z`.

Sixty days ends during 2026-09-28. To avoid making eligibility depend on the intraday schedule, pin
**2026-09-29** as the first eligible UTC as-of date. Amend AD-16's open boundary paragraph with that date in
this slice. This is conservative by less than one day, is recorded before any outcome can exist, and changes
none of the metric, horizon or failure rules.

The first outcome can mature only after its exact `T + 21 days` endpoint and a closing coverage checkpoint.
Twenty daily dates cannot be available until roughly early November. Earlier reports must say `Pending`;
they are expected accrual, not a defect.

Prospective coverage provenance must be running by **2026-09-08** to cover the whole trailing window of the
first eligible date. If it lands later, do not backfill success or move the precommitted boundary: the
affected company-dates are honestly `IncompleteAttentionCollection`, and later dates become usable as their
own complete windows accrue.

## 2. Correct the collection-coverage dependency before evaluating

AD-16 currently says the coverage requirement is satisfiable from existing
`PipelineRunRecord.Collectors`, aggregate `SourcesFailed` and `CollectionWarnings`. That is factually too
strong. The aggregate count cannot distinguish two failed RSS feeds from a failed `newssearch` feed, cannot
identify the affected company, and cannot reveal that a successful query hit its result limit.

A recent global run is therefore not proof of attention coverage. Amend that AD-16 dependency paragraph and
add prospective collector-level provenance. Do not infer or backfill it for old records.

### Durable records

Add a trailing optional `CollectorRuns` field to `PipelineRunRecord`. Each `CollectorRunRecord` carries,
in stable collector order:

- collector name;
- sources checked, succeeded and failed;
- items collected;
- the existing source failures; and
- optional per-company collection coverage.

Add a trailing optional `CompanyCoverage` field to `CollectionResult`. The `newssearch` collector emits
one row for every company in its `CollectionContext`: `CompanyId`, `ExpectedFeedCount`,
`SuccessfulFeedCount`, `HitEffectiveResultLimit`, and a stable sorted issue set. It can emit
`MissingFeed`, `SourceFailure` and `ResultLimitReached`. Compare the raw reader result count with the
effective clamped request limit, not the later relevance-filtered count or an unclamped config value.

Before `CollectionResultMerger.Merge` discards collector identity, `CollectionPass` combines each collector
name with its unmerged summary and optional coverage into `CollectorRunRecord` rows carried by
`CollectionPassResult`. If the already-computed health report contains a `newssearch` inventory warning,
the pass adds `CollectionHealthMismatch` to every newssearch company row; the collector itself does not have
that report. Those four names are the complete issue-token vocabulary, sorted ordinally; an empty set means
complete at that checkpoint. Every runner which writes a run record persists the rows. Other collectors may
leave company coverage null. A score-only run records no collection coverage. A partial collection run
(`CompanyFilter != null`) can never prove primary-screen coverage.

The field is observational only: trailing and optional for old JSON, not an evidence, signal, score,
fingerprint or strategy-comparability input. For coverage purposes, however, null means **unproven**, never
success.

### What “complete newssearch coverage” means

The live profile's third-party attention producer is `newssearch`. Its coverage for company C at checkpoint
R is complete only when all of the following hold:

1. the run is unfiltered and `newssearch` actually ran;
2. C had at least one configured `newssearch` feed;
3. every expected feed for C was checked and succeeded;
4. no feed's raw reader result count reached `MaxRecordsPerCompany`; equality means potentially truncated,
   even when the client-side relevance filter later keeps fewer articles; and
5. no collection-health warning says the `newssearch` feed inventory was incomplete.

The collector must calculate this while it still knows the feed-to-company binding and the raw returned item
count. Reconstructing it later from `ItemsCollected` is invalid.

This is an operational statement about Radar's configured news source, not a claim that Google News indexes
the whole web. Render that limitation. If another enabled collector can emit third-party
`MediaAttention`, fail with `UnsupportedAttentionCollector` until it supplies the same coverage contract;
do not silently mix its signals into an outcome whose coverage cannot be proved.

### Complete interval

For a company and exact interval `(a, b]`, require a chain of complete checkpoints:

- one at or before `a`, no more than 36 hours earlier;
- one at or after `b`, no more than 36 hours later; and
- no gap greater than 36 hours between consecutive complete checkpoints spanning the interval.

The 36-hour maximum accommodates ordinary drift in a once-daily job without treating a missed day as
covered. It is a collection-cadence rule, not a shortened outcome: evidence is still counted through the
exact `b` endpoint, and there is no price-style exit tolerance.

Apply this separately to `(T - 21 days, T]` and `(T, T + 21 days]`. A legacy checkpoint without
`CollectorRuns`, a failed/capped company feed, a partial run or a missing link breaks the chain and drops
that company-date as `IncompleteAttentionCollection`. Report the more specific coverage reason alongside
the AD-16 reason.

## 3. Build the publisher-count observation without look-ahead

For company C and exact interval `(a, b]`, read durable signals and evidence. A signal is relevant only when:

- `CompanyId == C`;
- `Type == MediaAttention`;
- `ReviewStatus == Approved`;
- `ObservedAtUtc > a && ObservedAtUtc <= b`;
- its evidence resolves;
- the evidence is `NewsArticle` from a supported third-party attention collector; and
- its real publisher is nonblank.

Use the news evidence metadata's `publisher` value as the real outlet. Do not count the collector's
company-feed-name fallback in `EvidenceItem.SourceName` when that metadata value is blank; it is not a
third-party publisher. A relevant signal with missing evidence drops the company-date as
`UnresolvedComparatorEvidence` or `UnresolvedOutcomeEvidence`. A blank real publisher is instead
`MissingComparatorPublisher` or `MissingOutcomePublisher`. Missing or unsupported collector attribution
is `UnresolvedComparatorProvenance` or `UnresolvedOutcomeProvenance`. None is silently omitted from the
count.

> **Amended during PR review (2026-08-03) — RECORDED attribution only.** As written, this paragraph is
> narrower than the Constraints section's "No inferred success" below. An article is admitted only when the
> collector stamp spec 146 records is present; spec 151's *inferred* attribution is
> `Unresolved*Provenance` exactly like missing attribution. Otherwise the primary metric would move with
> `Radar:Scoring:InferLegacyCollectorAttribution`, a scoring-only flag, and a precommitted screen cannot
> depend on a knob. See the AD-16 amendment in `docs/architecture-decisions.md`.

Canonicalise publisher names only by trimming, collapsing internal whitespace and comparing
case-insensitively. Do not add a hand-maintained Reuters/Reuters.com-style entity map after seeing outcomes.
Distinct URLs or articles from the same canonical publisher count once. Do not test novelty against history.

When coverage is complete and there are no relevant signals, the publisher count is the valid integer
**zero**. Keep it. Selecting only companies where attention arrived would select on the outcome and destroy
the test.

Put this construction in one shared `AttentionPublisherCountBuilder` and call it for both windows. The
comparator and outcome must not acquire subtly different filters.

## 4. Select snapshots and form the daily screen

Read `disclosure-led-v11`, `disclosure-led-v10-control`, `baseline-earnings-only`,
`baseline-activity-only` and `baseline-media-only` through the existing snapshot-store factory. A primary
candidate is a v11 snapshot from an unfiltered full run whose exact `PipelineRunRecord.CreatedAtUtc` equals
`WindowEndUtc`. Group by the UTC date of `T`; if more than one candidate exists, choose the latest exact
instant using only run/snapshot provenance, never the future outcome.

For each candidate date:

1. exclude event-enriched companies before counting N;
2. require a usable v11 snapshot, complete comparator and outcome coverage, resolving publisher counts, and
   defined persistence and outcome values;
3. require at least 20 companies after those exclusions;
4. over exactly that company set compute `rho_v11`, `rho_persistence` and
   `delta = rho_v11 - rho_persistence`;
5. compute `rho_attention_score` over the same set as a secondary reported diagnostic;
6. when an exact-time v10 control snapshot exists for every company in that set, compute `rho_v10` and
   `rho_v11 - rho_v10` as formula diagnostics; otherwise report `IncompleteControlSupport`; and
7. likewise compute each fixed `baseline-*` arm's rho on the full same set, or report its support/degeneracy.
   These rows are retained for spec 155's later “every baseline” gate but cannot alter AD-16's status here.

A constant outcome, v11 predictor or persistence predictor excludes the date under
`ConstantOutcome`, `ConstantPrimaryPredictor` or `ConstantPersistencePredictor`. A constant secondary
`AttentionScore`, v10 control or configured baseline makes only that diagnostic undefined in this slice.
Never emit NaN.

When prerequisites are available, the primary `ScreenStatus` is exactly:

- fewer than 20 dates: `Pending`;
- at least 20 and median delta `<= 0`: `Miss`;
- at least 20 and median delta `> 0`: `ClearsNecessaryScreen`.

A missing/invalid binding cohort or unsupported attention collector instead sets top-level
`EvaluationAvailability = Unavailable`, records the stable reason and leaves `ScreenStatus` null. Do not
mislabel a configuration failure as accrual. Use the three screen tokens in JSON and human-readable
restrained wording in Markdown. The daily windows
overlap, so this result has no confidence or significance claim. Spec 155's purged interval is a later
confirmatory layer; this slice must preserve the per-date rows it will consume but does not implement or
simulate that interval.

Run the event-enriched cohort through the same builders as a separate exploratory section. Never pool its
companies with the primary, never let it satisfy the primary N, and never let its result change the primary
status.

## 5. Read seams and artifacts

Add a bounded deterministic run-history read such as
`IPipelineRunStore.ReadBetweenAsync(startInclusiveUtc, endInclusiveUtc, ct)`, ordered by
`CreatedAtUtc` then `Id`. Do not load an arbitrary “recent N” and mistake truncation for absence.

Load `docs/cohorts/event-enriched-2026-07.json` through an Application abstraction with its file
implementation in Infrastructure. Missing, malformed or contradictory cohort configuration yields
`CohortConfigurationUnavailable` and suppresses the primary status; silently including all companies would
violate the accepted exclusion.

Write a dedicated deterministic artifact set:

- `data/efficacy/attention-arrival-screen.json` — machine-readable source of truth, including per-date rows,
  per-company exclusions and stable reasons;
- `data/efficacy/attention-arrival-screen.csv` — one row per candidate date with N, correlations, delta and
  drop counts; and
- `data/efficacy/attention-arrival-screen.md` — concise operator summary, boundary, coverage limitations,
  primary status and separate exploratory cohort.

Use a dedicated `IAttentionArrivalArtifactStore` or an explicitly named method on the efficacy artifact
store; Application must not write files directly. Best-effort artifact failure follows AD-8 and cannot affect
scores or the pipeline's durable evidence.

Invoke the generator after a normal full run has persisted its run record and snapshots. Re-running over
unchanged stores must be byte-identical. Early runs still write a useful `Pending` artifact with exclusion
counts so coverage instrumentation is tested before the first outcome matures.

## Files (verify against the tree before implementation)

- `src/Radar.Application/Collectors/CollectionResult.cs`
- `src/Radar.Application/Collectors/CollectionSummary.cs`
- `src/Radar.Application/Pipeline/CollectionPass.cs`
- `src/Radar.Application/Pipeline/ICollectionPass.cs`
- `src/Radar.Application/Pipeline/PipelineRunRecord.cs`
- `src/Radar.Application/Pipeline/IPipelineRunStore.cs`
- all three pipeline runners which persist run records
- `src/Radar.Infrastructure/News/NewsAttentionCollector.cs`
- `src/Radar.Infrastructure/FileSystem/FilePipelineRunStore.cs`
- score, signal, evidence and company/cohort read seams
- new Application evaluator/result/renderer types under `Efficacy/Attention`
- new Infrastructure artifact/cohort stores and DI wiring
- worker orchestration and tests
- `docs/architecture-decisions.md`

## Constraints

- **Read-side evaluation only.** No score, signal, evidence or review is created, amended or deleted.
- **No scoring/fingerprint/pin change.** Collector coverage is observational run provenance.
- **No outcome retuning.** Metric, 21-day horizon, comparator, minimum N, cohort and miss rule stay exactly as
  AD-16 fixes them.
- **No look-ahead.** Metric windows use exact snapshot `WindowEndUtc`, not a whole UTC date.
- **No inferred success.** Legacy/null, aggregate counts, partial runs and capped results cannot prove
  coverage.
- **No valid-zero loss.** Complete no-attention windows remain zero and in-sample.
- Deterministic ordering and serialization throughout (AD-3); no bootstrap or sampling.
- No advice vocabulary (AD-9), price input (AD-14) or automatic strategy promotion.

## Out of scope

- Implementing spec 155's purged confirmatory interval.
- Changing `newssearch` query depth to rescue capped observations. Measure and report cap frequency first;
  any prospective collection change is a separate spec.
- Publisher entity resolution beyond the fixed whitespace/case canonicalisation.
- Benchmark-adjusted price.
- Correcting, deleting or re-ranking accrued signals/snapshots.
- Claiming that operational source coverage equals the whole media market.

## Acceptance criteria

- [ ] AD-16 records **2026-09-29** as the concrete first eligible date and corrects the claim that aggregate
      legacy run fields can prove coverage.
- [ ] Every new run record preserves stable per-collector summaries; `newssearch` additionally records
      per-company missing/failure/cap coverage, while old null records remain readable and unproven.
- [ ] A `newssearch` result exactly at `MaxRecordsPerCompany` is incomplete even when relevance filtering
      keeps fewer items.
- [ ] Partial and score-only runs cannot prove coverage; a complete unfiltered run can supply a checkpoint.
- [ ] Comparator and outcome use one exact-time publisher-count builder with intervals
      `(T - 21d, T]` and `(T, T + 21d]`; no date-rounding or endpoint tolerance.
- [ ] A complete window with no relevant signals returns zero; missing evidence, missing real publisher or
      broken coverage produces a named exclusion, never an undercount.
- [ ] Publisher count uses the real metadata publisher, canonicalised only by whitespace and case, and never
      counts the synthetic feed-name fallback.
- [ ] The primary cohort exclusion loads from the checked-in JSON and is applied before minimum N; config
      failure suppresses the primary status.
- [ ] Per eligible date, v11 and persistence correlations use exactly the same at-least-20 companies;
      secondary, v10 and configured-baseline diagnostics cannot alter the primary status.
- [ ] Each configured `baseline-*` arm is reported only on full primary-company support, preserving the rows
      spec 155 will need for its later joint-support gate.
- [ ] When evaluation is available, screen status is exactly `Pending`, `Miss` or
      `ClearsNecessaryScreen` under AD-16's fixed median-delta rule, with no confidence language; unavailable
      prerequisites leave it null under a separately named availability failure.
- [ ] Event-enriched output is separate, exploratory and incapable of changing the primary result.
- [ ] JSON, CSV and Markdown artifacts expose boundaries, support, all stable exclusion counts and the
      operational-coverage limitation; identical input stores produce byte-identical output.
- [ ] Fixtures cover a valid zero, a collector failure, result-limit censoring, unresolved evidence, blank
      publisher fallback, exact open/closed endpoints, cohort exclusion, constant predictors/outcome, fewer
      than 20 companies, fewer than 20 dates and a matured miss.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
