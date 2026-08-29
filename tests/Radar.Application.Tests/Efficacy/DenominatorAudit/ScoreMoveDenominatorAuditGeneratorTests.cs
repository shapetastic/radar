using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.DenominatorAudit;

/// <summary>
/// The spec-172 orchestration: reads each configured strategy's series through the SHARED strategy-store
/// selector seam, writes exactly one artifact pair, and fails CLOSED when the selected store cannot serve
/// the stored evidence links (never silently reporting zero links).
/// </summary>
public sealed class ScoreMoveDenominatorAuditGeneratorTests
{
    /// <summary>A link-bearing fake store: the happy-path double for the file store's dual-interface shape.</summary>
    private sealed class FakeLinkedSnapshotStore : IScoreSnapshotFileStore, IScoreSnapshotLinkReader
    {
        private readonly Dictionary<Guid, IReadOnlyList<ScoreSnapshotWithLinks>> _byCompany = [];

        public FakeLinkedSnapshotStore With(Guid companyId, params ScoreSnapshotWithLinks[] series)
        {
            _byCompany[companyId] = series;
            return this;
        }

        public Task<IReadOnlyList<ScoreSnapshotWithLinks>> ReadAllWithLinksForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            Task.FromResult(_byCompany.TryGetValue(companyId, out var series)
                ? series
                : []);

        public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            throw new NotSupportedException("The audit reads through the link-bearing projection only.");

        public Task<DurableWriteResult> WriteAsync(
            CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct) =>
            throw new NotSupportedException("The audit must be read-only over score history.");

        public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAuditArtifactStore : IDenominatorAuditArtifactStore
    {
        public List<(string Csv, string Markdown)> Written { get; } = [];

        public Task<DenominatorAuditPaths> WriteAsync(string csv, string markdown, CancellationToken ct)
        {
            Written.Add((csv, markdown));
            return Task.FromResult(new DenominatorAuditPaths(
                DurableWriteResult.Succeeded("score-move-denominator.csv"),
                DurableWriteResult.Succeeded("score-move-denominator.md")));
        }
    }

    private static ScoreSnapshotWithLinks Point(
        Guid companyId, DateTimeOffset windowEnd, int opportunity, params string[] linkReasons)
    {
        var snapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(opportunity)
            .WithWindow(windowEnd.AddDays(-30), windowEnd)
            .WithCreatedAtUtc(windowEnd)
            .Build();

        var links = linkReasons
            .Select(reason => new ScoreEvidenceLink(
                Guid.NewGuid(), snapshot.Id, Guid.NewGuid(), Guid.NewGuid(), reason, 3))
            .ToList();

        return new ScoreSnapshotWithLinks(snapshot, links);
    }

    private static ScoringStrategySet Strategies(params string[] names) =>
        new(names
            .Select((name, i) => new ScoringStrategyDefinition(
                name, "default", new ScoringWeights(), IsPrimary: i == 0))
            .ToList());

    private static ScoreMoveDenominatorAuditGenerator Create(
        ScoringStrategySet strategies,
        FakeStrategyScoreSnapshotStoreSelector selector,
        FakeCompanyRepository companies,
        RecordingAuditArtifactStore artifacts) =>
        new(
            strategies,
            selector,
            companies,
            new ScoreMoveDenominatorAuditRenderer(),
            artifacts,
            NullLogger<ScoreMoveDenominatorAuditGenerator>.Instance);

