# Spec 170 §4 — deliberate fail-open sweep of `Radar.Application/Efficacy/**` (phase 1: enumerate only)

**Scope.** Every predicate in `Radar.Application/Efficacy/**` that decides whether an observation, a date, a
company or a result is **admissible, complete, eligible or qualifying**, examined for how the
absent/unknown/unparseable input behaves: does it fail **CLOSED** (excluded / refused / suppressed) or
**OPEN** (admitted / satisfied / silently defaulted)? Predicates found already correct are listed too — a
sweep listing only defects cannot be checked for coverage.

**Method.** File-by-file read of the tree as of this slice (spec 170 applied), classifying each
verdict-deciding branch. Rendering-only code (`EfficacySvgRenderer`, `EfficacyCsvRenderer`,
`StrategyLeaderboardRenderer`, `AttentionArrivalRenderer`, the markdown/CSV halves of
`PairedComparisonRenderer`) was scanned for verdict-DECIDING predicates and contains none; its token-fallback
branches are noted at the end because they render, not decide.

**Headline.** Beyond the three findings spec 170 declared (and fixes in §§1–3), **one deliberate,
code-documented open-shaped predicate** and **two open-shaped fallbacks worth recording** were found — all
three enumerated below with recommendations, **none fixed in this slice** (phase 2 discipline: a repair that
changes eligibility needs its own reviewed spec). Everything else in the tree fails closed.

---

## 1. The three declared findings (fixed in this slice, listed for completeness)

| # | Predicate | Pre-170 behaviour | Verdict pre-170 | Fixed |
|---|---|---|---|---|
| D1 | `PairedComparisonHarness` gate: `QualifiesUnderAd15 = gateReasons.Count == 0` from the **price side alone** | An absent/never-run AD-16 screen did not enter the verdict at all — the artifact could print the claim licence while the condition AD-15 makes binding had never been evaluated | **OPEN** (a gate reading as satisfied because its precondition was never checked) | §1: renamed to `SatisfiesPriceGate`; the composite `Ad15ClaimGate` takes the prerequisite as a nullable parameter and `null` ⇒ `ad16-screen-not-calculated`, which can never qualify |
| D2 | `StrategyObservationBuilder` keyed the paired intersection on `(CompanyId, DateOnly)` | Two arms' same-day observations paired even when their `WindowEndUtc` knowledge cutoffs differed (partial rerun); the instant was not even persisted onto `EfficacyPoint` | **OPEN** (pairing asserted a sameness that was never checked) | §2: exact-instant projection; a point with no instant fails CLOSED out of the claim path (`ObservationsWithoutAsOfInstant`); differing instants are not paired (`ObservationsWithMismatchedAsOfInstant`) |
| D3 | `PairedComparisonRenderer` rendered the all-history `JointSupport` beside the out-of-sample claim sentence | One label over two quantities; no eligible support and no per-block N were rendered | **OPEN** (in the disclosure, not the computation) | §3: `EligibleJointSupport` (EMPTY when no boundary — never the all-history number), per-block company N, and the blocks CSV |

---

## 2. Enumeration — Comparison path (price outcome)

### `Comparison/ForwardReturn.cs` — is this (company, as-of) observation admissible?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| Entry admission `bar.Date > asOf` (the single admission filter) | No bar strictly after D | **CLOSED** — `NoForwardBar`, named and counted |
| `entry.Date == exit.Date` | Only one forward bar | **CLOSED** — `SingleForwardBar` |
| `exit.Date < minimumExitDate` (spec 152 coverage) | Bars exist but do not reach the horizon | **CLOSED** — `PartialWindow`, its own tally, checked BEFORE the price check |
| `entryPrice <= 0` | Unusable price | **CLOSED** — `NonPositiveEntryPrice` |
| `AdjClose > 0 ? AdjClose : Close` fallback | Zero/negative adjusted close | Fallback to `Close`; if neither positive the observation drops. **CLOSED** (the fallback can only substitute a *present* value, never invent one) |
| Tolerance range guard (`0 ≤ tolerance < horizon`, enforced in the method, not only in options) | A vacuous tolerance | **CLOSED** — throws at the boundary; a tolerance ≥ horizon would have made the coverage check vacuous (the exact fail-open spec 152 removed) |

