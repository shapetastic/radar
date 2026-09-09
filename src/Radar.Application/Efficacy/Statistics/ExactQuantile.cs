namespace Radar.Application.Efficacy.Statistics;

/// <summary>
/// The ONE deterministic sample-quantile definition shared by every efficacy artifact that needs a quantile
/// other than the median (reuse over copy — CLAUDE.md). Nearest-rank, no interpolation: sort ascending and
/// return the order statistic at <c>k = clamp(ceil(p·n), 1, n)</c>.
/// <para>
/// <b>Why nearest-rank.</b> It is exact, culture-free and returns a value that IS in the sample — an
/// interpolated quantile invents a number no observation produced, which is the wrong posture for an
/// artifact whose whole job is to report what the corpus actually contains. Pure and deterministic (AD-3):
/// identical input yields identical output, with no clock, randomness or sort dependency (ties are data and
/// are not broken).
/// </para>
/// <para>
/// <b>The median deliberately does NOT live here.</b> The repo's one median definition is
/// <see cref="ExactMedianInterval.MedianOf"/> (the mean of the two middle order statistics at even n), and
/// every median in the codebase routes through it. Calling this helper at <c>p = 0.5</c> would give the
/// nearest-rank answer instead, which differs at even n — so a caller reporting min/p25/median/p75/max must
/// take the median from <see cref="ExactMedianInterval.MedianOf"/> and STATE the two conventions beside the
/// numbers rather than quietly mixing them.
/// </para>
/// </summary>
public static class ExactQuantile
{
    /// <summary>
    /// The nearest-rank quantile of <paramref name="values"/> at probability <paramref name="p"/> ∈ [0, 1].
    /// Throws on an empty sample: "the quantile of nothing" has no honest value, and returning 0 would render
    /// as a measured zero.
    /// </summary>
    public static double Of(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        if (!double.IsFinite(p) || p < 0.0 || p > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(p), p, "The quantile probability must be a finite value in [0, 1].");
        }

        var n = values.Count;
        var sorted = new double[n];
        for (var i = 0; i < n; i++)
        {
            sorted[i] = values[i];
        }

        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(p * n);
        if (rank < 1)
        {
            rank = 1;
        }

        if (rank > n)
        {
            rank = n;
        }

        return sorted[rank - 1];
    }
}
