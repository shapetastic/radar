using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy;
using Radar.Domain.Scoring;
using Radar.TestSupport;

using static Radar.Application.Tests.Efficacy.EfficacyTestFakes;

namespace Radar.Application.Tests.Efficacy;

public sealed class EfficacyDatasetBuilderTests
{
    private static EfficacyDatasetBuilder Build(
        FakeCompanyRepository repo, FakeScoreSnapshotFileStore scores, FakePriceHistoryStore prices) =>
        new(repo, scores, prices, NullLogger<EfficacyDatasetBuilder>.Instance);

    private static CompanyScoreSnapshot SnapshotOn(Guid companyId, DateOnly date, int opportunity = 50) =>
        new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(opportunity)
            .WithCreatedAtUtc(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero))
            .Build();

    [Fact]
    public async Task BuildAsync_PairsEachScoreToPriceBarAtOrBefore_NoLookAhead()
    {
        var companyId = Guid.NewGuid();
        var company = new CompanyBuilder().WithId(companyId).WithTicker("MRCY").Build();

        var d10 = new DateOnly(2026, 6, 10);
        var d12 = new DateOnly(2026, 6, 12);
        var d15 = new DateOnly(2026, 6, 15);

        var prices = new FakePriceHistoryStore().With("MRCY",
            Bar(d10, 100m), Bar(d12, 102m), Bar(d15, 105m));

        var before = SnapshotOn(companyId, new DateOnly(2026, 6, 5));   // predates all bars → null
        var onBar = SnapshotOn(companyId, d12);                          // exact bar → d12
        var between = SnapshotOn(companyId, new DateOnly(2026, 6, 13));  // between d12/d15 → d12
        var after = SnapshotOn(companyId, new DateOnly(2026, 6, 20));    // after last bar → d15 (never future)

        var scores = new FakeScoreSnapshotFileStore().With(companyId, after, before, onBar, between);

        var builder = Build(new FakeCompanyRepository(company), scores, prices);
        var series = await builder.BuildAsync(CancellationToken.None);

        var one = Assert.Single(series);
        Assert.Equal("MRCY", one.Ticker);

        // Ascending by ScoreDate.
        Assert.Equal(
            [new DateOnly(2026, 6, 5), d12, new DateOnly(2026, 6, 13), new DateOnly(2026, 6, 20)],
            one.Points.Select(p => p.ScoreDate).ToArray());

        // No look-ahead pairing.
        Assert.Null(one.Points[0].PriceAsOfDate);
        Assert.Null(one.Points[0].PriceClose);
        Assert.Equal(d12, one.Points[1].PriceAsOfDate);
        Assert.Equal(102m, one.Points[1].PriceClose);
        Assert.Equal(d12, one.Points[2].PriceAsOfDate);   // between → at-or-before = d12
        Assert.Equal(d15, one.Points[3].PriceAsOfDate);   // after → last bar, never a future bar
        Assert.Equal(105m, one.Points[3].PriceClose);

        // The dense price line is carried in full.
        Assert.Equal(3, one.PriceBars.Count);
    }

    [Fact]
    public async Task BuildAsync_SkipsBlankTicker_AndYieldsNullPriceFieldsWhenNoHistory()
    {
        var withScoresNoPrice = Guid.NewGuid();

        var companies = new FakeCompanyRepository(
            new CompanyBuilder().WithId(withScoresNoPrice).WithTicker("NOPX").Build(),
            new CompanyBuilder().WithTicker("   ").Build(),
            new CompanyBuilder().WithTicker(null).Build());

        var scores = new FakeScoreSnapshotFileStore()
            .With(withScoresNoPrice, SnapshotOn(withScoresNoPrice, new DateOnly(2026, 6, 10)));

        // No price history registered for NOPX.
        var prices = new FakePriceHistoryStore();

        var builder = Build(companies, scores, prices);
        var series = await builder.BuildAsync(CancellationToken.None);

        // Blank/null-ticker companies are omitted.
        var one = Assert.Single(series);
        Assert.Equal("NOPX", one.Ticker);

        // Score points exist, but with no price history all price fields are null and PriceBars is empty.
        var p = Assert.Single(one.Points);
        Assert.Null(p.PriceAsOfDate);
        Assert.Null(p.PriceClose);
        Assert.Null(p.PriceAdjClose);
        Assert.Empty(one.PriceBars);
    }

    [Fact]
    public async Task BuildAsync_JoinsMultipleCompaniesIndependently_AndWritesNothing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var companies = new FakeCompanyRepository(
            new CompanyBuilder().WithId(a).WithTicker("AAA").Build(),
            new CompanyBuilder().WithId(b).WithTicker("BBB").Build());

        var d = new DateOnly(2026, 6, 10);
        var scores = new FakeScoreSnapshotFileStore()
            .With(a, SnapshotOn(a, d, opportunity: 40))
            .With(b, SnapshotOn(b, d, opportunity: 70));
        var prices = new FakePriceHistoryStore()
            .With("AAA", Bar(d, 10m))
            .With("BBB", Bar(d, 20m));

        var builder = Build(companies, scores, prices);
        var series = await builder.BuildAsync(CancellationToken.None);

        Assert.Equal(2, series.Count);
        var seriesA = series.Single(s => s.Ticker == "AAA");
        var seriesB = series.Single(s => s.Ticker == "BBB");
        Assert.Equal(40, seriesA.Points.Single().OpportunityScore);
        Assert.Equal(70, seriesB.Points.Single().OpportunityScore);
        Assert.Equal(10m, seriesA.Points.Single().PriceClose);
        Assert.Equal(20m, seriesB.Points.Single().PriceClose);

        // Read-only: neither store was written.
        Assert.Equal(0, scores.WriteCount);
        Assert.Equal(0, prices.WriteCount);
    }

    [Fact]
    public async Task BuildAsync_SeriesKeyIsStrategyName_LegacyNullReadsAsPrimaryDefaultSeries()
    {
        // Spec 141: the efficacy segment key is the STRATEGY NAME, not the fingerprint. A legacy snapshot
        // written before spec 137 carries a null strategy name and must read as the primary "default" series
        // — the pre-137 composition IS the default strategy, so those 851 accrued snapshots stay in the same
        // series as today's primary run rather than being orphaned into an unnamed one. A named non-primary
        // strategy keeps its own key. The fingerprint rides along untouched as annotation.
        var companyId = Guid.NewGuid();
        var company = new CompanyBuilder().WithId(companyId).WithTicker("MRCY").Build();

        var legacy = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithStrategyName(null)
            .WithScoringConfigVersion("radar-scoring-fp-old")
            .WithCreatedAtUtc(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero))
            .Build();
        var primary = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithStrategyName("default")
            .WithScoringConfigVersion("radar-scoring-fp-new")
            .WithCreatedAtUtc(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero))
            .Build();
        var other = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithStrategyName("insider-only")
            .WithScoringConfigVersion("radar-scoring-fp-new")
            .WithCreatedAtUtc(new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero))
            .Build();

        var scores = new FakeScoreSnapshotFileStore().With(companyId, legacy, primary, other);
        var builder = Build(new FakeCompanyRepository(company), scores, new FakePriceHistoryStore());

        var one = Assert.Single(await builder.BuildAsync(CancellationToken.None));

        Assert.Equal(["default", "default", "insider-only"], one.Points.Select(p => p.SeriesKey).ToArray());
        Assert.Equal(
            ["radar-scoring-fp-old", "radar-scoring-fp-new", "radar-scoring-fp-new"],
            one.Points.Select(p => p.ScoringConfigVersion ?? string.Empty).ToArray());
    }
}
