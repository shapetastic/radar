# Task: Surface live strategy leaders before outcome efficacy matures

## Overview

Radar already scores every configured strategy on every full run. The 2026-08-21 report proves that the
data exists: all ten strategies scored 74 companies, and the complete per-strategy rankings are appended
after roughly 3,700 lines of primary-strategy narrative. But the report's prominent `Highest opportunity`,
movement and action sections remain `default`-only, while the efficacy leaderboard correctly drops the nine
younger strategies until their forward outcomes are mature enough for its in-sample window.

Those two honest decisions combine into a product failure: **a live experimental strategy can surface a
company today, the company can move before the 21-day outcome matures, and the operator can miss the signal
because the only visible front-of-report ranking belongs to `default`.** Waiting for an outcome is required
to judge efficacy; it is not a reason to hide the prediction that is being judged.

This slice adds one compact `Live strategy leaders` section near the top of the weekly report. It shows the
current top five within every configured strategy and the exact scoring cutoff. It also makes the efficacy
leaderboard say explicitly that a strategy dropped from efficacy ranking may still be scoring live.

This is **observability, not validation**. It answers:

- what each strategy is saying now;
- whether it produces a discriminating ranking rather than a constant score;
- which live candidates need research before their outcomes exist.

It does not answer whether a strategy predicts returns. That remains the read-only efficacy path, with its
unchanged 21-day horizon and AD-15 boundary.

## Assignment

Worktree: any  
Dependencies: spec 150 merged (per-strategy report sections).  
Estimated time: ~3–4 hours (including the new strategy-entry fail-open guard and golden updates).

## 1. Three states that must never be conflated

The rendered language must distinguish these states exactly:

| State | Forward price required? | Meaning |
| --- | --- | --- |
| Live strategy score | No | The strategy's current ranked prediction; show immediately. |
| Descriptive efficacy observation | Yes | The declared forward outcome exists and can be compared with the stored score. |
| AD-15 claim support | Yes, plus the precommitted boundary/purge/gate | Eligible evidence for the paired confirmatory comparison. |

The live section must contain this sentence, or byte-equivalent wording with the same meaning:

> Live scores are shown immediately and are never gated on a future price. Forward outcomes are required
> only to evaluate the strategy later; these rankings are not efficacy results.

Do not call an immature strategy "unscored" or "unavailable" when score snapshots exist. In the efficacy
leaderboard, preserve the existing count and change the heading exactly from `## Dropped strategies (N)` to
**`## Dropped from efficacy ranking (N)`**. Immediately below it add:

> A strategy listed here may still be scoring every company live; this section means only that its declared
> forward-outcome sample cannot yet be ranked.

No statistical rule, exclusion count or dropped-strategy reason changes.

## 2. Explicit research/comparator purpose — never infer it from a name

Add a closed reporting-purpose value to the already-resolved strategy definition:

```csharp
public enum StrategyPurpose
{
    Research,
    Comparator
}
```

`ScoringStrategyDefinition` gains an additive init-only `Purpose`, defaulting to `Research`. Bind the
optional `Radar:Strategies[i]:Purpose` case-insensitively and fail fast on an unknown value, naming the exact
configuration path and the two valid values.

There is currently **no allowlist on a `Radar:Strategies[i]` entry**: specs 149/174 guard the nested inline
`Weights` and named-profile/options sections, but the entry loop itself reads known children and silently
ignores every sibling. Create that entry-level fail-open guard now. The complete valid key set is exactly:

```text
Name, ScoringProfile, Weights, SignalTypes, Formula, Channels, Purpose
```

Match keys case-insensitively, exactly as `IConfiguration` binding does. An unknown key names its exact
`Radar:Strategies:{index}:{key}` path and the valid set, so `Purpsoe` cannot silently become `Research` and
the already-dangerous `SignalTypess` shape can no longer silently widen a strategy to all signal types. This
is a deliberate startup-behaviour tightening across every strategy entry, not an extension of a
spec-149/174 entry allowlist that already exists. The shipped `default.json` is clean and needs no repair.

Update the shipped `default.json` deliberately:

- `Comparator`: `baseline-earnings-only`, `baseline-activity-only`, `baseline-media-only`,
  `disclosure-led-v10-control`;
- `Research`: every other strategy (the default is sufficient and need not be written redundantly).

`IsPrimary` remains an independent property, but a configured primary with `Purpose = Comparator` is an
invalid strategy set and fails startup naming both the primary and the remedy. The primary owns the Radar
narrative and labels, so silently displaying a comparator primary under Research would make the config say
two contradictory things. A valid primary is labelled `primary research`.

