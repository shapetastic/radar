using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Lifecycle;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed partial class WeeklyReportBuilderTests
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

        // Spec 169's time-bounded read: inclusive bounds, ascending CreatedAtUtc then Id (AD-3). The weekly
        // report never calls it; it is implemented so the fake still satisfies the interface.
        public Task<IReadOnlyList<PipelineRunRecord>> ReadBetweenAsync(
            DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PipelineRunRecord>>(records
                .Where(r => r.CreatedAtUtc >= startInclusiveUtc && r.CreatedAtUtc <= endInclusiveUtc)
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.Id)
                .ToList());
    }

    // A minimal in-test IScoreSnapshotFileStore that serves the previous snapshot from a pre-seeded
    // list, mirroring the real store's contract (latest strictly-before, CreatedAtUtc then Id
    // descending). Keeps most builder tests disk-free.
    private sealed class FakeScoreSnapshotFileStore(IReadOnlyList<CompanyScoreSnapshot> snapshots)
        : IScoreSnapshotFileStore
    {
        public FakeScoreSnapshotFileStore() : this([]) { }

        public Task<DurableWriteResult> WriteAsync(
            CompanyScoreSnapshot snapshot,
            IReadOnlyList<ScoreEvidenceLink> links,
            CancellationToken ct) => Task.FromResult(DurableWriteResult.Succeeded("unused"));

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

    // Spec 184: the primary strategy resolves to the SAME store the harness's scoreFiles parameter
    // supplies (so the lead==primary path is byte-identical to pre-184); each non-primary strategy gets
    // its own cached fake store, mirroring the production StrategyScopedScoreSnapshotFileStoreFactory.
    internal sealed class FakeScoreSnapshotFileStoreFactory(IScoreSnapshotFileStore primary)
        : IScoreSnapshotFileStoreFactory
    {
        private readonly Dictionary<string, IScoreSnapshotFileStore> _byStrategy =
            new(StringComparer.OrdinalIgnoreCase);

        public IScoreSnapshotFileStore Primary { get; } = primary;

        public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy)
        {
            if (strategy.IsPrimary)
            {
                return Primary;
            }

            if (!_byStrategy.TryGetValue(strategy.Name, out var store))
            {
                store = new FakeScoreSnapshotFileStore();
                _byStrategy[strategy.Name] = store;
            }

            return store;
        }

        /// <summary>Seeds a NON-primary strategy's file store with pre-existing snapshots.</summary>
        public void Seed(string strategyName, IReadOnlyList<CompanyScoreSnapshot> snapshots) =>
            _byStrategy[strategyName] = new FakeScoreSnapshotFileStore(snapshots);
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

    // Captures the assembled model on its way to the production renderer, so a test can assert what the
    // BUILDER produced (e.g. that a single-strategy run yields no strategy sections at all) rather than
    // inferring it from the rendered markdown. Rendering is delegated verbatim, so output is unchanged.
    private sealed class CapturingRenderer(IWeeklyReportRenderer inner) : IWeeklyReportRenderer
    {
        public WeeklyReportModel? LastModel { get; private set; }

        public string Render(WeeklyReportModel model)
        {
            LastModel = model;
            return inner.Render(model);
        }
    }

    // A strategy engine that can report its effective config but CANNOT score: the weekly report only ever
    // reads persisted snapshots (spec 150 is a read-only slice), so any attempt to score from the reporting
    // path fails loudly here instead of silently minting a snapshot.
    private sealed class NonScoringEngine(EffectiveScoringConfig config) : IScoringEngine
    {
        public EffectiveScoringConfig EffectiveConfig => config;

        public Task<CompanyScoreResult> ScoreCompanyAsync(
            Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct) =>
            throw new NotSupportedException(
                "The weekly report must never score; it reads persisted snapshots only.");
    }

    private sealed class FakeScoringStrategyFactory : IScoringStrategyFactory
    {
        public FakeScoringStrategyFactory(IReadOnlyList<TestStrategy> strategies)
        {
            Runtimes =
            [
                .. strategies.Select(s => new ScoringStrategyRuntime(
                    new ScoringStrategyDefinition(
                        Name: s.Name,
                        ScoringProfile: s.Name,
                        Weights: new ScoringWeights(),
                        IsPrimary: s.IsPrimary)
                    {
                        Formula = s.Formula,
                        Purpose = s.Purpose,
                    },
                    new NonScoringEngine(new EffectiveScoringConfig(
                        Fingerprint: s.Fingerprint,
                        EngineVersion: "mvp-engine-v1",
                        FormulaVersion: s.Formula,
                        Weights: new ScoringWeights(),
                        AttentionDescriptor: "attention",
                        SignalSourceDescriptor: "rules=v1;",
                        InsiderMaterialityDescriptor: "insider",
                        MediaCollapseDescriptor: "media",
                        Window: TimeSpan.FromDays(30))))),
            ];
        }

        public IReadOnlyList<ScoringStrategyRuntime> Runtimes { get; }

        public ScoringStrategyRuntime Primary => Runtimes.First(r => r.Definition.IsPrimary);
    }

    /// <summary>One configured strategy, as the report-side tests need to describe it.</summary>
    private sealed record TestStrategy(
        string Name,
        bool IsPrimary,
        string Fingerprint = "radar-scoring-fp-000000000000",
        string Formula = ScoreFormulaVersions.V8,
        StrategyPurpose Purpose = StrategyPurpose.Research);

    private static readonly IReadOnlyList<TestStrategy> SingleDefaultStrategy =
        [new TestStrategy("default", IsPrimary: true)];

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
        public CapturingRenderer Renderer { get; }
        public FakeScoringStrategyFactory StrategyFactory { get; }

        /// <summary>
        /// The SAME factory the scoring stage writes through: the primary strategy resolves to
        /// <see cref="Scores"/>, every other strategy to its own repository (which a test seeds by asking
        /// for it here — repeated calls return the same instance).
        /// </summary>
        public StrategyScopedScoreRepositoryFactory ScoreRepositories { get; }

        public WeeklyReportBuilder Builder { get; }

        /// <summary>
        /// Spec 184: the per-strategy snapshot FILE-store factory the builder reads a non-primary LEAD's
        /// cross-run "previous" snapshots through. The primary resolves to the same store the harness's
        /// <c>scoreFiles</c> parameter supplies (byte-identity with the pre-184 path); each non-primary
        /// strategy gets its own cached fake, which a test seeds by asking for it here.
        /// </summary>
        public FakeScoreSnapshotFileStoreFactory ScoreFileStores { get; }

        public Harness(
            WeeklyReportOptions? options = null,
            IReadOnlyList<PipelineRunRecord>? runs = null,
            IScoreSnapshotFileStore? scoreFiles = null,
            IReadOnlyList<TestStrategy>? strategies = null,
            IOperatingCallSource? operatingCalls = null,
            IStrategyEvidenceFactsSource? evidenceFacts = null)
        {
            CountingSignals = new CountingSignalRepository(Signals);
            Policy = new RecordingActionPolicy(new WeeklyReportActionPolicyV1());
            Renderer = new CapturingRenderer(new MarkdownWeeklyReportRenderer());
            StrategyFactory = new FakeScoringStrategyFactory(strategies ?? SingleDefaultStrategy);
            ScoreRepositories = new StrategyScopedScoreRepositoryFactory(Scores);
            ScoreFileStores = new FakeScoreSnapshotFileStoreFactory(
                scoreFiles ?? new FakeScoreSnapshotFileStore());
            Builder = new WeeklyReportBuilder(
                Companies,
                Scores,
                Evidence,
                CountingSignals,
                SignalReviews,
                Policy,
                Renderer,
                Reports,
                new FakeRunStore(runs ?? []),
                ScoreFileStores.Primary,
                StrategyFactory,
                ScoreRepositories,
                ScoreFileStores,
                operatingCalls ?? NullOperatingCallSource.Instance,
                evidenceFacts ?? UnavailableStrategyEvidenceFactsSource.Instance,
                options ?? new WeeklyReportOptions(),
                new FixedTimeProvider(FixedNow),
                NullLogger<WeeklyReportBuilder>.Instance);
        }

        /// <summary>The score repository a non-primary strategy's snapshots must be seeded into.</summary>
        public IScoreRepository RepositoryFor(string strategyName) =>
            ScoreRepositories.ForStrategy(
                StrategyFactory.Runtimes.First(r =>
                    string.Equals(r.Definition.Name, strategyName, StringComparison.OrdinalIgnoreCase))
                    .Definition);
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
    public async Task DifferentStrategySeriesRendersScoringUpdatedAndNoThesisLabel()
    {
        // INTENT UPDATED BY SPEC 141. The comparability gate now keys on the SERIES (the strategy name), not
        // on the ScoringConfigVersion fingerprint: the previous snapshot came from a different STRATEGY
        // ("insider-only") than the current run (the primary/default series). Even though the trajectory
        // dropped 80 → 70 (which would normally trip deterioration), the two snapshots measure different
        // hypotheses, so the movement must render "(scoring updated)" and the policy must NOT emit a thesis
        // label — the spec-69 Mercury defect, restated on the key that actually distinguishes two scorings.
        var companyId = Guid.NewGuid();

        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithStrategyName("insider-only")
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
    public async Task SameStrategyDifferentFingerprintStillComparable_RendersDelta()
    {
        // THE spec-141 reversal, stated as a test. Both snapshots come from the SAME strategy but carry
        // different ScoringConfigVersion fingerprints — which, before 141, was the single most common reason
        // the report said "(scoring updated)": the fingerprint folded the enabled-collector set, so switching
        // on a collector a strategy consumes nothing from silently broke every week-over-week delta. A
        // strategy is immutable by convention (enforced at startup by StrategyIdentityGuard), so a moved
        // fingerprint within one name is drift to be reported, not a reason to stop comparing.
        var companyId = Guid.NewGuid();

        var prevSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithStrategyName("default")
            .WithScoringConfigVersion("radar-scoring-fp-before")
            .WithOpportunityScore(60)
            .WithTrajectoryScore(55)
            .WithEvidenceConfidenceScore(70)
            .WithCreatedAtUtc(BeforePeriod)
            .Build();

        var h = new Harness(scoreFiles: new FakeScoreSnapshotFileStore([prevSnapshot]));

        var currentSnapshotId = Guid.NewGuid();
        var currentSnapshot = new ScoreSnapshotBuilder()
            .WithId(currentSnapshotId)
            .WithCompanyId(companyId)
            .WithStrategyName("default")
            .WithScoringConfigVersion("radar-scoring-fp-after")
            .WithOpportunityScore(80)
            .WithTrajectoryScore(70)
            .WithEvidenceConfidenceScore(70)
            .WithCreatedAtUtc(InPeriod)
            .Build();

        await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
        await h.Scores.AddSnapshotAsync(currentSnapshot, default);
        await SeedEvidenceLinkAsync(h, currentSnapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("(Opportunity +20, Trajectory +15 vs last run)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("(scoring updated)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldOnDiskSnapshotLackingStampReadsAsPrimarySeriesAndIsComparable()
    {
        // INTENT REVERSED BY SPEC 141, deliberately. An old on-disk snapshot written before the
        // ScoringConfigVersion/StrategyName fields existed reads back with BOTH null. Under the old
        // fingerprint-keyed gate that made it permanently incomparable — "(scoring updated)" forever, for
        // every one of the 851 accrued snapshots. The series key is now the STRATEGY NAME, and a null name
        // canonicalises to the primary "default" series (ScoreSeriesKey), because the pre-137 composition IS
        // the default strategy. So legacy history compares against today's primary run instead of being
        // orphaned, and the real week-over-week delta renders. Written through the real file store so the
        // null round-trip is exercised, not assumed.
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
                .WithStrategyName(null)
                .WithOpportunityScore(60)
                .WithTrajectoryScore(80)
                .WithCreatedAtUtc(BeforePeriod)
                .Build();
            await fileStore.WriteAsync(priorSnapshot, Array.Empty<ScoreEvidenceLink>(), default);

            var h = new Harness(scoreFiles: fileStore);

            var currentSnapshotId = Guid.NewGuid();
            // The current run is the primary strategy carrying a REAL (and different) fingerprint stamp — the
            // combination that used to force "(scoring updated)". It must now compare.
            var currentSnapshot = new ScoreSnapshotBuilder()
                .WithId(currentSnapshotId)
                .WithCompanyId(companyId)
                .WithScoringConfigVersion("radar-scoring-fp-something-new")
                .WithStrategyName("default")
                .WithOpportunityScore(80)
                .WithTrajectoryScore(70)
                .WithCreatedAtUtc(InPeriod)
                .Build();

            await h.Companies.AddAsync(new CompanyBuilder().WithId(companyId).Build(), default);
            await h.Scores.AddSnapshotAsync(currentSnapshot, default);
            await SeedEvidenceLinkAsync(h, currentSnapshotId);

            var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

            var markdown = result.Report.MarkdownContent;
            Assert.Contains(
                "(Opportunity +20, Trajectory -10 vs last run)", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("(scoring updated)", markdown, StringComparison.Ordinal);
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
        // Spec 150: the builder now resolves IScoringStrategyFactory, whose ScoringStrategyFactory
        // implementation takes ISignalFileStore — so a composition that renders a report must register the
        // signal store too (the Worker always does).
        services.AddFileSignalStore(Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}"));
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
    public async Task EntryCarriesCompanyFollowingTierIntoRenderedNotednessLine()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(
            h, companyId, snapshotId, opportunity: 70, followingTier: FollowingTier.Mega);

        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Multi-year supply agreement announced.");

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Contains(
            "- **Notedness:** Attention ", result.Report.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains(
            "· Following: Mega (already broadly followed)",
            result.Report.MarkdownContent,
            StringComparison.Ordinal);
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

    // ---------------------------------------------------------------------------------------------
    // Spec 150 — one plain ranked table per configured strategy. Nothing below is combined ACROSS
    // strategies (no disagreement metric, no merged ranking, no composite) and nothing below carries a
    // label, evidence block or "why noticed": those stay primary-only, deliberately.
    // ---------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<TestStrategy> TwoStrategies =
    [
        new TestStrategy("default", IsPrimary: true, "radar-scoring-fp-111111111111"),
        new TestStrategy("filings-led", IsPrimary: false, "radar-scoring-fp-222222222222", "radar-formula-v9"),
    ];

    /// <summary>Seeds only the company record (no snapshot), for "this strategy did not score it" cases.</summary>
    private static async Task SeedCompanyOnlyAsync(Harness h, Guid companyId, string name, string ticker)
    {
        var company = new CompanyBuilder()
            .WithId(companyId)
            .WithName(name)
            .WithTicker(ticker)
            .Build();
        await h.Companies.AddAsync(company, default);
    }

    /// <summary>
    /// Seeds one snapshot (and, unless suppressed, one score-evidence link) into the repository the named
    /// strategy writes through — the same <see cref="StrategyScopedScoreRepositoryFactory"/> the scoring
    /// stage uses, so this exercises the production read path rather than a test-only one.
    /// </summary>
    private static async Task SeedStrategySnapshotAsync(
        Harness h,
        string strategyName,
        Guid companyId,
        Guid snapshotId,
        int opportunity,
        DateTimeOffset? createdAt = null,
        bool withLink = true,
        int trajectory = 60,
        int attention = 20,
        int evidenceConfidence = 80,
        int velocity = 50)
    {
        var repository = h.RepositoryFor(strategyName);

        var snapshot = new ScoreSnapshotBuilder()
            .WithId(snapshotId)
            .WithCompanyId(companyId)
            .WithOpportunityScore(opportunity)
            .WithTrajectoryScore(trajectory)
            .WithAttentionScore(attention)
            .WithEvidenceConfidenceScore(evidenceConfidence)
            .WithSignalVelocityScore(velocity)
            .WithStrategyName(strategyName)
            .WithCreatedAtUtc(createdAt ?? InPeriod)
            .Build();
        await repository.AddSnapshotAsync(snapshot, default);

        if (withLink)
        {
            await repository.AddEvidenceLinkAsync(
                new ScoreEvidenceLink(
                    Id: DeriveGuid(snapshotId, 0x21),
                    ScoreSnapshotId: snapshotId,
                    SignalId: DeriveGuid(snapshotId, 0x61),
                    EvidenceId: DeriveGuid(snapshotId, 0xE1),
                    ContributionReason: "Contributed to the score.",
                    ContributionWeight: 5),
                default);
        }
    }

    /// <summary>The rendered markdown from the first "## Strategy:" heading onwards.</summary>
    private static string StrategySectionsOf(string markdown)
    {
        var index = markdown.IndexOf("## Strategy:", StringComparison.Ordinal);
        return index < 0 ? string.Empty : markdown[index..];
    }

    [Fact]
    public async Task SingleConfiguredStrategy_ProducesNoStrategySectionsAtAll()
    {
        var h = new Harness();
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // Null, not an empty list: "no sections" has exactly one representation.
        Assert.Null(h.Renderer.LastModel!.Strategies);
        Assert.DoesNotContain("## Strategy:", result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoStrategies_RenderOneTableEach_PrimaryFirst_WithFingerprintAndCounts()
    {
        var h = new Harness(strategies: TwoStrategies);
        var acmeId = Guid.NewGuid();
        var borealisId = Guid.NewGuid();
        await SeedCompanyAsync(h, acmeId, Guid.NewGuid(), opportunity: 71, name: "Acme Dynamics",
            ticker: "ACME");
        await SeedCompanyAsync(h, borealisId, Guid.NewGuid(), opportunity: 40, name: "Borealis Systems",
            ticker: "BOR");

        // The primary series lives in the shared repository (already seeded by SeedCompanyAsync); the
        // non-primary strategy writes its own.
        await SeedStrategySnapshotAsync(h, "filings-led", acmeId, Guid.NewGuid(), opportunity: 12);
        await SeedStrategySnapshotAsync(h, "filings-led", borealisId, Guid.NewGuid(), opportunity: 88);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var sections = h.Renderer.LastModel!.Strategies;
        Assert.NotNull(sections);
        Assert.Equal(["default", "filings-led"], sections.Select(s => s.StrategyName).ToArray());
        Assert.True(sections[0].IsPrimary);
        Assert.False(sections[1].IsPrimary);
        Assert.Equal("radar-formula-v8", sections[0].FormulaVersion);
        Assert.Equal("radar-formula-v9", sections[1].FormulaVersion);
        Assert.Equal("radar-scoring-fp-222222222222", sections[1].ScoringConfigVersion);
        Assert.Equal(2, sections[0].CompaniesScored);
        Assert.Equal(2, sections[1].CompaniesScored);

        Assert.Contains(
            "## Strategy: default (radar-formula-v8) — primary (the series reported above)",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Fingerprint: radar-scoring-fp-111111111111 · 2 companies scored · 2 with linked evidence",
            markdown, StringComparison.Ordinal);
        Assert.Contains("## Strategy: filings-led (radar-formula-v9)", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Fingerprint: radar-scoring-fp-222222222222 · 2 companies scored · 2 with linked evidence",
            markdown, StringComparison.Ordinal);

        // Each strategy ranks on its OWN scores: Acme leads the primary, Borealis leads filings-led.
        var filingsLed = markdown[markdown.IndexOf("## Strategy: filings-led", StringComparison.Ordinal)..];
        Assert.Contains("| 1 | Borealis Systems | BOR | 88 |", filingsLed, StringComparison.Ordinal);
        Assert.Contains("| 2 | Acme Dynamics | ACME | 12 |", filingsLed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompanyOnlyOneStrategyScored_AppearsInThatTableOnly_AndTheOtherCountReflectsIt()
    {
        var h = new Harness(strategies: TwoStrategies);
        var scoredByBoth = Guid.NewGuid();
        var primaryOnly = Guid.NewGuid();
        await SeedCompanyAsync(h, scoredByBoth, Guid.NewGuid(), opportunity: 70, name: "Shared Corp",
            ticker: "SHR");
        await SeedCompanyAsync(h, primaryOnly, Guid.NewGuid(), opportunity: 65, name: "Primary Only Corp",
            ticker: "PON");

        // filings-led scored only ONE of the two companies.
        await SeedStrategySnapshotAsync(h, "filings-led", scoredByBoth, Guid.NewGuid(), opportunity: 50);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var sections = h.Renderer.LastModel!.Strategies!;

        Assert.Equal(2, sections[0].CompaniesScored);
        Assert.Equal(1, sections[1].CompaniesScored);
        Assert.Equal(["Shared Corp"], sections[1].Rows.Select(r => r.CompanyName).ToArray());

        var filingsLed = result.Report.MarkdownContent[
            result.Report.MarkdownContent.IndexOf("## Strategy: filings-led", StringComparison.Ordinal)..];
        Assert.Contains("1 company scored · 1 with linked evidence", filingsLed, StringComparison.Ordinal);
        // The unscored company is OMITTED, never invented with a zero row.
        Assert.DoesNotContain("Primary Only Corp", filingsLed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompanyScoredOutsideThePeriod_IsOmittedFromThatStrategysTable()
    {
        var h = new Harness(strategies: TwoStrategies);
        var companyId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, Guid.NewGuid(), opportunity: 70, name: "Stale Corp",
            ticker: "STL");

        await SeedStrategySnapshotAsync(
            h, "filings-led", companyId, Guid.NewGuid(), opportunity: 55, createdAt: BeforePeriod);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var sections = h.Renderer.LastModel!.Strategies!;

        Assert.Equal(0, sections[1].CompaniesScored);
        Assert.Empty(sections[1].Rows);
        Assert.Contains("0 companies scored · 0 with linked evidence",
            result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroEvidenceLinkSnapshot_IsExcludedFromRows_ButCountedAsScored()
    {
        var h = new Harness(strategies: TwoStrategies);
        var withEvidence = Guid.NewGuid();
        var withoutEvidence = Guid.NewGuid();
        await SeedCompanyOnlyAsync(h, withEvidence, "Evidenced Corp", "EVD");
        await SeedCompanyOnlyAsync(h, withoutEvidence, "Zero Signal Corp", "ZRO");

        await SeedStrategySnapshotAsync(h, "filings-led", withEvidence, Guid.NewGuid(), opportunity: 30);
        // A neutral zero-signal snapshot: scored, but backed by no evidence at all (spec 53).
        await SeedStrategySnapshotAsync(
            h, "filings-led", withoutEvidence, Guid.NewGuid(), opportunity: 99, withLink: false);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var section = h.Renderer.LastModel!.Strategies![1];

        Assert.Equal(2, section.CompaniesScored);
        Assert.Equal(1, section.CompaniesWithLinkedEvidence);
        Assert.Equal(["Evidenced Corp"], section.Rows.Select(r => r.CompanyName).ToArray());
        Assert.False(section.Truncated); // the exclusion is NOT a truncation

        var markdown = result.Report.MarkdownContent;
        Assert.Contains("2 companies scored · 1 with linked evidence", markdown, StringComparison.Ordinal);
        // Despite the highest Opportunity in the set, the zero-evidence company never surfaces.
        Assert.DoesNotContain("Zero Signal Corp", StrategySectionsOf(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowsAreOrderedByOpportunityDescendingThenCompanyIdAscending()
    {
        var lowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var highId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var topId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        var h = new Harness(strategies: TwoStrategies);
        await SeedCompanyOnlyAsync(h, lowId, "Tie A", "TIEA");
        await SeedCompanyOnlyAsync(h, highId, "Tie B", "TIEB");
        await SeedCompanyOnlyAsync(h, topId, "Top", "TOP");

        // Seeded deliberately out of rank order, and the two ties share an Opportunity.
        await SeedStrategySnapshotAsync(h, "filings-led", highId, Guid.NewGuid(), opportunity: 40);
        await SeedStrategySnapshotAsync(h, "filings-led", topId, Guid.NewGuid(), opportunity: 90);
        await SeedStrategySnapshotAsync(h, "filings-led", lowId, Guid.NewGuid(), opportunity: 40);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var section = h.Renderer.LastModel!.Strategies![1];

        Assert.Equal(["Top", "Tie A", "Tie B"], section.Rows.Select(r => r.CompanyName).ToArray());
        Assert.Equal([1, 2, 3], section.Rows.Select(r => r.Rank).ToArray());

        // Deterministic across an independently-seeded, identically-configured second run (AD-3).
        var h2 = new Harness(strategies: TwoStrategies);
        await SeedCompanyOnlyAsync(h2, topId, "Top", "TOP");
        await SeedCompanyOnlyAsync(h2, lowId, "Tie A", "TIEA");
        await SeedCompanyOnlyAsync(h2, highId, "Tie B", "TIEB");
        await SeedStrategySnapshotAsync(h2, "filings-led", lowId, Guid.NewGuid(), opportunity: 40);
        await SeedStrategySnapshotAsync(h2, "filings-led", highId, Guid.NewGuid(), opportunity: 40);
        await SeedStrategySnapshotAsync(h2, "filings-led", topId, Guid.NewGuid(), opportunity: 90);

        var result2 = await h2.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // The snapshot ids differ between the two harnesses, but the strategy TABLE (which cites no ids)
        // must be byte-identical.
        Assert.Equal(
            StrategySectionsOf(result.Report.MarkdownContent),
            StrategySectionsOf(result2.Report.MarkdownContent));
    }

    [Fact]
    public async Task MaxItemsCapsEachSectionIndependently_AndTheHeaderSaysSo()
    {
        // MaxItems = 1: each strategy shows its own top row, and neither crowds the other out.
        var h = new Harness(new WeeklyReportOptions { MaxItems = 1 }, strategies: TwoStrategies);
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        await SeedCompanyAsync(h, alphaId, Guid.NewGuid(), opportunity: 70, name: "Alpha Corp",
            ticker: "ALP");
        await SeedCompanyAsync(h, bravoId, Guid.NewGuid(), opportunity: 60, name: "Bravo Corp",
            ticker: "BRV");

        // filings-led ranks them the other way round, so an independent cap is observable.
        await SeedStrategySnapshotAsync(h, "filings-led", alphaId, Guid.NewGuid(), opportunity: 10);
        await SeedStrategySnapshotAsync(h, "filings-led", bravoId, Guid.NewGuid(), opportunity: 99);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var sections = h.Renderer.LastModel!.Strategies!;

        Assert.All(sections, s => Assert.Single(s.Rows));
        Assert.All(sections, s => Assert.True(s.Truncated));
        Assert.Equal("Alpha Corp", sections[0].Rows[0].CompanyName);
        Assert.Equal("Bravo Corp", sections[1].Rows[0].CompanyName);

        var markdown = result.Report.MarkdownContent;
        Assert.Equal(
            2,
            markdown.Split("· 2 companies scored · 2 with linked evidence · showing top 1").Length - 1);
        Assert.DoesNotContain("Bravo Corp | BRV | 60", StrategySectionsOf(markdown),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrategySectionsCarryNoLabelsNoEvidenceAndNoWhyNoticed()
    {
        var h = new Harness(strategies: TwoStrategies);
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, snapshotId, opportunity: 70, name: "Acme Dynamics",
            ticker: "ACME");
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Multi-year agreement announced.");
        await SeedStrategySnapshotAsync(h, "filings-led", companyId, Guid.NewGuid(), opportunity: 55);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var sectionsOnly = StrategySectionsOf(result.Report.MarkdownContent);

        Assert.NotEqual(string.Empty, sectionsOnly);
        Assert.DoesNotContain("- Label:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("- Evidence:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("- Why noticed:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Investigate", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Ignore", sectionsOnly, StringComparison.Ordinal);
        // The policy is still consulted exactly once per surfaced PRIMARY entry — never per strategy row.
        Assert.Single(h.Policy.Contexts);
    }

    [Fact]
    public async Task PipeInACompanyNameIsEscapedInTheStrategyTable()
    {
        var h = new Harness(strategies: TwoStrategies);
        var companyId = Guid.NewGuid();
        await SeedCompanyOnlyAsync(h, companyId, "Acme | Dynamics", "AC|ME");
        await SeedStrategySnapshotAsync(h, "filings-led", companyId, Guid.NewGuid(), opportunity: 44);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Contains(@"| 1 | Acme \| Dynamics | AC\|ME | 44 |",
            result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiStrategyDiWiringProducesStrategySections()
    {
        // The production wiring, not a hand-built builder: AddSingleton<IWeeklyReportBuilder,
        // WeeklyReportBuilder>() must resolve the strategy factory + score-repository factory itself, or a
        // multi-strategy deployment silently renders a single-strategy report.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Strategies:0:Name"] = "default",
                ["Radar:Strategies:0:ScoringProfile"] = "default",
                ["Radar:Strategies:1:Name"] = "low-media",
                ["Radar:Strategies:1:ScoringProfile"] = "low-media",
                ["Radar:Scoring:Profiles:low-media:MediaReachWeight"] = "0.02",
                ["Radar:PrimaryStrategy"] = "default",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        // BEFORE AddRadarApplicationServices so the config-bound set wins over its TryAdd default.
        services.AddRadarScoringStrategies(configuration);
        services.AddRadarApplicationServices();
        services.AddFilePipelineRunStore(Path.Combine(Path.GetTempPath(), $"radar-runs-{Guid.NewGuid():N}"));
        services.AddFileScoreStore(Path.Combine(Path.GetTempPath(), $"radar-scores-{Guid.NewGuid():N}"));
        services.AddFileSignalStore(Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
        var provider = services.BuildServiceProvider();

        var companies = provider.GetRequiredService<ICompanyRepository>();
        var strategySet = provider.GetRequiredService<ScoringStrategySet>();
        var repositories = provider.GetRequiredService<IScoreRepositoryFactory>();

        var companyId = Guid.NewGuid();
        await companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithName("Acme Dynamics").WithTicker("ACME").Build(),
            default);

        foreach (var strategy in strategySet.Strategies)
        {
            var repository = repositories.ForStrategy(strategy);
            var snapshotId = Guid.NewGuid();
            await repository.AddSnapshotAsync(
                new ScoreSnapshotBuilder()
                    .WithId(snapshotId)
                    .WithCompanyId(companyId)
                    .WithOpportunityScore(70)
                    .WithStrategyName(strategy.Name)
                    .WithCreatedAtUtc(InPeriod)
                    .Build(),
                default);
            await repository.AddEvidenceLinkAsync(
                new ScoreEvidenceLink(
                    Id: Guid.NewGuid(),
                    ScoreSnapshotId: snapshotId,
                    SignalId: Guid.NewGuid(),
                    EvidenceId: Guid.NewGuid(),
                    ContributionReason: "Contributed to the score.",
                    ContributionWeight: 5),
                default);
        }

        var builder = provider.GetRequiredService<IWeeklyReportBuilder>();
        var result = await builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        Assert.Contains("## Strategy: default (radar-formula-v8) — primary", markdown,
            StringComparison.Ordinal);
        Assert.Contains("## Strategy: low-media (radar-formula-v8)", markdown, StringComparison.Ordinal);
        Assert.Contains("1 company scored · 1 with linked evidence", markdown, StringComparison.Ordinal);
        // Each section carries the strategy's OWN fingerprint, resolved from its own engine.
        var lowMediaFingerprint = provider.GetRequiredService<IScoringStrategyFactory>()
            .Runtimes.Single(r => r.Definition.Name == "low-media").Engine.EffectiveConfig.Fingerprint;
        Assert.Contains($"Fingerprint: {lowMediaFingerprint} ·", markdown, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec 176 — the live strategy leaders are a second RENDERING of the spec-150 sections, never a
    // second construction: the builder's only new work is carrying Purpose onto each section.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Counts the only file-store read the builder performs (the primary movement lookup).</summary>
    private sealed class CountingScoreSnapshotFileStore : IScoreSnapshotFileStore
    {
        public int ReadLatestBeforeCallCount { get; private set; }

        public int ReadAllForCompanyCallCount { get; private set; }

        public Task<DurableWriteResult> WriteAsync(
            CompanyScoreSnapshot snapshot,
            IReadOnlyList<ScoreEvidenceLink> links,
            CancellationToken ct) => Task.FromResult(DurableWriteResult.Succeeded("unused"));

        public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct)
        {
            ReadLatestBeforeCallCount++;
            return Task.FromResult<CompanyScoreSnapshot?>(null);
        }

        public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
            Guid companyId, CancellationToken ct)
        {
            ReadAllForCompanyCallCount++;
            return Task.FromResult<IReadOnlyList<CompanyScoreSnapshot>>([]);
        }
    }

    [Fact]
    public async Task StrategySections_CarryTheRuntimeDefinitionsPurpose()
    {
        var h = new Harness(strategies:
        [
            new TestStrategy("default", IsPrimary: true),
            new TestStrategy("baseline-activity-only", IsPrimary: false,
                Purpose: StrategyPurpose.Comparator),
            new TestStrategy("filings-led", IsPrimary: false),
        ]);
        await SeedCompanyAsync(h, Guid.NewGuid(), Guid.NewGuid(), opportunity: 70);

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var sections = h.Renderer.LastModel!.Strategies!;
        Assert.Equal(
            [StrategyPurpose.Research, StrategyPurpose.Comparator, StrategyPurpose.Research],
            sections.Select(s => s.Purpose).ToArray());
        // Grouping metadata only: the spec-150 section list itself keeps its primary-first configured order,
        // NOT a purpose-grouped order — the renderer groups, the builder does not.
        Assert.Equal(
            ["default", "baseline-activity-only", "filings-led"],
            sections.Select(s => s.StrategyName).ToArray());
    }

    [Fact]
    public async Task LiveLeadersSummary_AddsNoRepositoryOrFileStoreRead()
    {
        // The compact summary consumes the ALREADY-BUILT StrategyReportSection rows. With one surfaced
        // primary company and two strategies, the only file-store read is the primary walk's single
        // movement lookup — rendering the live section on top of the same model adds zero reads.
        var scoreFiles = new CountingScoreSnapshotFileStore();
        var h = new Harness(scoreFiles: scoreFiles, strategies: TwoStrategies);
        var companyId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, Guid.NewGuid(), opportunity: 70);
        await SeedStrategySnapshotAsync(h, "filings-led", companyId, Guid.NewGuid(), opportunity: 55);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // The section IS rendered…
        Assert.Contains("## Live strategy leaders", result.Report.MarkdownContent, StringComparison.Ordinal);
        // …and the read counts are exactly the pre-176 ones: one movement lookup for the surfaced primary
        // entry, and no ReadAllForCompanyAsync at all (that path belongs to efficacy, not reporting).
        Assert.Equal(1, scoreFiles.ReadLatestBeforeCallCount);
        Assert.Equal(0, scoreFiles.ReadAllForCompanyCallCount);
    }

    [Fact]
    public async Task LiveLeaders_RenderBeforeHighestOpportunity_FromTheSameSectionRows()
    {
        var h = new Harness(strategies: TwoStrategies);
        var companyId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId, Guid.NewGuid(), opportunity: 70, name: "Acme Dynamics",
            ticker: "ACME");
        await SeedStrategySnapshotAsync(h, "filings-led", companyId, Guid.NewGuid(), opportunity: 55);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
        var markdown = result.Report.MarkdownContent;

        var live = markdown.IndexOf("## Live strategy leaders", StringComparison.Ordinal);
        var highest = markdown.IndexOf("## Highest opportunity", StringComparison.Ordinal);
        Assert.True(live >= 0 && highest >= 0 && live < highest,
            "The live summary must render before the Highest opportunity narrative.");

        // Both strategies' leaders appear, each with its OWN rank-1 row read off its own section.
        Assert.Contains("| default (primary research) | 1 | Acme Dynamics | ACME | 70 |",
            markdown, StringComparison.Ordinal);
        Assert.Contains("| filings-led | 1 | Acme Dynamics | ACME | 55 |",
            markdown, StringComparison.Ordinal);
    }
}
