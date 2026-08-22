# Task: Theme Radar — semantic classification, characterization, report and exploratory evaluation

## Overview

Spec 178 accrues immutable point-in-time theme observations and versioned theme/exposure declarations. This
slice adds everything that interprets them: a semantic classifier that distinguishes discussion from demand,
transparent descriptive characterization (deliberately **not** a composite score), a standalone Theme Radar
report joining themes to declared exposures, and a read-only exploratory evaluator over later prices.

It runs inside the same standalone `theme` run mode spec 178 created, after capture, in the same process and
run. Nothing here touches company scoring, the baseline run, or any AD-15/AD-16 claim family.

The live report carries this fixed language:

> Theme Radar is an exploratory shadow view of externally observed trends and declared company exposures. A
> rising discussion is not proof of demand, and a mapped company is not a Radar recommendation.

## Assignment

Worktree: any
Dependencies: spec 178 merged (declarations, capture, theme run mode); spec 177 transitively (payload and
store primitives). Spec 179 is parallel work, not a prerequisite.
Estimated time: ~2 days.

## 1. Semantic classification: discussion is not demand

Add a separate `IThemeEvidenceAnalyzer` over the existing `IChatClient` seam, registered only in theme mode
and only when an AI provider is configured. An enabled analysis with no configured provider fails the theme
run explicitly rather than quietly treating every article as unclassified. It receives one theme's frozen
hypothesis/behaviour definition and id-labelled captured text. It receives no company score, mapped-company
name, security price or future outcome; those would encourage it to reinterpret articles to fit an
investment story. Exclusion-marked observations (spec 178 §6) are not supplied.

Closed per-observation schema:

```text
Relevance       Relevant | Incidental | Irrelevant | InsufficientContent
Direction       SupportsTheme | RunsAgainstTheme | Mixed | None
EvidenceKind    Anecdote | Awareness | Consideration | ActionIntent | CompletedAction |
                CapacityOrHiring | TransactionOrOrder | Regulation | SupplyConstraint | OtherSpecified
Confidence      0..1
EventSummary
SupportingExcerpts[]
```

For the tattoo example:

- "people discuss regretting tattoos" may be `Anecdote` or `Awareness`;
- "searching for/removal consultations are rising" may be `ActionIntent`;
- reported bookings or procedures may be `CompletedAction`;
- a clinic ordering/hiring for additional removal capacity may be `CapacityOrHiring`; and
- a celebrity-regret story with no broader behaviour is not silently promoted to demand.

Mechanical validation mirrors spec 179 §6, including its definition of **archived text** (the union of the
exact fields actually supplied to the model for that observation):

- cited observation ids must be in the supplied bundle;
- every excerpt must be an exact ordinal substring of a supplied text field for that observation;
- all enums/confidences must be valid;
- `OtherSpecified` requires a non-blank factual kind; and
- invalid or uncited claims are dropped and counted, with drop reasons persisted so the strictness cost is
  measurable.

If validation leaves no supported classification, store `InsufficientContent`/`ValidationFailed`, never a
negative or quiet observation. Persist every attempt under
`data/theme-radar/assessments/{model-policy}/{themeId}/...` with model id, prompt/schema version, definition
hash, ordered input hashes, raw-response hash, status and validated result. A model/prompt/schema/definition
change forms a new cohort and cache key; it never overwrites or reuses an incompatible assessment.

**Bound new work per run.** Cache by input-bundle identity so already-classified observations replay free,
and cap fresh model calls with `MaxNewClassificationsPerRun` (default 100), mirroring the
`Ai:MaxFilingsPerRun` precedent: a definition-version bump is a new cohort and a full cache miss by design,
and without the cap one such bump turns a single run into an unbounded backlog drain. The manifest records
classified/cached/deferred counts; a deferred backlog is visible, never silent, and a run with a deferred
backlog is incomplete for §2's windows.

## 2. Characterize the theme before inventing `ThemeMomentum`

For each complete theme checkpoint D, persist a snapshot under
`data/theme-radar/snapshots/{themeId}/v{definitionVersion}/{snapshotId}.json` with transparent descriptive
fields rather than one weighted score:

