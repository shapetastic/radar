namespace Radar.Application.Efficacy.Statistics;

/// <summary>Why a Spearman coefficient does not exist for a pair of vectors. Named, never a fabricated 0 or NaN.</summary>
public enum SpearmanDegeneracy
{
    /// <summary>It exists.</summary>
    None = 0,

    /// <summary>Fewer than two observations: no rank variance can exist at all.</summary>
    TooFewObservations,

    /// <summary>Every value in the FIRST vector is identical, so it has no rank variance.</summary>
    ConstantFirst,

    /// <summary>Every value in the SECOND vector is identical, so it has no rank variance.</summary>
    ConstantSecond,
}

/// <summary>
/// A Spearman coefficient or its named degeneracy, with the observation count reported either way — "how much
/// data was there" is exactly what a reader needs to discount a missing number.
/// </summary>
public readonly record struct SpearmanResult(double? Rho, SpearmanDegeneracy Degeneracy, int ObservationCount)
{
    public bool IsDefined => Rho is not null;
}

/// <summary>
/// THE Spearman rank-correlation core: average ranks over tied runs, one accumulation order, one clamp.
/// <para>
/// EXTRACTED BY SPEC 225 from <c>Radar.Application.Efficacy.Comparison.RankCorrelation</c>, NOT COPIED
/// (CLAUDE.md reuse-over-copy). The EvidenceConfidence measurement needs ρ between score components and must
/// not reach the price-facing comparison namespace (the statistics module is outcome-agnostic by guardrail),
/// so the ranking + coefficient moved HERE verbatim and <c>RankCorrelation.ComputeRho</c> /
/// <c>RankCorrelation.AverageRanks</c> now delegate to it — every existing consumer (the paired comparison,
/// the attention screen, the denominator audit) computes the identical bits through the identical code path,
/// which their pinned tests guard.
/// </para>
/// <para>
/// Ties take AVERAGE ranks — the standard deterministic convention and the only one that leaves ρ invariant
/// under the input order. Undefined only for a genuinely unanswerable input: fewer than two observations, or a
/// constant vector on either side. A perfect ±1 is DEFINED. Pure (AD-3): no clock, no randomness, no I/O.
/// </para>
/// </summary>
public static class SpearmanRankCorrelation
{
    /// <summary>Spearman ρ over two index-aligned vectors, or the named reason it does not exist.</summary>
    public static SpearmanResult Compute(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Count != second.Count)
        {
            throw new ArgumentException(
                $"Vectors must be index-aligned, but got {first.Count} and {second.Count}.",
                nameof(second));
        }

        var n = first.Count;

        // Two points is the floor for any rank variance to exist at all; below it there is nothing to
        // correlate, and reporting 0 would be a fabricated answer rather than a missing one.
        if (n < 2)
        {
            return new SpearmanResult(null, SpearmanDegeneracy.TooFewObservations, n);
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
            return new SpearmanResult(null, SpearmanDegeneracy.ConstantFirst, n);
        }

        if (syy <= 0.0)
        {
            return new SpearmanResult(null, SpearmanDegeneracy.ConstantSecond, n);
        }

        // Clamped only against floating-point overshoot at the ±1 boundary.
        var rho = Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1.0, 1.0);
        return new SpearmanResult(rho, SpearmanDegeneracy.None, n);
    }

    /// <summary>
    /// 1-based ranks with AVERAGE ranks over tied runs (the deterministic convention: a run of k equal values
    /// occupying positions p..p+k-1 all receive their mean rank, so the rank total is invariant).
    /// </summary>
    public static double[] AverageRanks(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

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
