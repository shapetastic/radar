using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Scoring;
using Radar.Application.Signals;
using Radar.Application.SignalExtraction;
using Radar.Application.Storage;
using Radar.Application.Tests.NewsRisk;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.News;

/// <summary>
/// Spec 191's provenance acceptance criterion, end to end and against the REAL pieces (real join, real
/// admission rules, real extractor, real mapper, real <see cref="ScoringEngine"/> and real
/// <c>radar-formula-v8</c>): <b>a score built from a directional news signal resolves its full chain back
/// to an archived observation</b> — score → evidence link → signal → the signal's provenance envelope →
/// judgment id + cohort key + observation id → the archive record and its article URL/publisher.
/// <para>
/// This is the claim spec 191 §2 makes ("a signal whose provenance cannot be recorded is not emitted
/// directionally"), made TRUE by walking it rather than asserted.
/// </para>
/// </summary>
public sealed class NewsDirectionalProvenanceChainTests
{
    private static readonly Guid CompanyId = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ObservationId = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid JudgmentId = new("22222222-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Observed = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    private const string Headline = "Acme reports record quarterly orders";
    private const string CohortKey = "openai:deepseek|prompt|schema|stage1=x|families=y";

    [Fact]
    public async Task AScoreBuiltFromADirectionalNewsSignal_ResolvesItsWholeChainBackToAnObservation()
    {
        // ---- The stores, populated exactly as a live collection pass leaves them.
        var observation = NewsRiskTestData.Observation(
            CompanyId, Headline, Observed, observationId: ObservationId);
        var archive = new InMemoryNewsObservationArchive();
        await archive.WriteAsync(observation, CancellationToken.None);

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithSourceName("Example Wire")
            .WithSourceUrl("https://example.com/acme")
            .WithTitle(Headline)
            .WithRawText($"{Headline} — Example Wire (2026-08-20T00:00:00Z). Source: https://example.com/acme")
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithQuality(EvidenceQuality.Medium)
            .WithPublishedAtUtc(Observed)
            .WithCollectedAtUtc(Observed)
            .Build();

        var evidenceRepository = new InMemoryEvidenceRepository();
        await evidenceRepository.AddIfNewAsync(evidence, CancellationToken.None);

        var judgmentStore = new ReadOnlyJudgmentStore([Judgment()]);

        // ---- The REAL read source, the REAL extractor, the REAL mapper.
        var readSource = new NewsDirectionalReadSource(
            archive,
            evidenceRepository,
            judgmentStore,
            new NewsDirectionalReadOptions(CohortKey),
            NullLogger<NewsDirectionalReadSource>.Instance);

        // Prepared at the RUN as-of instant, exactly as CollectionPass prepares it before the extract loop.
        await readSource.PrepareAsync(AsOf, CancellationToken.None);

        var extractor = new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights(), readSource);

        var extracted = Assert.Single((await extractor.ExtractAsync(evidence, CancellationToken.None)).Signals);
        Assert.Equal(nameof(SignalDirection.Positive), extracted.Direction);

        var mapped = ExtractedSignalMapper.ToSignal(extracted, evidence, Observed);
        Assert.True(mapped.IsValid, string.Join("; ", mapped.Errors));

        var signal = mapped.Signal! with
        {
            CompanyId = CompanyId,
            ReviewStatus = SignalReviewStatus.Approved,
        };

        // ---- Score it with the real engine + real v8 formula.
        var signalRepository = new InMemorySignalRepository();
        await signalRepository.AddAsync(signal, CancellationToken.None);

        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var engine = new ScoringEngine(
            signalRepository,
            new NoFileHistory(),
            evidenceRepository,
            new InMemoryScoreRepository(),
            new InMemoryCompanyRepository(),
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new StubDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions { Window = TimeSpan.FromDays(30) },
            NullLogger<ScoringEngine>.Instance);

        var result = await engine.ScoreCompanyAsync(CompanyId, AsOf, CancellationToken.None);

        // ---- WALK THE CHAIN. Every hop is a real lookup, never a restated constant.
        var link = Assert.Single(result.Links);
        Assert.Equal(result.Snapshot.Id, link.ScoreSnapshotId);

        var linkedSignal = await signalRepository.GetByIdAsync(link.SignalId, CancellationToken.None);
        Assert.NotNull(linkedSignal);
        Assert.Equal(SignalType.MediaAttention, linkedSignal.Type);
        Assert.Equal(SignalDirection.Positive, linkedSignal.Direction);

        var linkedEvidence = await evidenceRepository.GetByIdAsync(link.EvidenceId, CancellationToken.None);
        Assert.NotNull(linkedEvidence);
        Assert.Equal(evidence.Id, linkedEvidence.Id);

        Assert.True(
            EvidenceMetadata.TryRead(linkedSignal.MetadataJson, out var provenance, out _),
            "A directional news signal must carry a readable provenance envelope.");
        Assert.Equal(
            JudgmentId,
            Guid.Parse(provenance[NewsDirectionalSignalMetadata.JudgmentIdKey]));
        Assert.Equal(CohortKey, provenance[NewsDirectionalSignalMetadata.JudgmentCohortKeyKey]);

        var resolvedObservationId =
            Guid.Parse(provenance[NewsDirectionalSignalMetadata.ObservationIdKey]);
        var archived = Assert.Single(
            await archive.GetAllAsync(CancellationToken.None),
            o => o.ObservationId == resolvedObservationId);

        Assert.Equal(Headline, archived.Headline);
        Assert.Equal(CompanyId, archived.CompanyId);
        Assert.False(string.IsNullOrWhiteSpace(archived.GoogleLandingUrl));
        Assert.False(string.IsNullOrWhiteSpace(archived.Publisher));

        // And the judgment the cohort key + id point at is genuinely in the store.
        var resolvedJudgment = Assert.Single(
            await judgmentStore.GetAllAsync(CancellationToken.None),
            j => j.JudgmentId == JudgmentId && j.CohortKey == CohortKey);
        Assert.Equal(NewsJudgmentStatus.Judged, resolvedJudgment.Status);
        Assert.Equal(NewsJudgmentTrajectory.Improving, resolvedJudgment.BusinessTrajectory);
    }

