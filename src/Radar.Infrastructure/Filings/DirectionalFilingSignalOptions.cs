namespace Radar.Infrastructure.Filings;

/// <summary>
/// Tunables for <see cref="DirectionalFilingSignalSource"/>: the confidence gate below which the AI read
/// produces no directional signal, the per-run cost cap, and the in-range Strength/Novelty constants each
/// emitted signal carries (they clear the deterministic reviewer floors). Validated at registration by
/// <c>AddDirectionalFilingSignals</c>.
/// </summary>
public sealed class DirectionalFilingSignalOptions
{
    /// <summary>Gate: an AI confidence below this yields no directional signal. In [0,1]. Default 0.6.</summary>
    public decimal MinConfidence { get; init; } = 0.6m;

    /// <summary>Cost cap: analyze at most this many filings per run. Must be &gt; 0. Default 5.</summary>
    public int MaxFilingsPerRun { get; init; } = 5;

    /// <summary>Signal strength constant (in-range; clears the reviewer MinMaterialStrength floor). Default 6.</summary>
    public int Strength { get; init; } = 6;

    /// <summary>Signal novelty constant (in-range; clears the reviewer MinNovelty floor). Default 6.</summary>
    public int Novelty { get; init; } = 6;

    /// <summary>
    /// Per-run 429 circuit breaker: after this many CONSECUTIVE rate-limited (HTTP 429) earnings reads in a run,
    /// stop attempting the remaining filings this run (SEC's www.sec.gov host appears blocked). A success or a
    /// cache hit resets the count; a non-429 failure does not trip it. Default 2. Set to 0 to disable the breaker
    /// (unbounded — the pre-spec-107 behaviour), used for parity testing. Must not be negative.
    /// </summary>
    public int MaxConsecutiveRateLimited { get; init; } = 2;
}
