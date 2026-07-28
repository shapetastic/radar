using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The composed comparison: one join per strategy over that strategy's OWN persisted store, then the pure
/// harness, then one artifact pair. The primary strategy's series must be exactly what the existing
/// single-series spec-101/108 read produces — no regression, by construction.
/// </summary>
public sealed class StrategyComparisonReportGeneratorTests
{
    private static ScoringStrategyDefinition Strategy(string name, bool primary) =>
        new(name, "default", new ScoringWeights(), primary);

    private static CompanyScoreSnapshot Snapshot(Guid companyId, DateOnly asOf, int opportunity) =>
        new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(opportunity)
            .WithWindow(
                new DateTimeOffset(asOf.Year, asOf.Month, asOf.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-30),
                new DateTimeOffset(asOf.Year, asOf.Month, asOf.Day, 0, 0, 0, TimeSpan.Zero))
            .WithCreatedAtUtc(new DateTimeOffset(asOf.Year, asOf.Month, asOf.Day, 0, 0, 0, TimeSpan.Zero))
            .Build();

    private sealed record Fixture(
        StrategyComparisonReportGenerator Generator,
        EfficacyDatasetBuilder Builder,
        RecordingEfficacyArtifactStore Artifacts,
        StrategyComparisonOptions Options,
        FakeScoreSnapshotFileStore PrimaryStore,
        FakeScoreSnapshotFileStore SecondaryStore);

    /// <summary>
    /// The four-company synthetic world of <see cref="ComparisonFixtures"/>, but reached through the REAL read
    /// path: persisted snapshots in a per-strategy score store, joined by the real
    /// <see cref="EfficacyDatasetBuilder"/>. The two strategies' stores hold deliberately MIRRORED scores over
    /// identical prices. The builder is injected with the PRIMARY store, so <c>BuildAsync(ct)</c> — the
    /// existing single-series spec-101/108 entry point — reproduces the primary series.
    /// </summary>
    private static Fixture BuildFixture(int minimumObservations = 20)
    {
        var companyCount = ComparisonFixtures.CompanyIds.Length;

        var companies = new FakeCompanyRepository(
            [.. Enumerable.Range(0, companyCount).Select(c => new CompanyBuilder()
                .WithId(ComparisonFixtures.CompanyIds[c])
                .WithTicker(ComparisonFixtures.Tickers[c])
                .Build())]);

        var prices = new FakePriceHistoryStore();
        for (var c = 0; c < companyCount; c++)
        {
            prices.With(ComparisonFixtures.Tickers[c], [.. ComparisonFixtures.Bars(c)]);
        }

        var primaryStore = new FakeScoreSnapshotFileStore();
        var secondaryStore = new FakeScoreSnapshotFileStore();

        for (var c = 0; c < companyCount; c++)
        {
            var companyId = ComparisonFixtures.CompanyIds[c];
            var companyIndex = c;

            // Primary: scores aligned with the company's price slope ⇒ a positive relationship.
            primaryStore.With(
                companyId,
                [.. Enumerable.Range(0, ComparisonFixtures.AsOfDateCount).Select(d => Snapshot(
                    companyId,
                    ComparisonFixtures.AsOf(d),
                    ComparisonFixtures.AlignedThroughout(companyIndex, d)))]);

            // Mirror: the company ordering reversed ⇒ a negative relationship over the very same prices.
            secondaryStore.With(
                companyId,
                [.. Enumerable.Range(0, ComparisonFixtures.AsOfDateCount).Select(d => Snapshot(
                    companyId,
                    ComparisonFixtures.AsOf(d),
                    ComparisonFixtures.AlignedThroughout(companyCount - 1 - companyIndex, d)))]);
        }

        var builder = new EfficacyDatasetBuilder(
            companies, primaryStore, prices, NullLogger<EfficacyDatasetBuilder>.Instance);

        var strategies = new ScoringStrategySet(
            [Strategy("primary", primary: true), Strategy("mirror", primary: false)]);

        var selector = new FakeStrategyScoreSnapshotStoreSelector()
            .With("primary", primaryStore)
            .With("mirror", secondaryStore);

        var artifacts = new RecordingEfficacyArtifactStore();
        // The production exit tolerance (spec 152): the fixture's price bars are daily and span every as-of date
        // plus the horizon, so every window is genuinely complete and nothing is admitted by a loose knob.
        var options = new StrategyComparisonOptions(
            21, 1.0 / 3.0, minimumObservations, ComparisonFixtures.ExitToleranceDays);

        var generator = new StrategyComparisonReportGenerator(
            strategies,
            selector,
            builder,
            new StrategyComparisonHarness(),
            new StrategyLeaderboardRenderer(),
            artifacts,
            options,
            NullLogger<StrategyComparisonReportGenerator>.Instance);

        return new Fixture(generator, builder, artifacts, options, primaryStore, secondaryStore);
    }