- relevant observations and distinct publishers over 7 and 28 days;
- observations by `EvidenceKind` over 7 and 28 days;
- supporting versus runs-against counts;
- raw provider items, exact-URL dedupes, exclusion-marked and classifier-excluded counts;
- query coverage/cap/failure counts;
- headline-family concentration; and
- the corresponding prior-window values where complete.

Windows use only observations known by D and require complete relevant coverage; an incomplete window renders
as incomplete, never as a low number.

`headline-family` is a versioned, deterministic near-duplicate diagnostic over normalized headline text and
publication proximity. It exists to show when 40 publishers repeated one story. Preserve raw article count
and publisher breadth beside it; do not claim that headline families are real-world events.

Do not combine these fields into `ThemeMomentum`, a Watch label or a common threshold in this slice. In
particular, do not choose weights after seeing which combination makes tattoo removal look strongest. The
report may say `Increasing descriptive activity` only when it names the exact raw comparison, for example:

> 8 relevant distinct-publisher observations in the last 7 days versus 2 in the preceding 7 days; 1 of 8 was
> ActionIntent and 6 were Anecdote/Awareness.

That sentence is much more useful — and more honest — than a premature score of 73.

## 3. Render Theme and Exposure as a join, not a blended rank

Write a standalone Theme Radar Markdown/JSON report to
`data/theme-radar/reports/theme-radar-{yyyy-MM-dd}.md` at the end of the theme run. For each theme, render:

1. hypothesis, definition version/status and the per-(theme, version) prospective boundary;
2. collection/assessment completeness, including any deferred classification backlog;
3. the §2 descriptive table and change versus prior complete windows;
4. cited supporting and contrary observations, including evidence kind;
5. declared watched-company exposures;
6. proposed/outside-universe exposures in a separate research queue; and
7. the explicit no-material-exposure message when applicable.

For each declared exposure show direction, mechanism, materiality, geography, valid-from date and primary
citations. Theme and exposure sections join only when definition/exposure versions and `validFromUtc` permit.
Never multiply a theme metric by materiality, merge it with Opportunity, average it across themes or rank
exposed companies in this slice.

If the same external article also exists in a company's spec-177 news-observation archive, mark it as
overlap — **the join key is the normalized landing URL** (the two archives' payload hashes cover different
fields and never match by construction). Do not present a shared article as two independent corroborating
sources.

A theme-run failure writes a named incomplete artifact. The baseline company report is untouched and cannot
reference theme output in this slice.

## 4. Exploratory validation and the eventual strategy boundary

Persisted snapshots and exposure versions make later evaluation possible without rerunning the model. Add a
read-only export/evaluator that joins frozen records to outcomes only after the checkpoint:

- 21-, 63- and 126-day forward price returns, reported as separate exploratory horizons through the existing
  `ForwardReturn.TryCompute`/spec-152 tolerance semantics and complete-window exclusions;
- maximum adverse/favourable close move over each complete horizon;
- direction-adjusted results (`Beneficiary` versus `AdverselyExposed`, symmetric); and
- later operating evidence only when a separately specified, point-in-time segment metric exists.

Price is never read during theme capture, classification, snapshotting or exposure mapping — only here. Each
row pins theme/exposure/model/input hashes and reports every exclusion reason.

Keep cohorts separate by:

- theme definition version;
- exposure version;
- model/prompt/schema;
- source/capture mode;
- Development versus Prospective; and
- watched versus outside-universe status.

The three price horizons are deliberately exploratory because the commercial lag has not been established. Do
not select the best horizon and call it confirmation. A later strategy spec must declare, before its outcomes
exist: one immutable `theme-led-v1` formula; one primary outcome/horizon; eligible theme/exposure statuses
and minimum coverage; the first eligible as-of date; and comparators, multiplicity and promotion/failure
rules. Until then, Theme Radar can generate research leads but cannot claim alpha.

Add an architecture test mirroring spec 179 §10: the Scoring/pipeline closures reference no theme type; the
classifier and snapshot builder reference no price type; only the evaluator reaches price.

