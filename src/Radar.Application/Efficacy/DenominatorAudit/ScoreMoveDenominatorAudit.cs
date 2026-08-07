using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// The pure spec-172 computation: consecutive-snapshot-pair observations, the two Spearman coefficients (via
/// the SHARED <see cref="RankCorrelation.ComputeRho"/> — reuse over copy, never a second Spearman), and the
/// binned |ΔOpportunity| distribution. Fully deterministic (AD-3): no sampling, no randomness, no wall-clock —
/// identical input yields identical output.
/// <para>
/// <b>Consecutive SNAPSHOTS, not consecutive calendar days.</b> Snapshots are walked in as-of order (anchored
/// on <c>WindowEndUtc</c> — the knowledge-window end, following spec 140's precedent; <c>CreatedAtUtc</c> then
/// <c>Id</c> break ties so the order is total) and every neighbouring pair yields one observation. A gap in
/// the as-of dates still pairs the neighbouring snapshots; a company with a single snapshot contributes no
/// pair and that is not an error.
/// </para>
/// </summary>
public static class ScoreMoveDenominatorAudit
{
    /// <summary>The fixed, ordered bin labels of the |ΔOpportunity|-by-DirectionalCount table.</summary>
    public static readonly IReadOnlyList<string> BinLabels = ["0", "1", "2", "3", "4+"];

    /// <summary>
    /// Whether an evidence link's contribution reason counts as DIRECTIONAL (spec 172's "not Neutral").
    /// <para>
    /// Both shipped formulas render the reason as <c>"{Type} ({Direction}), strength …"</c> with the direction
    /// token — Positive / Neutral / Negative / Mixed — as the FIRST parenthesised token, so the rule
    /// classifies by that token: <c>(Neutral)</c> ⇒ neutral; anything else — Positive, Negative, Mixed, AND an
    /// unparseable reason (no parentheses, empty, null) — counts as directional. The spec's wording is "not
    /// Neutral", so nothing is ever silently classified INTO the neutral bucket: only an explicit
    /// <c>(Neutral)</c> token is neutral.
    /// </para>
    /// </summary>
    public static bool IsDirectional(string? contributionReason)
    {
        if (string.IsNullOrEmpty(contributionReason))
        {
            return true;
        }

        var open = contributionReason.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return true;
        }

        var close = contributionReason.IndexOf(')', open + 1);
        if (close < 0)
        {
            return true;
        }

