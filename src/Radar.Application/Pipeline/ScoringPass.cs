using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Companies;

namespace Radar.Application.Pipeline;

/// <summary>
/// Stage 6, extracted verbatim from <see cref="RadarPipelineRunner"/> (spec 144). One instance serves the
/// combined run and the standalone <c>score</c> pass, so there is exactly one stage-6 loop in the codebase.
/// <para>
/// It deliberately takes no collector, mapper, extractor, resolver, reviewer or AI dependency: a scoring pass
/// that cannot reach a collector cannot accidentally collect.
/// </para>
/// </summary>
public sealed class ScoringPass : IScoringPass
{
    private readonly IScoringStrategyFactory _scoringStrategies;
    private readonly IScoreSnapshotFileStoreFactory _scoreFileStores;
    private readonly IScoringConfigStore _scoringConfigStore;
    private readonly ILogger<ScoringPass> _logger;

    public ScoringPass(
        IScoringStrategyFactory scoringStrategies,
        IScoreSnapshotFileStoreFactory scoreFileStores,
        IScoringConfigStore scoringConfigStore,
        ILogger<ScoringPass> logger)
    {
        ArgumentNullException.ThrowIfNull(scoringStrategies);
        ArgumentNullException.ThrowIfNull(scoreFileStores);
        ArgumentNullException.ThrowIfNull(scoringConfigStore);
        ArgumentNullException.ThrowIfNull(logger);

        _scoringStrategies = scoringStrategies;
        _scoreFileStores = scoreFileStores;
        _scoringConfigStore = scoringConfigStore;
        _logger = logger;
    }

    public async Task<ScoringPassResult> RunAsync(
        IReadOnlyList<Company> companies, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(companies);
        ct.ThrowIfCancellationRequested();

        var companiesScored = 0;

        // Spec 193 §1: snapshots computed but NOT durably persisted, summed across EVERY strategy (see
        // ScoringPassResult for why this axis is not primary-only).
        var snapshotsNotPersisted = 0;

        // Spec 201 §1: per-strategy effective-config files that did NOT land. Since spec 202 §1 such a
        // strategy is skipped for the pass (below), so this counts strategies that scored NOTHING this pass.
        var scoringConfigsNotPersisted = 0;

        // Spec 197 §3: ScoringEngine is ONE STRATEGY, so its former per-company Warnings for unresolved
        // evidence (spec 145) and neutralized accrued news directions (spec 194 §1.4) were really per
        // strategy × company — 460 lines on the live baseline, burying the exceptional failures. The engine
        // now RETURNS those counts and this pass, which can see the whole strategy × company grid, emits at
        // most ONE Warning per category for the entire pass, labelling the population honestly. A pass with
        // nothing to report logs nothing at all.
        var assemblyDiagnostics = new ScoreAssemblyDiagnosticsAggregator("Scoring pass");

        // Stage 6: score every company at asOfUtc, once PER CONFIGURED STRATEGY (spec 137) — strategies ×
        // companies. EVERYTHING ABOVE THIS POINT RUNS EXACTLY ONCE: collection, the AI directional read,
        // extraction, resolution, review and signal persistence are shared, strategy-independent work, and
        // the whole point of the slice is one collection pass feeding N independently-stamped scorings. The
        // strategy loop therefore starts here, after signal persistence, and never earlier.
        //
        // Each strategy is one already-configured engine instance (one engine IS one strategy): it applies
        // the window/Approved-only filter and writes its own snapshot + links; the pass does not pre-filter
        // which companies have signals (a company with no in-window signals yields a valid neutral snapshot).
        // The caller supplies the company list (the combined run reuses the one the collection pass loaded, so
        // it is still a single repository read per run).
        // The per-strategy score file store mirrors each snapshot + its links to disk (AD-8), the durable twin
        // of that strategy's score repository — the PRIMARY strategy writes to the existing location, so the
        // efficacy read, the report and all accrued history are untouched. Snapshots are upsert-by-Id (the
        // store overwrites last-write-wins), and the store swallows disk errors, so this must not change any
        // counter or abort the run.
        //
        // companiesScored deliberately counts the PRIMARY strategy's companies only, so the run record's
        // counter keeps its established meaning ("how many companies were scored this run") instead of
        // silently multiplying by the strategy count. Neutral zero-signal snapshots still count.
        //
        // The effective scoring config is identical for every company within a strategy (same engine,
        // formula, weights, tier map), so persist it ONCE PER STRATEGY — not per company — content-addressed
        // by its fingerprint (insert-if-new), so a historical snapshot's ScoringConfigVersion stamp
        // dereferences back to the exact weights (provenance completion, AD-10-as-amended, spec 91). Two
        // strategies resolving to the same config dedupe naturally. The store swallows disk errors and a
        // failure never aborts the run — but since spec 202 §1 it DOES change what is written: see the
        // durability precondition inside the loop.
        var strategies = _scoringStrategies.Runtimes;

        // Spec 202 §1: the strategies this pass SKIPPED because their config record is not durable, in run
        // order. Null on the result when none was skipped — never an empty list standing in for "none".
        List<string>? strategiesSkipped = null;

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            // Spec 202 §1 — DURABILITY PRECONDITION. A snapshot's ScoringConfigVersion must dereference to a
            // record on disk (the spec-91 provenance chain; spec 148 Part B closed this for replay). Until spec 202
            // the forward path merely COUNTED a failed config write and scored anyway, so snapshots could
            // carry a stamp that dereferenced to nothing. Now a strategy whose record did not land this pass
            // writes NO snapshot: it is counted, named, and simply retried by the next run — the store is
            // content-addressed and insert-if-new, so nothing has to be repaired. Every OTHER strategy still
            // scores; a strategy whose record was already on disk (AlreadyAvailable) is durable and proceeds.
            var configWrite = await _scoringConfigStore
                .WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)
                .ConfigureAwait(false);
            if (!configWrite.Written)
            {
                scoringConfigsNotPersisted++;
                (strategiesSkipped ??= []).Add(strategy.Definition.Name);
                continue;
            }