### `Comparison/StrategyObservationBuilder.cs` — which observations exist?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| Forward-return definedness routes to `usable` vs `withoutForwardPrice`/`partialWindow` | Undefined return | **CLOSED** — excluded with a per-key tally |
| Same-key duplicate (`byKey[key] = …`) | Repeated (company, as-of) | Last-occurrence-wins, deterministic, in BOTH projections. Not an admissibility hole — one observation survives either way. Spec 170 §2.1 explicitly retains this (a throw would be a new fatal condition over unmeasured stores) |
| **Instant projection admission (`point.AsOfInstantUtc is { }`)** — new in this slice | No recorded instant | **CLOSED** — excluded from the claim path and counted (`WithoutAsOfInstant`); never date-paired as a fallback |
| `var asOf = point.AsOfDate ?? point.ScoreDate` | No recorded as-of date | **OPEN-shaped fallback — recorded below as observation O2** |

### `Comparison/StrategyComparisonHarness.cs` (marginal leaderboard) — is a strategy rankable?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| `inSample.Count < MinimumObservations` / `outOfSample.Count < MinimumObservations` | Too little history | **CLOSED** — `InsufficientInSampleObservations` / `InsufficientOutOfSampleObservations`, named per strategy on the result |
| `!inMetric.Correlation.IsDefined` / `!outMetric...` | Degenerate metric | **CLOSED** — `DegenerateInSampleMetric` / `DegenerateOutOfSampleMetric` with the correlation's own reason |
| Chronological split index (floor with 1e-9 nudge, never clamped up) | Tiny history | **CLOSED** — an empty side is honest "insufficient history"; nothing is manufactured to avoid it |

### `Comparison/RankCorrelation.cs` — does a coefficient/interval exist?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| `n < MinimumObservationsFloor` (interval) / `n < 2` (bare ρ) | Too few observations | **CLOSED** — `TooFewObservations`; never a fabricated 0 |
| `sxx <= 0` / `syy <= 0` | Constant vector | **CLOSED** — `ConstantScores` / `ConstantReturns` |
| `|ρ| >= 1` (interval only) | Zero-width interval | **CLOSED** — `PerfectCorrelation` (a zero-width interval would read as certainty) |
| Misaligned vectors | Caller defect | **CLOSED** — throws |

### `Comparison/PairedComparisonHarness.cs` — what can enter the claim, and does the price gate pass?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| Primary name match (`!= 1` match) / primary is a `baseline-*` | Misconfiguration | **CLOSED** — throws naming the strategy and the key |
| Joint intersection: present in EVERY baseline at the **exact instant** (this slice) | Missing anywhere / differing instant | **CLOSED** — not paired; mismatched keys counted |
| Outcome consistency across arms | Forward outcome disagrees | **CLOSED** — dropped and counted (`InconsistentOutcomeObservationsDropped`); never one arm's value silently preferred |
| `observations.Count < MinimumCompaniesPerDate` | Thin cross-section | **CLOSED** — `too-few-companies`, counted |
| Constant primary / constant outcome / constant baseline | Degenerate date | **CLOSED** — date dropped for the WHOLE family with a named reason (and the offending baseline named) |
| Boundary: `options.FirstEligibleAsOf is null` | No precommitted boundary | **CLOSED** — `no-precommitted-evaluation-boundary`; the gate can never pass; `EligibleJointSupport` is EMPTY (this slice), never the all-history figure |
| Purge overlap | Overlapping nominal window | **CLOSED** — skipped and counted `overlapping-outcome-window` |
| Observed-interval non-overlap assertion | A future exit-rule regression | **CLOSED** — throws (a violated assertion is a surfaced defect, not a silent claim) |
| Price gate: interval undefined / median ≤ 0 / lower ≤ 0, per baseline | Insufficient or adverse data | **CLOSED** — structured `Ad15GateReason` per failure; `SatisfiesPriceGate` true only when the reason list is empty |

