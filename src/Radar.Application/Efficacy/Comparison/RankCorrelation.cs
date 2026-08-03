namespace Radar.Application.Efficacy.Comparison;

/// <summary>Why a rank correlation (or its interval) does not exist for a window.</summary>
public enum RankCorrelationUndefinedReason
{
    /// <summary>It exists; not a drop.</summary>
    None = 0,

    /// <summary>Fewer observations than a Fisher-z interval needs (its standard error is 1/sqrt(n-3)).</summary>
    TooFewObservations,

    /// <summary>Every score in the window is identical, so the score vector has no rank variance.</summary>
    ConstantScores,

    /// <summary>Every forward return in the window is identical, so the return vector has no rank variance.</summary>
    ConstantReturns,

    /// <summary>|ρ| = 1 exactly: the Fisher-z interval collapses to zero width, which would read as certainty.</summary>
    PerfectCorrelation,
}

/// <summary>
/// A window's rank correlation together with its two-sided interval. <c>ObservationCount</c> is reported even
/// when undefined, because "how much data was there" is exactly what the reader needs in order to discount a
/// missing number.
/// </summary>
public sealed record RankCorrelationResult(
    bool IsDefined,
    double Rho,
    double LowerBound,
    double UpperBound,
    int ObservationCount,
    RankCorrelationUndefinedReason Reason)
{
    public static RankCorrelationResult Undefined(int n, RankCorrelationUndefinedReason reason) =>
        new(IsDefined: false, Rho: 0.0, LowerBound: 0.0, UpperBound: 0.0, ObservationCount: n, Reason: reason);
}

/// <summary>
/// Spearman ρ ALONE, with no interval (spec 169). Same coefficient, same average-rank convention, same
/// degeneracy names — only the Fisher-z interval is absent.
/// <para>
/// <b>Why a second shape and not a second implementation.</b> AD-16's screen consumes ρ and nothing else: its
/// windows overlap by construction, so it makes no confidence or significance claim and has no use for an
/// interval. It also must NOT inherit <see cref="RankCorrelationUndefinedReason.PerfectCorrelation"/>, which
/// exists purely because a zero-width interval would read as certainty — a genuine ρ = ±1 is a perfectly
/// usable coefficient, and discarding that date would silently drop a real observation from a precommitted
/// screen. Both shapes share ONE ranking + ONE coefficient computation below.
/// </para>
/// </summary>
public readonly record struct SpearmanRhoResult(
    bool IsDefined,
    double Rho,
    int ObservationCount,
    RankCorrelationUndefinedReason Reason)
{
    public static SpearmanRhoResult Undefined(int n, RankCorrelationUndefinedReason reason) =>
        new(IsDefined: false, Rho: 0.0, ObservationCount: n, Reason: reason);
}

/// <summary>
/// Spearman's rank correlation ρ between a strategy's scores and the subsequent price movement they are being
/// judged against, plus a closed-form Fisher-z interval for its dispersion (spec 140).
/// <para>
/// <b>Rank, not level.</b> Radar's scores are bounded 0–100 ordinals with no claim to a linear relationship
/// with return magnitude; the only claim being tested is "did higher scores tend to be followed by larger
/// movement". Ties take AVERAGE ranks, which is the standard deterministic convention and the only one that
/// leaves ρ invariant under the input order.
/// </para>
/// <para>
/// <b>Dispersion is closed-form, never resampled.</b> The interval is
/// <c>tanh(atanh(ρ) ± z·1/sqrt(n−3))</c>. No bootstrap and no random sampling: spec 140 requires the whole
/// comparison to be a pure function of its inputs, and a resampled interval would make two runs over identical
/// data disagree.
/// </para>
/// <para>
/// <b>The honest caveat, stated because the number invites over-reading:</b> observations are pooled across
/// companies and as-of dates, so they are NOT independent — horizons overlap in time and companies move
/// together — which makes the Fisher-z interval OPTIMISTICALLY narrow. It is a dispersion indicator, not a
/// significance test.
/// </para>
/// <para>
/// Every degeneracy is named rather than silently producing NaN, ±∞, or a fabricated 0: too few observations,
/// a constant vector on either side, and |ρ| = 1 (where the interval collapses to zero width and would read as
/// certainty) each return an undefined result carrying its reason.
/// </para>
/// </summary>
public static class RankCorrelation
{
    /// <summary>
    /// Spearman ρ over <paramref name="scores"/> vs <paramref name="forwardReturns"/> (index-aligned), with a
    /// two-sided Fisher-z interval at the given normal quantile.
    /// </summary>
    public static RankCorrelationResult Compute(
        IReadOnlyList<double> scores,
        IReadOnlyList<double> forwardReturns,
        double normalQuantile)
    {
        var n = RequireAligned(scores, forwardReturns, nameof(forwardReturns));

        // n - 3 must be positive for the Fisher-z standard error to exist at all. Checked BEFORE the
        // coefficient (unchanged from the original ordering) so a tiny window reports "too few" rather than a
        // constant-vector reason that happens to fire first.
        if (n < StrategyComparisonOptions.MinimumObservationsFloor)
        {
            return RankCorrelationResult.Undefined(n, RankCorrelationUndefinedReason.TooFewObservations);
        }

        var coefficient = ComputeRho(scores, forwardReturns);
        if (!coefficient.IsDefined)
        {
            return RankCorrelationResult.Undefined(n, coefficient.Reason);
        }

        var rho = coefficient.Rho;

        if (Math.Abs(rho) >= 1.0)
        {
            return RankCorrelationResult.Undefined(n, RankCorrelationUndefinedReason.PerfectCorrelation);
        }

        var z = Math.Atanh(rho);
        var se = 1.0 / Math.Sqrt(n - 3.0);
        var lower = Math.Tanh(z - (normalQuantile * se));
        var upper = Math.Tanh(z + (normalQuantile * se));

        return new RankCorrelationResult(
            IsDefined: true,
            Rho: rho,
            LowerBound: lower,
            UpperBound: upper,
            ObservationCount: n,
            Reason: RankCorrelationUndefinedReason.None);
    }

