using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 194 §1.4 — accrued spec-191 directional news signals must stop asserting a direction they never
/// earned, without a single byte of persisted history being deleted or rewritten.
/// <para>
/// The failure being closed: spec 191 took a news article's direction at EXTRACTION time from the company's
/// latest admitted judgment, which by the live stage order had been produced from EARLIER articles and had
/// never read the one being extracted. 24 such signals are on disk, 16 of them inside the live 60-day
/// window; the stores are append-only and already-seen evidence is never re-extracted, so a read-side
/// admission transform is the only honest lever left.
/// </para>
/// <para>
/// <b>MUTATION PROOF.</b> Revert
/// <c>LegacyNewsInheritanceNeutralization.Neutralize</c>'s <c>Direction = SignalDirection.Neutral</c> and
/// <see cref="AccruedLegacySignal_IsScoredNeutralAtTheOrdinaryNewsStrength"/>,
/// <see cref="PreviousWindow_IsNeutralizedToo"/> and
/// <see cref="Engine_NeutralizedLegacySignal_ScoresExactlyLikeTheHonestPre191Signal"/> all turn red.
/// Replace the metadata-shape match in <c>Classify</c> with a bare <c>Direction != Neutral</c> test and
/// <see cref="UnrelatedDirectionalMediaSignals_PassThroughUntouched"/> and
/// <see cref="WellFormedJudgmentSignal_IsNotNeutralized"/> turn red — that pair is what stops this transform
/// from swallowing the §1.2 judgment-derived signal it exists to make room for. Drop the
/// <c>MalformedJudgmentSignalEnvelope</c> branch and
/// <see cref="MalformedV1Envelope_FailsClosed_AndIsCountedOnItsOwnAxis"/> turns red.
/// </para>
/// </summary>
public sealed class LegacyNewsInheritanceNeutralizationTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution order (observed instant, then signal id) is stable across
    // machines (AD-3).
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArticleEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ArticleSignalId = new("33333333-3333-3333-3333-333333333333");

    // ---------------------------------------------------------------------------------------------
    // The match, and the substitution.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AccruedLegacySignal_IsScoredNeutralAtTheOrdinaryNewsStrength()
    {
        // The exact accrued shape: a directional MediaAttention signal carrying spec 191's judgment/cohort/
        // observation provenance and NO news-judgment-signal-v1 token.
        var legacy = LegacyNewsSignal(SignalDirection.Negative, strength: 5);
        var input = new[] { legacy };

        var result = LegacyNewsInheritanceNeutralization.Apply(input);

        var admitted = Assert.Single(result.Signals);
        Assert.Equal(SignalDirection.Neutral, admitted.Direction);
        Assert.Equal(4, admitted.Strength);

        // Everything else is carried through untouched, so the admitted signal is still walkable back to
        // the record on disk — the suppression is legible, not an erasure.
        Assert.Equal(legacy.Id, admitted.Id);
        Assert.Equal(legacy.EvidenceId, admitted.EvidenceId);
        Assert.Equal(legacy.Novelty, admitted.Novelty);
        Assert.Equal(legacy.Confidence, admitted.Confidence);
        Assert.Equal(legacy.SupportingExcerpt, admitted.SupportingExcerpt);
        Assert.Equal(legacy.Reason, admitted.Reason);
        Assert.Equal(legacy.ObservedAtUtc, admitted.ObservedAtUtc);
        Assert.Equal(legacy.CreatedAtUtc, admitted.CreatedAtUtc);
        Assert.Equal(legacy.MetadataJson, admitted.MetadataJson);

        // Counted on the legacy axis, keyed by the signal's OWN id (it survives, unlike a superseded one).
        Assert.Equal(
            LegacyNewsInheritanceKind.AccruedLegacyInheritance, result.NeutralizedKinds[legacy.Id]);
        Assert.Equal(1, result.LegacyInheritanceCount);
        Assert.Equal(0, result.MalformedEnvelopeCount);
        Assert.Equal(1, result.TotalNeutralized);

        // READ-SIDE ONLY: the transform must not mutate the record it was handed. `Signal` is a record and
        // `with` copies, but this is the assertion that keeps it that way.
        Assert.Equal(SignalDirection.Negative, legacy.Direction);
        Assert.Equal(5, legacy.Strength);
    }

    [Fact]
    public void MalformedV1Envelope_FailsClosed_AndIsCountedOnItsOwnAxis()
    {
        // Claims news-judgment-signal-v1 but carries no cohort key: the version's promised provenance is
        // not there, so the direction is unverifiable and fails closed.
        var incomplete = NewsSignal(
            SignalDirection.Positive,
            strength: 7,
            metadataJson: Metadata(new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] =
                    NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = "e6e1d0f4-1111-4a11-9111-aaaaaaaaaaaa",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Improving",
            }));

        var result = LegacyNewsInheritanceNeutralization.Apply(new[] { incomplete });

        var admitted = Assert.Single(result.Signals);
        Assert.Equal(SignalDirection.Neutral, admitted.Direction);
        Assert.Equal(4, admitted.Strength);

        // A DIFFERENT fact from the spec-191 residue — a current writer producing unverifiable provenance —
        // so the two axes are never pooled.
        Assert.Equal(
            LegacyNewsInheritanceKind.MalformedJudgmentSignalEnvelope,
            result.NeutralizedKinds[incomplete.Id]);
        Assert.Equal(0, result.LegacyInheritanceCount);
        Assert.Equal(1, result.MalformedEnvelopeCount);
    }

    [Fact]
    public void UnreadableEnvelope_OnADirectionalNewsSignal_AlsoFailsClosed()
    {
        // The version token that would say whose envelope this is lives inside bytes that cannot be parsed,
        // so "unrelated family" is unprovable while "a direction whose grounding cannot be read" is certain.
        var corrupt = NewsSignal(SignalDirection.Negative, strength: 6, metadataJson: "{\"metadata\": ");

        var result = LegacyNewsInheritanceNeutralization.Apply(new[] { corrupt });

        Assert.Equal(SignalDirection.Neutral, Assert.Single(result.Signals).Direction);
        Assert.Equal(1, result.MalformedEnvelopeCount);
        Assert.Equal(0, result.LegacyInheritanceCount);
    }

    [Fact]
    public void WellFormedJudgmentSignal_IsNotNeutralized()
    {
        // The spec-194 §1.2 shape, composed through the SAME shared EvidenceMetadata envelope the
        // materializer will use. Its direction was grounded in the evidence the judgment actually cited, so
        // this transform must leave it completely alone — that is the whole point of the correction.
        var materialized = NewsSignal(
            SignalDirection.Negative,
            strength: 7,
            metadataJson: Metadata(new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] =
                    NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = "7f0d2c11-2222-4b22-9222-bbbbbbbbbbbb",
                [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "deepseek|p2|s2|stage1|families",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
            }));

        var input = new[] { materialized };
        var result = LegacyNewsInheritanceNeutralization.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Empty(result.NeutralizedKinds);
        Assert.Equal(SignalDirection.Negative, materialized.Direction);
        Assert.Equal(7, materialized.Strength);
    }

    [Fact]
    public void UnrelatedDirectionalMediaSignals_PassThroughUntouched()
    {
        // None of these is the legacy shape, and every one of them is directional — so a transform that
        // tested `Direction != Neutral` alone would wrongly swallow all three.
        var noMetadata = NewsSignal(SignalDirection.Positive, strength: 6, metadataJson: null);
        var unrelatedMetadata = NewsSignal(
            SignalDirection.Negative,
            strength: 6,
            metadataJson: Metadata(new Dictionary<string, string> { ["someOtherProducer"] = "value" }));

        // A directional NON-media signal carrying the very legacy keys: the type gate must exclude it.
        var filing = new SignalBuilder()
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Positive)
            .WithStrength(8)
            .WithMetadataJson(LegacyMetadata())
            .Build();

        var input = new[] { noMetadata, unrelatedMetadata, filing };
        var result = LegacyNewsInheritanceNeutralization.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Empty(result.NeutralizedKinds);
    }

    [Fact]
    public void TheUntouchedFastPath_ReturnsTheInputInstance_AndCountsNothing()
    {
        var input = new[]
        {
            NewsSignal(SignalDirection.Neutral, strength: 4, metadataJson: null),
            new SignalBuilder().WithType(SignalType.CustomerWin).Build(),
        };

        var result = LegacyNewsInheritanceNeutralization.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Empty(result.NeutralizedKinds);
        Assert.Equal(0, result.TotalNeutralized);

        // The empty map is SHARED across every fast-path result, so it must also be immutable: a consumer
        // casting the interface back to a mutable dictionary would otherwise poison every other result.
        Assert.IsNotType<Dictionary<Guid, LegacyNewsInheritanceKind>>(result.NeutralizedKinds);
    }

    [Fact]
    public void OrderIsPreserved_AndOnlyMatchedSignalsAreRewritten()
    {
        var first = new SignalBuilder().WithType(SignalType.CustomerWin).Build();
        var legacy = LegacyNewsSignal(SignalDirection.Positive, strength: 6);
        var last = NewsSignal(SignalDirection.Neutral, strength: 4, metadataJson: null);

        var result = LegacyNewsInheritanceNeutralization.Apply(new[] { first, legacy, last });

        Assert.Equal([first.Id, legacy.Id, last.Id], result.Signals.Select(s => s.Id).ToArray());
        Assert.Same(first, result.Signals[0]);
        Assert.Same(last, result.Signals[2]);
        Assert.Equal(SignalDirection.Neutral, result.Signals[1].Direction);
        Assert.Equal(1, result.TotalNeutralized);
    }

    [Fact]
    public void CurrentWindowPairs_AreRewrittenInPlaceOfTheirSignal()
    {
        // The ScoringSignal overload must swap the SIGNAL and keep the paired evidence instance.
        var legacy = LegacyNewsSignal(SignalDirection.Negative, strength: 5);
        var evidence = new EvidenceBuilder().WithId(legacy.EvidenceId).Build();
        var pair = new ScoringSignal(legacy, evidence);

        var result = LegacyNewsInheritanceNeutralization.Apply(new[] { pair });

        var admitted = Assert.Single(result.Signals);
        Assert.Equal(SignalDirection.Neutral, admitted.Signal.Direction);
        Assert.Equal(4, admitted.Signal.Strength);
        Assert.Same(evidence, admitted.Evidence);
        Assert.Equal(1, result.LegacyInheritanceCount);
    }

    [Fact]
    public void PreviousWindow_IsNeutralizedToo()
    {
        // The previous/velocity window is activity-only and builds no evidence links (AD-6), so the ONLY
        // place its suppression can be observed is this result — which is exactly why the engine applies the
        // transform there as well. An inherited direction must not be allowed to misdirect velocity either.
        var legacy = LegacyNewsSignal(SignalDirection.Negative, strength: 5);

        var result = LegacyNewsInheritanceNeutralization.Apply(new List<Signal> { legacy });

        Assert.Equal(SignalDirection.Neutral, Assert.Single(result.Signals).Direction);
        Assert.Equal(4, Assert.Single(result.Signals).Strength);
        Assert.Equal(1, result.LegacyInheritanceCount);
    }

    [Fact]
    public void TheRuleIsVersioned_SoSpec194Part2CanFoldItIntoTheScoringIdentity()
    {
        // Declared public in this pass precisely so §2 can hash it. It is hashed into NOTHING yet, and this
        // pass moves no pin.
        Assert.Equal("legacy-news-inheritance-v1", LegacyNewsInheritanceNeutralization.Version);
    }

    // ---------------------------------------------------------------------------------------------
    // The engine-level regression: the neutralized score IS the honest pre-191 score.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Engine_NeutralizedLegacySignal_ScoresExactlyLikeTheHonestPre191Signal()
    {
        // Both fixtures are CONSTRUCTED records — no live data is read, so this cannot go green by copying
        // a mutable store.
        var (legacyResult, legacyLogger) = await ScoreAsync(legacyInheritedDirection: true);
        var (honestResult, honestLogger) = await ScoreAsync(legacyInheritedDirection: false);

        // The whole point: an accrued inherited direction now scores exactly as the Neutral attention event
        // the pre-191 extractor would have written for that article.
        Assert.Equal(honestResult.Snapshot.TrajectoryScore, legacyResult.Snapshot.TrajectoryScore);
        Assert.Equal(honestResult.Snapshot.OpportunityScore, legacyResult.Snapshot.OpportunityScore);
        Assert.Equal(honestResult.Snapshot.AttentionScore, legacyResult.Snapshot.AttentionScore);
        Assert.Equal(
            honestResult.Snapshot.EvidenceConfidenceScore, legacyResult.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(
            honestResult.Snapshot.SignalVelocityScore, legacyResult.Snapshot.SignalVelocityScore);
        Assert.Equal(honestResult.Snapshot.Explanation, legacyResult.Snapshot.Explanation);
        Assert.Equal(honestResult.Snapshot.ComponentJson, legacyResult.Snapshot.ComponentJson);

        // Provenance is preserved: the signal still contributes its own link over its own evidence, at the
        // weight the Neutral direction earns.
        var legacyLink = Assert.Single(legacyResult.Links);
        var honestLink = Assert.Single(honestResult.Links);
        Assert.Equal(ArticleSignalId, legacyLink.SignalId);
        Assert.Equal(ArticleEvidenceId, legacyLink.EvidenceId);
        Assert.Equal(honestLink.ContributionWeight, legacyLink.ContributionWeight);

        // …and the score never silently disagrees with the record on disk: the suppression is named.
        Assert.Equal(
            "MediaAttention (Neutral), strength 4, confidence 0.50 (scored Neutral: accrued spec-191 news "
                + "direction was inherited from a judgment that never read this article "
                + "(legacy-news-inheritance-v1))",
            legacyLink.ContributionReason);
        Assert.Equal(
            "MediaAttention (Neutral), strength 4, confidence 0.50", honestLink.ContributionReason);

        // One aggregated per-company Warning naming both axes and both windows (spec-145 precedent).
        var line = Assert.Single(
            legacyLogger.Entries, e => e.Message.StartsWith("Neutralized ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Neutralized 1 accrued spec-191 inherited news direction(s)", line.Message);
        Assert.Contains("0 unverifiable judgment-signal envelope(s)", line.Message);

        // A run with nothing to suppress logs nothing new at all.
        Assert.DoesNotContain(
            honestLogger.Entries, e => e.Message.StartsWith("Neutralized ", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The fixture: one company whose ONLY in-window signal is the article attention event — either the
    /// accrued spec-191 directional record, or the honest Neutral one the pre-191 extractor wrote.
    /// </summary>
    private static async Task<(CompanyScoreResult Result, CapturingLogger Logger)> ScoreAsync(
        bool legacyInheritedDirection)
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var logger = new CapturingLogger();

        var engine = new ScoringEngine(
            signals,
            new NullSignalFileStore(),
            evidence,
            new InMemoryScoreRepository(),
            companies,
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions(),
            logger);

        await companies.AddAsync(new CompanyBuilder().WithId(CompanyId).Build(), CancellationToken.None);

        var article = new EvidenceBuilder()
            .WithId(ArticleEvidenceId)
            .WithContentHash(ArticleEvidenceId.ToString("N"))
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithSourceName("Example Wire")
            .WithQuality(EvidenceQuality.Medium)
            .WithPublishedAtUtc(WindowEnd.AddDays(-3))
            .WithCollectedAtUtc(WindowEnd.AddDays(-3))
            .Build();
        await evidence.AddIfNewAsync(article, CancellationToken.None);

        var builder = new SignalBuilder()
            .WithId(ArticleSignalId)
            .WithEvidenceId(ArticleEvidenceId)
            .WithCompanyId(CompanyId)
            .WithType(SignalType.MediaAttention)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-3))
            .WithCreatedAtUtc(WindowEnd.AddDays(-3));

        builder = legacyInheritedDirection
            // The accrued record: a Negative direction inherited from a judgment that never read this
            // article, carrying spec 191's provenance envelope.
            ? builder
                .WithDirection(SignalDirection.Negative)
                .WithStrength(6)
                .WithMetadataJson(LegacyMetadata())
            // The honest control: exactly what the pre-191 (and post-§1.1) extractor writes for an article.
            : builder
                .WithDirection(SignalDirection.Neutral)
                .WithStrength(4)
                .WithMetadataJson(null);

        await signals.AddAsync(builder.Build(), CancellationToken.None);

        var result = await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
        return (result, logger);
    }

    private static Signal LegacyNewsSignal(SignalDirection direction, int strength) =>
        NewsSignal(direction, strength, LegacyMetadata());

    private static Signal NewsSignal(SignalDirection direction, int strength, string? metadataJson) =>
        new SignalBuilder()
            .WithType(SignalType.MediaAttention)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithObservedAtUtc(WindowEnd.AddDays(-3))
            .WithCreatedAtUtc(WindowEnd.AddDays(-3))
            .WithMetadataJson(metadataJson)
            .Build();

    /// <summary>The exact spec-191 envelope: judgment id + cohort key + matched observation, and no v1 token.</summary>
    private static string LegacyMetadata() => Metadata(new Dictionary<string, string>
    {
        [NewsDirectionalSignalMetadata.JudgmentIdKey] = "9c8f7e6d-3333-4c33-9333-cccccccccccc",
        [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "deepseek|p2|s2|stage1|families",
        [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
        [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
    });

    /// <summary>Composed through the SHARED envelope writer — never a hand-rolled second JSON shape.</summary>
    private static string Metadata(IReadOnlyDictionary<string, string> metadata) =>
        EvidenceMetadata.Compose(metadata, []);

    private sealed class CapturingLogger : ILogger<ScoringEngine>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>The previous/velocity window is deliberately empty: this fixture pins the CURRENT window.</summary>
    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=newssearch;";

        public IReadOnlyList<string> EnabledCollectors() => ["newssearch"];
    }
}
