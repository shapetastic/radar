using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>
/// Which code path PRODUCED a persisted signal, as far as its own persisted fields can say. A closed set; every
/// signal maps to exactly one member, and <see cref="Unclassified"/> is a counted answer, never a guess.
/// </summary>
public enum SignalProducer
{
    /// <summary>No rule below matched (e.g. an accrued signal from a retired producer). Counted, never inferred further.</summary>
    Unclassified = 0,

    /// <summary>
    /// The AI directional earnings read (<c>DirectionalFilingSignalSource</c>, the confident Improving /
    /// Deteriorating path). <b>INFERRED</b>: that path writes no producer stamp, so it is recognised by
    /// elimination — a <c>GuidanceChange</c> over <c>Filing</c> evidence whose Reason is not a keyword-rule
    /// Reason and which carries neither the read-outcome envelope nor a news-judgment envelope. Its
    /// <c>Confidence</c> is the model's self-reported value, lowered to the comparability cap when the release
    /// declared a comparability break.
    /// </summary>
    AiEarningsReadDirectional,

    /// <summary>
    /// The AI earnings read's NON-directional signal (Mixed / Unknown / below-confidence, spec 204). RECORDED:
    /// recognised by the <c>filingReadOutcome</c> envelope key. Its <c>Confidence</c> is the keyword fallback's
    /// constant, not the model's.
    /// </summary>
    AiEarningsReadNonDirectional,

    /// <summary>A judgment-derived news signal (spec 194). RECORDED: a well-formed news-judgment envelope.</summary>
    NewsJudgment,

    /// <summary>A news-judgment envelope that is the retired spec-191 shape, or malformed. RECORDED, kept apart.</summary>
    NewsJudgmentLegacyOrMalformed,

    /// <summary>The deterministic keyword extractor's phrase rules. RECORDED: the extractor's own Reason prefix.</summary>
    KeywordPhraseRule,

    /// <summary>The deterministic keyword extractor's Neutral news MediaAttention event. RECORDED: its exact Reason.</summary>
    KeywordMediaAttention,
}

/// <summary>
/// <c>signal-producer-v1</c>: classifies a persisted signal's producer from its OWN fields plus its evidence's
/// source type, through the producers' own constants and predicates (reuse, not a copy of their wording).
/// Pure and total (AD-3). Rule order — the first match wins:
/// <list type="number">
/// <item>the read-outcome envelope (<see cref="FilingReadSignalMetadata.CarriesReadOutcome(string?)"/>) ⇒
/// <see cref="SignalProducer.AiEarningsReadNonDirectional"/>;</item>
/// <item>a news-judgment envelope (<see cref="NewsDirectionalSignalMetadata.ClassifyProvenance"/>):
/// judgment-derived ⇒ <see cref="SignalProducer.NewsJudgment"/>, legacy or malformed ⇒
/// <see cref="SignalProducer.NewsJudgmentLegacyOrMalformed"/>;</item>
/// <item>Reason starts with <see cref="KeywordSignalReasons.MatchedPhrasePrefix"/> ⇒
/// <see cref="SignalProducer.KeywordPhraseRule"/>; Reason equals <see cref="KeywordSignalReasons.MediaAttention"/> ⇒
/// <see cref="SignalProducer.KeywordMediaAttention"/>;</item>
/// <item><c>GuidanceChange</c> over <c>Filing</c> evidence ⇒ <see cref="SignalProducer.AiEarningsReadDirectional"/>
/// (the ONE inferred member — see its remarks);</item>
/// <item>otherwise <see cref="SignalProducer.Unclassified"/>.</item>
/// </list>
/// </summary>
public static class SignalProducerRule
{
    public const string Version = "signal-producer-v1";

    public static SignalProducer Classify(Signal signal, EvidenceSourceType evidenceSourceType)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (FilingReadSignalMetadata.CarriesReadOutcome(signal.MetadataJson))
        {
            return SignalProducer.AiEarningsReadNonDirectional;
        }

        switch (NewsDirectionalSignalMetadata.ClassifyProvenance(signal.MetadataJson))
        {
            case NewsJudgmentSignalProvenance.JudgmentDerived:
                return SignalProducer.NewsJudgment;
            case NewsJudgmentSignalProvenance.None:
                break;
            default:
                return SignalProducer.NewsJudgmentLegacyOrMalformed;
        }

        var reason = signal.Reason ?? string.Empty;
        if (reason.StartsWith(KeywordSignalReasons.MatchedPhrasePrefix, StringComparison.Ordinal))
        {
            return SignalProducer.KeywordPhraseRule;
        }

        if (string.Equals(reason, KeywordSignalReasons.MediaAttention, StringComparison.Ordinal))
        {
            return SignalProducer.KeywordMediaAttention;
        }

        if (signal.Type == SignalType.GuidanceChange && evidenceSourceType == EvidenceSourceType.Filing)
        {
            return SignalProducer.AiEarningsReadDirectional;
        }

        return SignalProducer.Unclassified;
    }

    /// <summary>
    /// True when the signal's Reason carries the spec-160 comparability-cap annotation — i.e. the cap LOWERED
    /// the persisted confidence. Meaningful for <see cref="SignalProducer.AiEarningsReadDirectional"/> only; a
    /// read at or below the cap is never annotated, so <c>false</c> there means "the cap did not move it".
    /// </summary>
    public static bool ComparabilityCapNoted(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return (signal.Reason ?? string.Empty).Contains(
            FilingReadSignalMetadata.ComparabilityCapReasonMarker, StringComparison.Ordinal);
    }

    /// <summary>A short human label for rendering; the enum NAME remains the machine contract.</summary>
    public static string Describe(SignalProducer producer) => producer switch
    {
        SignalProducer.AiEarningsReadDirectional => "the AI earnings read (directional; producer inferred)",
        SignalProducer.AiEarningsReadNonDirectional => "the AI earnings read (non-directional)",
        SignalProducer.NewsJudgment => "a news judgment",
        SignalProducer.NewsJudgmentLegacyOrMalformed => "a legacy/malformed news-judgment envelope",
        SignalProducer.KeywordPhraseRule => "a keyword phrase rule",
        SignalProducer.KeywordMediaAttention => "the keyword news MediaAttention event",
        _ => "an unclassified producer",
    };
}
