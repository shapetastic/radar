using System.Text.RegularExpressions;

namespace Radar.Application.Filings;

/// <summary>
/// The shared advice-language guard (CLAUDE.md hard rule / AD-9): Radar must never surface "buy", "sell",
/// "guaranteed", or "safe bet". Extracted from <c>ChatFilingAnalyzer</c> (spec 115, reuse over copy) so every
/// consumer scrubs against ONE regex — a second pasted copy would silently drift when only one got the next
/// fix. Whole-word matching (word boundaries) so legitimate release terms like "share buyback" or "seller"
/// are not false-positives.
/// <para>
/// Moved from <c>Radar.Infrastructure.Filings</c> by spec 179 (public, unchanged regex): the news-risk claim
/// validation lives in <c>Radar.Application</c>, which cannot reference Infrastructure, so the ONE definition
/// now sits on the Application side and every existing Infrastructure/CalibrationAudit call site routes here.
/// </para>
/// </summary>
public static partial class AdviceLanguageGuard
{
    /// <summary>Whether <paramref name="text"/> contains advice language Radar must never surface.</summary>
    public static bool ContainsAdviceLanguage(string text) => AdviceLanguage().IsMatch(text);

    [GeneratedRegex(
        @"\b(?:buy|sell|guaranteed)\b|\bsafe bet\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdviceLanguage();
}
