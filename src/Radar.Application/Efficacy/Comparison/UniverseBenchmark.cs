using Radar.Application.Prices;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>Why an excess forward return does not exist for one observation (spec 183 §2).</summary>
public enum BenchmarkExcessUnavailableReason
{
    /// <summary>The excess return WAS computed; not an exclusion.</summary>
    None = 0,

    /// <summary>
    /// The benchmark could not be used at this (date, horizon): the frozen universe failed to load, or fewer
    /// eligible peers resolved than the coverage rule requires. The pooled/descriptive observation is EXCLUDED
    /// with this named, counted reason — never silently fed the raw return instead.
    /// </summary>
    BenchmarkUnavailable,

    /// <summary>
    /// The target company is not a member of the frozen universe (added to the seed after the freeze).
    /// "Within this pond" is only a claim about members of the pond; such companies join at
    /// <c>benchmark-universe-v2</c>, prospectively.
    /// </summary>
    NotInBenchmarkUniverse,
}

/// <summary>
/// One member's forward-return resolution at one (date, horizon, tolerance) — resolved through the SAME
/// spec-152 <see cref="ForwardReturn.TryCompute"/> rules every consumer uses, so the member-level poison-bar
/// guarantee (<c>bar.Date &gt; D</c>) and the exit-tolerance rule apply to the benchmark exactly as they apply
/// to the target. An unresolved member STAYS in the denominator and carries its reason.
/// </summary>
public sealed record BenchmarkMemberResolution(
    Guid CompanyId,
    string Ticker,
    bool Resolved,
    double ForwardReturnValue,
    ForwardReturnUnavailableReason Reason);

/// <summary>
/// The whole universe resolved at one (universeVersion, D, horizon, exitTolerance) — computed ONCE and shared
/// by every consumer (spec 183 §1), so two arms can never derive different outcomes from different member sets
/// or window rules. Members appear in artifact order (the deterministic accumulation order, AD-3).
/// </summary>
public sealed record UniverseBenchmarkDay(
    string UniverseVersion,
    string UniverseContentHash,
    DateOnly AsOf,
    int HorizonDays,
    int ExitToleranceDays,
    IReadOnlyList<BenchmarkMemberResolution> Members)
{
    public int MemberCount => Members.Count;

    public int ResolvedCount { get; } = Members.Count(m => m.Resolved);

    /// <summary>Every unresolved member with its reason — the per-day coverage provenance.</summary>
    public IReadOnlyList<BenchmarkMemberResolution> Unresolved { get; } =
        [.. Members.Where(m => !m.Resolved)];
}

/// <summary>
/// One excess-return outcome, carrying the full coverage provenance the spec requires every adjusted
/// observation to record: universe version + hash, eligible peer count, resolved count and the required bound.
/// </summary>
public sealed record BenchmarkExcessResult(
    bool IsDefined,
    double Excess,
    double PeerMeanForwardReturn,
    int EligiblePeers,
    int ResolvedPeers,
    int RequiredResolvedPeers,
    BenchmarkExcessUnavailableReason Reason)
{
    public static BenchmarkExcessResult Unavailable(
        BenchmarkExcessUnavailableReason reason, int eligiblePeers, int resolvedPeers, int requiredPeers) =>
        new(
            IsDefined: false,
            Excess: 0.0,
            PeerMeanForwardReturn: 0.0,
            EligiblePeers: eligiblePeers,
            ResolvedPeers: resolvedPeers,
            RequiredResolvedPeers: requiredPeers,
            Reason: reason);
}

/// <summary>
/// THE central benchmark computation (spec 183 §1): the equal-weight mean forward return of the OTHER resolved
/// frozen-universe members, self-excluded, at one (universeVersion, D, horizon, exitTolerance) — computed once
/// per key, cached, and shared by the spec-140 leaderboard and the spec-179 news-risk evaluator
/// (reuse-over-copy; a second copy could silently disagree about the member set or the window rules).
/// <para>
/// <b>The excess definition and the coverage rule are CODE CONSTANTS, deliberately not configurable</b>
/// (spec 183 §§1–2): a tunable benchmark definition would let an operator move what "excess" means between
/// runs, and every published excess number would then be conditional on a knob nobody recorded. The rule:
/// <c>eligiblePeers = members − target</c>; <c>required = max(40, ceil(0.90 × eligiblePeers))</c>;
/// usable iff <c>resolvedPeers ≥ required</c>. The proportion is evaluated in INTEGER arithmetic
/// (<c>ceil(9n/10) = (9n + 9) / 10</c>) so an eligible count that is an exact multiple of ten cannot round up
/// through a binary-representation artifact (0.90 is not exactly representable; <c>0.90 × 70</c> computes to
/// slightly above 63.0 and would ceil to 64).
/// </para>
/// <para>
/// <b>Deterministic (AD-3).</b> Members resolve and accumulate in artifact order; no clock, no randomness.
/// The day cache is an optimisation only — a cache miss and a cache hit produce byte-identical values.
/// </para>
/// <para>
/// <b>AD-14.</b> This type consumes price and lives strictly downstream of scoring; nothing here feeds
/// evidence, signals or a score.
/// </para>
/// </summary>
public sealed class UniverseBenchmark
{
    /// <summary>
    /// The coverage floor: below 40 resolved peers the pond is a radically different pond (self-exclusion
    /// stops being a ~1/(N−1) effect) and no excess is computed. Code constant, never configuration.
    /// </summary>
    public const int MinimumResolvedPeers = 40;

