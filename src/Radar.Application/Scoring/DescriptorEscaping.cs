namespace Radar.Application.Scoring;

/// <summary>
/// The one shared escaping primitive for the canonical, delimiter-separated <b>scoring-fingerprint
/// descriptors</b> (AD-3/AD-10): the attention tier map, the signal-source descriptor, and the AI
/// directional-filing descriptor all serialize <c>key=value;key=value;</c> (with <c>,</c> separating list
/// items), so any value spliced into one must have those delimiters percent-escaped or two different configs
/// could serialize to the same string and share a fingerprint.
/// <para>
/// Extracted from the previously duplicated private <c>Escape</c> helpers (CLAUDE.md reuse rule) so the
/// escaping cannot drift between call sites. The escape marker <c>%</c> MUST be replaced first, before the
/// delimiters it encodes, or the mapping is not injective.
/// </para>
/// </summary>
public static class DescriptorEscaping
{
    /// <summary>
    /// Percent-escapes the reserved descriptor delimiters (<c>%</c>, <c>=</c>, <c>;</c>, <c>,</c>) so a value
    /// spliced into a descriptor keeps the serialization injective. Null/empty is returned unchanged.
    /// </summary>
    public static string Escape(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : value.Replace("%", "%25", StringComparison.Ordinal)
                .Replace("=", "%3D", StringComparison.Ordinal)
                .Replace(";", "%3B", StringComparison.Ordinal)
                .Replace(",", "%2C", StringComparison.Ordinal);
}
