using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed class WeeklyReportBuilderTests
{
    // periodEnd is the inclusive end of the window; with a 7-day period the window is
    // (periodEnd - 7d, periodEnd].
    private static readonly DateTimeOffset PeriodEnd = new(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InPeriod = new(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforePeriod = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Harness
    {
        public InMemoryCompanyRepository Companies { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public InMemorySignalRepository Signals { get; } = new();
        public InMemoryReportRepository Reports { get; } = new();
        public WeeklyReportBuilder Builder { get; }

        public Harness(WeeklyReportOptions? options = null)
        {
            Builder = new WeeklyReportBuilder(
                Companies,
                Scores,
                Evidence,
                Signals,
                new WeeklyReportActionPolicyV1(),
                new MarkdownWeeklyReportRenderer(),
                Reports,
                options ?? new WeeklyReportOptions(),
                new FixedTimeProvider(FixedNow),
                NullLogger<WeeklyReportBuilder>.Instance);
        }
    }

    private static async Task SeedCompanyAsync(
        Harness h,
        Guid companyId,
        Guid snapshotId,
        int opportunity,
        string name = "Acme Corp",
        string ticker = "ACME",
        DateTimeOffset? createdAt = null,
        int trajectory = 50,
        int evidenceConfidence = 50)
    {
        var company = new CompanyBuilder()
            .WithId(companyId)
            .WithName(name)
            .WithTicker(ticker)
            .Build();
        await h.Companies.AddAsync(company, default);

        var snapshot = new ScoreSnapshotBuilder()
            .WithId(snapshotId)
            .WithCompanyId(companyId)
            .WithOpportunityScore(opportunity)
            .WithTrajectoryScore(trajectory)
            .WithEvidenceConfidenceScore(evidenceConfidence)
            .WithCreatedAtUtc(createdAt ?? InPeriod)
            .Build();
        await h.Scores.AddSnapshotAsync(snapshot, default);
    }

    private static async Task<(Guid evidenceId, string sourceUrl)> SeedEvidenceLinkAsync(
        Harness h, Guid snapshotId, string sourceUrl = "https://example.com/acme-news")
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new EvidenceBuilder()
            .WithId(evidenceId)
            .WithTitle("Acme lands major customer")
            .WithSourceUrl(sourceUrl)
            .WithContentHash($"hash-{evidenceId}")
            .Build();
        await h.Evidence.AddIfNewAsync(evidence, default);

        var link = new ScoreEvidenceLink(
            Id: Guid.NewGuid(),
            ScoreSnapshotId: snapshotId,
            SignalId: Guid.NewGuid(),
            EvidenceId: evidenceId,
            ContributionReason: "Customer win raised trajectory.",
            ContributionWeight: 8);
        await h.Scores.AddEvidenceLinkAsync(link, default);

        return (evidenceId, sourceUrl);
    }

    [Fact]
    public async Task IncludesCompanyWithInPeriodSnapshotAndExcludesPriorOnly()
    {
        var h = new Harness();

        var included = Guid.NewGuid();
        await SeedCompanyAsync(h, included, Guid.NewGuid(), opportunity: 70, name: "Included", ticker: "INC");

        var excludedCompany = Guid.NewGuid();
        var excludedSnapshot = Guid.NewGuid();
        // Only snapshot is before the window → company excluded.
        await SeedCompanyAsync(
            h, excludedCompany, excludedSnapshot, opportunity: 90, name: "Excluded", ticker: "EXC",
            createdAt: BeforePeriod);

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        Assert.Single(result.Items);
        Assert.Equal(included, result.Items[0].CompanyId);
    }

    [Fact]
    public async Task UsesLatestInPeriodAsCurrentAndPriorAsPreviousForPolicy()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();

        // Previous (before period, low trajectory).
        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithTrajectoryScore(50)
            .WithEvidenceConfidenceScore(80)
            .WithCreatedAtUtc(BeforePeriod)
            .Build();

        // Current (in period, clearly improved trajectory).
        var currentSnapshotId = Guid.NewGuid();
        var currentSnapshot = new ScoreSnapshotBuilder()
            .WithId(currentSnapshotId)
            .WithCompanyId(companyId)
            .WithTrajectoryScore(70)
            .WithEvidenceConfidenceScore(80)
            .WithCreatedAtUtc(InPeriod)
            .Build();

        await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await h.Scores.AddSnapshotAsync(prevSnapshot, default);
        await h.Scores.AddSnapshotAsync(currentSnapshot, default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        var item = Assert.Single(result.Items);
        Assert.Equal(currentSnapshotId, item.ScoreSnapshotId);
        // The prior snapshot fed the policy, yielding an improving thesis.
        Assert.Equal(RadarReportAction.ThesisImproving, item.SuggestedAction);
    }

    [Fact]
    public async Task RanksByOpportunityDescendingAndAppliesMaxItemsCap()
    {
        var h = new Harness(new WeeklyReportOptions { MaxItems = 2 });

        var low = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var high = Guid.NewGuid();
        await SeedCompanyAsync(h, low, Guid.NewGuid(), opportunity: 30, name: "Low", ticker: "LOW");
        await SeedCompanyAsync(h, mid, Guid.NewGuid(), opportunity: 55, name: "Mid", ticker: "MID");
        await SeedCompanyAsync(h, high, Guid.NewGuid(), opportunity: 80, name: "High", ticker: "HIGH");

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(high, result.Items[0].CompanyId);
        Assert.Equal(1, result.Items[0].Rank);
        Assert.Equal(mid, result.Items[1].CompanyId);
        Assert.Equal(2, result.Items[1].Rank);
    }

    [Fact]
    public async Task ProvenanceItemCarriesSnapshotIdAndMarkdownContainsEvidenceUrlAndSnapshotId()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);
        var (_, sourceUrl) = await SeedEvidenceLinkAsync(h, snapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        var item = Assert.Single(result.Items);
        Assert.Equal(snapshotId, item.ScoreSnapshotId);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(sourceUrl, markdown, StringComparison.Ordinal);
        Assert.Contains($"Score snapshot: {snapshotId}", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAllowedLabelsAppearAndMarkdownContainsAllDisclaimers()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70, name: "A", ticker: "A");
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 45, name: "B", ticker: "B");
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 10, name: "C", ticker: "C");

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        var allowed = new[]
        {
            RadarReportAction.Investigate,
            RadarReportAction.Watch,
            RadarReportAction.NeedsMoreEvidence,
            RadarReportAction.ThesisImproving,
            RadarReportAction.ThesisDeteriorating,
        };
        Assert.All(result.Items, i => Assert.Contains(i.SuggestedAction, allowed));
        Assert.DoesNotContain(result.Items, i => i.SuggestedAction == RadarReportAction.Ignore);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("> Not financial advice.", markdown, StringComparison.Ordinal);
        Assert.Contains("> For research only.", markdown, StringComparison.Ordinal);
        Assert.Contains("> Human review required.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurfacesInPeriodSignalsNeedingReview()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var signal = new SignalBuilder()
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithCompanyMention("Beta Inc")
            .WithReason("Ambiguous customer-win phrasing needs a human.")
            .WithObservedAtUtc(InPeriod)
            .Build();
        await h.Signals.AddAsync(signal, default);

        // An approved signal in-period must NOT surface.
        var approved = new SignalBuilder()
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(InPeriod)
            .Build();
        await h.Signals.AddAsync(approved, default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("## Signals needing review", markdown, StringComparison.Ordinal);
        Assert.Contains("Ambiguous customer-win phrasing needs a human.", markdown, StringComparison.Ordinal);
        Assert.Contains($"signal {signal.Id}", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(approved.Id.ToString(), markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistsReportAndItemsRetrievableOrderedByRank()
    {
        var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedCompanyAsync(h, a, Guid.NewGuid(), opportunity: 80, name: "A", ticker: "A");
        await SeedCompanyAsync(h, b, Guid.NewGuid(), opportunity: 40, name: "B", ticker: "B");

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        var stored = await h.Reports.GetByIdAsync(result.Report.Id, default);
        Assert.NotNull(stored);
        Assert.Equal(result.Report.MarkdownContent, stored!.MarkdownContent);

        var items = await h.Reports.GetItemsAsync(result.Report.Id, default);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Rank);
        Assert.Equal(2, items[1].Rank);
        Assert.Equal(a, items[0].CompanyId);
        Assert.Equal(b, items[1].CompanyId);
    }

    [Fact]
    public async Task EmptyPeriodYieldsValidReportWithHeadingAndDisclaimersAndZeroItems()
    {
        var h = new Harness();
        // No companies / no in-period snapshots.

        var result = await h.Builder.GenerateAsync(PeriodEnd, default);

        Assert.Empty(result.Items);
        var markdown = result.Report.MarkdownContent;
        Assert.Contains("# Radar Weekly", markdown, StringComparison.Ordinal);
        Assert.Contains("> Not financial advice.", markdown, StringComparison.Ordinal);
        Assert.Contains("> For research only.", markdown, StringComparison.Ordinal);
        Assert.Contains("> Human review required.", markdown, StringComparison.Ordinal);

        var stored = await h.Reports.GetByIdAsync(result.Report.Id, default);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task ReproducibleOverSameStateAndClock()
    {
        // Two independent harnesses seeded with identical fixed ids and the same fixed clock.
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var snapshotA = Guid.NewGuid();
        var snapshotB = Guid.NewGuid();

        async Task<WeeklyReportResult> RunAsync()
        {
            var h = new Harness();
            await SeedCompanyAsync(h, companyA, snapshotA, opportunity: 80, name: "A", ticker: "A");
            await SeedCompanyAsync(h, companyB, snapshotB, opportunity: 40, name: "B", ticker: "B");
            return await h.Builder.GenerateAsync(PeriodEnd, default);
        }

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(first.Report.MarkdownContent, second.Report.MarkdownContent);
        Assert.Equal(first.Items.Count, second.Items.Count);

        var firstTuples = first.Items
            .Select(i => (i.CompanyId, i.ScoreSnapshotId, i.SuggestedAction, i.Rank))
            .ToList();
        var secondTuples = second.Items
            .Select(i => (i.CompanyId, i.ScoreSnapshotId, i.SuggestedAction, i.Rank))
            .ToList();
        Assert.Equal(firstTuples, secondTuples);
    }

    [Fact]
    public async Task DiWiringResolvesBuilderAndGeneratesReport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
        var provider = services.BuildServiceProvider();

        var companies = provider.GetRequiredService<Radar.Application.Abstractions.Persistence.ICompanyRepository>();
        var scores = provider.GetRequiredService<Radar.Application.Abstractions.Persistence.IScoreRepository>();
        var companyId = Guid.NewGuid();
        await companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await scores.AddSnapshotAsync(
            new ScoreSnapshotBuilder()
                .WithCompanyId(companyId)
                .WithOpportunityScore(70)
                .WithCreatedAtUtc(InPeriod)
                .Build(),
            default);

        var builder = provider.GetRequiredService<IWeeklyReportBuilder>();
        var result = await builder.GenerateAsync(PeriodEnd, default);

        Assert.Single(result.Items);
    }
}
