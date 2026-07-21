using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
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

    // A minimal in-test IPipelineRunStore that returns pre-seeded records newest-first and honours the
    // requested count via Take (mirroring the real store's cap and AD-3 ordering).
    private sealed class FakeRunStore(IReadOnlyList<PipelineRunRecord> records) : IPipelineRunStore
    {
        public Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct) =>
            Task.FromResult("unused");

        public Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PipelineRunRecord>>(records.Take(Math.Max(0, count)).ToList());
    }

    // A minimal in-test IScoreSnapshotFileStore that serves the previous snapshot from a pre-seeded
    // list, mirroring the real store's contract (latest strictly-before, CreatedAtUtc then Id
    // descending). Keeps most builder tests disk-free.
    private sealed class FakeScoreSnapshotFileStore(IReadOnlyList<CompanyScoreSnapshot> snapshots)
        : IScoreSnapshotFileStore
    {
        public FakeScoreSnapshotFileStore() : this([]) { }

        public Task<string> WriteAsync(
            CompanyScoreSnapshot snapshot,
            IReadOnlyList<ScoreEvidenceLink> links,
            CancellationToken ct) => Task.FromResult("unused");

        public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            Task.FromResult(snapshots
                .Where(s => s.CompanyId == companyId && s.CreatedAtUtc < beforeUtc)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault());

        public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanyScoreSnapshot>>(snapshots
                .Where(s => s.CompanyId == companyId)
                .OrderBy(s => s.CreatedAtUtc)
                .ThenBy(s => s.Id)
                .ToList());
    }

    // Counts GetByIdAsync calls so a test can prove the builder resolves each contributing signal once
    // (the "why noticed" refs and the policy's corroboration input are the SAME list, not two fetches).
    private sealed class CountingSignalRepository(InMemorySignalRepository inner) : ISignalRepository
    {
        public int GetByIdCallCount { get; private set; }

        public Task AddAsync(Signal signal, CancellationToken ct) => inner.AddAsync(signal, ct);

        public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            GetByIdCallCount++;
            return inner.GetByIdAsync(id, ct);
        }

        public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct) =>
            inner.GetByCompanyAsync(companyId, ct);

        public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
            DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
            inner.GetObservedBetweenAsync(startUtc, endUtc, ct);
    }

    // Records the contexts handed to the policy so a test can assert what the builder populated, while
    // still delegating the actual decision to the production policy.
    private sealed class RecordingActionPolicy(IReportActionPolicy inner) : IReportActionPolicy
    {
        public List<ReportActionContext> Contexts { get; } = [];

        public string Version => inner.Version;

        public ReportActionResult Decide(ReportActionContext context)
        {
            Contexts.Add(context);
            return inner.Decide(context);
        }
    }

    private sealed class Harness
    {
        public InMemoryCompanyRepository Companies { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public InMemorySignalRepository Signals { get; } = new();
        public InMemorySignalReviewRepository SignalReviews { get; } = new();
        public InMemoryReportRepository Reports { get; } = new();
        public CountingSignalRepository CountingSignals { get; }
        public RecordingActionPolicy Policy { get; }
        public WeeklyReportBuilder Builder { get; }

        public Harness(
            WeeklyReportOptions? options = null,
            IReadOnlyList<PipelineRunRecord>? runs = null,
            IScoreSnapshotFileStore? scoreFiles = null)
        {
            CountingSignals = new CountingSignalRepository(Signals);
            Policy = new RecordingActionPolicy(new WeeklyReportActionPolicyV1());
            Builder = new WeeklyReportBuilder(
                Companies,
                Scores,
                Evidence,
                CountingSignals,
                SignalReviews,
                Policy,
                new MarkdownWeeklyReportRenderer(),
                Reports,
                new FakeRunStore(runs ?? []),
                scoreFiles ?? new FakeScoreSnapshotFileStore(),
                options ?? new WeeklyReportOptions(),
                new FixedTimeProvider(FixedNow),
                NullLogger<WeeklyReportBuilder>.Instance);
        }
    }

    // Builds a PipelineRunRecord with a distinctive collector + counts so ordering/cap assertions are
    // unambiguous. Only the fields the footer projects are meaningful here.
    private static PipelineRunRecord RunRecord(
        DateTimeOffset createdAt, string collector, int evidenceNew) =>
        new(
            Id: Guid.NewGuid(),
            CreatedAtUtc: createdAt,
            Collectors: [collector],
            EvidenceCollected: evidenceNew,
            EvidenceNew: evidenceNew,
            SignalsExtracted: 0,
            SignalsValid: 0,
            SignalsApproved: 0,
            SignalsNeedingReview: 0,
            CompaniesScored: 0,
            SourcesChecked: 0,
            SourcesFailed: 0,
            ReportId: null);

    private static async Task SeedCompanyAsync(
        Harness h,
        Guid companyId,
        Guid snapshotId,
        int opportunity,
        string name = "Acme Corp",
        string ticker = "ACME",
        DateTimeOffset? createdAt = null,
        int trajectory = 50,
        int evidenceConfidence = 50,
        bool withLink = true,
        FollowingTier followingTier = FollowingTier.Small)
    {
        var company = new CompanyBuilder()
            .WithId(companyId)
            .WithName(name)
            .WithTicker(ticker)
            .WithFollowingTier(followingTier)
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

        // A company surfaces in the report only when its snapshot has at least one score-evidence
        // link (spec 53: zero-signal snapshots are an absence of data, not an opportunity). Seed a
        // default link so the company surfaces, unless the caller explicitly wants a zero-signal
        // snapshot (withLink: false). Ids are derived deterministically from the snapshot id so two
        // independent harnesses seeded with identical ids produce identical reports (AD-3).
        if (withLink)
        {
            var evidenceId = DeriveGuid(snapshotId, 0xE0);
            var evidence = new EvidenceBuilder()
                .WithId(evidenceId)
                .WithContentHash($"hash-{evidenceId}")
                .Build();
            await h.Evidence.AddIfNewAsync(evidence, default);

            var link = new ScoreEvidenceLink(
                Id: DeriveGuid(snapshotId, 0x11),
                ScoreSnapshotId: snapshotId,
                SignalId: DeriveGuid(snapshotId, 0x51),
                EvidenceId: evidenceId,
                ContributionReason: "Contributed to the score.",
                ContributionWeight: 5);
            await h.Scores.AddEvidenceLinkAsync(link, default);
        }
    }

    // Derives a deterministic Guid from a base Guid by XORing its last byte with a tag, so seeded
    // link/evidence/signal ids are stable across independent harness runs (determinism tests).
    private static Guid DeriveGuid(Guid baseId, byte tag)
    {
        var bytes = baseId.ToByteArray();
        bytes[^1] ^= tag;
        return new Guid(bytes);
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            "  - CustomerWin (Positive): Present signal reason.", markdown, StringComparison.Ordinal);
        // The missing signal's id should not appear in the "why noticed" block (it has no bullet).
        var whyNoticedIndex = markdown.IndexOf("- Why noticed:", StringComparison.Ordinal);
        var whyNoticedTail = markdown[whyNoticedIndex..];
        Assert.DoesNotContain(missingSignalId.ToString(), whyNoticedTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoResolvableSignalsYieldsNoWhyNoticedBlock()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        // The default link surfaces the company (spec 53) but references an unresolved signal id, so
        // there is no "why noticed" bullet to render.
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Single(result.Items);
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Single(result.Items);
        Assert.Equal(included, result.Items[0].CompanyId);
    }

    [Fact]
    public async Task UsesLatestInPeriodAsCurrentAndPriorAsPreviousForPolicy()
    {
        var companyId = Guid.NewGuid();

        // Previous (before period, low trajectory). Sourced from the file store (cross-run), NOT the
        // in-memory repo, so seed the fake score file store with it.
        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithTrajectoryScore(50)
            .WithEvidenceConfidenceScore(80)
            .WithCreatedAtUtc(BeforePeriod)
            .Build();

        var h = new Harness(scoreFiles: new FakeScoreSnapshotFileStore([prevSnapshot]));

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
        await h.Scores.AddSnapshotAsync(currentSnapshot, default);
        // The current snapshot needs ≥1 score-evidence link to surface (spec 53).
        await SeedEvidenceLinkAsync(h, currentSnapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var item = Assert.Single(result.Items);
        Assert.Equal(currentSnapshotId, item.ScoreSnapshotId);
        // The prior snapshot fed the policy, yielding an improving thesis.
        Assert.Equal(RadarReportAction.ThesisImproving, item.SuggestedAction);
    }

    [Fact]
    public async Task RendersScoreDeltaClauseFromPreviousSnapshot()
    {
        var companyId = Guid.NewGuid();

        // Previous (before period): lower opportunity/trajectory. Sourced from the file store.
        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(61)
            .WithTrajectoryScore(56)
            .WithCreatedAtUtc(BeforePeriod)
            .Build();

        var h = new Harness(scoreFiles: new FakeScoreSnapshotFileStore([prevSnapshot]));

        // Current (in period): clearly higher, so deltas are +19/+19.
        var currentSnapshotId = Guid.NewGuid();
        var currentSnapshot = new ScoreSnapshotBuilder()
            .WithId(currentSnapshotId)
            .WithCompanyId(companyId)
            .WithOpportunityScore(80)
            .WithTrajectoryScore(75)
            .WithCreatedAtUtc(InPeriod)
            .Build();

        await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await h.Scores.AddSnapshotAsync(currentSnapshot, default);
        await SeedEvidenceLinkAsync(h, currentSnapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            "(Opportunity +19, Trajectory +19 vs last run)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendersFirstSnapshotClauseWhenNoPreviousSnapshot()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("(first snapshot)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PriorSnapshotPresentOnlyOnDiskYieldsCrossRunDelta()
    {
        // The core acceptance criterion: the prior snapshot exists ONLY in the on-disk score file
        // store (an earlier run's persisted snapshot), never in this run's in-memory repo. The
        // builder must still surface a real delta, proving the cross-run read-back works.
        var tempDir = Path.Combine(Path.GetTempPath(), $"radar-scores-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var companyId = Guid.NewGuid();

            var fileStore = new FileScoreSnapshotStore(
                new FileScoreSnapshotStoreOptions { RootDirectory = tempDir },
                NullLogger<FileScoreSnapshotStore>.Instance);

            // Prior snapshot: persisted to disk only (an earlier run), lower scores.
            var priorSnapshot = new ScoreSnapshotBuilder()
                .WithId(Guid.NewGuid())
                .WithCompanyId(companyId)
                .WithOpportunityScore(60)
                .WithTrajectoryScore(55)
                .WithCreatedAtUtc(BeforePeriod)
                .Build();
            await fileStore.WriteAsync(priorSnapshot, Array.Empty<ScoreEvidenceLink>(), default);

            var h = new Harness(scoreFiles: fileStore);

            // Current run's snapshot + link live ONLY in the in-memory repo (this run's provenance).
            var currentSnapshotId = Guid.NewGuid();
            var currentSnapshot = new ScoreSnapshotBuilder()
                .WithId(currentSnapshotId)
                .WithCompanyId(companyId)
                .WithOpportunityScore(80)
                .WithTrajectoryScore(70)
                .WithCreatedAtUtc(InPeriod)
                .Build();

            await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
            await h.Scores.AddSnapshotAsync(currentSnapshot, default);
            await SeedEvidenceLinkAsync(h, currentSnapshotId);

            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

            var markdown = result.Report.MarkdownContent;
            // Deltas are current - prior: Opportunity 80-60=+20, Trajectory 70-55=+15.
            Assert.Contains(
                "(Opportunity +20, Trajectory +15 vs last run)", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("(first snapshot)", markdown, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task DifferentScoringGenerationRendersScoringUpdatedAndNoThesisLabel()
    {
        // The previous snapshot was produced by a DIFFERENT scoring generation (v0) than the current
        // run (default v1). Even though the trajectory dropped 80 → 70 (which would normally trip
        // deterioration), the snapshots are not comparable, so the movement must render
        // "(scoring updated)" and the policy must NOT emit a thesis label — that drop is a
        // scoring-logic artifact, not a real-world change (the Mercury defect).
        var companyId = Guid.NewGuid();

        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithScoringConfigVersion("radar-scoring-config-v0")
            .WithOpportunityScore(70)
            .WithTrajectoryScore(80)
            .WithEvidenceConfidenceScore(70)
            .WithCreatedAtUtc(BeforePeriod)
            .Build();

        var h = new Harness(scoreFiles: new FakeScoreSnapshotFileStore([prevSnapshot]));

        var currentSnapshotId = Guid.NewGuid();
        var currentSnapshot = new ScoreSnapshotBuilder()
            .WithId(currentSnapshotId)
            .WithCompanyId(companyId)
            .WithOpportunityScore(70)
            .WithTrajectoryScore(70)
            .WithEvidenceConfidenceScore(70)
            .WithCreatedAtUtc(InPeriod)
            .Build();

        await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await h.Scores.AddSnapshotAsync(currentSnapshot, default);
        await SeedEvidenceLinkAsync(h, currentSnapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("(scoring updated)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("vs last run)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("(first snapshot)", markdown, StringComparison.Ordinal);

        var item = Assert.Single(result.Items);
        Assert.NotEqual(RadarReportAction.ThesisDeteriorating, item.SuggestedAction);
        Assert.NotEqual(RadarReportAction.ThesisImproving, item.SuggestedAction);
    }

    [Fact]
    public async Task OldOnDiskSnapshotLackingStampIsNotComparableRendersScoringUpdated()
    {
        // An old on-disk snapshot written before the ScoringConfigVersion field existed reads back with
        // a null stamp. A null stamp is never comparable, so the report renders "(scoring updated)" and
        // does not crash. Here we simulate that by writing a prior snapshot with a null stamp via the
        // real file store.
        var tempDir = Path.Combine(Path.GetTempPath(), $"radar-scores-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var companyId = Guid.NewGuid();

            var fileStore = new FileScoreSnapshotStore(
                new FileScoreSnapshotStoreOptions { RootDirectory = tempDir },
                NullLogger<FileScoreSnapshotStore>.Instance);

            var priorSnapshot = new ScoreSnapshotBuilder()
                .WithId(Guid.NewGuid())
                .WithCompanyId(companyId)
                .WithScoringConfigVersion(null)
                .WithOpportunityScore(60)
                .WithTrajectoryScore(80)
                .WithCreatedAtUtc(BeforePeriod)
                .Build();
            await fileStore.WriteAsync(priorSnapshot, Array.Empty<ScoreEvidenceLink>(), default);

            var h = new Harness(scoreFiles: fileStore);

            var currentSnapshotId = Guid.NewGuid();
            var currentSnapshot = new ScoreSnapshotBuilder()
                .WithId(currentSnapshotId)
                .WithCompanyId(companyId)
                .WithOpportunityScore(80)
                .WithTrajectoryScore(70)
                .WithCreatedAtUtc(InPeriod)
                .Build();

            await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
            await h.Scores.AddSnapshotAsync(currentSnapshot, default);
            await SeedEvidenceLinkAsync(h, currentSnapshotId);

            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

            var markdown = result.Report.MarkdownContent;
            Assert.Contains("(scoring updated)", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("vs last run)", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("(first snapshot)", markdown, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(high, result.Items[0].CompanyId);
        Assert.Equal(1, result.Items[0].Rank);
        Assert.Equal(mid, result.Items[1].CompanyId);
        Assert.Equal(2, result.Items[1].Rank);
    }

    [Fact]
    public async Task ExcludesZeroSignalCompanyAndSurfacesSignalBearingOnesRanked()
    {
        var h = new Harness();

        // Two signal-bearing companies (default link) and one zero-signal company. The zero-signal
        // company has the HIGHEST opportunity to prove inclusion is decided by provenance (links),
        // not by the opportunity score (spec 53).
        var high = Guid.NewGuid();
        var low = Guid.NewGuid();
        var zeroSignal = Guid.NewGuid();
        await SeedCompanyAsync(h, high, Guid.NewGuid(), opportunity: 70, name: "High", ticker: "HIGH");
        await SeedCompanyAsync(h, low, Guid.NewGuid(), opportunity: 40, name: "Low", ticker: "LOW");
        await SeedCompanyAsync(
            h, zeroSignal, Guid.NewGuid(), opportunity: 99, name: "ZeroSignal", ticker: "ZERO",
            withLink: false);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // Only the two signal-bearing companies surface, ranked by opportunity descending.
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(high, result.Items[0].CompanyId);
        Assert.Equal(1, result.Items[0].Rank);
        Assert.Equal(low, result.Items[1].CompanyId);
        Assert.Equal(2, result.Items[1].Rank);
        Assert.DoesNotContain(result.Items, i => i.CompanyId == zeroSignal);

        // The zero-signal company never appears in the rendered "Highest opportunity" list.
        Assert.DoesNotContain("ZeroSignal", result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllZeroSignalRunYieldsEmptyHighestOpportunityWithoutError()
    {
        var h = new Harness();

        // Every in-period company has zero score-evidence links.
        await SeedCompanyAsync(
            h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 80, name: "A", ticker: "A", withLink: false);
        await SeedCompanyAsync(
            h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 40, name: "B", ticker: "B", withLink: false);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Empty(result.Items);
        var markdown = result.Report.MarkdownContent;
        Assert.Contains("# Radar Weekly", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("(no linked evidence)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvenanceItemCarriesSnapshotIdAndMarkdownContainsEvidenceUrlAndSnapshotId()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);
        var (_, sourceUrl) = await SeedEvidenceLinkAsync(h, snapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("## Signals needing review", markdown, StringComparison.Ordinal);
        Assert.Contains("Ambiguous customer-win phrasing needs a human.", markdown, StringComparison.Ordinal);
        Assert.Contains($"signal {signal.Id}", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(approved.Id.ToString(), markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsReviewSurfacesStoredReviewDecisionAndSummaryAsReviewReason()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var signalId = Guid.NewGuid();
        var signal = new SignalBuilder()
            .WithId(signalId)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithCompanyMention("Beta Inc")
            .WithReason("Matched phrase 'partnership'.")
            .WithObservedAtUtc(InPeriod)
            .Build();
        await h.Signals.AddAsync(signal, default);

        await h.SignalReviews.AddAsync(
            new Radar.Domain.Signals.SignalReview(
                Id: Guid.NewGuid(),
                SignalId: signalId,
                ReviewerName: "radar-signal-reviewer",
                Decision: SignalReviewDecision.EscalateToHuman,
                Summary: "Unresolved company mention",
                IssuesJson: null,
                ReviewedAtUtc: InPeriod),
            default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        // Extractor reason stays the Summary; the review decision + summary is the ReviewReason.
        Assert.Contains(
            $"- Beta Inc: Matched phrase 'partnership'. — EscalateToHuman: Unresolved company mention (signal {signalId})",
            markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsReviewDoesNotDoublePrefixWhenSummaryAlreadyStartsWithDecision()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var signalId = Guid.NewGuid();
        var signal = new SignalBuilder()
            .WithId(signalId)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithCompanyMention("Beta Inc")
            .WithReason("Matched phrase 'partnership'.")
            .WithObservedAtUtc(InPeriod)
            .Build();
        await h.Signals.AddAsync(signal, default);

        // DeterministicSignalReviewer writes summaries already prefixed with the decision; the
        // builder must not render "EscalateToHuman: EscalateToHuman: 2 issue(s).".
        await h.SignalReviews.AddAsync(
            new Radar.Domain.Signals.SignalReview(
                Id: Guid.NewGuid(),
                SignalId: signalId,
                ReviewerName: "radar-signal-reviewer",
                Decision: SignalReviewDecision.EscalateToHuman,
                Summary: "EscalateToHuman: 2 issue(s).",
                IssuesJson: null,
                ReviewedAtUtc: InPeriod),
            default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            $"- Beta Inc: Matched phrase 'partnership'. — EscalateToHuman: 2 issue(s). (signal {signalId})",
            markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("EscalateToHuman: EscalateToHuman:", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsReviewSurfacesLatestStoredReviewWhenMultipleExist()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var signalId = Guid.NewGuid();
        await h.Signals.AddAsync(new SignalBuilder()
            .WithId(signalId)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .WithCompanyMention("Beta Inc")
            .WithReason("Matched phrase 'partnership'.")
            .WithObservedAtUtc(InPeriod)
            .Build(), default);

        var earlier = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero);

        await h.SignalReviews.AddAsync(
            new Radar.Domain.Signals.SignalReview(
                Id: Guid.NewGuid(),
                SignalId: signalId,
                ReviewerName: "reviewer-a",
                Decision: SignalReviewDecision.ReduceConfidence,
                Summary: "Weak or unknown source quality",
                IssuesJson: null,
                ReviewedAtUtc: earlier),
            default);
        await h.SignalReviews.AddAsync(
            new Radar.Domain.Signals.SignalReview(
                Id: Guid.NewGuid(),
                SignalId: signalId,
                ReviewerName: "reviewer-b",
                Decision: SignalReviewDecision.EscalateToHuman,
                Summary: "Unresolved company mention",
                IssuesJson: null,
                ReviewedAtUtc: later),
            default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        // The latest review (by ReviewedAtUtc) wins.
        Assert.Contains(
            "— EscalateToHuman: Unresolved company mention (signal", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("ReduceConfidence", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsReviewFallsBackToPendingReviewWhenNoStoredReview()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var signalId = Guid.NewGuid();
        await h.Signals.AddAsync(new SignalBuilder()
            .WithId(signalId)
            .WithReviewStatus(SignalReviewStatus.Pending)
            .WithCompanyMention("Beta Inc")
            .WithReason("Matched phrase 'partnership'.")
            .WithObservedAtUtc(InPeriod)
            .Build(), default);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains(
            $"- Beta Inc: Matched phrase 'partnership'. — Pending review (signal {signalId})",
            markdown, StringComparison.Ordinal);
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
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

            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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
            return await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
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
            () => h.Builder.GenerateAsync(nonUtc, CollectionSummary.Empty, null, default));
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

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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
        // WeeklyReportBuilder now depends on IPipelineRunStore; register the file store (Infrastructure)
        // so the builder resolves from the container.
        services.AddFilePipelineRunStore(Path.Combine(Path.GetTempPath(), $"radar-runs-{Guid.NewGuid():N}"));
        // WeeklyReportBuilder now also depends on IScoreSnapshotFileStore; register the file store.
        services.AddFileScoreStore(Path.Combine(Path.GetTempPath(), $"radar-scores-{Guid.NewGuid():N}"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
        var provider = services.BuildServiceProvider();

        var companies = provider.GetRequiredService<Radar.Application.Abstractions.Persistence.ICompanyRepository>();
        var scores = provider.GetRequiredService<Radar.Application.Abstractions.Persistence.IScoreRepository>();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await scores.AddSnapshotAsync(
            new ScoreSnapshotBuilder()
                .WithId(snapshotId)
                .WithCompanyId(companyId)
                .WithOpportunityScore(70)
                .WithCreatedAtUtc(InPeriod)
                .Build(),
            default);
        // The snapshot needs ≥1 score-evidence link to surface (spec 53).
        await scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshotId,
                SignalId: Guid.NewGuid(),
                EvidenceId: Guid.NewGuid(),
                ContributionReason: "Contributed to the score.",
                ContributionWeight: 5),
            default);

        var builder = provider.GetRequiredService<IWeeklyReportBuilder>();
        var result = await builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

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

        var result = await h.Builder.GenerateAsync(PeriodEnd, summary, null, default);

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
            () => h.Builder.GenerateAsync(PeriodEnd, null!, null, default));
    }

    [Fact]
    public async Task RecentRunsFooterRendersPriorRunsNewestFirstFromStore()
    {
        var runs = new[]
        {
            RunRecord(new DateTimeOffset(2026, 2, 7, 14, 0, 0, TimeSpan.Zero), "alpha", 12),
            RunRecord(new DateTimeOffset(2026, 2, 6, 9, 0, 0, TimeSpan.Zero), "bravo", 7),
            RunRecord(new DateTimeOffset(2026, 2, 5, 8, 0, 0, TimeSpan.Zero), "charlie", 3),
        };
        var h = new Harness(runs: runs);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("## Recent runs", markdown, StringComparison.Ordinal);

        // Store order (newest-first) is preserved: alpha, then bravo, then charlie.
        var alphaIndex = markdown.IndexOf("collectors: alpha", StringComparison.Ordinal);
        var bravoIndex = markdown.IndexOf("collectors: bravo", StringComparison.Ordinal);
        var charlieIndex = markdown.IndexOf("collectors: charlie", StringComparison.Ordinal);
        Assert.True(alphaIndex >= 0 && bravoIndex >= 0 && charlieIndex >= 0);
        Assert.True(alphaIndex < bravoIndex, "Recent runs should render in store (newest-first) order.");
        Assert.True(bravoIndex < charlieIndex, "Recent runs should render in store (newest-first) order.");
    }

    [Fact]
    public async Task RecentRunsFooterCappedByRecentRunsInReport()
    {
        var runs = new[]
        {
            RunRecord(new DateTimeOffset(2026, 2, 7, 14, 0, 0, TimeSpan.Zero), "alpha", 12),
            RunRecord(new DateTimeOffset(2026, 2, 6, 9, 0, 0, TimeSpan.Zero), "bravo", 7),
            RunRecord(new DateTimeOffset(2026, 2, 5, 8, 0, 0, TimeSpan.Zero), "charlie", 3),
        };
        var h = new Harness(new WeeklyReportOptions { RecentRunsInReport = 2 }, runs);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("collectors: alpha", markdown, StringComparison.Ordinal);
        Assert.Contains("collectors: bravo", markdown, StringComparison.Ordinal);
        // The third (oldest) run is dropped by the cap.
        Assert.DoesNotContain("collectors: charlie", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassesContributingSignalsAndFollowingTierIntoActionContext()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(
            h, companyId, snapshotId, opportunity: 70, followingTier: FollowingTier.Mid);

        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Multi-year supply agreement announced.");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.");

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var context = Assert.Single(h.Policy.Contexts);
        Assert.Equal(FollowingTier.Mid, context.FollowingTier);
        Assert.Equal(
            [SignalType.CustomerWin, SignalType.StrategicPartnership],
            context.ContributingSignals.Select(s => s.Type).ToArray());
        Assert.All(
            context.ContributingSignals, s => Assert.Equal(SignalDirection.Positive, s.Direction));
    }

    [Fact]
    public async Task CorroboratedUnderFollowedLowOpportunityCompanySurfacesAsWatchNotIgnore()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        // Opportunity below the Watch line (40) with adequate evidence: Ignore under the old policy.
        await SeedCompanyAsync(
            h, companyId, snapshotId, opportunity: 30, trajectory: 55, evidenceConfidence: 70,
            followingTier: FollowingTier.Small);

        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var item = Assert.Single(result.Items);
        Assert.Equal(RadarReportAction.Watch, item.SuggestedAction);
        Assert.Contains("corroborating positive signal types", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WellFollowedLowOpportunityCompanyStillSurfacesAsIgnore()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(
            h, companyId, snapshotId, opportunity: 30, trajectory: 55, evidenceConfidence: 70,
            followingTier: FollowingTier.Mega);

        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var item = Assert.Single(result.Items);
        Assert.Equal(RadarReportAction.Ignore, item.SuggestedAction);
    }

    [Fact]
    public async Task ResolvesEachContributingSignalOnceForBothPolicyAndWhyNoticed()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70);

        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // 3 distinct link signal ids (the default seeded link plus the two above) → 3 lookups. The
        // policy reuses the SAME built list, so moving BuildSignalRefsAsync before Decide must not
        // double the fetches.
        Assert.Equal(3, h.CountingSignals.GetByIdCallCount);

        // The rendered "why noticed" block is unchanged (same refs, same order).
        var markdown = result.Report.MarkdownContent;
        var customerIndex = markdown.IndexOf("CustomerWin (Positive)", StringComparison.Ordinal);
        var partnershipIndex = markdown.IndexOf("StrategicPartnership (Positive)", StringComparison.Ordinal);
        Assert.True(customerIndex >= 0 && partnershipIndex >= 0);
        Assert.True(customerIndex < partnershipIndex, "Signals should be ordered by type.");
    }

    [Fact]
    public async Task RecentRunsFooterOmittedWhenStoreEmpty()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.DoesNotContain("## Recent runs", result.Report.MarkdownContent, StringComparison.Ordinal);
    }
}
