using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.SignalExtraction;

/// <summary>
/// Spec 191 §2 — the <see cref="EvidenceSourceType.NewsArticle"/> branch takes its DIRECTION from an
/// admitted news read, and falls back to EXACTLY today's Neutral signal for anything Radar has not read.
/// The fallback's byte-identity is the compatibility proof; the provenance triple is mandatory.
/// </summary>
public sealed class KeywordSignalExtractorNewsDirectionTests
{
    private const string NeutralReason = "Third-party news coverage (media attention)";

    private static readonly DateTimeOffset CollectedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid ObservationId = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid JudgmentId = new("22222222-0000-0000-0000-000000000002");
    private const string CohortKey = "openai:deepseek|prompt|schema|stage1=x|families=y";

    private static EvidenceItem NewsEvidence(string title = "Acme reports record quarterly orders") =>
        new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithTitle(title)
            .WithSourceName("Example Wire")
            .WithRawText($"{title} — example.wire (2026-08-20T00:00:00Z). Source: https://example.com/a")
            .WithCollectedAtUtc(CollectedAt)
            .Build();

    private static Task<ExtractSignalsOutput> ExtractAsync(
        EvidenceItem evidence, INewsDirectionalReadSource? reads) =>
        new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights(), reads)
            .ExtractAsync(evidence, CancellationToken.None);

    private static NewsDirectionalRead Read(
        SignalDirection direction = SignalDirection.Positive,
        int strength = 6,
        string token = "improving") =>
        new(direction, strength, ObservationId, JudgmentId, CohortKey, token);

    [Fact]
    public async Task NoRegisteredSource_ProducesExactlyTheHistoricalNeutralSignal()
    {
        var evidence = NewsEvidence();

        var withoutSource = Assert.Single((await ExtractAsync(evidence, reads: null)).Signals);

        Assert.Equal(SignalType.MediaAttention.ToString(), withoutSource.SignalType);
        Assert.Equal(nameof(SignalDirection.Neutral), withoutSource.Direction);
        Assert.Equal(4, withoutSource.Strength);
        Assert.Equal(4, withoutSource.Novelty);
        Assert.Equal(0.5m, withoutSource.Confidence);
        Assert.Equal(NeutralReason, withoutSource.Reason);
        Assert.Equal(evidence.SourceName, withoutSource.CompanyMention);
        Assert.Null(withoutSource.MetadataJson);
    }

    [Fact]
    public async Task ASourceThatAdmitsNothing_IsByteIdenticalToNoSourceAtAll()
    {
        // "Unjoined / no admitted judgment ⇒ exactly today's behaviour, unchanged" (spec 191 §2). Asserted
        // as record EQUALITY over the whole ExtractedSignal, not field by field, so a future added field
        // cannot silently escape the comparison.
        var evidence = NewsEvidence();

        var withoutSource = await ExtractAsync(evidence, reads: null);
        var withSilentSource = await ExtractAsync(evidence, new StubReadSource(null));

        Assert.Equal(withoutSource.Signals, withSilentSource.Signals);
        Assert.Equal(withoutSource.OverallSummary, withSilentSource.OverallSummary);
    }

    [Theory]
    [InlineData(SignalDirection.Positive, "improving")]
    [InlineData(SignalDirection.Negative, "deteriorating")]
    public async Task AnAdmittedRead_SetsDirectionAndStrength_AndKeepsEverythingElse(
        SignalDirection direction, string token)
    {
        var evidence = NewsEvidence();
        var neutral = Assert.Single((await ExtractAsync(evidence, reads: null)).Signals);

        var output = await ExtractAsync(evidence, new StubReadSource(Read(direction, 7, token)));
        var signal = Assert.Single(output.Signals);

        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal(direction.ToString(), signal.Direction);
        Assert.Equal(7, signal.Strength);
        // Everything the spec says the read does NOT scale:
        Assert.Equal(neutral.Novelty, signal.Novelty);
        Assert.Equal(neutral.Confidence, signal.Confidence);
        Assert.Equal(neutral.CompanyMention, signal.CompanyMention);
        Assert.Equal(neutral.SupportingExcerpt, signal.SupportingExcerpt);
        Assert.Equal("1 media-attention signal extracted from news coverage.", output.OverallSummary);
    }

    [Fact]
    public async Task ADirectionalSignalsReason_NamesTheTrajectory_AndCarriesNoAdviceLanguage()
    {
        var signal = Assert.Single(
            (await ExtractAsync(NewsEvidence(), new StubReadSource(Read(token: "deteriorating")))).Signals);

        Assert.Equal(
            "Third-party news coverage (media attention; judged business trajectory: deteriorating)",
            signal.Reason);
        foreach (var banned in new[] { "buy", "sell", "guaranteed upside", "safe bet" })
        {
            Assert.DoesNotContain(banned, signal.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task NoDirectionalNewsSignalExistsWithoutAllThreeProvenanceFields()
    {
        var signal = Assert.Single((await ExtractAsync(NewsEvidence(), new StubReadSource(Read()))).Signals);

        Assert.NotEqual(nameof(SignalDirection.Neutral), signal.Direction);
        Assert.NotNull(signal.MetadataJson);
        Assert.True(EvidenceMetadata.TryRead(signal.MetadataJson, out var metadata, out _));
        Assert.Equal(
            JudgmentId.ToString("D"), metadata[NewsDirectionalSignalMetadata.JudgmentIdKey]);
        Assert.Equal(CohortKey, metadata[NewsDirectionalSignalMetadata.JudgmentCohortKeyKey]);
        Assert.Equal(
            ObservationId.ToString("D"), metadata[NewsDirectionalSignalMetadata.ObservationIdKey]);
        Assert.Equal("improving", metadata[NewsDirectionalSignalMetadata.TrajectoryKey]);
    }

    [Fact]
    public async Task ADirectionalSignal_RoundTripsThroughTheMapper_PreservingItsProvenance()
    {
        var evidence = NewsEvidence();
        var extracted = Assert.Single(
            (await ExtractAsync(evidence, new StubReadSource(Read(SignalDirection.Negative, 8, "deteriorating")))).Signals);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(SignalDirection.Negative, result.Signal!.Direction);
        Assert.Equal(8, result.Signal.Strength);
        Assert.Equal(extracted.MetadataJson, result.Signal.MetadataJson);
    }

    [Fact]
    public async Task ANeutralSignal_MapsToASignalCarryingNoMetadata()
    {
        var evidence = NewsEvidence();
        var extracted = Assert.Single((await ExtractAsync(evidence, reads: null)).Signals);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Null(result.Signal!.MetadataJson);
    }

    [Fact]
    public async Task TheSourceIsNeverConsultedForNonNewsEvidence()
    {
        // The read seam is inside the ONE EvidenceSourceType branch; a filing must not pay for it.
        var source = new StubReadSource(Read());
        var filing = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.Filing)
            .WithTitle("Acme wins contract with the US Navy")
            .WithRawText("Acme wins contract with the US Navy.")
            .WithCollectedAtUtc(CollectedAt)
            .Build();

        await ExtractAsync(filing, source);

        Assert.Equal(0, source.Calls);
    }

    private sealed class StubReadSource(NewsDirectionalRead? read) : INewsDirectionalReadSource
    {
        public int Calls { get; private set; }

        public int Prepares { get; private set; }

        public Task PrepareAsync(DateTimeOffset asOfUtc, CancellationToken ct)
        {
            Prepares++;
            return Task.CompletedTask;
        }

        public Task<NewsDirectionalRead?> TryReadAsync(EvidenceItem evidence, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(read);
        }
    }
}
