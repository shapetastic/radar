using System.Text.RegularExpressions;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The shared advice-language guard (CLAUDE.md hard rule / AD-9): Radar must never surface "buy", "sell",
/// "guaranteed", or "safe bet". Extracted from <see cref="ChatFilingAnalyzer"/> (spec 115, reuse over copy) so
/// the analyzer's rationale validation and the filing-read debug store scrub against ONE regex — a second
/// pasted copy would silently drift when only one got the next fix. Whole-word matching (word boundaries) so
/// legitimate release terms like "share buyback" or "seller" are not false-positives.
/// </summary>
internal static partial class AdviceLanguageGuard
{
    /// <summary>Whether <paramref name="text"/> contains advice language Radar must never surface.</summary>
    public static bool ContainsAdviceLanguage(string text) => AdviceLanguage().IsMatch(text);

    [GeneratedRegex(
        @"\b(?:buy|sell|guaranteed)\b|\bsafe bet\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdviceLanguage();
}
