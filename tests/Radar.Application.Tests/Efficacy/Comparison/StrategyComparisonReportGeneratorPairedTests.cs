using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The spec-155 paired step inside <see cref="StrategyComparisonReportGenerator"/>: it runs over the SAME
/// already-built series the leaderboard consumed, writes its own artifact pair when configured, writes the
/// honest exploratory artifact when baselines exist but no primary was predeclared, and skips (log-only)
/// only when there is nothing to pair at all.
/// </summary>
public sealed class StrategyComparisonReportGeneratorPairedTests
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
        RecordingEfficacyArtifactStore Artifacts);

    /// <summary>
    /// The <see cref="ComparisonFixtures"/> world through the real read path: a primary aligned with price
    /// and a mirrored <c>baseline-mirror</c> (or a non-baseline "mirror") in its own store.
    /// </summary>
    private static Fixture BuildFixture(
        string secondaryName, PairedComparisonOptions? pairedOptions)
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
            primaryStore.With(
                companyId,
                [.. Enumerable.Range(0, ComparisonFixtures.AsOfDateCount).Select(d => Snapshot(
                    companyId,
                    ComparisonFixtures.AsOf(d),
                    ComparisonFixtures.AlignedThroughout(companyIndex, d)))]);
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
            [Strategy("primary", primary: true), Strategy(secondaryName, primary: false)]);

        var selector = new FakeStrategyScoreSnapshotStoreSelector()
            .With("primary", primaryStore)
            .With(secondaryName, secondaryStore);

        var artifacts = new RecordingEfficacyArtifactStore();
        var options = new StrategyComparisonOptions(
            21, 1.0 / 3.0, 20, ComparisonFixtures.ExitToleranceDays);

        var generator = new StrategyComparisonReportGenerator(
            strategies,
            selector,
            builder,
            new StrategyComparisonHarness(),
            new StrategyLeaderboardRenderer(),
            artifacts,
            options,
            NullLogger<StrategyComparisonReportGenerator>.Instance,
            pairedOptions);

        return new Fixture(generator, artifacts);
    }

    private static PairedComparisonOptions Paired(string configuredPrimary) =>
        new(
            configuredPrimary,
            firstEligibleAsOf: null,
            minimumCompaniesPerDate: 2,
            new StrategyComparisonOptions(21, 1.0 / 3.0, 20, ComparisonFixtures.ExitToleranceDays));

    [Fact]
    public async Task GenerateAsync_WithPairedOptionsAndABaseline_WritesThePairedArtifacts()
    {
        var fixture = BuildFixture("baseline-mirror", Paired("primary"));

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        var (csv, markdown, blocksCsv) = Assert.Single(fixture.Artifacts.PairedComparisons);
        Assert.StartsWith("status,primaryStrategy,", csv, StringComparison.Ordinal);
        Assert.Contains("baseline-mirror", markdown, StringComparison.Ordinal);
        Assert.Contains(PairedComparisonRenderer.Framing, markdown, StringComparison.Ordinal);
        Assert.StartsWith(
            "baseline,blockDate,companies,primaryRho,baselineRho,pairedDelta",
            blocksCsv,
            StringComparison.Ordinal);

        // The leaderboard pair is still written — the paired artifact is additive, not a replacement.
        Assert.Single(fixture.Artifacts.Leaderboards);
    }

    [Fact]
    public async Task GenerateAsync_WithNoPrerequisite_TheArtifactFailsClosedAsNotCalculated()
    {
        // The 1-arg overload is "no attention screen in this composition": the composite gate must read
        // ad16-screen-not-calculated, never silently qualify from the price side.
        var fixture = BuildFixture("baseline-mirror", Paired("primary"));

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        var (csv, markdown, _) = Assert.Single(fixture.Artifacts.PairedComparisons);
        Assert.Contains("ad16-screen-not-calculated", csv, StringComparison.Ordinal);
        Assert.Contains("ad16-screen-not-calculated", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("adding value", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_WithAPrerequisite_TheArtifactCarriesItsOutcome()
    {
        var fixture = BuildFixture("baseline-mirror", Paired("primary"));

        await fixture.Generator.GenerateAsync(
            Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Pending), CancellationToken.None);

        var (csv, markdown, _) = Assert.Single(fixture.Artifacts.PairedComparisons);
        Assert.Contains(",pending,", csv, StringComparison.Ordinal);
        Assert.Contains("ad16-screen-pending", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("adding value", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_BaselinesExistButNoPrimaryNamed_WritesTheHonestExploratoryArtifact()
    {
        var fixture = BuildFixture("baseline-mirror", Paired(configuredPrimary: ""));

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        var (_, markdown, _) = Assert.Single(fixture.Artifacts.PairedComparisons);
        Assert.Contains("No primary was predeclared", markdown, StringComparison.Ordinal);
        Assert.Contains("Status: EXPLORATORY", markdown, StringComparison.Ordinal);
        Assert.Contains("'primary'", markdown, StringComparison.Ordinal);   // paired the pipeline primary
    }

    [Fact]
    public async Task GenerateAsync_NoBaselinesAndNoPrimaryNamed_SkipsThePairedArtifact()
    {
        var fixture = BuildFixture("mirror", Paired(configuredPrimary: ""));

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        Assert.Empty(fixture.Artifacts.PairedComparisons);
        Assert.Single(fixture.Artifacts.Leaderboards);      // the leaderboard is unaffected
    }

    [Fact]
    public async Task GenerateAsync_PrimaryNamedButNoBaselines_StillWritesTheHonestNoBaselinesArtifact()
    {
        var fixture = BuildFixture("mirror", Paired("primary"));

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        var (csv, markdown, _) = Assert.Single(fixture.Artifacts.PairedComparisons);
        Assert.Contains("no-baselines", csv, StringComparison.Ordinal);
        Assert.Contains("no-baselines", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_WithoutPairedOptions_IsThePre155ShapeAndWritesNoPairedArtifact()
    {
        var fixture = BuildFixture("baseline-mirror", pairedOptions: null);

        await fixture.Generator.GenerateAsync(CancellationToken.None);

        Assert.Empty(fixture.Artifacts.PairedComparisons);
        Assert.Single(fixture.Artifacts.Leaderboards);
    }
}
