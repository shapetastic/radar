using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Composes the spec-140 comparison: for every configured strategy it runs the SAME deterministic
/// no-look-ahead join the per-company efficacy read uses (<see cref="EfficacyDatasetBuilder"/>) over that
/// strategy's own score store, hands the resulting series to the pure
/// <see cref="StrategyComparisonHarness"/>, and writes the rendered leaderboard.
/// <para>
/// <b>No second read path and no second join.</b> The strategy series come from the existing
/// <see cref="IStrategyScoreSnapshotStoreSelector"/> seam over the existing stores, and the price side comes
/// from the existing price reference store the builder already reads — this slice adds no price source
/// (spec 140 forbids a second one).
/// </para>
/// <para>
/// <b>Insufficient history is a result, not an error.</b> With too little joined data the harness returns a
/// leaderboard with zero ranked strategies and every candidate named as dropped; that is written out honestly
/// rather than skipped, so "we cannot tell yet" is visible instead of silent.
/// </para>
/// </summary>
public sealed class StrategyComparisonReportGenerator : IStrategyComparisonReportGenerator
{
    private readonly ScoringStrategySet _strategies;
    private readonly IStrategyScoreSnapshotStoreSelector _stores;
    private readonly EfficacyDatasetBuilder _builder;
    private readonly StrategyComparisonHarness _harness;
    private readonly StrategyLeaderboardRenderer _renderer;
    private readonly IEfficacyArtifactStore _artifactStore;
    private readonly StrategyComparisonOptions _options;
    private readonly ILogger<StrategyComparisonReportGenerator> _logger;

    public StrategyComparisonReportGenerator(
        ScoringStrategySet strategies,
        IStrategyScoreSnapshotStoreSelector stores,
        EfficacyDatasetBuilder builder,
        StrategyComparisonHarness harness,
        StrategyLeaderboardRenderer renderer,
        IEfficacyArtifactStore artifactStore,
        StrategyComparisonOptions options,
        ILogger<StrategyComparisonReportGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _strategies = strategies;
        _stores = stores;
        _builder = builder;
        _harness = harness;
        _renderer = renderer;
        _artifactStore = artifactStore;
        _options = options;
        _logger = logger;
    }

    public async Task<StrategyLeaderboard> GenerateAsync(CancellationToken ct)
    {
        var series = new List<StrategyScoreSeries>(_strategies.Strategies.Count);
        foreach (var strategy in _strategies.Strategies)
        {
            ct.ThrowIfCancellationRequested();

            var store = _stores.ForStrategy(strategy);
            var companies = await _builder.BuildAsync(store, ct).ConfigureAwait(false);
            series.Add(new StrategyScoreSeries(strategy.Name, companies));
        }

        var leaderboard = _harness.Compare(series, _options);

        var csv = _renderer.RenderCsv(leaderboard);
        var markdown = _renderer.RenderMarkdown(leaderboard);
        await _artifactStore.WriteLeaderboardAsync(csv, markdown, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Strategy comparison over {Series}: {Compared} of {Considered} strateg(ies) ranked, "
                + "{Dropped} dropped, {InSampleDates} in-sample / {OutOfSampleDates} out-of-sample as-of "
                + "date(s). Headline (out-of-sample): {Headline}.",
            _stores.SeriesDescription,
            leaderboard.StrategiesCompared,
            leaderboard.StrategiesConsidered,
            leaderboard.DroppedStrategies.Count,
            leaderboard.Windows.InSampleAsOfDates,
            leaderboard.Windows.OutOfSampleAsOfDates,
            leaderboard.Headline?.StrategyName ?? "(none — insufficient history)");

        foreach (var drop in leaderboard.DroppedStrategies)
        {
            _logger.LogInformation(
                "Strategy comparison dropped '{Strategy}': {Reason} ({InSample} in-sample / {OutOfSample} "
                    + "out-of-sample observation(s), metric {MetricReason}).",
                drop.StrategyName,
                drop.Reason,
                drop.InSampleObservations,
                drop.OutOfSampleObservations,
                drop.MetricReason);
        }

        return leaderboard;
    }
}