    [Fact]
    public async Task GenerateAsync_ReadsEachStrategyFromItsOwnStoreAndRanksThem()
    {
        var fixture = BuildFixture();

        var leaderboard = await fixture.Generator.GenerateAsync(CancellationToken.None);

        Assert.Equal(2, leaderboard.StrategiesConsidered);
        Assert.Equal(2, leaderboard.StrategiesCompared);
        Assert.Empty(leaderboard.DroppedStrategies);

        var primary = leaderboard.Rows.Single(r => r.StrategyName == "primary");
        var mirror = leaderboard.Rows.Single(r => r.StrategyName == "mirror");

        // Same companies, same prices, same dates — the ONLY difference is the persisted score series, and it
        // moves the metric to the opposite sign. Per-strategy independence, demonstrated end to end.
        Assert.True(primary.InSample.Correlation.Rho > 0.3);
        Assert.True(mirror.InSample.Correlation.Rho < -0.3);
        Assert.Equal(1, primary.Rank);
    }

    [Fact]
    public async Task GenerateAsync_PrimarySeriesIsExactlyTheExistingSingleSeriesRead()
    {
        var fixture = BuildFixture();

        var leaderboard = await fixture.Generator.GenerateAsync(CancellationToken.None);

        // Recompute the primary row from the EXISTING single-series read (the spec-101/108 entry point,
        // which reads the injected store) and assert it lands on the same numbers.
        var existingRead = await fixture.Builder.BuildAsync(CancellationToken.None);
        var recomputed = new StrategyComparisonHarness().Compare(
            [new StrategyScoreSeries("primary", existingRead)],
            fixture.Options);

        var fromGenerator = leaderboard.Rows.Single(r => r.StrategyName == "primary");
        var fromExistingRead = Assert.Single(recomputed.Rows);

        Assert.Equal(fromExistingRead.InSample.Correlation.Rho, fromGenerator.InSample.Correlation.Rho, 12);
        Assert.Equal(fromExistingRead.InSample.Coverage, fromGenerator.InSample.Coverage);
        Assert.Equal(fromExistingRead.OutOfSample.Coverage, fromGenerator.OutOfSample.Coverage);

        // …and the primary store was never written to (FakeScoreSnapshotFileStore.WriteAsync throws).
        Assert.Equal(0, fixture.PrimaryStore.WriteCount);
        Assert.Equal(0, fixture.SecondaryStore.WriteCount);
    }

    [Fact]
    public async Task GenerateAsync_WritesExactlyOneLeaderboardPairStatingNAndTheFraming()
    {
        var fixture = BuildFixture();

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        var (csv, markdown) = Assert.Single(fixture.Artifacts.Leaderboards);
        Assert.Contains("Strategies compared (ranked): 2", markdown, StringComparison.Ordinal);
        Assert.Contains(StrategyLeaderboardRenderer.Framing, markdown, StringComparison.Ordinal);
        Assert.StartsWith("status,rank,strategy,", csv, StringComparison.Ordinal);

        // It renders no per-company artifact — that is the other generator's job.
        Assert.Empty(fixture.Artifacts.Written);
    }

    [Fact]
    public async Task GenerateAsync_WithTooLittleHistoryStillWritesAnHonestLeaderboard()
    {
        // A minimum of 500 observations cannot be met by 2 companies × 30 dates ⇒ everything is dropped.
        var fixture = BuildFixture(minimumObservations: 500);

        var leaderboard = await fixture.Generator.GenerateAsync(CancellationToken.None);

        Assert.Equal(0, leaderboard.StrategiesCompared);
        Assert.Equal(2, leaderboard.DroppedStrategies.Count);
        Assert.Null(leaderboard.Headline);

        var (_, markdown) = Assert.Single(fixture.Artifacts.Leaderboards);
        Assert.Contains("No strategy could be ranked", markdown, StringComparison.Ordinal);
        Assert.Contains("| mirror | insufficient-in-sample-observations |", markdown, StringComparison.Ordinal);
        Assert.Contains("| primary | insufficient-in-sample-observations |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        var first = await BuildFixture().Generator.GenerateAsync(CancellationToken.None);
        var second = await BuildFixture().Generator.GenerateAsync(CancellationToken.None);

        var renderer = new StrategyLeaderboardRenderer();
        Assert.Equal(renderer.RenderCsv(first), renderer.RenderCsv(second));
        Assert.Equal(renderer.RenderMarkdown(first), renderer.RenderMarkdown(second));
    }

    [Fact]
    public async Task GenerateAsync_PropagatesCancellation()
    {
        var fixture = BuildFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Generator.GenerateAsync(cts.Token));
    }
}