    private static DateTimeOffset Day(int day) => new(2026, 7, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GenerateAsync_BuildsPerStrategyResults_AndWritesExactlyOneArtifactPair()
    {
        var companyId = Guid.NewGuid();
        var company = new CompanyBuilder().WithId(companyId).WithTicker("UFPT").Build();

        var store = new FakeLinkedSnapshotStore().With(
            companyId,
            Point(companyId, Day(1), 40, "MediaAttention (Neutral), strength 2, confidence 0.60"),
            Point(
                companyId,
                Day(2),
                57,
                "GuidanceChange (Positive), strength 8, confidence 0.90",
                "MediaAttention (Neutral), strength 2, confidence 0.60"));

        var strategies = Strategies("default");
        var selector = new FakeStrategyScoreSnapshotStoreSelector().With("default", store);
        var artifacts = new RecordingAuditArtifactStore();

        var report = await Create(strategies, selector, new FakeCompanyRepository(company), artifacts)
            .GenerateAsync(CancellationToken.None);

        var result = Assert.Single(report.Strategies);
        Assert.Equal("default", result.StrategyName);
        Assert.Equal(1, result.CompaniesWalked);
        Assert.Equal(1, result.CompaniesWithPairs);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(17, observation.DeltaOpportunity);
        Assert.Equal(2, observation.LinkCount);
        Assert.Equal(1, observation.DirectionalCount);

        var written = Assert.Single(artifacts.Written);
        Assert.Contains("Observations are NOT independent", written.Csv, StringComparison.Ordinal);
        Assert.Contains("Observations are NOT independent", written.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_CompanyWithASingleSnapshot_ContributesNoPair_AndStillWritesHonestly()
    {
        var companyId = Guid.NewGuid();
        var company = new CompanyBuilder().WithId(companyId).WithTicker("IOSP").Build();

        var store = new FakeLinkedSnapshotStore().With(
            companyId, Point(companyId, Day(1), 40, "GuidanceChange (Positive), strength 8, confidence 0.90"));

        var artifacts = new RecordingAuditArtifactStore();
        var report = await Create(
                Strategies("default"),
                new FakeStrategyScoreSnapshotStoreSelector().With("default", store),
                new FakeCompanyRepository(company),
                artifacts)
            .GenerateAsync(CancellationToken.None);

        var result = Assert.Single(report.Strategies);
        Assert.Empty(result.Observations);
        Assert.Equal(0, result.CompaniesWithPairs);
        Assert.Equal(1, result.CompaniesWalked);
        Assert.Single(artifacts.Written); // an honest "nothing to pair" artifact, not a skip
    }

    [Fact]
    public async Task GenerateAsync_StoreWithoutTheLinkRead_FailsClosed_NamingTheStrategy()
    {
        // FakeStrategyScoreSnapshotStoreSelector falls back to a scalar-only store for an unknown strategy,
        // which deliberately does NOT implement the link-bearing read.
        var company = new CompanyBuilder().WithId(Guid.NewGuid()).WithTicker("CAT").Build();

        var generator = Create(
            Strategies("default"),
            new FakeStrategyScoreSnapshotStoreSelector(),
            new FakeCompanyRepository(company),
            new RecordingAuditArtifactStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(CancellationToken.None));

        Assert.Contains("'default'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fails closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_MultipleStrategies_EachReadsItsOwnSeries_InConfiguredOrder()
    {
        var companyId = Guid.NewGuid();
        var company = new CompanyBuilder().WithId(companyId).WithTicker("AEHR").Build();

        var defaultStore = new FakeLinkedSnapshotStore().With(
            companyId,
            Point(companyId, Day(1), 40, "GuidanceChange (Positive), strength 8, confidence 0.90"),
            Point(companyId, Day(2), 45, "GuidanceChange (Positive), strength 8, confidence 0.90"));
        var filingsStore = new FakeLinkedSnapshotStore().With(
            companyId,
            Point(companyId, Day(1), 10, "InsiderBuying (Neutral), strength 2, confidence 0.60"),
            Point(companyId, Day(2), 30, "InsiderBuying (Neutral), strength 2, confidence 0.60"));

        var report = await Create(
                Strategies("default", "filings-led"),
                new FakeStrategyScoreSnapshotStoreSelector()
                    .With("default", defaultStore)
                    .With("filings-led", filingsStore),
                new FakeCompanyRepository(company),
                new RecordingAuditArtifactStore())
            .GenerateAsync(CancellationToken.None);

        Assert.Equal(["default", "filings-led"], report.Strategies.Select(r => r.StrategyName));
        Assert.Equal(5, Assert.Single(report.Strategies[0].Observations).DeltaOpportunity);
        var filings = Assert.Single(report.Strategies[1].Observations);
        Assert.Equal(20, filings.DeltaOpportunity);
        Assert.Equal(0, filings.DirectionalCount); // its only link is Neutral
    }
}
