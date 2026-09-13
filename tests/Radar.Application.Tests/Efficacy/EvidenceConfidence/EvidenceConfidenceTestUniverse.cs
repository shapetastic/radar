using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.Efficacy.EvidenceConfidence;
using Radar.Application.Scoring;
using Radar.Application.Tests.Efficacy.Attention;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// One scored signal's evidence shape: what the EvidenceConfidence terms read, plus (spec-225 follow-up) the
/// fields the best-confidence producer classification reads. The defaults are the builder's, which classify
/// as <c>Unclassified</c>.
/// </summary>
internal sealed record TestSignalSpec(
    EvidenceSourceType SourceType,
    EvidenceQuality Quality,
    decimal Confidence,
    SignalType Type = SignalType.CustomerWin,
    SignalDirection Direction = SignalDirection.Positive,
    string Reason = "Customer win phrase detected.",
    string? SignalMetadataJson = null,
    string? EvidenceMetadataJson = null,
    string? Title = null,
    int Strength = 6);

/// <summary>
/// An offline universe for the spec-225 reporter: companies, their latest persisted snapshot WITH stored links,
/// and the signals/evidence those links resolve to. Every persisted component is computed THROUGH
/// <see cref="ScoreSignalMath"/> (never by hand), so a test that asserts "recomputed == persisted" is asserting
/// the production path against itself; a test that wants a disagreement overrides the persisted value explicitly.
/// </summary>
internal sealed class EvidenceConfidenceTestUniverse
{
    public static readonly DateTimeOffset Instant = new(2026, 9, 12, 21, 48, 24, TimeSpan.Zero);

    public static readonly TimeSpan Window = TimeSpan.FromDays(60);

    public static readonly TestSignalSpec MediumPressRelease =
        new(EvidenceSourceType.PressRelease, EvidenceQuality.Medium, 0.6m);

    public static readonly TestSignalSpec HighFiling =
        new(EvidenceSourceType.Filing, EvidenceQuality.High, 0.8m);

    public static readonly TestSignalSpec HighNews =
        new(EvidenceSourceType.NewsArticle, EvidenceQuality.High, 0.8m);

    public static readonly TestSignalSpec MediumInsider =
        new(EvidenceSourceType.InsiderTransaction, EvidenceQuality.Medium, 0.6m);

    private readonly Dictionary<Guid, List<ScoreSnapshotWithLinks>> _series = [];

    public ScoringWeights Weights { get; } = new();

    public List<Company> Companies { get; } = [];

    public FakeSignalRepository Signals { get; } = new();

    public FakeEvidenceRepository Evidence { get; } = new();

    public ScoringStrategySet Strategies { get; set; } = ScoringStrategySet.SingleDefault(new ScoringWeights());

    public FakeLinkedSnapshotStore Store()
    {
        var store = new FakeLinkedSnapshotStore();
        foreach (var (companyId, series) in _series)
        {
            store.With(companyId, series.ToArray());
        }

        return store;
    }

    /// <summary>
    /// Adds a company with one persisted snapshot at <paramref name="windowEnd"/> (default: the instant) whose
    /// EvidenceConfidence and Opportunity are computed through the production primitives from
    /// <paramref name="signals"/>, <paramref name="trajectory"/>, <paramref name="attention"/> and the tier.
    /// </summary>
    public Company AddCompany(
        string ticker,
        int trajectory,
        int attention,
        IReadOnlyList<TestSignalSpec> signals,
        FollowingTier tier = FollowingTier.Small,
        DateTimeOffset? windowEnd = null,
        int? persistedEvidenceConfidence = null,
        int? persistedOpportunity = null,
        bool withholdFirstSignal = false,
        bool withholdFirstEvidence = false,
        string? scoringConfigVersion = "radar-scoring-fp-test")
    {
        var company = new CompanyBuilder()
            .WithId(Guid.NewGuid())
            .WithTicker(ticker)
            .WithName(ticker + " Inc")
            .WithFollowingTier(tier)
            .Build();
        Companies.Add(company);
        AddSnapshot(
            company, trajectory, attention, signals, windowEnd ?? Instant, persistedEvidenceConfidence,
            persistedOpportunity, withholdFirstSignal, withholdFirstEvidence, scoringConfigVersion);
        return company;
    }