Purpose is report metadata only:

- it is **not** a score input;
- it is excluded from `ScoringConfigVersion` and from strategy-series identity;
- changing it moves no fingerprint and creates no efficacy segment;
- it changes no strategy order, score, label or stored snapshot; and
- `StrategyIdentityGuard` must not treat a purpose-only edit as a scoring-identity change.

Do not use a `baseline-` prefix test, a `-control` suffix test, formula type, or a hard-coded name set. Those
would create a second, drifting definition of which arms are comparators.

## 3. Reuse the spec-150 model exactly — no second read

`WeeklyReportBuilder.BuildStrategySectionsAsync` already reads every strategy's snapshots once, selects the
latest in-period snapshot per company, fetches its links and produces the ranked `StrategyReportSection`.
The live summary must consume that same result. It must not invoke scoring, reopen score files, read prices,
or add another strategy-store traversal.

Do not add previous-score or previous-rank movement in this slice. The production `IScoreRepository` is
in-memory and contains only the current process; cross-run movement requires the strategy-scoped
`IScoreSnapshotFileStoreFactory` read path. Adding up to 50 historical file reads to solve a visibility
problem would widen the slice without helping the operator see today's leaders. The primary narrative's
existing movement remains available for `default`; per-strategy movement can be a later, separately bounded
feature if live use shows it is needed.

The compact summary is a second rendering of the first five existing `StrategyReportSection.Rows`, not a
second construction of them. `StrategyReportRow` remains the provenance source: every value comes from its
attached current `CompanyScoreSnapshot`, and its existing snapshot/company guardrails run once before any
section is rendered.

Thread `Purpose` onto `StrategyReportSection` as an additive property (default `Research` for compatibility
at existing construction sites), and populate it from `runtime.Definition.Purpose` inside
`BuildStrategySectionsAsync`. The renderer sees `WeeklyReportModel`, not strategy runtimes; grouping must use
this carried value and must not infer purpose from a name, formula or channel.

## 4. Render the compact summary before `Highest opportunity`

When `WeeklyReportModel.Strategies` is non-null, render `## Live strategy leaders` immediately after the
standing disclaimers and before `## Highest opportunity`. The existing detailed primary narrative and the
complete spec-150 strategy tables remain unchanged and stay in their current locations.

Render two subsections in this order:

1. `### Research arms` — primary first, then the remaining research strategies in configured order.
2. `### Comparators — diagnostic only` — comparator strategies in configured order.

Render **one combined table per subsection**. For each strategy, append at most its first five existing
`StrategyReportSection.Rows` to that subsection's table:

```markdown
| strategy | rank | company | ticker | Opportunity | as-of UTC |
| --- | ---: | --- | --- | ---: | --- |
| narrative-led-v2 | 1 | Aehr Test Systems | AEHR | 29 | 2026-08-21 22:15Z |
```

Rules:

- Rank is the existing within-strategy rank. Never create a merged or cross-strategy rank.
- `as-of UTC` is the current snapshot's exact `WindowEndUtc`, not the report date and not
  `CreatedAtUtc`. Render `yyyy-MM-dd HH:mmZ` invariantly. Two rows with different knowledge cutoffs must be
  visibly different rather than reading as one synchronized table.
- Escape Markdown table cells through the renderer's one existing escaping helper; do not copy it.
- Five is a presentation constant for this compact section, not a scoring threshold or configuration knob.
  If a section exposes fewer than five evidence-linked rows, render what exists and never manufacture rows.
- A strategy with zero surfaced rows is retained in its subsection table as
  `| <strategy> | — | No evidence-linked live scores in this report window. | — | — | — |`. An empty
  experimental arm is a result, not grounds to omit the arm.
- The summary uses the already capped/evidence-filtered spec-150 rows. It does not weaken spec 53 by
  surfacing a zero-link score.

The first subsection repeats the fixed honesty line from §1 and adds:

> Scores and score magnitudes are comparable only within the same strategy. Repeated company names across
> arms are not a consensus signal.

The comparator subsection adds:

> Comparators are displayed to diagnose what the research arms may merely be reproducing. A comparator
> leader is not a Radar candidate.

## 5. No consensus, no inversion, no anecdotal winner

This slice deliberately does **not** calculate:

