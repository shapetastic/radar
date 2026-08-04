using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Prices;
using Radar.Application.Scoring;

namespace Radar.Application.Efficacy;

/// <summary>
/// The deterministic, no-look-ahead JOIN (AD-14 read side): for each seeded company with a non-blank ticker it
/// reads the company's persisted score-snapshot history (<see cref="IScoreSnapshotFileStore"/>) and its daily
/// price series (<see cref="IPriceHistoryStore"/>), pairing each score to the price bar at-or-before its date
/// (NEVER a future bar). It is <b>read-only over score history + price</b> — it depends on no
/// evidence/signal/scoring <i>write</i> path, produces no evidence/signal/score, and writes nothing. Pure over
/// the stores' output: the same persisted data yields the same dataset.
/// </summary>
public sealed class EfficacyDatasetBuilder
{
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoreSnapshotFileStore _scoreStore;
    private readonly IPriceHistoryStore _priceStore;
    private readonly ILogger<EfficacyDatasetBuilder> _logger;

    public EfficacyDatasetBuilder(
        ICompanyRepository companyRepository,
        IScoreSnapshotFileStore scoreStore,
        IPriceHistoryStore priceStore,
        ILogger<EfficacyDatasetBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(scoreStore);
        ArgumentNullException.ThrowIfNull(priceStore);
        ArgumentNullException.ThrowIfNull(logger);

        _companyRepository = companyRepository;
        _scoreStore = scoreStore;
        _priceStore = priceStore;
        _logger = logger;
    }

    /// <summary>
    /// Builds the dataset over the REGISTERED score-snapshot store — the spec-101/108 forward series. Exactly
    /// <c>BuildAsync(_scoreStore, ct)</c>; there is one join implementation, not two.
    /// </summary>
    public Task<IReadOnlyList<CompanyEfficacySeries>> BuildAsync(CancellationToken ct) =>
        BuildAsync(_scoreStore, ct);

    /// <summary>
    /// Builds the dataset over an EXPLICIT score-snapshot store (spec 140), so the same deterministic
    /// no-look-ahead join can be run per scoring strategy — the strategy-scoped stores handed out by
    /// <c>IScoreSnapshotFileStoreFactory</c> / <c>IReplayScoreSnapshotFileStoreFactory</c> — instead of only
    /// over the registered one. Company universe, price read and join semantics are identical; only WHICH
    /// score series is read differs, so the primary strategy's store reproduces the existing single-series
    /// read by construction.
    /// </summary>
    public async Task<IReadOnlyList<CompanyEfficacySeries>> BuildAsync(
        IScoreSnapshotFileStore scoreStore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scoreStore);

        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var series = new List<CompanyEfficacySeries>(companies.Count);

        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            var ticker = company.Ticker?.Trim();
            if (string.IsNullOrEmpty(ticker))
            {
                // A company without a ticker has no price series to join against — skip it.
                continue;
            }

            var snapshots = await scoreStore.ReadAllForCompanyAsync(company.Id, ct).ConfigureAwait(false);
            var history = await _priceStore.ReadAsync(ticker, ct).ConfigureAwait(false);
            var bars = history?.Bars ?? [];

            var points = new List<EfficacyPoint>(snapshots.Count);
            foreach (var snapshot in snapshots)
            {
                // AD-3/AD-7: the run instant (UTC) is the series-point timestamp.
                var scoreDate = DateOnly.FromDateTime(snapshot.CreatedAtUtc.UtcDateTime);
                var bar = FindBarAtOrBefore(bars, scoreDate);

                points.Add(new EfficacyPoint(
                    ScoreDate: scoreDate,
                    TrajectoryScore: snapshot.TrajectoryScore,
                    OpportunityScore: snapshot.OpportunityScore,
                    AttentionScore: snapshot.AttentionScore,
                    EvidenceConfidenceScore: snapshot.EvidenceConfidenceScore,
                    SignalVelocityScore: snapshot.SignalVelocityScore,
                    // Spec 141: the SERIES key is the strategy name (legacy null ⇒ the primary "default"
                    // series, so pre-137 history is not orphaned); the fingerprint rides along as recorded
                    // provenance the renderer still annotates but no longer segments on.
                    SeriesKey: ScoreSeriesKey.For(snapshot),
                    ScoringConfigVersion: snapshot.ScoringConfigVersion,
                    PriceAsOfDate: bar?.Date,
                    PriceClose: bar?.Close,
                    PriceAdjClose: bar?.AdjClose)
                {
                    // Spec 140: the knowledge-window end — the honest anchor for a forward-return horizon.
                    // Equal to ScoreDate on a forward run; the simulated as-of on a replay snapshot.
                    AsOfDate = DateOnly.FromDateTime(snapshot.WindowEndUtc.UtcDateTime),
                    // Spec 170: the SAME fact at full precision, for exact-instant pairing in the paired
                    // comparison. Read by no per-company renderer and not by the marginal leaderboard.
                    AsOfInstantUtc = snapshot.WindowEndUtc,
                });
            }

            series.Add(new CompanyEfficacySeries(
                CompanyId: company.Id,
                CompanyName: company.Name,
                Ticker: ticker,
                Points: points,
                PriceBars: bars));
        }

        _logger.LogInformation(
            "Efficacy dataset built: {SeriesCount} series (companies with a non-blank ticker).",
            series.Count);

        return series;
    }

    /// <summary>
    /// Returns the price bar with the greatest <see cref="PriceBar.Date"/> that is at-or-before
    /// <paramref name="scoreDate"/> (NO LOOK-AHEAD), or <c>null</c> when the score predates all bars / there is
    /// no price history. Bars are ascending by date, so the last bar not past the score date is the answer.
    /// </summary>
    private static PriceBar? FindBarAtOrBefore(IReadOnlyList<PriceBar> bars, DateOnly scoreDate)
    {
        // Binary search over the ascending-by-date list: find the LAST bar with Date <= scoreDate.
        var lo = 0;
        var hi = bars.Count - 1;
        PriceBar? best = null;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (bars[mid].Date <= scoreDate)
            {
                best = bars[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }
}
