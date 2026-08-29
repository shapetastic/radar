namespace Radar.Application.SignalExtraction;

/// <summary>
/// The magnitudes a judgment-derived news signal carries (spec 194 §1.2), declared HERE — beside
/// <see cref="NewsDirectionalSignalMetadata"/>, the envelope that signal is written with — rather than in
/// <c>Radar.Application.News</c> (spec 201 §3).
/// <para>
/// <b>Why they moved.</b> <c>Radar.Application.Scoring</c> must not reference <c>Radar.Application.News</c>
/// or <c>Radar.Application.NewsRisk</c> — that ban is now enforced at SOURCE level, not only on the type
/// graph — yet <c>LegacyNewsInheritanceNeutralization</c> legitimately needs the Neutral media-attention
/// strength it substitutes, and reading it as a <c>const</c> from <c>News</c> was a reference the type-graph
/// guard could not see. <c>Radar.Application.SignalExtraction</c> is a namespace Scoring already references
/// legitimately, so the constants live here and <c>Radar.Application.News.NewsTrajectorySignalRules</c> (the
/// trajectory→direction MAPPING, which needs the NewsRisk trajectory enum and therefore cannot move) reads
/// them from here. The <c>news=…;</c> scoring-identity segment (spec 194 §2) encodes these BY VALUE, so the
/// relocation moves no fingerprint — asserted by test.
/// </para>
/// <para>
/// Provenance of the values: the Novelty/Confidence pair was verified against <c>KeywordSignalExtractor</c>'s
/// spec-191 news branch, which set <c>Novelty: 4, Confidence: 0.5m</c> on BOTH its Neutral and its
/// directional signal, and the base strength IS the Neutral <c>MediaAttention</c> strength the extractor has
/// always emitted. This deliberately does NOT edit the extractor to read these consts: spec 194 §1.1
/// restored that branch to a byte-identical pre-191 form and proving it stays that way is worth more than
/// sharing a literal. If the ordinary news branch's magnitudes ever move, move these with them.
/// </para>
/// </summary>
internal static class NewsTrajectorySignalConstants
{
    /// <summary>The Neutral <c>MediaAttention</c> strength the extractor has always emitted — the floor a directional read builds on.</summary>
    internal const int BaseStrength = 4;

    /// <summary>The maximum number of judge findings that contribute to strength.</summary>
    internal const int MaxFindingContribution = 3;

    /// <summary>The bonus for a judgment whose stage-1 typing was COMPLETE (nothing deferred, nothing failed).</summary>
    internal const int CompleteTypingBonus = 1;

    /// <summary>The Novelty a news attention signal carries (see the class remarks for provenance).</summary>
    internal const int Novelty = 4;

    /// <summary>The Confidence a news attention signal carries (see the class remarks for provenance).</summary>
    internal const decimal Confidence = 0.5m;
}