        var token = contributionReason.AsSpan(open + 1, close - open - 1).Trim();
        return !token.Equals("Neutral", StringComparison.Ordinal);
    }

    /// <summary>
    /// One company's series → its consecutive-pair observations, in as-of order. Deltas are LATER minus
    /// EARLIER; the link counts are the LATER snapshot's (the evidence base the new score rests on);
    /// <c>AsOfDate</c> is the later snapshot's <c>WindowEndUtc</c> calendar date (UTC).
    /// </summary>
    public static IReadOnlyList<DenominatorObservation> BuildObservations(
        string strategyName, IReadOnlyList<ScoreSnapshotWithLinks> series)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentNullException.ThrowIfNull(series);

        if (series.Count < 2)
        {
            return []; // a single snapshot (or none) contributes no pair — not an error
        }

        // As-of order, made total: WindowEndUtc (the anchor), then CreatedAtUtc, then Id (AD-3).
        var ordered = new List<ScoreSnapshotWithLinks>(series);
        ordered.Sort(static (a, b) =>
        {
            var byWindowEnd = a.Snapshot.WindowEndUtc.CompareTo(b.Snapshot.WindowEndUtc);
            if (byWindowEnd != 0)
            {
                return byWindowEnd;
            }

            var byCreated = a.Snapshot.CreatedAtUtc.CompareTo(b.Snapshot.CreatedAtUtc);
            return byCreated != 0 ? byCreated : a.Snapshot.Id.CompareTo(b.Snapshot.Id);
        });

        var observations = new List<DenominatorObservation>(ordered.Count - 1);
        for (var i = 1; i < ordered.Count; i++)
        {
            var earlier = ordered[i - 1].Snapshot;
            var later = ordered[i].Snapshot;
            var laterLinks = ordered[i].Links;

            var directional = 0;
            foreach (var link in laterLinks)
            {
                if (IsDirectional(link.ContributionReason))
                {
                    directional++;
                }
            }

            observations.Add(new DenominatorObservation(
                StrategyName: strategyName,
                CompanyId: later.CompanyId,
                AsOfDate: DateOnly.FromDateTime(later.WindowEndUtc.UtcDateTime),
                DeltaOpportunity: later.OpportunityScore - earlier.OpportunityScore,
                DeltaTrajectory: later.TrajectoryScore - earlier.TrajectoryScore,
                LinkCount: laterLinks.Count,
                DirectionalCount: directional));
        }

        return observations;
    }

    /// <summary>
    /// One strategy's pooled observations → the two coefficients and the bin table.
    /// <para>
    /// The coefficients come from the shared <see cref="RankCorrelation.ComputeRho"/> with
    /// <c>first = |ΔOpportunity|</c> and <c>second =</c> the count vector, so its degeneracy vocabulary maps
    /// as: <c>ConstantScores</c> ⇒ the |ΔOpportunity| vector has no rank variance; <c>ConstantReturns</c> ⇒
    /// the count vector has no rank variance. <c>ComputeRho</c>'s floor is 2 observations (it computes the
    /// coefficient alone — the "fewer than 4" floor belongs to interval-bearing correlations, which this audit
    /// does not compute), and a perfect |ρ| = 1 is DEFINED here because there is no interval to collapse.
    /// </para>
    /// </summary>
    public static DenominatorAuditStrategyResult Compute(
        string strategyName,
        int companiesWalked,
        int companiesWithPairs,
        IReadOnlyList<DenominatorObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentNullException.ThrowIfNull(observations);

        var absDelta = new double[observations.Count];
        var directional = new double[observations.Count];
        var links = new double[observations.Count];
        for (var i = 0; i < observations.Count; i++)
        {
            absDelta[i] = observations[i].AbsDeltaOpportunity;
            directional[i] = observations[i].DirectionalCount;
            links[i] = observations[i].LinkCount;
        }

        return new DenominatorAuditStrategyResult(
            StrategyName: strategyName,
            CompaniesWalked: companiesWalked,
            CompaniesWithPairs: companiesWithPairs,
            Observations: observations,
            RhoAbsDeltaVsDirectionalCount: RankCorrelation.ComputeRho(absDelta, directional),
            RhoAbsDeltaVsLinkCount: RankCorrelation.ComputeRho(absDelta, links),
            Bins: BuildBins(observations));
    }

    /// <summary>
    /// The |ΔOpportunity| distribution grouped by DirectionalCount: exact bins 0..3 plus a 4+ bin. Bins are
    /// stable and ordered; an empty bin carries a zero count and null statistics (rendered empty, never
    /// dropped).
    /// </summary>
    private static IReadOnlyList<DenominatorBin> BuildBins(IReadOnlyList<DenominatorObservation> observations)
    {
        var bins = new List<DenominatorBin>(BinLabels.Count);
        for (var bin = 0; bin < BinLabels.Count; bin++)
        {
            var isOpenEnded = bin == BinLabels.Count - 1;
            var values = new List<double>();
            foreach (var o in observations)
            {
                var inBin = isOpenEnded ? o.DirectionalCount >= bin : o.DirectionalCount == bin;
                if (inBin)
                {
                    values.Add(o.AbsDeltaOpportunity);
                }
            }

            if (values.Count == 0)
            {
                bins.Add(new DenominatorBin(BinLabels[bin], 0, null, null));
                continue;
            }

            values.Sort(); // exact int-valued doubles → a total, deterministic order
            bins.Add(new DenominatorBin(
                BinLabels[bin],
                values.Count,
                MedianOfSorted(values),
                Percentile90OfSorted(values)));
        }

        return bins;
    }

    /// <summary>
    /// The pinned median convention (deterministic, closed-form, sorted order statistics): odd count ⇒ the
    /// middle order statistic; even count ⇒ the arithmetic mean of the two middle order statistics.
    /// </summary>
    internal static double MedianOfSorted(IReadOnlyList<double> sortedAscending)
    {
        var n = sortedAscending.Count;
        return n % 2 == 1
            ? sortedAscending[(n - 1) / 2]
            : (sortedAscending[(n / 2) - 1] + sortedAscending[n / 2]) / 2.0;
    }

    /// <summary>
    /// The pinned 90th-percentile convention: NEAREST-RANK, i.e. the order statistic at 1-based rank
    /// <c>ceil(0.9·n)</c>. No interpolation — always an actually-observed value; deterministic and closed-form.
    /// </summary>
    internal static double Percentile90OfSorted(IReadOnlyList<double> sortedAscending)
    {
        var n = sortedAscending.Count;
        var rank = (int)Math.Ceiling(0.9 * n);
        return sortedAscending[Math.Max(rank, 1) - 1];
    }
}
