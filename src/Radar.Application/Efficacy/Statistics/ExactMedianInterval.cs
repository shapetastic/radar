using System.Globalization;

namespace Radar.Application.Efficacy.Statistics;

/// <summary>Why an exact median interval does not exist for a block set. Machine-readable, never a log line.</summary>
public enum MedianIntervalUndefinedReason
{
    /// <summary>It exists; not a degeneracy.</summary>
    None = 0,

    /// <summary>
    /// Fewer purged blocks than the confidence level can support: no order statistic pair covers the median
    /// at the required level (six blocks is the floor at 95%). The confidence level is NEVER weakened to
    /// manufacture an interval, and no NaN is ever published — "we cannot say yet" is the honest answer.
    /// </summary>
    InsufficientPurgedBlocks,
}

/// <summary>
/// The exact two-sided order-statistic interval for a population median, or the named reason it does not
/// exist. <c>BlockCount</c> is reported even when undefined — "how many blocks were there" is exactly what a
/// reader needs in order to discount a missing interval.
/// </summary>
/// <param name="Lower">The k-th smallest delta (one-based order statistic k = <paramref name="LowerOrderStatistic"/>).</param>
/// <param name="Upper">The (n−k+1)-th smallest delta.</param>
/// <param name="LowerOrderStatistic">k — recorded so a reader can verify the interval, not merely trust it.</param>
/// <param name="AchievedCoverage">
/// The exact coverage <c>1 − 2·BinomialCdf(k−1; n, 0.5)</c> at the chosen k. Always ≥ the requested level by
/// construction; disclosed because an order-statistic interval over-covers rather than hitting the level
/// exactly, and hiding that would make two intervals at different n look more comparable than they are.
/// </param>
public sealed record ExactMedianIntervalResult(
    bool IsDefined,
    double Lower,
    double Upper,
    int LowerOrderStatistic,
    double AchievedCoverage,
    int BlockCount,
    MedianIntervalUndefinedReason Reason)
{
    public static ExactMedianIntervalResult Undefined(int blockCount, MedianIntervalUndefinedReason reason) =>
        new(
            IsDefined: false,
            Lower: 0.0,
            Upper: 0.0,
            LowerOrderStatistic: 0,
            AchievedCoverage: 0.0,
            BlockCount: blockCount,
            Reason: reason);
}

/// <summary>
/// The exact two-sided 95% order-statistic interval for a population median (spec 155): sort the n deltas
/// ascending, choose the LARGEST integer <c>k ≥ 1</c> with <c>1 − 2·BinomialCdf(k−1; n, 0.5) ≥ 0.95</c>, and
/// report <c>[delta_(k), delta_(n−k+1)]</c> in one-based order statistics.
/// <para>
/// <b>Why this interval.</b> It is deterministic (AD-3 — no bootstrap, no resampling: a resampled interval
/// would make two runs over identical data disagree), assumes no parametric shape, and is exact under the
/// predeclared model that the purged blocks are independent draws from a stable distribution. It is NOT
/// assumption-free: purging removes the known mechanical forward-window overlap but cannot prove independence
/// or stationarity across market regimes — every renderer must state that limitation BESIDE the interval.
/// </para>
/// <para>
/// <b>Ties are data.</b> Tied deltas make the order-statistic interval conservative (it over-covers); they
/// are deliberately not special-cased, smoothed or jittered.
/// </para>
/// <para>
/// <b>Outcome-agnostic:</b> consumes bare doubles, so the AD-16 attention evaluator can reuse it over its
/// publisher-count deltas without importing the price harness.
/// </para>
/// </summary>
public static class ExactMedianInterval
{
    /// <summary>The only confidence level this project ships. The parameter exists for tests, not for tuning.</summary>
    public const double DefaultConfidenceLevel = 0.95;

    /// <summary>
    /// Absorbs the terminal-digit representation error of an exactly-computed coverage (the same posture as
    /// the harness's split-index nudge): 0.95 itself is not representable in binary, so a strict ≥ against
    /// the literal could reject a coverage that is mathematically equal to the level. A fixed constant, so
    /// the comparison stays deterministic.
    /// </summary>
    private const double CoverageTolerance = 1e-12;

    /// <summary>
    /// Computes the exact two-sided order-statistic interval for the median of <paramref name="values"/>.
    /// Returns <see cref="MedianIntervalUndefinedReason.InsufficientPurgedBlocks"/> when NO <c>k ≥ 1</c>
    /// reaches the coverage (true for n &lt; 6 at 95%); the confidence level is never weakened.
    /// </summary>
    public static ExactMedianIntervalResult Compute(
        IReadOnlyList<double> values, double confidenceLevel = DefaultConfidenceLevel)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!double.IsFinite(confidenceLevel) || confidenceLevel <= 0.0 || confidenceLevel >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidenceLevel),
                confidenceLevel,
                "The confidence level must be strictly between 0 and 1; "
                    + confidenceLevel.ToString("R", CultureInfo.InvariantCulture)
                    + " describes no two-sided interval.");
        }

        var n = values.Count;
        if (n == 0)
        {
            return ExactMedianIntervalResult.Undefined(0, MedianIntervalUndefinedReason.InsufficientPurgedBlocks);
        }

        var sorted = new double[n];
        for (var i = 0; i < n; i++)
        {
            sorted[i] = values[i];
        }

        Array.Sort(sorted);

        // Coverage 1 − 2·Cdf(k−1) is strictly decreasing in k, so scan k upward and keep the last k that
        // still reaches the level. k may not exceed (n+1)/2, past which delta_(k) would overtake
        // delta_(n−k+1) and the "interval" would be inverted.
        var bestK = 0;
        var bestCoverage = 0.0;
        for (var k = 1; k <= (n + 1) / 2; k++)
        {
            var coverage = 1.0 - (2.0 * ExactBinomial.CdfAtHalf(k - 1, n));
            if (coverage >= confidenceLevel - CoverageTolerance)
            {
                bestK = k;
                bestCoverage = coverage;
            }
            else
            {
                break;
            }
        }

        if (bestK == 0)
        {
            return ExactMedianIntervalResult.Undefined(
                n, MedianIntervalUndefinedReason.InsufficientPurgedBlocks);
        }

        return new ExactMedianIntervalResult(
            IsDefined: true,
            Lower: sorted[bestK - 1],
            Upper: sorted[n - bestK],
            LowerOrderStatistic: bestK,
            AchievedCoverage: bestCoverage,
            BlockCount: n,
            Reason: MedianIntervalUndefinedReason.None);
    }

    /// <summary>
    /// The exact sample median: the middle order statistic for odd n, the mean of the two middle order
    /// statistics for even n (the standard deterministic convention). Kept here beside the interval so the
    /// point estimate and its interval can never be computed under two different median definitions.
    /// </summary>
    public static double MedianOf(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var n = values.Count;
        ArgumentOutOfRangeException.ThrowIfZero(n);

        var sorted = new double[n];
        for (var i = 0; i < n; i++)
        {
            sorted[i] = values[i];
        }

        Array.Sort(sorted);

        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }
}
