using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 170's compatibility half, asserted rather than assumed: the trailing nullable
/// <see cref="EfficacyPoint.AsOfInstantUtc"/> is read by NEITHER the per-company CSV/SVG renderers NOR the
/// marginal leaderboard path, so adding it changes not one byte of those artifacts, and the shared
/// observation builder's date-deduplicated projection is identical with and without instants.
/// </summary>
public sealed class ExactInstantByteStabilityTests
{
    /// <summary>One strategy world rendered twice: with instants (the new read) and without (the old shape).</summary>
    private static (StrategyScoreSeries With, StrategyScoreSeries Without) TwinSeries()
    {
        var withInstants = PairedFixtures.Series(
            "primary", PairedFixtures.Aligned, PairedFixtures.Daily(30));
        var withoutInstants = PairedFixtures.Series(
            "primary", PairedFixtures.Aligned, PairedFixtures.Daily(30), instant: (_, _) => null);
        return (withInstants, withoutInstants);
    }

    [Fact]
    public void PerCompanyCsvAndSvg_AreByteIdentical_WithAndWithoutTheInstant()
    {
        var (with, without) = TwinSeries();
        var csv = new EfficacyCsvRenderer();
        var svg = new EfficacySvgRenderer();

        foreach (var (companyWith, companyWithout) in with.Companies.Zip(without.Companies))
        {
            Assert.Equal(csv.Render(companyWithout), csv.Render(companyWith));
            Assert.Equal(svg.Render(companyWithout), svg.Render(companyWith));
        }
    }

    [Fact]
    public void MarginalLeaderboard_IsByteIdentical_WithAndWithoutTheInstant()
    {
        var (with, without) = TwinSeries();
        var mirrorWith = PairedFixtures.Series("mirror", PairedFixtures.AntiAligned, PairedFixtures.Daily(30));
        var mirrorWithout = PairedFixtures.Series(
            "mirror", PairedFixtures.AntiAligned, PairedFixtures.Daily(30), instant: (_, _) => null);

        var harness = new StrategyComparisonHarness();
        var options = new StrategyComparisonOptions(
            PairedFixtures.HorizonDays, 1.0 / 3.0, 4, PairedFixtures.ExitToleranceDays);

        var renderer = new StrategyLeaderboardRenderer();
        var benchmark = PairedFixtures.Benchmark();
        var leaderboardWith = harness.Compare([with, mirrorWith], options, benchmark);
        var leaderboardWithout = harness.Compare([without, mirrorWithout], options, benchmark);

        Assert.Equal(renderer.RenderCsv(leaderboardWithout), renderer.RenderCsv(leaderboardWith));
        Assert.Equal(renderer.RenderMarkdown(leaderboardWithout), renderer.RenderMarkdown(leaderboardWith));
    }

    [Fact]
    public void DateProjection_IsIdentical_WithAndWithoutTheInstant()
    {
        var (with, without) = TwinSeries();

        var setWith = StrategyObservationBuilder.Build(
            with, PairedFixtures.HorizonDays, PairedFixtures.ExitToleranceDays);
        var setWithout = StrategyObservationBuilder.Build(
            without, PairedFixtures.HorizonDays, PairedFixtures.ExitToleranceDays);

        // The marginal projection is byte-for-byte today's behaviour: the SAME observations in the SAME
        // order with the SAME tallies, instants or not.
        Assert.Equal(setWithout.Usable, setWith.Usable);
        Assert.Equal(setWithout.WithoutForwardPrice, setWith.WithoutForwardPrice);
        Assert.Equal(setWithout.PartialWindow, setWith.PartialWindow);

        // Only the claim-path projection differs: without instants it is empty and every usable
        // observation is counted as instant-less (fail closed, per key).
        Assert.NotEmpty(setWith.UsableByInstant);
        Assert.Equal(0, setWith.WithoutAsOfInstant);
        Assert.Empty(setWithout.UsableByInstant);
        Assert.Equal(setWithout.Usable.Count, setWithout.WithoutAsOfInstant);
    }

    [Fact]
    public void SameInstantDuplicates_ResolveLastOccurrenceWins_InBothProjections_NothingThrows()
    {
        // Two same-day points for company 0 with the SAME instant and different scores: the builder must
        // keep the LAST occurrence in both projections (spec 170 §2.1 retains the rule) and never throw.
        var asOf = PairedFixtures.AsOf(0);
        var instant = PairedFixtures.InstantOf(0);

        EfficacyPoint Point(int score) => new(
            ScoreDate: asOf,
            TrajectoryScore: 0,
            OpportunityScore: score,
            AttentionScore: 0,
            EvidenceConfidenceScore: 0,
            SignalVelocityScore: 0,
            SeriesKey: "primary",
            ScoringConfigVersion: null,
            PriceAsOfDate: null,
            PriceClose: null,
            PriceAdjClose: null)
        {
            AsOfDate = asOf,
            AsOfInstantUtc = instant,
        };

        var series = new StrategyScoreSeries(
            "primary",
            [
                new CompanyEfficacySeries(
                    PairedFixtures.CompanyIds[0],
                    "Company PAAA",
                    "PAAA",
                    [Point(10), Point(90)],
                    PairedFixtures.Bars(0)),
            ]);

        var set = StrategyObservationBuilder.Build(
            series, PairedFixtures.HorizonDays, PairedFixtures.ExitToleranceDays);

        Assert.Equal(90, Assert.Single(set.Usable).Score);
        Assert.Equal(90, Assert.Single(set.UsableByInstant).Score);
        Assert.Equal(0, set.WithoutAsOfInstant);
    }
}
