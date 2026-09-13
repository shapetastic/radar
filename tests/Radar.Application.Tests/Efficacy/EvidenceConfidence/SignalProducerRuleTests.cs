using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.EvidenceConfidence;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// <c>signal-producer-v1</c>, one rule per test. The keyword cases go THROUGH the real extractor, so a change to
/// its Reason wording that bypassed <see cref="KeywordSignalReasons"/> would fail here rather than silently
/// re-classify every keyword signal as Unclassified — or, over filing evidence, as the AI read.
/// </summary>
public sealed class SignalProducerRuleTests
{
    private static readonly DateTimeOffset Collected = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<Signal> ExtractedAsync(EvidenceSourceType sourceType, string title, string rawText)
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(sourceType)
            .WithTitle(title)
            .WithRawText(rawText)
            .WithCollectedAtUtc(Collected)
            .Build();
        var output = await new KeywordSignalExtractor(
                NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
            .ExtractAsync(evidence, CancellationToken.None);
        var extracted = output.Signals[0];
        return new SignalBuilder()
            .WithType(Enum.Parse<SignalType>(extracted.SignalType))
            .WithDirection(Enum.Parse<SignalDirection>(extracted.Direction))
            .WithConfidence(extracted.Confidence)
            .WithReason(extracted.Reason)
            .Build();
    }

    [Fact]
    public async Task TheKeywordFallbackOverAFiling_IsAKeywordRule_NotTheAiRead()
    {
        // The spec-57 Neutral GuidanceChange every earnings 8-K gets — a GuidanceChange over Filing evidence,
        // exactly the shape the AI-read inference would otherwise claim. The keyword Reason wins first.
        var signal = await ExtractedAsync(
            EvidenceSourceType.Filing, "8-K — Report", "Acme announced results of operations for the quarter.");

        Assert.Equal(SignalType.GuidanceChange, signal.Type);
        Assert.StartsWith(KeywordSignalReasons.MatchedPhrasePrefix, signal.Reason, StringComparison.Ordinal);
        Assert.Equal(SignalProducer.KeywordPhraseRule, SignalProducerRule.Classify(signal, EvidenceSourceType.Filing));
    }

    [Fact]
    public async Task TheNewsMediaAttentionEvent_IsTheKeywordMediaAttentionProducer()
    {
        var signal = await ExtractedAsync(EvidenceSourceType.NewsArticle, "Acme in the news", "Acme was mentioned.");

        Assert.Equal(KeywordSignalReasons.MediaAttention, signal.Reason);
        Assert.Equal(
            SignalProducer.KeywordMediaAttention, SignalProducerRule.Classify(signal, EvidenceSourceType.NewsArticle));
    }

    [Fact]
    public void MatchedPhrase_IsTheExtractorsHistoricalWording()
    {
        Assert.Equal("Matched phrase 'partnership'", KeywordSignalReasons.MatchedPhrase("partnership"));
    }

    [Fact]
    public void AGuidanceChangeOverAFiling_WithAModelRationale_IsTheInferredAiDirectionalRead()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Positive)
            .WithReason("Revenue grew 12% year over year and guidance was raised.")
            .Build();

        Assert.Equal(SignalProducer.AiEarningsReadDirectional, SignalProducerRule.Classify(signal, EvidenceSourceType.Filing));
        Assert.False(SignalProducerRule.ComparabilityCapNoted(signal));

        // The same signal over NON-filing evidence is not inferred to be the read.
        Assert.Equal(SignalProducer.Unclassified, SignalProducerRule.Classify(signal, EvidenceSourceType.PressRelease));
    }

    [Fact]
    public void ACappedDirectionalRead_IsNotedThroughTheProducersOwnMarker()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.GuidanceChange)
            .WithReason("Record net income doubled." + FilingReadSignalMetadata.ComparabilityCapReasonMarker
                + "'litigation settlement')")
            .Build();

        Assert.Equal(SignalProducer.AiEarningsReadDirectional, SignalProducerRule.Classify(signal, EvidenceSourceType.Filing));
        Assert.True(SignalProducerRule.ComparabilityCapNoted(signal));
    }

    [Fact]
    public void TheReadOutcomeEnvelope_IsTheNonDirectionalRead_WhateverItsReason()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Mixed)
            .WithReason("AI earnings read: Mixed 0.85 — two-sided quarter")
            .WithMetadataJson(FilingReadSignalMetadata.Compose(FilingNoSignalCause.Mixed, "Mixed", 0.85m, "model"))
            .Build();

        Assert.Equal(
            SignalProducer.AiEarningsReadNonDirectional, SignalProducerRule.Classify(signal, EvidenceSourceType.Filing));
    }

    [Fact]
    public void AJudgmentEnvelope_IsTheNewsJudgmentProducer_EvenWithTheMediaAttentionReasonPrefix()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.MediaAttention)
            .WithDirection(SignalDirection.Negative)
            .WithReason(KeywordSignalReasons.MediaAttention + "; direction from the stage-2 news judgment")
            .WithMetadataJson(NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
                Guid.NewGuid(), "cohort", "deteriorating", [Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]))
            .Build();

        Assert.Equal(SignalProducer.NewsJudgment, SignalProducerRule.Classify(signal, EvidenceSourceType.NewsArticle));
    }

    [Fact]
    public void AnUnreadableEnvelope_FailsClosedOntoTheLegacyOrMalformedAxis()
    {
        var signal = new SignalBuilder().WithMetadataJson("{not json").Build();

        Assert.Equal(
            SignalProducer.NewsJudgmentLegacyOrMalformed, SignalProducerRule.Classify(signal, EvidenceSourceType.NewsArticle));
    }

    [Fact]
    public void AnythingElse_IsUnclassified_NeverGuessed()
    {
        var signal = new SignalBuilder().WithType(SignalType.CustomerWin).WithReason("Customer win phrase detected.").Build();

        Assert.Equal(SignalProducer.Unclassified, SignalProducerRule.Classify(signal, EvidenceSourceType.PressRelease));
        Assert.Contains("unclassified", SignalProducerRule.Describe(SignalProducer.Unclassified), StringComparison.Ordinal);
    }
}