- number of strategies containing a company;
- average rank, Borda score, merged rank or consensus score;
- agreement/disagreement badges;
- a Watch/Ignore label for a non-primary strategy;
- a common raw-score threshold such as Opportunity ≥ 40 across formulas;
- an inverse or contrarian strategy; or
- 5/10-day returns selected after seeing an interesting company.

The current EOSE result illustrates the boundary. EOSE leading several related filings arms is valuable
live observability and a reason to inspect those arms. It is neither corroboration—the arms share evidence
and formula ancestry—nor a reason to invert them after one memorable outcome. If an arm systematically
ranks future losers highly, the existing efficacy coefficients will become negative; that is the measured
case for a separately named inverse hypothesis, declared prospectively, not an anecdotal edit here.

## 6. Efficacy and precommitments are untouched

- No price is read by reporting and price remains forbidden as a scoring input (AD-14).
- The 21-day forward horizon, four-day exit tolerance and partial-window exclusions are unchanged.
- The AD-16 attention outcome, 2026-09-29 first eligible date, minimum support and coverage rules are
  unchanged.
- The AD-15 primary, baselines, purge, interval, composite gate and 2026-09-29 claim boundary are unchanged.
- The live section may be read immediately, including before 2026-09-29. It is explicitly outside both
  claim families.
- No score, fingerprint, formula version, rule-set version, strategy name or stored artifact is rewritten.

## Files to inspect

- `src/Radar.Application/Scoring/ScoringStrategyDefinition.cs`
- `src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- `src/Radar.Application/Reporting/WeeklyReportBuilder.cs`
- `src/Radar.Application/Reporting/StrategyReportSection.cs`
- `src/Radar.Application/Reporting/StrategyReportRow.cs`
- `src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs`
- `src/Radar.Application/Reporting/WeeklyReportModel.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyLeaderboardRenderer.cs`
- `scripts/run-profiles/default.json`
- the existing spec-149/174 configuration-guard, spec-150 strategy-section and renderer golden tests

## Tests

- Builder/model: the live summary is derived from the existing strategy sections; `Purpose` is carried from
  the runtime definition onto each section; no additional repository or file-store read occurs.
- Provenance guards: mismatched current snapshot id/company still fail loudly before either rendering.
- Renderer: summary occurs before `Highest opportunity`; each subsection has one combined table; research
  and comparator grouping follows configured order; primary is first; top-five cap is per strategy.
- Exact cutoff: `WindowEndUtc`, including time, renders; `CreatedAtUtc` cannot accidentally substitute.
- Empty arm: strategy name plus the explicit empty message renders.
- Honesty: the no-forward-price and no-cross-strategy-comparison wording is pinned.
- Strategy-entry guard: all seven valid keys bind case-insensitively; every unknown sibling (including
  `Purpsoe` and `SignalTypess`) fails fast with the exact indexed path and valid set; existing default and
  overlay profiles remain clean.
- Purpose binding: absent ⇒ `Research`; valid values bind case-insensitively; unknown token fails fast with
  the exact path and remedy; a Comparator primary fails startup; purpose-only edits do not move fingerprints
  or trip `StrategyIdentityGuard`.
- Efficacy renderer: heading is exactly `Dropped from efficacy ranking (N)`, preserving the count, and every
  numeric field and reason remains byte-identical.
- Single-strategy compatibility: because `Strategies` remains null, the entire report stays byte-identical
  to the current single-strategy golden output.
- Existing full spec-150 sections remain present and in the same configured order; labels, evidence and
  `why noticed` remain primary-only.

Do not run tests concurrently with another agent's solution-wide test run. At implementation handoff:
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` must pass in one
coordinated test session.

## Acceptance criteria

- [ ] Every configured strategy's current leaders are visible near the top of the report on the run that
      produced them; no future price or efficacy eligibility can suppress the section.
- [ ] Research arms and comparators are classified by explicit, fail-closed metadata, never name inference;
      the shipped four comparators are marked.
- [ ] Every strategy entry is guarded by the seven-key case-insensitive allowlist; a Comparator primary is
      rejected rather than silently displayed as research.
- [ ] Each live row shows existing within-strategy rank, current Opportunity and exact `WindowEndUtc`, all
      backed by the existing cited current snapshot.
- [ ] The summary composes no ranks or scores across strategies and assigns no non-primary labels.
- [ ] Efficacy dropped wording no longer reads as if the strategy failed to score live; statistics unchanged.
- [ ] Single-strategy output is byte-identical; complete spec-150 tables remain; no extra repository pass.
- [ ] No scoring/precommitment/fingerprint/store change; build and coordinated tests green.
