namespace Radar.Application.NewsTyping;

/// <summary>
/// The ONE versioned normalization of a short news text (a typed fact statement, an article headline).
/// Extracted from <see cref="FactFamilyBuilder"/> by spec 191 §1 ("extract and share it, do not write a
/// second normalizer") so the fact layer's claim key and the news observation ↔ evidence join key are
/// produced by the SAME rule and cannot drift.
/// <para>
/// The rule, byte-for-byte what <c>fact-family-v1</c> shipped and <c>fact-family-v2</c> inherits:
/// lowercase invariant, every non-ASCII-letter/digit character becomes a space, split on spaces with empty
/// entries removed and entries trimmed, collected into an ORDINAL <see cref="HashSet{T}"/>. Negation tokens
/// and numbers survive BY CONSTRUCTION — they are letters/digits — because stripping them would erase
/// exactly the distinctions the fact layer's contradiction rule protects.
/// </para>
/// <para>
/// <b><see cref="Normalize"/> is a TOKEN-SET join, not a whitespace collapse.</b> It joins the hash set, so
/// a repeated token appears exactly ONCE. That is deliberate and load-bearing: it is what
/// <c>FactFamilyBuilder</c> has always produced for its canonical claim key and therefore what every accrued
/// family id was derived from. Do not "fix" it — a different string here re-keys every family and forks the
/// stage-2 judgment cohort.
/// </para>
/// <para>
/// <b>It is NOT order-insensitive</b>, despite being a set: <see cref="HashSet{T}"/> enumerates an add-only
/// set in first-insertion order, so <c>"alpha beta"</c> and <c>"beta alpha"</c> normalize differently
/// (pinned by test). Callers must not rely on order-insensitivity. The spec-191 observation ↔ evidence join
/// is unaffected because in production the observation headline and the evidence title are the SAME source
/// string — the news collector maps one article title into both.
/// </para>
/// <para>
/// <b><see cref="Version"/> is an IDENTITY input</b> (it composes <c>FactFamilyBuilder.IdentityString</c>,
/// which composes the stage-2 cohort key). Changing the rule means declaring a new version, never editing
/// this one in place.
/// </para>
/// </summary>
public static class NewsTextNormalization
{
    /// <summary>The versioned rule identity, folded into the fact-family builder identity.</summary>
    public const string Version = "statement-normalization-v1";

    /// <summary>
    /// The normalized token SET: ordinal and duplicate-free. <b>Enumeration order is first-occurrence
    /// order</b>, not an order-insensitive canonical order — see the class remarks, and do not assume
    /// otherwise.
    /// </summary>
    public static HashSet<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var chars = text.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ');
        return new string([.. chars])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The normalized canonical string — the token set joined by a single space (see the class remarks:
    /// this deduplicates and reorders, by design).
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(' ', Tokens(text));
    }
}
