using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Collectors;
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

    // Seeds a stored signal plus a score-evidence link (with stored evidence) referencing it, so the
    // builder's "why noticed" assembly resolves the signal. Returns the signal id.
    private static async Task<Guid> SeedSignalLinkAsync(
        Harness h,
        Guid snapshotId,
        Guid signalId,
        SignalType type,
        SignalDirection direction,
        string reason)
    {
        var signal = new SignalBuilder()
            .WithId(signalId)
            .WithType(type)
            .WithDirection(direction)
            .WithReason(reason)
            .Build();
        await h.Signals.AddAsync(signal, default);

        var evidenceId = Guid.NewGuid();
        var evidence = new EvidenceBuilder()
            .WithId(evidenceId)
            .WithContentHash($"hash-{evidenceId}")
            .Build();
        await h.Evidence.AddIfNewAsync(evidence, default);

        var link = new ScoreEvidenceLink(
            Id: Guid.NewGuid(),
            ScoreSnapshotId: snapshotId,
            SignalId: signalId,
            EvidenceId: evidenceId,
            ContributionReason: "Contributed to the score.",
            ContributionWeight: 5);
        await h.Scores.AddEvidenceLinkAsync(link, default);

        return signalId;
    }

    [Fact]
    public async Task WhyNoticedListsDistinctSignalsOrderedByTypeThenDirectionThenId()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        // GovernmentContract sorts after CustomerWin in enum order, so seeding it first proves the
        // builder reorders by type.
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.GovernmentContract, SignalDirection.Positive,
            "NASA-related contract evidence found.");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Multi-launch agreement announced.");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("- Why noticed:", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "  - CustomerWin (Positive): Multi-launch agreement announced.",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "  - GovernmentContract (Positive): NASA-related contract evidence found.",
            markdown, StringComparison.Ordinal);

        // Ordered by Type (enum): CustomerWin before GovernmentContract.
        var customerIndex = markdown.IndexOf("CustomerWin (Positive)", StringComparison.Ordinal);
        var govIndex = markdown.IndexOf("GovernmentContract (Positive)", StringComparison.Ordinal);
        Assert.True(customerIndex < govIndex, "Signals should be ordered by type.");
    }

    [Fact]
    public async Task WhyNoticedCollapsesDuplicateSignalIdsToOneBullet()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        var signalId = Guid.NewGuid();
        // First link seeds the signal; the second link references the same signal id.
        await SeedSignalLinkAsync(
            h, snapshotId, signalId, SignalType.CustomerWin, SignalDirection.Positive,
            "Unique customer-win reason.");
        var dupEvidenceId = Guid.NewGuid();
        await h.Evidence.AddIfNewAsync(
            new EvidenceBuilder().WithId(dupEvidenceId).WithContentHash($"hash-{dupEvidenceId}").Build(),
            default);
        await h.Scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshotId,
                SignalId: signalId,
                EvidenceId: dupEvidenceId,
                ContributionReason: "Second link, same signal.",
                ContributionWeight: 3),
            default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        var whyNoticedIndex = markdown.IndexOf("- Why noticed:", StringComparison.Ordinal);
        Assert.True(whyNoticedIndex >= 0);

        // The reason text appears exactly once in the "why noticed" area.
        var first = markdown.IndexOf("Unique customer-win reason.", StringComparison.Ordinal);
        var next = markdown.IndexOf(
            "Unique customer-win reason.", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0, "Reason should appear once.");
        Assert.Equal(-1, next);
    }

    [Fact]
    public async Task WhyNoticedSkipsMissingSignalWithoutThrowingAndSurfacesPresentOnes()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        // A present signal that should render.
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Present signal reason.");

        // A link whose signal is NOT stored (no SeedSignalLinkAsync) — must be skipped, not thrown.
        var missingSignalId = Guid.NewGuid();
        var missingEvidenceId = Guid.NewGuid();
        await h.Evidence.AddIfNewAsync(
            new EvidenceBuilder().WithId(missingEvidenceId).WithContentHash($"hash-{missingEvidenceId}").Build(),
            default);
        await h.Scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshotId,
                SignalId: missingSignalId,
                EvidenceId: missingEvidenceId,
                ContributionReason: "Link to a missing signal.",
                ContributionWeight: 4),
            default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            "  - CustomerWin (Positive): Present signal reason.", markdown, StringComparison.Ordinal);
        // The missing signal's id should not appear in the "why noticed" block (it has no bullet).
        var whyNoticedIndex = markdown.IndexOf("- Why noticed:", StringComparison.Ordinal);
        var whyNoticedTail = markdown[whyNoticedIndex..];
        Assert.DoesNotContain(missingSignalId.ToString(), whyNoticedTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoContributingSignalsYieldsNoWhyNoticedBlock()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        // No score-evidence links seeded for this snapshot.
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        Assert.DoesNotContain("- Why noticed:", markdown, StringComparison.Ordinal);
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var allowed = new[]
        {
            RadarReportAction.Investigate,
            RadarReportAction.Watch,
            RadarReportAction.Ignore,
            RadarReportAction.NeedsMoreEvidence,
            RadarReportAction.ThesisImproving,
            RadarReportAction.ThesisDeteriorating,
        };
        Assert.All(result.Items, i => Assert.Contains(i.SuggestedAction, allowed));

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("## Signals needing review", markdown, StringComparison.Ordinal);
        Assert.Contains("Ambiguous customer-win phrasing needs a human.", markdown, StringComparison.Ordinal);
        Assert.Contains($"signal {signal.Id}", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(approved.Id.ToString(), markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsReviewKeepsMostRecentSignalsUnderCapInDescendingObservedOrder()
    {
        var h = new Harness(new WeeklyReportOptions { MaxItems = 2 });
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        // ObservedAtUtc recency order is deliberately the OPPOSITE of Id order: the LARGEST Id is
        // the most recent and the SMALLEST Id is the oldest. Under the old OrderBy(Id).Take(2) the
        // surfaced set would be {oldest, middle} — dropping the newest — so this test goes red under
        // the old ordering and green only with the recency-first key.
        var idNewest = new Guid("00000000-0000-0000-0000-000000000003");
        var idMiddle = new Guid("00000000-0000-0000-0000-000000000002");
        var idOldest = new Guid("00000000-0000-0000-0000-000000000001");

        var observedNewest = new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero);
        var observedMiddle = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);
        var observedOldest = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero);

        await h.Signals.AddAsync(new SignalBuilder()
            .WithId(idNewest)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithReason("Newest needs review.")
            .WithObservedAtUtc(observedNewest)
            .Build(), default);
        await h.Signals.AddAsync(new SignalBuilder()
            .WithId(idMiddle)
            .WithReviewStatus(SignalReviewStatus.Pending)
            .WithReason("Middle needs review.")
            .WithObservedAtUtc(observedMiddle)
            .Build(), default);
        await h.Signals.AddAsync(new SignalBuilder()
            .WithId(idOldest)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithReason("Oldest needs review.")
            .WithObservedAtUtc(observedOldest)
            .Build(), default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);
        var markdown = result.Report.MarkdownContent;

        // The two most-recent signals are surfaced; the oldest is dropped by the cap.
        var newestIndex = markdown.IndexOf($"signal {idNewest}", StringComparison.Ordinal);
        var middleIndex = markdown.IndexOf($"signal {idMiddle}", StringComparison.Ordinal);
        Assert.True(newestIndex >= 0, "Newest needs-review signal should be present.");
        Assert.True(middleIndex >= 0, "Middle needs-review signal should be present.");
        Assert.DoesNotContain($"signal {idOldest}", markdown, StringComparison.Ordinal);

        // Descending ObservedAtUtc order: newest appears before middle.
        Assert.True(newestIndex < middleIndex, "Needs-review signals should be most-recent-first.");
    }

    [Fact]
    public async Task NeedsReviewTiebreaksBySignalIdAscendingDeterministically()
    {
        var sharedObserved = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);
        var smallerId = new Guid("00000000-0000-0000-0000-000000000001");
        var largerId = new Guid("00000000-0000-0000-0000-000000000002");
        // Fixed company/snapshot ids so the whole markdown (not just the needs-review section)
        // is reproducible across the two independent builds.
        var companyId = new Guid("00000000-0000-0000-0000-0000000000a1");
        var snapshotId = new Guid("00000000-0000-0000-0000-0000000000b1");

        async Task<string> RunAsync()
        {
            var h = new Harness();
            await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

            await h.Signals.AddAsync(new SignalBuilder()
                .WithId(largerId)
                .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
                .WithReason("Larger-id signal at shared instant.")
                .WithObservedAtUtc(sharedObserved)
                .Build(), default);
            await h.Signals.AddAsync(new SignalBuilder()
                .WithId(smallerId)
                .WithReviewStatus(SignalReviewStatus.Pending)
                .WithReason("Smaller-id signal at shared instant.")
                .WithObservedAtUtc(sharedObserved)
                .Build(), default);

            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);
            return result.Report.MarkdownContent;
        }

        var first = await RunAsync();
        var second = await RunAsync();

        // Deterministic across independent builds.
        Assert.Equal(first, second);

        // Same instant → smaller Id ordered first.
        var smallerIndex = first.IndexOf($"signal {smallerId}", StringComparison.Ordinal);
        var largerIndex = first.IndexOf($"signal {largerId}", StringComparison.Ordinal);
        Assert.True(smallerIndex >= 0 && largerIndex >= 0, "Both signals should be present.");
        Assert.True(smallerIndex < largerIndex, "Same-instant signals tiebreak by Id ascending.");
    }

    [Fact]
    public async Task PersistsReportAndItemsRetrievableOrderedByRank()
    {
        var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedCompanyAsync(h, a, Guid.NewGuid(), opportunity: 80, name: "A", ticker: "A");
        await SeedCompanyAsync(h, b, Guid.NewGuid(), opportunity: 40, name: "B", ticker: "B");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

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
            return await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);
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
    public async Task RejectsNonUtcPeriodEnd()
    {
        var h = new Harness();
        var nonUtc = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Builder.GenerateAsync(nonUtc, CollectionSummary.Empty, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositivePeriod(int days)
    {
        Assert.Throws<ArgumentException>(
            () => new Harness(new WeeklyReportOptions { Period = TimeSpan.FromDays(days) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RejectsNonPositiveMaxItems(int maxItems)
    {
        Assert.Throws<ArgumentException>(
            () => new Harness(new WeeklyReportOptions { MaxItems = maxItems }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyReportType(string reportType)
    {
        Assert.Throws<ArgumentException>(
            () => new Harness(new WeeklyReportOptions { ReportType = reportType }));
    }

    [Fact]
    public async Task ExcludesSignalObservedExactlyAtPeriodStart()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        // Window is exclusive on its start bound: periodStart = PeriodEnd - 7d.
        var periodStart = PeriodEnd - new WeeklyReportOptions().Period;
        var onStart = new SignalBuilder()
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithReason("Observed exactly at the exclusive start bound.")
            .WithObservedAtUtc(periodStart)
            .Build();
        await h.Signals.AddAsync(onStart, default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        var markdown = result.Report.MarkdownContent;
        Assert.DoesNotContain(onStart.Id.ToString(), markdown, StringComparison.Ordinal);
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
        var result = await builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, default);

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task AttachesPassedCollectionSummaryToReportFooter()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var summary = new CollectionSummary(
            SourcesChecked: 4,
            SourcesSucceeded: 3,
            SourcesFailed: 1,
            ItemsCollected: 9,
            Failures: [new SourceFailure("Acme Feed", "https://acme.example/rss", "HTTP 503")]);

        var result = await h.Builder.GenerateAsync(PeriodEnd, summary, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("## Collection summary", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Radar checked 4 source(s) this run; 1 could not be read.", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "- Acme Feed (https://acme.example/rss): HTTP 503", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNullCollectionSummary()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => h.Builder.GenerateAsync(PeriodEnd, null!, default));
    }
}