    /// <summary>A second (or later) persisted snapshot for an existing company — for latest-selection tests.</summary>
    public void AddSnapshot(
        Company company,
        int trajectory,
        int attention,
        IReadOnlyList<TestSignalSpec> signals,
        DateTimeOffset windowEnd,
        int? persistedEvidenceConfidence = null,
        int? persistedOpportunity = null,
        bool withholdFirstSignal = false,
        bool withholdFirstEvidence = false,
        string? scoringConfigVersion = "radar-scoring-fp-test")
    {
        var snapshotId = Guid.NewGuid();
        var pairs = new List<ScoringSignal>();
        var links = new List<ScoreEvidenceLink>();
        for (var i = 0; i < signals.Count; i++)
        {
            var spec = signals[i];
            var evidenceBuilder = new EvidenceBuilder()
                .WithId(Guid.NewGuid())
                .WithSourceType(spec.SourceType)
                .WithQuality(spec.Quality)
                .WithMetadataJson(spec.EvidenceMetadataJson)
                .WithContentHash("hash-" + Guid.NewGuid().ToString("N"));
            if (spec.Title is not null)
            {
                evidenceBuilder.WithTitle(spec.Title);
            }

            var evidence = evidenceBuilder.Build();
            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithCompanyId(company.Id)
                .WithEvidenceId(evidence.Id)
                .WithConfidence(spec.Confidence)
                .WithType(spec.Type)
                .WithDirection(spec.Direction)
                .WithReason(spec.Reason)
                .WithStrength(spec.Strength)
                .WithMetadataJson(spec.SignalMetadataJson)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(windowEnd.AddDays(-1 - i))
                .WithCreatedAtUtc(windowEnd.AddDays(-1 - i))
                .Build();
            pairs.Add(new ScoringSignal(signal, evidence));
            if (!(withholdFirstSignal && i == 0))
            {
                Signals.With(signal);
            }

            if (!(withholdFirstEvidence && i == 0))
            {
                Evidence.With(evidence);
            }

            links.Add(new ScoreEvidenceLink(Guid.NewGuid(), snapshotId, signal.Id, evidence.Id, "test", 0));
        }

        // The formula writes all-zero components for an empty window; anything else goes through the
        // production primitives.
        var empty = pairs.Count == 0;
        var ec = empty ? 0 : ScoreSignalMath.EvidenceConfidenceScore(pairs, Weights);
        var ecPersisted = persistedEvidenceConfidence ?? ec;
        var discount = ScoreSignalMath.NotednessDiscount(Weights, attention, company.FollowingTier);
        var opportunity = persistedOpportunity
            ?? (empty ? 0 : ScoreSignalMath.OpportunityComposition(trajectory, ecPersisted, discount));

        var snapshot = new ScoreSnapshotBuilder()
            .WithId(snapshotId)
            .WithCompanyId(company.Id)
            .WithScoringVersion(ScoreFormulaVersions.V8)
            .WithTrajectoryScore(empty ? 0 : trajectory)
            .WithAttentionScore(empty ? 0 : attention)
            .WithEvidenceConfidenceScore(ecPersisted)
            .WithOpportunityScore(opportunity)
            .WithSignalVelocityScore(empty ? 0 : 50)
            .WithWindow(windowEnd - Window, windowEnd)
            .WithCreatedAtUtc(windowEnd)
            .WithScoringConfigVersion(scoringConfigVersion)
            .WithStrategyName(ScoringStrategySet.DefaultStrategyName)
            .Build();

        if (!_series.TryGetValue(company.Id, out var series))
        {
            series = [];
            _series[company.Id] = series;
        }

        series.Add(new ScoreSnapshotWithLinks(snapshot, links));
    }

    /// <summary>A seeded company the store has never scored.</summary>
    public Company AddCompanyWithoutSnapshot(string ticker)
    {
        var company = new CompanyBuilder().WithId(Guid.NewGuid()).WithTicker(ticker).WithName(ticker + " Inc").Build();
        Companies.Add(company);
        return company;
    }

    public EvidenceConfidenceDistributionReporter Reporter(
        IStrategyScoreSnapshotStoreSelector? selector = null,
        ILogger<EvidenceConfidenceDistributionReporter>? logger = null) =>
        new(
            Strategies,
            selector ?? new FakeStrategyScoreSnapshotStoreSelector().With(ScoringStrategySet.DefaultStrategyName, Store()),
            new FakeCompanyRepository(Companies.ToArray()),
            Signals,
            Evidence,
            logger ?? NullLogger<EvidenceConfidenceDistributionReporter>.Instance);

    public Task<EvidenceConfidenceDistributionReport> BuildAsync(
        IStrategyScoreSnapshotStoreSelector? selector = null,
        ILogger<EvidenceConfidenceDistributionReporter>? logger = null) =>
        Reporter(selector, logger).BuildAsync(CancellationToken.None);
}

/// <summary>Captures every log line so a test can assert the idle/summary line was emitted.</summary>
internal sealed class CapturingReporterLogger : ILogger<EvidenceConfidenceDistributionReporter>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception)));
}

/// <summary>Records what the generator asked to write; optionally reports every file as not persisted.</summary>
internal sealed class RecordingEvidenceConfidenceArtifactStore(bool failWrites = false) : IEvidenceConfidenceArtifactStore
{
    public List<(string Json, string Csv, string Markdown)> Written { get; } = [];

    public Task<EvidenceConfidenceArtifactPaths> WriteAsync(string json, string csv, string markdown, CancellationToken ct)
    {
        Written.Add((json, csv, markdown));
        return Task.FromResult(new EvidenceConfidenceArtifactPaths(
            failWrites ? Radar.Application.Storage.DurableWriteResult.NotPersisted("evidence-confidence.json") : Radar.Application.Storage.DurableWriteResult.Succeeded("evidence-confidence.json"),
            failWrites ? Radar.Application.Storage.DurableWriteResult.NotPersisted("evidence-confidence.csv") : Radar.Application.Storage.DurableWriteResult.Succeeded("evidence-confidence.csv"),
            failWrites ? Radar.Application.Storage.DurableWriteResult.NotPersisted("evidence-confidence.md") : Radar.Application.Storage.DurableWriteResult.Succeeded("evidence-confidence.md")));
    }
}