    /// <summary>The coverage proportion: at least 90% of eligible peers must resolve. Code constant.</summary>
    public const double RequiredResolvedPeerProportion = 0.90;

    private readonly BenchmarkUniverse _universe;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> _barsByPriceSeriesKey;
    private readonly HashSet<Guid> _memberIds;
    private readonly Dictionary<(DateOnly AsOf, int HorizonDays, int ExitToleranceDays), UniverseBenchmarkDay> _days = [];
    private readonly Lock _gate = new();

    /// <param name="barsByPriceSeriesKey">
    /// Price bars keyed by the artifact's own <see cref="BenchmarkUniverseMember.PriceSeriesKey"/> (ordinal) —
    /// loaded once by the provider from the existing price store. A member with no entry simply fails to
    /// resolve (<see cref="ForwardReturnUnavailableReason.NoForwardBar"/>) and stays in the denominator.
    /// </param>
    public UniverseBenchmark(
        BenchmarkUniverse universe,
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> barsByPriceSeriesKey)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(barsByPriceSeriesKey);

        _universe = universe;
        _barsByPriceSeriesKey = barsByPriceSeriesKey;
        _memberIds = [.. universe.Members.Select(m => m.CompanyId)];
    }

    public BenchmarkUniverse Universe => _universe;

    /// <summary>
    /// The universe resolved at one (D, horizon, tolerance) — cached, so every consumer of the same key reads
    /// the IDENTICAL computation and provenance.
    /// </summary>
    public UniverseBenchmarkDay DayAt(DateOnly asOf, int horizonDays, int exitToleranceDays)
    {
        lock (_gate)
        {
            var key = (asOf, horizonDays, exitToleranceDays);
            if (_days.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var members = new List<BenchmarkMemberResolution>(_universe.Members.Count);
            foreach (var member in _universe.Members)
            {
                var bars = _barsByPriceSeriesKey.GetValueOrDefault(member.PriceSeriesKey) ?? [];
                var forward = ForwardReturn.TryCompute(bars, asOf, horizonDays, exitToleranceDays);
                members.Add(new BenchmarkMemberResolution(
                    member.CompanyId,
                    member.Ticker,
                    forward.IsDefined,
                    forward.IsDefined ? forward.Value : 0.0,
                    forward.Reason));
            }

            var day = new UniverseBenchmarkDay(
                _universe.UniverseVersion,
                _universe.ContentHash,
                asOf,
                horizonDays,
                exitToleranceDays,
                members);
            _days[key] = day;
            return day;
        }
    }

    /// <summary>
    /// The excess of <paramref name="rawForwardReturn"/> over the equal-weight mean forward return of the
    /// OTHER resolved members (self-excluded when the target is a member), or the named reason none exists.
    /// The self-exclusion is what makes per-date excess a positive affine transform of the raw return for
    /// members (<c>excessᵢ = N/(N−1) × (rᵢ − mean(all))</c> when all N members resolve), which is why the
    /// paired path needs no benchmark gate at all (spec 183 Overview).
    /// </summary>
    public BenchmarkExcessResult TryExcess(
        Guid companyId, double rawForwardReturn, DateOnly asOf, int horizonDays, int exitToleranceDays)
    {
        if (!_memberIds.Contains(companyId))
        {
            return BenchmarkExcessResult.Unavailable(
                BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse,
                eligiblePeers: _universe.Members.Count,
                resolvedPeers: 0,
                requiredPeers: RequiredResolvedPeers(_universe.Members.Count));
        }

        var day = DayAt(asOf, horizonDays, exitToleranceDays);

        var eligiblePeers = day.MemberCount - 1;
        var required = RequiredResolvedPeers(eligiblePeers);

        // Accumulate in artifact member order (AD-3: floating-point addition is not associative).
        var resolvedPeers = 0;
        var sum = 0.0;
        foreach (var member in day.Members)
        {
            if (member.CompanyId == companyId || !member.Resolved)
            {
                continue;
            }

            resolvedPeers++;
            sum += member.ForwardReturnValue;
        }

        if (resolvedPeers < required)
        {
            return BenchmarkExcessResult.Unavailable(
                BenchmarkExcessUnavailableReason.BenchmarkUnavailable, eligiblePeers, resolvedPeers, required);
        }

        var peerMean = sum / resolvedPeers;
        return new BenchmarkExcessResult(
            IsDefined: true,
            Excess: rawForwardReturn - peerMean,
            PeerMeanForwardReturn: peerMean,
            EligiblePeers: eligiblePeers,
            ResolvedPeers: resolvedPeers,
            RequiredResolvedPeers: required,
            Reason: BenchmarkExcessUnavailableReason.None);
    }

    /// <summary>
    /// <c>max(40, ceil(0.90 × eligiblePeers))</c>, in integer arithmetic: <c>ceil(9n/10) = (9n + 9) / 10</c>.
    /// The double form would ceil <c>0.90 × 70</c> to 64 (binary 0.90 is slightly above nine tenths), silently
    /// requiring one more peer than the declared rule.
    /// </summary>
    public static int RequiredResolvedPeers(int eligiblePeers) =>
        Math.Max(MinimumResolvedPeers, ((9 * eligiblePeers) + 9) / 10);
}
