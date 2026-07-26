using Radar.Domain.Evidence;

namespace Radar.Application.Evidence;

/// <summary>
/// The single, pure rule that turns a <b>declared</b> quality string into an
/// <see cref="EvidenceQuality"/>. Extracted (spec 142) so the two places that need it cannot drift:
/// <list type="bullet">
/// <item><see cref="Radar.Application.Collectors.CollectedEvidenceMapper"/>, which applies it at
/// COLLECTION time to <c>CollectedEvidence.Metadata["quality"]</c>;</item>
/// <item>the durable raw-evidence hydration path, which applies it to the <c>metadata.quality</c> value
/// that same collection PERSISTED, so a legacy file written before <c>quality</c> became a first-class
/// field recovers the value the evidence actually carried when it was scored live — a recovery, never a
/// fabricated default.</item>
/// </list>
/// Deliberately pure (no logging, no dependencies): the mapper keeps its own Debug logging at the call
/// site, so the rule stays a value-in/value-out function that either caller can apply.
/// </summary>
public static class EvidenceQualityParser
{
    /// <summary>
    /// Maps a declared evidence-quality string to <see cref="EvidenceQuality"/>. Accepts only a defined
    /// enum name (case-insensitive); rejects digit-only input (which
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would otherwise accept as an ordinal).
    /// Missing, blank, or unparseable values map to <see cref="EvidenceQuality.Unknown"/>
    /// (skip-don't-throw). <paramref name="status"/> reports WHY, so a caller can log the two failure
    /// modes distinctly without re-implementing the rule.
    /// </summary>
    public static EvidenceQuality Parse(string? declared, out EvidenceQualityParseStatus status)
    {
        if (string.IsNullOrWhiteSpace(declared) || declared.Trim().All(char.IsDigit))
        {
            status = EvidenceQualityParseStatus.Missing;
            return EvidenceQuality.Unknown;
        }

        if (Enum.TryParse<EvidenceQuality>(declared, ignoreCase: true, out var quality) && Enum.IsDefined(quality))
        {
            status = EvidenceQualityParseStatus.Recognized;
            return quality;
        }

        status = EvidenceQualityParseStatus.Unrecognized;
        return EvidenceQuality.Unknown;
    }

    /// <summary>Convenience overload for callers that do not need to distinguish the failure modes.</summary>
    public static EvidenceQuality Parse(string? declared) => Parse(declared, out _);
}

/// <summary>Why <see cref="EvidenceQualityParser.Parse(string?, out EvidenceQualityParseStatus)"/> returned what it did.</summary>
public enum EvidenceQualityParseStatus
{
    /// <summary>The value named a defined <see cref="EvidenceQuality"/> member.</summary>
    Recognized,

    /// <summary>The value was null, blank, or digit-only ⇒ <see cref="EvidenceQuality.Unknown"/>.</summary>
    Missing,

    /// <summary>The value was present but named no defined member ⇒ <see cref="EvidenceQuality.Unknown"/>.</summary>
    Unrecognized,
}