    /// <summary>
    /// Spearman ρ over two index-aligned vectors, WITHOUT the Fisher-z interval (spec 169). THE coefficient
    /// computation — <see cref="Compute"/> calls this too, so there is exactly one ranking rule, one
    /// accumulation order and one clamp in the codebase.
    /// <para>
    /// Undefined only for a genuinely unanswerable input: fewer than two observations, or a constant vector on
    /// either side (<see cref="RankCorrelationUndefinedReason.ConstantScores"/> /
    /// <see cref="RankCorrelationUndefinedReason.ConstantReturns"/>). A perfect ±1 is DEFINED here — the
    /// interval that a ±1 would collapse is not being computed.
    /// </para>
    /// </summary>
    public static SpearmanRhoResult ComputeRho(
        IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        var n = RequireAligned(first, second, nameof(second));

        // Two points is the floor for any rank variance to exist at all; below it there is nothing to
        // correlate, and reporting 0 would be a fabricated answer rather than a missing one.
        if (n < 2)
        {
            return SpearmanRhoResult.Undefined(n, RankCorrelationUndefinedReason.TooFewObservations);
        }

        var rx = AverageRanks(first);
        var ry = AverageRanks(second);

        var mx = Mean(rx);
        var my = Mean(ry);

        double sxy = 0.0, sxx = 0.0, syy = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dx = rx[i] - mx;
            var dy = ry[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        if (sxx <= 0.0)
        {
            return SpearmanRhoResult.Undefined(n, RankCorrelationUndefinedReason.ConstantScores);
        }

        if (syy <= 0.0)
        {
            return SpearmanRhoResult.Undefined(n, RankCorrelationUndefinedReason.ConstantReturns);
        }

        // Clamped only against floating-point overshoot at the ±1 boundary.
        var rho = Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1.0, 1.0);

        return new SpearmanRhoResult(
            IsDefined: true, Rho: rho, ObservationCount: n, Reason: RankCorrelationUndefinedReason.None);
    }

    /// <summary>
    /// The shared null/alignment precondition. <paramref name="secondParameterName"/> is threaded from the
    /// CALLER so each public method's <see cref="ArgumentException.ParamName"/> keeps naming its own
    /// parameter — extracting this must not quietly change a public surface.
    /// </summary>
    private static int RequireAligned(
        IReadOnlyList<double> first, IReadOnlyList<double> second, string secondParameterName)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Count != second.Count)
        {
            throw new ArgumentException(
                $"Score/return vectors must be index-aligned, but got {first.Count} and "
                    + $"{second.Count}.",
                secondParameterName);
        }

        return first.Count;
    }

    /// <summary>
    /// 1-based ranks with AVERAGE ranks over tied runs (the deterministic convention: a run of k equal values
    /// occupying positions p..p+k-1 all receive their mean rank, so the rank total is invariant).
    /// </summary>
    internal static double[] AverageRanks(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var order = new int[n];
        for (var i = 0; i < n; i++)
        {
            order[i] = i;
        }

        // Value ascending, index ascending as the tie-break — so the permutation is total and deterministic
        // regardless of the sort's stability.
        Array.Sort(order, (a, b) =>
        {
            var byValue = values[a].CompareTo(values[b]);
            return byValue != 0 ? byValue : a.CompareTo(b);
        });

        var ranks = new double[n];
        var i2 = 0;
        while (i2 < n)
        {
            var j = i2;
            while (j + 1 < n && values[order[j + 1]].Equals(values[order[i2]]))
            {
                j++;
            }

            // Positions i2..j (0-based) → 1-based ranks i2+1..j+1 → mean = (i2 + j) / 2 + 1.
            var averageRank = (((double)i2 + j) / 2.0) + 1.0;
            for (var k = i2; k <= j; k++)
            {
                ranks[order[k]] = averageRank;
            }

            i2 = j + 1;
        }

        return ranks;
    }

    private static double Mean(double[] values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            sum += v;
        }

        return sum / values.Length;
    }
}
