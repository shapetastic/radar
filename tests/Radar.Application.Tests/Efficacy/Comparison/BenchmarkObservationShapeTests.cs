using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 183's structural "no lost paired support": the shared observation carries the raw return plus a
/// NULLABLE excess; the paired harness consumes only raw-return ranks (no benchmark gate, byte-identical
/// numeric outputs with or without a benchmark), while the pooled leaderboard consumes excess and excludes
/// null with named, counted reasons. Plus the per-date affine invariance, pinned.
/// </summary>
public sealed class BenchmarkObservationShapeTests
{
    private static readonly StrategyComparisonHarness Harness = new();
    private static readonly PairedComparisonHarness PairedHarness = new();

    /// <summary>
    /// A universe over the fixture companies whose coverage rule can NEVER pass: the 4 companies plus 20
    /// flat peers = 24 members ⇒ 23 eligible peers &lt; the 40 floor. Every member observation therefore
    /// gets a null excess with reason BenchmarkUnavailable — the shape spec 183's paired path must survive.
    /// </summary>
    private static UniverseBenchmark UndersizedUniverse()
    {
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var c = 0; c < ComparisonFixtures.CompanyIds.Length; c++)
        {
            members.Add((
                ComparisonFixtures.CompanyIds[c],
                ComparisonFixtures.Tickers[c],
                ComparisonFixtures.Bars(c)));
        }

        for (var p = 0; p < 20; p++)
        {
            members.Add((
                BenchmarkTestUniverse.PeerId(p),
                $"SM{p:D2}",
                BenchmarkTestUniverse.FlatBars(ComparisonFixtures.FirstAsOf, 91)));
        }

