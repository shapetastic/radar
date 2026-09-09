using Microsoft.Extensions.Logging;

using Radar.Application.Acquisitions;
using Radar.Application.Prices;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The production <see cref="IUniverseBenchmarkProvider"/>: reads the frozen artifact through
/// <see cref="IBenchmarkUniverseSource"/> and each member's price series through the EXISTING
/// <see cref="IPriceHistoryStore"/> — keyed by the artifact's own <c>priceSeriesKey</c>, never by anything
/// looked up in the mutable seed list (spec 183 §1). No second price source is introduced (spec 140 forbids
/// one). Loaded lazily, once per process, and cached, so the leaderboard and the news-risk evaluator share
/// one <see cref="UniverseBenchmark"/> instance and therefore one per-day computation.
/// </summary>
public sealed class UniverseBenchmarkProvider : IUniverseBenchmarkProvider
{
    private readonly IBenchmarkUniverseSource _source;
    private readonly IPriceHistoryStore _priceStore;

    // Spec 217 §3: the run-time acquisitions projection excess-vs-universe-v2 removes pinned members with.
    // REQUIRED (the Application library registers the inert NoPendingAcquisitionSource), because a silently
    // absent seam would leave a taken-out member in the peer mean while every test stayed green.
    private readonly IPendingAcquisitionSource _pendingAcquisitions;
    private readonly ILogger<UniverseBenchmarkProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private UniverseBenchmark? _benchmark;

    public UniverseBenchmarkProvider(
        IBenchmarkUniverseSource source,
        IPriceHistoryStore priceStore,
        IPendingAcquisitionSource pendingAcquisitions,
        ILogger<UniverseBenchmarkProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(priceStore);
        ArgumentNullException.ThrowIfNull(pendingAcquisitions);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _priceStore = priceStore;
        _pendingAcquisitions = pendingAcquisitions;
        _logger = logger;
    }

    /// <summary>
    /// SPEC 217 §3: the projection the cached benchmark was built with, exposed so the leaderboard harness
    /// applies the OBSERVATION exclusion over exactly the same acquisitions the PEER-MEAN exclusion used.
    /// Two independent reads could disagree if a recognition landed between them; one read cannot.
    /// </summary>
    public PendingAcquisitions Acquisitions { get; private set; } = PendingAcquisitions.None;

    public async Task<UniverseBenchmark?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return _benchmark;
            }

            // Read ONCE, before the universe, so both the null-universe path and the loaded path leave the
            // same projection on this provider for the harness to reuse.
            Acquisitions = await _pendingAcquisitions.GetAsync(ct).ConfigureAwait(false);

            var universe = await _source.ReadAsync(ct).ConfigureAwait(false);
            if (universe is null)
            {
                // The source already logged the specific failure; record the consequence once here.
                _logger.LogWarning(
                    "Benchmark universe unavailable — every excess forward return this process computes will "
                        + "be recorded as BenchmarkUnavailable (never a silent raw fallback).");
                _loaded = true;
                _benchmark = null;
                return null;
            }

            var bars = new Dictionary<string, IReadOnlyList<PriceBar>>(StringComparer.Ordinal);
            var membersWithoutPrice = 0;
            foreach (var member in universe.Members)
            {
                ct.ThrowIfCancellationRequested();
                var history = await _priceStore.ReadAsync(member.PriceSeriesKey, ct).ConfigureAwait(false);
                if (history is null || history.Bars.Count == 0)
                {
                    // The member STAYS in the denominator (spec 183 §2) — it resolves to NoForwardBar on
                    // every date and is recorded, per day, as unresolved with that reason.
                    membersWithoutPrice++;
                    continue;
                }

                bars[member.PriceSeriesKey] = history.Bars;
            }

            _logger.LogInformation(
                "Benchmark universe '{Version}' loaded: {Members} member(s), content hash {Hash}, frozen at "
                    + "{FrozenAt:yyyy-MM-dd}; {WithoutPrice} member(s) currently have no price series (they "
                    + "stay in the coverage denominator).",
                universe.UniverseVersion,
                universe.Members.Count,
                universe.ContentHash,
                universe.FrozenAtUtc,
                membersWithoutPrice);

            // Spec 217 §3: the v2 rule's effect on the pond, stated once per load. A measured zero is
            // information — it says the rule ran and found nothing, not that nobody looked.
            var pinnedMembers = universe.Members.Count(m => Acquisitions.For(m.CompanyId) is not null);
            _logger.LogInformation(
                "Benchmark rule {ExcessRule}: {PinnedMembers} frozen-universe member(s) are under a "
                    + "recognised pending acquisition and leave the equal-weight peer mean (and the coverage "
                    + "denominator) from their announcement date onward. Frozen membership is unchanged.",
                UniverseBenchmark.ExcessRuleVersion,
                pinnedMembers);

            _loaded = true;
            _benchmark = new UniverseBenchmark(universe, bars, Acquisitions);
            return _benchmark;
        }
        finally
        {
            _gate.Release();
        }
    }
}