            var scoreFileStore = _scoreFileStores.ForStrategy(strategy.Definition);

            foreach (var company in companies)
            {
                ct.ThrowIfCancellationRequested();

                var result = await strategy.Engine
                    .ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
                assemblyDiagnostics.Record(
                    strategy.Definition.Name, company.Id, asOfUtc, result.Diagnostics);
                var durable = await scoreFileStore
                    .WriteAsync(result.Snapshot, result.Links, ct).ConfigureAwait(false);
                if (durable.Outcome == DurableWriteOutcome.Failed)
                {
                    snapshotsNotPersisted++;
                }

                if (strategy.Definition.IsPrimary)
                {
                    companiesScored++;
                }
            }
        }

        // Spec 197 §3: at most one Warning per diagnostic category for the WHOLE pass. Emitted before the
        // spec-193 store Warning below purely to keep the two blocks' order stable; they are independent
        // facts (what could not be assembled vs what could not be written) and are never pooled.
        assemblyDiagnostics.LogAggregates(_logger);

        // Spec 193 §1: ONE aggregated Warning for the score-snapshot store per run (the spec-145 aggregation
        // precedent), never one line per failure.
        if (snapshotsNotPersisted > 0)
        {
            _logger.LogWarning(
                "{ScoreSnapshotsNotPersisted} score snapshot(s) this run could NOT be durably persisted to "
                    + "the score snapshot store (counted across all {StrategyCount} strategies). They exist "
                    + "in this process's score repository — so this run's report still sees the primary "
                    + "series — but nothing reached disk: the accrued score history does NOT contain them, "
                    + "and the efficacy/replay reads will not see them. The run was not aborted. This "
                    + "Warning is the ONLY report of these failures (spec 195 §1): the store no longer logs "
                    + "a Warning per failed file, so raise the score-snapshot-store log level to Debug to "
                    + "see the attempted paths.",
                snapshotsNotPersisted,
                strategies.Count);
        }

        // Spec 201 §1 / 202 §1: ONE aggregated Warning for the scoring-config store per pass, never one per
        // strategy, naming the strategies the precondition skipped. The count and the name list are the same
        // fact (a not-persisted config IS a skipped strategy), so one line carries both rather than two lines
        // the reader would have to reconcile.
        if (strategiesSkipped is not null)
        {
            _logger.LogWarning(
                "{ScoringConfigsNotPersisted} effective scoring config file(s) this run could NOT be durably "
                    + "persisted to the scoring-config store (of {StrategyCount} strategies). The affected "
                    + "strategies were SKIPPED for this pass and NO snapshot was written under them: "
                    + "{StrategiesSkipped}. A snapshot whose ScoringConfigVersion dereferences to nothing on "
                    + "disk is not written (durability precondition, spec 202 §1). The next run retries "
                    + "naturally — the store is content-addressed and insert-if-new — and the run was not "
                    + "aborted; every other strategy scored.",
                scoringConfigsNotPersisted,
                strategies.Count,
                string.Join(", ", strategiesSkipped));
        }

        return new ScoringPassResult(
            CompaniesScored: companiesScored,
            Strategies: [.. strategies.Select(s => s.Definition.Name)],
            PrimaryStrategy: _scoringStrategies.Primary.Definition.Name,
            ScoreSnapshotsNotPersisted: snapshotsNotPersisted,
            ScoringConfigsNotPersisted: scoringConfigsNotPersisted,
            StrategiesSkippedForUnpersistedConfig: strategiesSkipped);
    }
}
