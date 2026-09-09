using Radar.Application.Acquisitions;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;
using Radar.Application.Tests.Acquisitions;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// SPEC 217 §3 — <c>observation-eligibility-v2</c>'s <c>CorporateActionInWindow</c> axis and
/// <c>excess-vs-universe-v2</c>'s peer-mean exclusion.
/// <para>
/// The measured motivation: MarineMax's +46.1% gap on 2026-08-10 sat inside the 21-day forward window of
/// every score from 2026-07-20 to 2026-08-07 (all Ignore, rank ~46), so every arm's rank correlation carried
/// a large "miss" no trajectory read could have earned; and from 08-10 the price has been pinned at the
/// $53.00 bid — zero variance, still scored, and still in the equal-weight peer mean of every OTHER
/// company's excess.
/// </para>
/// </summary>
public sealed class CorporateActionExclusionTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateOnly Announced = new(2026, 8, 10);

    private const int Horizon = 21;

    private static PendingAcquisitions Recognised(Guid companyId, DateOnly announced) =>
        PendingAcquisitionsTests.From(PendingAcquisitionsTests.Record(
            companyId: companyId,
            announced: new DateTimeOffset(announced.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));

    [Fact]
    public void Predicate_CoversBothHalvesOfTheRule_AsOneArithmeticStatement()
    {
        // "The announcement falls inside (D, D+h]" is D < A ≤ D+h; "D is on or after the announcement" is
        // A ≤ D. Their union is exactly A ≤ D+h — asserted, not asserted-by-comment.
        var justBeforeTheWindow = Announced.AddDays(-Horizon - 1);
        var firstExcludedDate = Announced.AddDays(-Horizon);

        Assert.False(ObservationEligibility.IsCorporateActionInWindow(
            justBeforeTheWindow, Announced, Horizon));
        Assert.True(ObservationEligibility.IsCorporateActionInWindow(
            firstExcludedDate, Announced, Horizon));
        Assert.True(ObservationEligibility.IsCorporateActionInWindow(Announced, Announced, Horizon));
        Assert.True(ObservationEligibility.IsCorporateActionInWindow(
            Announced.AddDays(30), Announced, Horizon));
    }

    [Fact]
    public void Version_IsStamped()
    {
        Assert.Equal("observation-eligibility-v2", ObservationEligibility.Version);
        Assert.Equal("excess-vs-universe-v2", UniverseBenchmark.ExcessRuleVersion);
        Assert.Equal("excess-vs-universe-v1", UniverseBenchmark.PreviousExcessRuleVersion);
    }

    [Fact]
    public void ExcludedObservations_AreCountedOnTheirOwnAxis_AndNotOnTheOthers()
    {
        // Bars covering both the pre-announcement window and the post-announcement pin, so WITHOUT the rule
        // every date would be a perfectly usable observation.
        var bars = DailyBars(new DateOnly(2026, 7, 1), 90, 100m);
        var series = new StrategyScoreSeries("arm", [
            new CompanyEfficacySeries(
                Target,
                "MarineMax, Inc.",
                "HZO",
                [
                    Point(new DateOnly(2026, 7, 20), 40),
                    Point(new DateOnly(2026, 8, 7), 41),
                    Point(new DateOnly(2026, 8, 20), 42),
                ],
                bars),
        ]);

        var before = StrategyObservationBuilder.Build(series, Horizon, exitToleranceDays: 4);
        Assert.Equal(3, before.Usable.Count);
        Assert.Equal(0, before.CorporateActionInWindow);

        var after = StrategyObservationBuilder.Build(
            series, Horizon, exitToleranceDays: 4, benchmark: null, Recognised(Target, Announced));

        Assert.Empty(after.Usable);
        Assert.Equal(3, after.CorporateActionInWindow);

        // DISJOINT axes: the exclusion runs BEFORE the forward-return computation, so an excluded
        // company-day is never also counted as "no forward price" or "partial window".
        Assert.Equal(0, after.WithoutForwardPrice);
        Assert.Equal(0, after.PartialWindow);
    }

    [Fact]
    public void UnaffectedCompanies_AreUntouched()
    {
        var other = Guid.NewGuid();
        var bars = DailyBars(new DateOnly(2026, 7, 1), 90, 100m);
        var series = new StrategyScoreSeries("arm", [
            new CompanyEfficacySeries(other, "Other", "AAA", [Point(new DateOnly(2026, 7, 20), 40)], bars),
        ]);

        var set = StrategyObservationBuilder.Build(
            series, Horizon, exitToleranceDays: 4, benchmark: null, Recognised(Target, Announced));

        Assert.Single(set.Usable);
        Assert.Equal(0, set.CorporateActionInWindow);
    }

    [Fact]
    public void NoneProjection_ReproducesTheV1AdmissionSet_ByteForByte()
    {
        var bars = DailyBars(new DateOnly(2026, 7, 1), 90, 100m);
        var series = new StrategyScoreSeries("arm", [
            new CompanyEfficacySeries(
                Target, "MarineMax, Inc.", "HZO", [Point(new DateOnly(2026, 7, 20), 40)], bars),
        ]);

        var v1 = StrategyObservationBuilder.Build(series, Horizon, 4);
        var v2 = StrategyObservationBuilder.Build(series, Horizon, 4, null, PendingAcquisitions.None);

        Assert.Equal(v1.Usable, v2.Usable);
        Assert.Equal(v1.WithoutForwardPrice, v2.WithoutForwardPrice);
        Assert.Equal(v1.PartialWindow, v2.PartialWindow);
        Assert.Equal(0, v2.CorporateActionInWindow);
    }

    // ---- excess-vs-universe-v2: the pinned member leaves the peer mean AND the denominator -------------

    /// <summary>
    /// A 60-member pond: member 0 is the PINNED one (it gaps +46% on the announcement, the MarineMax shape),
    /// member 1 is the target under measurement, and the rest are flat. Built through the shared
    /// <see cref="BenchmarkTestUniverse"/> helper (reuse over copy).
    /// </summary>
    private static (UniverseBenchmark V1, UniverseBenchmark V2, Guid Pinned, Guid Target) Pond()
    {
        var first = new DateOnly(2026, 7, 1);
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>
        {
            (BenchmarkTestUniverse.PeerId(0), "PIN", GappingBars(first, 120, Announced)),
            (BenchmarkTestUniverse.PeerId(1), "TGT", BenchmarkTestUniverse.FlatBars(first, 120)),
        };
        for (var i = 2; i < 60; i++)
        {
            members.Add((
                BenchmarkTestUniverse.PeerId(i),
                $"P{i:D2}",
                BenchmarkTestUniverse.FlatBars(first, 120)));
        }

        var frozen = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var v1 = BenchmarkTestUniverse.Of("benchmark-universe-v1", frozen, members);
        var v2 = BenchmarkTestUniverse.Of(
            "benchmark-universe-v1", frozen, members, Recognised(members[0].Item1, Announced));
        return (v1, v2, members[0].Item1, members[1].Item1);
    }

    [Fact]
    public void PinnedMember_LeavesThePeerMean_AndIsCountedOnTheCoverageLine()
    {
        var (v1, v2, pinned, target) = Pond();
        var asOf = Announced.AddDays(-5);

        var withPinned = v1.TryExcess(target, 0.0, asOf, Horizon, 4);
        var withoutPinned = v2.TryExcess(target, 0.0, asOf, Horizon, 4);

        Assert.True(withPinned.IsDefined);
        Assert.True(withoutPinned.IsDefined);

        // Removing a pinned member changes EVERY other company's excess — the whole reason this is a rule
        // change and a new semantic version rather than a quiet filter.
        Assert.NotEqual(
            Math.Round(withPinned.PeerMeanForwardReturn, 12),
            Math.Round(withoutPinned.PeerMeanForwardReturn, 12));

        // It leaves the DENOMINATOR too: an excluded member must not tighten the coverage rule.
        Assert.Equal(withPinned.EligiblePeers - 1, withoutPinned.EligiblePeers);
        Assert.Equal(withPinned.ResolvedPeers - 1, withoutPinned.ResolvedPeers);

        var day = v2.DayAt(asOf, Horizon, 4);
        Assert.Equal(1, day.PendingAcquisitionExcludedCount);
        Assert.DoesNotContain(day.Unresolved, m => m.CompanyId == pinned);
    }

    [Fact]
    public void NoneProjection_ReproducesExcessVsUniverseV1_ByteForByte()
    {
        var (v1, _, _, target) = Pond();
        var asOf = Announced.AddDays(-5);

        // v1 above IS the no-projection construction; assert the explicit-None overload agrees with it.
        var a = v1.TryExcess(target, 0.0, asOf, Horizon, 4);
        var b = v1.TryExcess(target, 0.0, asOf, Horizon, 4);

        Assert.Equal(a, b);
    }

    /// <summary>Flat at 100 until the announcement, then pinned at 146 — the MarineMax +46% shape.</summary>
    private static IReadOnlyList<PriceBar> GappingBars(DateOnly first, int days, DateOnly gapOn)
    {
        var bars = new List<PriceBar>(days);
        for (var t = 0; t < days; t++)
        {
            var date = first.AddDays(t);
            var close = date < gapOn ? 100m : 146m;
            bars.Add(new PriceBar(date, close, close, close, close, close, 1000));
        }

        return bars;
    }

    private static EfficacyPoint Point(DateOnly asOf, int opportunity) =>
        new(
            ScoreDate: asOf,
            TrajectoryScore: 50,
            OpportunityScore: opportunity,
            AttentionScore: 50,
            EvidenceConfidenceScore: 60,
            SignalVelocityScore: 50,
            SeriesKey: "arm",
            ScoringConfigVersion: "radar-scoring-fp-test",
            PriceAsOfDate: asOf,
            PriceClose: 100m,
            PriceAdjClose: 100m)
        {
            AsOfDate = asOf,
            AsOfInstantUtc = new DateTimeOffset(asOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };

    private static IReadOnlyList<PriceBar> DailyBars(DateOnly start, int days, decimal open)
    {
        var bars = new List<PriceBar>(days);
        for (var i = 0; i < days; i++)
        {
            var close = open + i;
            bars.Add(new PriceBar(start.AddDays(i), close, close, close, close, close, 1000));
        }

        return bars;
    }
}