### `Comparison/PairedComparisonOptions.cs`, `Comparison/StrategyComparisonOptions.cs`

Constructor validation (minimum companies ≥ 2, minimum observations ≥ 4, tolerance range): **CLOSED** —
throws at the config boundary with the offending key named. Unparseable config values are rejected in the
composition root (`RadarWorkerServices`), outside this tree.

### `Claims/*` (new in this slice) — does the composite AD-15 gate qualify?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| `Ad15ClaimGate.Evaluate(..., null)` | No prerequisite supplied | **CLOSED** — `ad16-screen-not-calculated`; qualifying without a prerequisite is unconstructible |
| Outcome switch default arm | Unrecognised `Ad16ScreenOutcome` | **CLOSED** — `ad16-screen-invalid` |
| Inconsistent inputs (`satisfiesPriceGate` true beside non-empty price reasons) | Caller defect | **CLOSED** — cannot qualify |
| `Ad15AttentionPrerequisite.For` on an undefined enum value | Out-of-range outcome | **CLOSED** — coerced to `Invalid`; `WasCalculated` derived structurally (private ctor) |
| `Ad15GateReason` code vocabulary | Unknown code | **CLOSED** — constructor throws (the vocabulary is closed) |
| `PairedComparisonRenderer.RequireConsistent` | A verdict from another result | **CLOSED** — throws; a foreign verdict must not render as a claim |

---

## 3. Enumeration — Statistics helpers (outcome-agnostic)

| File | Predicate | Verdict |
|---|---|---|
| `ExactMedianInterval` | `n < 6` at 95% | **CLOSED** — `InsufficientPurgedBlocks`; the confidence level is never weakened to manufacture an interval; no NaN published |
| `ExactSignTest` | Every delta exactly zero | **CLOSED** — `NoNonZeroDeltas`; zeros are dropped from THIS diagnostic's N only, and the drop is reported |
| `OutcomeWindowPurge` | Inverted interval / unsorted candidates | **CLOSED** — throws (an unsorted caller is a defect to surface, not silently re-sort around) |
| `OutcomeWindowPurge` | Overlapping candidate | **CLOSED** — skipped into its own list; every candidate lands in exactly one of admitted/skipped |

---

## 4. Enumeration — Attention path (AD-16 screen)

### `Attention/AttentionArrivalScreenEvaluator.cs`

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| Prerequisite 1: cohort `!cohort.IsAvailable` | Missing/unreadable/malformed cohort file | **CLOSED** — `CohortConfigurationUnavailable` suppresses the primary status (silently including all companies would violate the 2026-07-31 amendment) |
| Prerequisite 2: another enabled collector can emit third-party `MediaAttention` without a coverage contract | Unprovable coverage source | **CLOSED** — `UnsupportedAttentionCollector` |
| Prerequisite 3: primary arm not configured | Nothing to screen | **CLOSED** — `PrimaryStrategyNotConfigured` |
| Prerequisite 4: cohort ticker resolves to a seeded company whose derivable CIK **differs** | Contradictory declaration | **CLOSED** — suppresses the evaluation naming both CIKs |
| …but: cohort ticker resolves and **no CIK is derivable** | Unverifiable declaration | **OPEN by documented design — recorded below as observation O1** |
| `ResolveCohortCompanyIds`: declared ticker not in the watch universe | Unknown ticker | Excludes nothing — correct and documented (a cohort may name a company before it is seeded); the *exclusion* side cannot fail open because an unwatched company has no snapshots to screen |
| Candidate anchors: `WindowEndUtc` must EXACTLY equal an unfiltered run's `CreatedAtUtc` that recorded the primary arm | No matching anchor | **CLOSED** — not a candidate (and the doc-comment records why "also collected" must NOT be added: it would zero the split deployment) |
| Boundary: `date < FirstEligibleAsOfDateUtc` | Pre-boundary date | **CLOSED** — `BeforeFirstEligibleDate`, counted, companies not even evaluated |
| `SnapshotAt`: exact-instant snapshot lookup | No snapshot at the exact instant | **CLOSED** — `NoPrimarySnapshot` for the company; for a fixed arm, `Incomplete{Control,Baseline}Support` for the WHOLE arm (never computed over a subset) |
| Coverage over comparator and outcome windows | Unprovable coverage | **CLOSED** — `IncompleteAttentionCollection` with the checkpoint sub-reason |
| `included < MinimumCompaniesPerDate` | Thin date | **CLOSED** — `InsufficientCompanies` |
| Degeneracy classification (constant outcome / primary / persistence) | No rank variance | **CLOSED** — each named; a constant secondary/control/baseline can NEVER exclude the date (correct direction: diagnostics cannot veto) |
| `Compose` status: `eligible < 20 ⇒ Pending; else median > 0 ⇒ Clears; else Miss` | Median undefined at ≥ 20 eligible dates | **CLOSED** — falls to `Miss`, not to `Clears`. (Practically unreachable: an eligible date requires a defined δ, so ≥ 1 eligible date ⇒ a defined median. Noted, not changed.) |