    private static NewsJudgmentRecord Judgment() => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: JudgmentId,
        RunId: null,
        CompanyId: CompanyId,
        CompanyName: "Acme",
        Ticker: "ACME",
        JudgeName: "deepinfra-deepseek",
        Provider: "openai",
        ModelId: "deepseek-ai/DeepSeek-V4-Flash",
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: "x",
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: CohortKey,
        FamilySetHash: "hash",
        Families: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: NewsTypingCompleteness.Complete,
        FamilyBundle: NewsJudgmentFamilyBundle.Complete,
        CoverageIssues: [],
        Status: NewsJudgmentStatus.Judged,
        BusinessTrajectory: NewsJudgmentTrajectory.Improving,
        ChallengeStrength: null,
        Findings: [],
        Rationale: "Order intake rose against the prior quarter.",
        FindingsTotal: 0,
        FindingsAccepted: 0,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: null,
        FailureDetail: null,
        Limits: new NewsJudgmentLimitsRecord(30, 50),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: Observed);

    private sealed class ReadOnlyJudgmentStore(IReadOnlyList<NewsJudgmentRecord> records) : INewsJudgmentStore
    {
        public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(records);

        public Task<NewsJudgmentRecord?> FindCompletedAsync(
            string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>No accrued on-disk history: the previous/velocity window is empty, which is fine here.</summary>
    private sealed class NoFileHistory : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded(string.Empty));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class StubDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => $"rules={KeywordSignalExtractor.RuleSetVersion};";

        public string CollectionProvenance() => "collectors=newssearch;";

        public IReadOnlyList<string> EnabledCollectors() => ["newssearch"];
    }
}