        return BenchmarkTestUniverse.Of(
            "benchmark-universe-v1", ComparisonFixtures.BenchmarkFrozenAtUtc, members);
    }

    [Fact]
    public void Observations_CarryRawAlways_AndExcessOnlyWhenTheBenchmarkResolves()
    {
        var strategy = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);

        var covered = StrategyObservationBuilder.Build(strategy, 21, 4, ComparisonFixtures.Benchmark());
        var uncovered = StrategyObservationBuilder.Build(strategy, 21, 4, UndersizedUniverse());

        Assert.All(covered.Usable, o =>
        {
            Assert.NotNull(o.ExcessForwardReturn);
            Assert.Equal(BenchmarkExcessUnavailableReason.None, o.ExcessUnavailableReason);
        });
        Assert.All(uncovered.Usable, o =>
        {
            Assert.Null(o.ExcessForwardReturn);
            Assert.Equal(BenchmarkExcessUnavailableReason.BenchmarkUnavailable, o.ExcessUnavailableReason);
        });

        // The RAW side is untouched by the benchmark: same observations, same raw values, same order.
        Assert.Equal(
            covered.Usable.Select(o => (o.AsOf, o.CompanyId, o.Score, o.RawForwardReturn)),
            uncovered.Usable.Select(o => (o.AsOf, o.CompanyId, o.Score, o.RawForwardReturn)));
    }

    [Fact]
    public void NullExcessObservations_SurviveIntoThePairedHarness_AndAreExcludedFromPooledCohorts()
    {
        // ONE shared fixture, both consumers (the spec's exact test): a coverage-failing benchmark makes
        // every excess null. The pooled leaderboard must exclude everything with the counted reason; the
        // paired harness must keep byte-identical support.
        var primary = PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(8));
        var baseline = PairedFixtures.Series(
            "baseline-anti", PairedFixtures.AntiAligned, PairedFixtures.Spaced(8));

        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var c = 0; c < 4; c++)
        {
            members.Add((PairedFixtures.CompanyIds[c], PairedFixtures.Tickers[c], PairedFixtures.Bars(c)));
        }

        var undersized = BenchmarkTestUniverse.Of(
            "benchmark-universe-v1",
            new DateTimeOffset(PairedFixtures.FirstAsOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            members);

        // Pooled: everything is BenchmarkUnavailable ⇒ nothing can rank, and the exclusion is COUNTED.
        var options = new StrategyComparisonOptions(
            PairedFixtures.HorizonDays, 1.0 / 3.0, 4, PairedFixtures.ExitToleranceDays);
        var leaderboard = Harness.Compare([primary, baseline], options, undersized);
        Assert.Empty(leaderboard.Rows);
        Assert.Equal(2, leaderboard.DroppedStrategies.Count);
        Assert.Equal(0, leaderboard.Windows.TotalAsOfDates);

        // Paired: SAME fixture, NO exclusion — support and every numeric output byte-identical to a run
        // with no benchmark at all (the pre-183 shape).
        var pairedOptions = PairedFixtures.Options();
        var withBenchmark = PairedHarness.Compare(
            [primary, baseline], "primary", primaryWasPredeclared: true, pairedOptions, undersized);
        var withoutBenchmark = PairedHarness.Compare(
            [primary, baseline], "primary", primaryWasPredeclared: true, pairedOptions);

        Assert.Equal(withoutBenchmark.JointSupport, withBenchmark.JointSupport);
        Assert.Equal(withoutBenchmark.EligibleJointSupport, withBenchmark.EligibleJointSupport);
        Assert.Equal(withoutBenchmark.CandidateDates.Count, withBenchmark.CandidateDates.Count);
        Assert.Equal(withoutBenchmark.DroppedDates.Count, withBenchmark.DroppedDates.Count);

        var renderer = new PairedComparisonRenderer();
        var verdictWith = Radar.Application.Efficacy.Claims.Ad15ClaimGate.Evaluate(
            withBenchmark.SatisfiesPriceGate, withBenchmark.PriceGateReasons, attentionPrerequisite: null);
        var verdictWithout = Radar.Application.Efficacy.Claims.Ad15ClaimGate.Evaluate(
            withoutBenchmark.SatisfiesPriceGate, withoutBenchmark.PriceGateReasons, attentionPrerequisite: null);
        Assert.Equal(
            renderer.RenderCsv(withoutBenchmark, verdictWithout),
            renderer.RenderCsv(withBenchmark, verdictWith));
        Assert.Equal(
            renderer.RenderMarkdown(withoutBenchmark, verdictWithout),
            renderer.RenderMarkdown(withBenchmark, verdictWith));
        Assert.Equal(
            renderer.RenderBlocksCsv(withoutBenchmark),
            renderer.RenderBlocksCsv(withBenchmark));
    }

    [Fact]
    public void PairedStatistic_IsByteIdentical_WithAndWithoutAFullCoverageBenchmark()
    {
        // The invariance made operational: even when excess IS defined everywhere, the paired outputs do
        // not move by a byte — the harness consumes only raw-return ranks.
        var primary = PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(8));
        var baseline = PairedFixtures.Series(
            "baseline-anti", PairedFixtures.AntiAligned, PairedFixtures.Spaced(8));
        var pairedOptions = PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.AsOf(0));

        var with = PairedHarness.Compare(
            [primary, baseline], "primary", primaryWasPredeclared: true, pairedOptions,
            PairedFixtures.Benchmark());
        var without = PairedHarness.Compare(
            [primary, baseline], "primary", primaryWasPredeclared: true, pairedOptions);

        var renderer = new PairedComparisonRenderer();
        var verdictWith = Radar.Application.Efficacy.Claims.Ad15ClaimGate.Evaluate(
            with.SatisfiesPriceGate, with.PriceGateReasons, attentionPrerequisite: null);
        var verdictWithout = Radar.Application.Efficacy.Claims.Ad15ClaimGate.Evaluate(
            without.SatisfiesPriceGate, without.PriceGateReasons, attentionPrerequisite: null);

        Assert.Equal(renderer.RenderCsv(without, verdictWithout), renderer.RenderCsv(with, verdictWith));
        Assert.Equal(
            renderer.RenderMarkdown(without, verdictWithout), renderer.RenderMarkdown(with, verdictWith));
        Assert.Equal(renderer.RenderBlocksCsv(without), renderer.RenderBlocksCsv(with));
    }

    [Fact]
    public void WithinOneDate_RanksByExcess_EqualRanksByRaw_Pinned()
    {
        // The affine invariance, pinned: excessᵢ = rᵢ − mean(rⱼ, j≠i) = N/(N−1) × (rᵢ − mean(all)) is a
        // strictly increasing per-date transform, so cross-sectional ordering is preserved exactly.
        var strategy = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var set = StrategyObservationBuilder.Build(strategy, 21, 4, ComparisonFixtures.Benchmark());

        foreach (var date in set.Usable.GroupBy(o => o.AsOf))
        {
            var byRaw = date.OrderBy(o => o.RawForwardReturn).Select(o => o.CompanyId).ToList();
            var byExcess = date.OrderBy(o => o.ExcessForwardReturn!.Value).Select(o => o.CompanyId).ToList();
            Assert.Equal(byRaw, byExcess);
        }
    }

    [Fact]
    public void ACompanyOutsideTheUniverse_IsCountedNotInBenchmarkUniverse_AndUntouchedInThePairedPath()
    {
        // Fixture company 3 is REMOVED from the universe (post-freeze addition to the seed): its pooled
        // observations are excluded with the named count; members keep their excess; the paired-path
        // projections still carry every observation.
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var c = 0; c < 3; c++)
        {
            members.Add((
                ComparisonFixtures.CompanyIds[c],
                ComparisonFixtures.Tickers[c],
                ComparisonFixtures.Bars(c)));
        }

        for (var p = 0; p < 44; p++)
        {
            members.Add((
                BenchmarkTestUniverse.PeerId(p),
                $"PR{p:D2}",
                BenchmarkTestUniverse.FlatBars(ComparisonFixtures.FirstAsOf, 91)));
        }

        var universe = BenchmarkTestUniverse.Of(
            "benchmark-universe-v1", ComparisonFixtures.BenchmarkFrozenAtUtc, members);

        var strategy = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var leaderboard = Harness.Compare([strategy], ComparisonFixtures.Options(4), universe);

        var row = Assert.Single(leaderboard.Rows);
        Assert.Equal(ComparisonFixtures.AsOfDateCount, row.ObservationsNotInBenchmarkUniverse);
        Assert.Equal(0, row.ObservationsBenchmarkUnavailable);
        Assert.Equal(3, row.InSample.Coverage.DistinctCompanies);        // the outsider never pools

        var set = StrategyObservationBuilder.Build(strategy, 21, 4, universe);
        var outsider = ComparisonFixtures.CompanyIds[3];
        var outsiderObservations = set.Usable.Where(o => o.CompanyId == outsider).ToList();

        // The outsider's observations are STILL USABLE (nothing gated their admission — the raw side is the
        // paired path's whole input); only their excess is absent, with the named reason.
        Assert.Equal(ComparisonFixtures.AsOfDateCount, outsiderObservations.Count);
        Assert.All(outsiderObservations, o =>
        {
            Assert.Null(o.ExcessForwardReturn);
            Assert.Equal(
                BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse, o.ExcessUnavailableReason);
        });
    }

    [Fact]
    public void TwoStrategiesAtTheSameDate_ConsumeTheIdenticalBenchmarkValueAndProvenance()
    {
        var benchmark = ComparisonFixtures.Benchmark();
        var a = StrategyObservationBuilder.Build(
            ComparisonFixtures.Strategy("a", ComparisonFixtures.AlignedThroughout), 21, 4, benchmark);
        var b = StrategyObservationBuilder.Build(
            ComparisonFixtures.Strategy("b", ComparisonFixtures.DateOnlyScore), 21, 4, benchmark);

        // Same (company, date) key ⇒ identical excess in BOTH strategies: one central computation, no
        // per-strategy member set or window rules.
        var byKeyA = a.Usable.ToDictionary(o => (o.CompanyId, o.AsOf), o => o.ExcessForwardReturn);
        foreach (var o in b.Usable)
        {
            Assert.Equal(byKeyA[(o.CompanyId, o.AsOf)], o.ExcessForwardReturn);
        }

        // …and the day provenance is literally the same computation (cached per key).
        Assert.Same(benchmark.DayAt(ComparisonFixtures.AsOf(0), 21, 4),
            benchmark.DayAt(ComparisonFixtures.AsOf(0), 21, 4));
    }
}