### `Attention/AttentionCoverageEvaluator.cs` — can coverage be PROVED for (company, interval)?

Every branch of `Classify` fails **CLOSED**; listed in order:

| Predicate | Verdict |
|---|---|
| `run.CompanyFilter is not null` | **CLOSED** — `PartialCollectionRun` |
| `run.CollectorRuns is null` | **CLOSED** — `ScoreOnlyRunWithoutCollection` / `LegacyCheckpointWithoutCollectorRuns` (null is UNPROVEN, never success — the 2026-08-03 amendment) |
| Attention collector absent from the run | **CLOSED** — `AttentionCollectorDidNotRun` |
| `CompanyCoverage is null` | **CLOSED** — `CompanyCoverageNotRecorded` |
| No row for this company | **CLOSED** — `CompanyNotInCollectionPass` |
| Issue token outside the CLOSED `CollectionCoverageIssues.All` vocabulary | **CLOSED** — `UnrecognizedCoverageIssue` (the PR-#174 fix: an unknown token must not certify the window it was warning about) |
| Counts that cannot describe a real observation | **CLOSED** — `InvalidCoverageCounts` |
| Missing feed / failed feed / capped feed / health mismatch (tokens AND defensive counts) | **CLOSED** — each named; a collector that *forgets* a token still fails closed on the counts |
| Chain rules (no record in span / no opening checkpoint / no closing checkpoint / gap > 36 h) | **CLOSED** — `NoRunRecords` / `NoCheckpointBeforeStart` / `NoCheckpointAfterEnd` / `CheckpointGapExceeded` |
| `NearestDisqualification` returns `None` when nothing ran in the failing window | Informational only — the chain reason already failed the interval; this cannot admit anything |

### `Attention/AttentionPublisherCountBuilder.cs` — does a publisher count exist?

| Predicate | Absent/unknown input | Verdict |
|---|---|---|
| Signal admission (company, `MediaAttention`, `Approved`, exact half-open interval) | Anything else | **CLOSED** — outside the metric |
| Evidence does not resolve | Missing evidence | **CLOSED** — the company-date DROPS (`Unresolved*Evidence`); never read as a lower count |
| `SourceType != NewsArticle` | Non-news evidence | Skipped as outside the metric (not a failure) — correct: a press release is not the market noticing |
| Attribution not `Recorded` **or** not the attention collector (ordinal) | Missing/INFERRED/unsupported attribution | **CLOSED** — drops the company-date; Inferred deliberately counts as unresolved (a precommitted metric must be invariant to `Radar:Scoring:InferLegacyCollectorAttribution`) |
| Publisher metadata blank after canonicalisation | No real outlet | **CLOSED** — drops the company-date (`Missing*Publisher`); the feed-name fallback is never counted |
| Complete window, zero relevant signals | Genuine zero | **Defined 0 stays in the sample** — correct: the central negative case; excluding it would select on the outcome |

### `Attention/CompanyCikIndex.cs`, `Attention/IExcludedCohortStore.cs`

| Predicate | Verdict |
|---|---|
| CIK not derivable from the `sec` feed URL | Company absent from the index — feeds the asymmetric check (see O1) |
| `Normalize` with no digits | `null` — "no declared CIK", which the contradiction check treats as unverifiable, not as a match |
| Cohort store contract: missing directory / unreadable / malformed | **CLOSED** — `ExcludedCohortSet.Unavailable` is a first-class state, never an empty list (the file implementation lives in Infrastructure, outside this tree, against this contract) |

### `EfficacyDatasetBuilder.cs`, `EfficacyReportGenerator.cs` (per-company read/render)

| Predicate | Verdict |
|---|---|
| Blank ticker | **CLOSED** — company skipped (no price series can exist for it); per-company silence, aggregate count logged |
| `FindBarAtOrBefore` finds nothing | Point renders unpaired (null price cells) — chart-only; the comparison path never reads these fields |
| Zero points / zero bars | **CLOSED** — series skipped with a logged reason and counted |

---

## 5. Observations recorded, NOT fixed (phase 2 material)

Per spec 170 §4, nothing below is changed in this slice. Each carries a recommendation.

### O1 — cohort CIK cross-check accepts an unverifiable declaration (deliberate, documented)

`AttentionArrivalScreenEvaluator.FindCohortContradiction` + `CompanyCikIndex`: when a cohort member's ticker
resolves to a seeded company but no CIK can be derived from its feeds, the check `continue`s — "cannot
verify" is deliberately not a contradiction. This is OPEN-shaped: an unverifiable declaration is treated as
consistent. It is documented in code with its rationale (failing the whole evaluation over a feed shape Radar
merely does not recognise would be a false alarm), and its blast radius is narrow — the *exclusion itself
still applies* by ticker; only the wrong-company cross-check is skipped, and every watched company today
carries a standard EDGAR submissions feed the regex recognises. **Recommendation:** acceptable as designed;
if stronger assurance is ever wanted, a spec could add a rendered "unverified cohort member" count to the
attention artifact so the unverified state is visible rather than silent. Does not materially change what is
eligible today.

### O2 — `StrategyObservationBuilder`'s `AsOfDate ?? ScoreDate` fallback anchors a date-projection
observation on the run date when the as-of date is absent

For every store-read point `EfficacyDatasetBuilder` populates `AsOfDate` (since spec 140), so the fallback
fires only for hand-constructed points — but when it fires it silently substitutes `CreatedAtUtc`'s date,
which for a replay-shaped point is the wrong anchor (the exact mislabelling spec 140 documented). The CLAIM
path is no longer exposed to it: spec 170 makes the paired intersection require the exact instant and fail
closed without one. The residual exposure is the marginal (descriptive) leaderboard over hypothetical
`AsOfDate`-less points. **Recommendation:** its own small spec could make the fallback a counted exclusion on
the marginal path too (mirroring `WithoutAsOfInstant`); not done here because it changes what the marginal
leaderboard admits, which is an eligibility change outside this slice's three declared findings.

### O3 — renderer token fallbacks `_ => "unknown"` (cosmetic; decide nothing)

`PairedComparisonRenderer.DropReasonToken`/`IntervalReasonToken`, `StrategyLeaderboardRenderer`'s two token
maps and `EfficacySvgRenderer`'s series-key fallback each render `"unknown"` for an enum value they have
never heard of. These are rendering fallbacks, not admissibility predicates — an unknown value has already
been counted/dropped by the closed logic upstream — so they change nothing about eligibility. Noted for
completeness; no change recommended (a rendering throw would turn a cosmetic unknown into a lost artifact).

---

## 6. Statement of result

**No new fail-open defect that materially changes what is admissible, complete, eligible or qualifying was
found beyond the three findings spec 170 declared.** The three declared findings are fixed by this slice
(§§1–3); observations O1–O3 are open-shaped but either deliberate-and-documented (O1), unreachable on every
production read path (O2), or purely cosmetic (O3), and each carries a recommendation above rather than a fix
here.
