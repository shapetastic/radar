namespace Radar.Application.SignalExtraction;

/// <summary>
/// The ONE definition of the two <c>Reason</c> texts <see cref="KeywordSignalExtractor"/> writes. Extracted by
/// the spec-225 follow-up (the strings moved verbatim; every emitted Reason is byte-identical) so a READER that
/// classifies a persisted signal's producer — the EvidenceConfidence measurement's
/// <c>SignalProducerRule</c> — recognises the extractor's output through the extractor's own constants rather
/// than a second copy of its wording, which would drift silently on the next edit.
/// <para>
/// Reason text is provenance/audit text only: no scoring component reads it, it is not a
/// <c>RuleSetVersion</c> input and it is not hashed into any fingerprint.
/// </para>
/// </summary>
public static class KeywordSignalReasons
{
    /// <summary>The prefix of every phrase-rule Reason: <c>Matched phrase '{phrase}'</c>.</summary>
    public const string MatchedPhrasePrefix = "Matched phrase '";

    /// <summary>The exact Reason of the spec-70 Neutral <c>MediaAttention</c> event for news coverage.</summary>
    public const string MediaAttention = "Third-party news coverage (media attention)";

    /// <summary>The phrase-rule Reason for <paramref name="phrase"/>: <c>Matched phrase '{phrase}'</c>.</summary>
    public static string MatchedPhrase(string phrase) => MatchedPhrasePrefix + phrase + "'";
}
