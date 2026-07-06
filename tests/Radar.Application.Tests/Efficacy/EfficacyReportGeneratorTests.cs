using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy;
using Radar.Domain.Scoring;
using Radar.TestSupport;

using static Radar.Application.Tests.Efficacy.EfficacyTestFakes;

namespace Radar.Application.Tests.Efficacy;

public sealed class EfficacyReportGeneratorTests
{
    private static CompanyScoreSnapshot SnapshotFor(Guid companyId) =>
        new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero))
            .Build();

    private static EfficacyReportGenerator Create(
        FakeCompanyRepository repo,
        FakeScoreSnapshotFileStore scores,
        FakePriceHistoryStore prices,
        RecordingEfficacyArtifactStore artifacts)
    {
        var builder = new EfficacyDatasetBuilder(
            repo, scores, prices, NullLogger<EfficacyDatasetBuilder>.Instance);
        return new EfficacyReportGenerator(
            builder,
            new EfficacySvgRenderer(),
            new EfficacyCsvRenderer(),
            artifacts,
            NullLogger<EfficacyReportGenerator>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_RendersRenderableSeries_SkipsSeriesMissingASide()
    {
        var rendered = Guid.NewGuid();
        var noPrice = Guid.NewGuid();
        var noScore = Guid.NewGuid();

        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithId(rendered).WithTicker("AAA").Build(),
            new CompanyBuilder().WithId(noPrice).WithTicker("BBB").Build(),
            new CompanyBuilder().WithId(noScore).WithTicker("CCC").Build());

        var scores = new FakeScoreSnapshotFileStore()
            .With(rendered, SnapshotFor(rendered))
            .With(noPrice, SnapshotFor(noPrice));
        // noScore has no snapshots registered.

        var prices = new FakePriceHistoryStore()
            .With("AAA", Bar(new DateOnly(2026, 6, 12), 100m))
            .With("CCC", Bar(new DateOnly(2026, 6, 12), 50m));
        // BBB has no price history.

        var artifacts = new RecordingEfficacyArtifactStore();
        var generator = Create(repo, scores, prices, artifacts);

        await generator.GenerateAsync(CancellationToken.None);

        // Only the series with BOTH a score point and a price bar is written.
        var written = Assert.Single(artifacts.Written);
        Assert.Equal("AAA", written.Ticker);
        Assert.StartsWith("<svg", written.Svg, StringComparison.Ordinal);
        Assert.Contains("scoreDate,scoringConfigVersion", written.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_MissingSides_DoesNotThrow()
    {
        var noPrice = Guid.NewGuid();

        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithId(noPrice).WithTicker("BBB").Build());
        var scores = new FakeScoreSnapshotFileStore().With(noPrice, SnapshotFor(noPrice));
        var prices = new FakePriceHistoryStore();
        var artifacts = new RecordingEfficacyArtifactStore();

        var generator = Create(repo, scores, prices, artifacts);

        // A series missing the price side is skipped, not thrown.
        await generator.GenerateAsync(CancellationToken.None);

        Assert.Empty(artifacts.Written);
    }

    [Fact]
    public async Task GenerateAsync_CallerCancellation_Propagates()
    {
        var companyId = Guid.NewGuid();
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithId(companyId).WithTicker("AAA").Build());
        var scores = new FakeScoreSnapshotFileStore().With(companyId, SnapshotFor(companyId));
        var prices = new FakePriceHistoryStore().With("AAA", Bar(new DateOnly(2026, 6, 12), 100m));
        var artifacts = new RecordingEfficacyArtifactStore();

        var generator = Create(repo, scores, prices, artifacts);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(cts.Token));
    }
}