## 5. Out of scope, recorded not built

- **Open-ended theme/company discovery queues** (the hypothesis-generator pass): a future spec of its own,
  if ever. Its shape is constrained in advance: proposals would write only to `data/theme-radar/proposals/`,
  could never edit declarations or `companies.json`, and their discovery evidence would stay Development —
  "find a trend in today's stories, then score today's stories as proof" remains structurally impossible.
- `ThemeMomentum` or any composite theme score, Watch labels, thresholds.
- `theme-led-v1` or any scoring-strategy integration.
- New provider types beyond the spec-177/178 news capture.

## 6. Configuration additions

Extend the `Radar:ThemeResearch` block (fail-closed, theme mode only):

```json
{
  "AnalyzeWithAi": true,
  "MaxNewClassificationsPerRun": 100,
  "SnapshotAndReport": true
}
```

Unknown keys/invalid bounds fail startup. The AI provider configuration is the existing `Radar:Ai` block; no
new provider plumbing. All limits are cost/operational controls, recorded in run manifests and assessments,
hashed into no scoring fingerprint.

## Files to inspect

- spec-178 theme declaration/capture/store/mode components
- `src/Radar.Application/Ai/IChatClientFactory.cs`
- `src/Radar.Infrastructure/Filings/AdviceLanguageGuard.cs`
- `src/Radar.Infrastructure/Filings/ChatFilingAnalyzer.cs` (typed AI/validation precedent)
- `src/Radar.Application/Scoring/MediaAttentionCollapse.cs` (de-noising precedent, not a reusable theme score)
- `src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs`
- `src/Radar.Application/Efficacy/` price-resolution/date-block primitives
- `src/Radar.Worker/RadarWorkerOptions.cs`
- `src/Radar.Worker/RadarWorkerServices.cs`
- `scripts/run-radar.ps1`

## Tests

### Classification

- The analyzer receives no company score, exposure, price, post-cutoff text or exclusion-marked observation.
- Exact-substring validation rejects fabricated/omitted-field citations; drop counts/reasons persist.
- All-invalid claims become `ValidationFailed`/`InsufficientContent`, never a quiet zero.
- Model/prompt/schema/definition changes create distinct assessment cohorts and cache keys.
- The new-classification cap defers rather than drops; deferred counts render and mark the run incomplete.
- Enabled analysis without a configured provider fails the theme run explicitly.

### Characterization and reporting

- Seven-/28-day fields use only observations known by D and require complete coverage.
- Raw article, publisher, headline-family and evidence-kind counts remain distinct; no composite exists.
- One syndicated story cannot masquerade as 40 independent headline families.
- Theme and exposure sections join only when versions and valid-from times permit.
- Shared company/theme URLs are marked overlap via landing-URL join, not corroboration.
- No-exposure and incomplete-run messages render; the baseline company report is unchanged.

### Evaluation

- Development/prospective, definition, exposure, model and capture cohorts never pool.
- Partial price horizons fail closed through reused efficacy primitives; direction adjustment is symmetric.
- The evaluator reads frozen snapshots/outcomes only and cannot invoke collection, AI or scoring.
- Architecture guard: Scoring/pipeline reference no theme type; only the evaluator reaches price.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one coordinated
session.

## Acceptance criteria

- [ ] Theme relevance/behaviour kind is semantically classified with exact cited text; regret/awareness
      cannot silently become purchase/action intent.
- [ ] New model work per run is capped and cached; a deferred backlog is visible and marks the run
      incomplete.
- [ ] Theme activity is reported as transparent descriptive fields, not a tuned composite score.
- [ ] A real theme with no established public-company exposure is reported honestly rather than forced into
      a candidate.
- [ ] Theme/exposure joins respect versions and valid-from times; overlap with company news is marked by
      landing URL, never presented as corroboration.
- [ ] Development, prospective and capture-mode cohorts remain distinct through export and exploratory
      outcomes.
- [ ] No existing score, strategy, label, fingerprint, AD-15/AD-16 claim, baseline run or normal report
      changes.
- [ ] Build and coordinated tests green.
