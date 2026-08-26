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
        // strategies resolving to the same config dedupe naturally. Best-effort like the other file stores:
        // the store swallows disk errors, so a failure logs + continues and the snapshots still carry the
        // stamp — it never aborts the run or changes any counter.
        var strategies = _scoringStrategies.Runtimes;
        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            await _scoringConfigStore
                .WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)
                .ConfigureAwait(false);

            var scoreFileStore = _scoreFileStores.ForStrategy(strategy.Definition);

            foreach (var company in companies)
            {
                ct.ThrowIfCancellationRequested();

                var result = await strategy.Engine
                    .ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
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

        // Spec 193 §1: ONE aggregated Warning for the score-snapshot store per run (the spec-145 aggregation
        // precedent), never one line per failure.
        if (snapshotsNotPersisted > 0)
        {
            _logger.LogWarning(
                "{ScoreSnapshotsNotPersisted} score snapshot(s) this run could NOT be durably persisted to "
                    + "the score snapshot store (counted across all {StrategyCount} strategies). They exist "
                    + "in this process's score repository — so this run's report still sees the primary "
                    + "series — but nothing reached disk: the accrued score history does NOT contain them, "
                    + "and the efficacy/replay reads will not see them. The run was not aborted; see the "
                    + "per-write Warnings above for the failing paths.",
                snapshotsNotPersisted,
                strategies.Count);
        }

        return new ScoringPassResult(
            CompaniesScored: companiesScored,
            Strategies: [.. strategies.Select(s => s.Definition.Name)],
            PrimaryStrategy: _scoringStrategies.Primary.Definition.Name,
            ScoreSnapshotsNotPersisted: snapshotsNotPersisted);
    }
}
