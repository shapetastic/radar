using System.Globalization;
using System.Text;

using Radar.Application.Filings;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// Deterministic comparability scan of a stripped EX-99.1 earnings-release body (spec 160): a fixed phrase
/// table (like <c>KeywordSignalExtractor</c>'s — deterministic code before AI, never a second model opinion)
/// that detects the release <b>declaring its own comparability breaks</b> ("litigation settlement",
/// "discontinued operations", …). When a cap-triggering phrase matches, <c>DirectionalFilingSignalSource</c>
/// bounds the AI read's persisted confidence by
/// <see cref="DirectionalFilingSignalOptions.ComparabilityConfidenceCap"/> — the model's read is kept; only the
/// weight Radar assigns it is bounded by what the release itself discloses (the CASS 2026-07-29 failure class:
/// a headline GAAP doubling built on a prior-year securities loss + a litigation-settlement recovery read at
/// confidence 0.90).
/// <para>
/// The scan runs on the FULL stripped body, BEFORE the analyzer's <c>MaxInputLength</c> truncation — a marker
/// past the truncation point is still a marker. Matching is case-insensitive, whitespace-normalised, verbatim
/// phrase containment; both result lists are ordered (table order) and distinct.
/// </para>
/// </summary>
public static class EarningsComparabilityScan
{
    /// <summary>
    /// The comparability-scan rule-STRUCTURE identity (parallel to <c>KeywordSignalExtractor.RuleSetVersion</c>),
    /// folded into the scoring fingerprint via the directional-filing descriptor's <c>cmpscan=</c> field and into
    /// every cache record's <see cref="Policy"/> string. OBLIGATION: change EITHER phrase table below (add,
    /// remove, or reword a phrase, or move one between groups) ⇒ bump this version — a policy-mismatched cache
    /// record is deliberately a MISS, so the bump is what makes a table change re-analyze instead of silently
    /// replaying reads scanned under the old rules.
    /// </summary>
    public const string Version = "cmpscan-v1";

    // Cap-triggering (v1) — phrases that specifically declare a comparability break. Perimeter changes first,
    // then one-off items. Deliberately EXCLUDED from both tables: "non-GAAP" / "adjusted" — essentially every
    // earnings release contains reconciliation boilerplate, so those phrases would cap everything and turn the
    // cap into a constant (a constant re-scaling of every AI read is a Strength edit wearing a costume).
    private static readonly string[] CapTriggeringPhrases =
    [
        "discontinued operations",
        "divestiture",
        "divested",
        "impairment",
        "litigation settlement",
        "legal settlement",
        "one-time",
        "one time",
        "non-recurring",
        "nonrecurring",
        "gain on sale",
        "loss on sale",
        "securities loss",
        "securities losses",
        "bad debt recovery",
    ];

    // Diagnostic-only — recorded, NEVER caps. These correlate with perimeter changes but over-match ordinary
    // prose (demoted per the spec-160 review): "continuing operations" is standard GAAP presentation language
    // whenever a discontinued segment exists in ANY comparative period; the sale phrasings match
    // product/stock/asset-sale prose unrelated to a perimeter change. They are persisted in the cache/debug
    // records so their true hit rate and co-occurrence with cap-triggering markers is measurable from live data;
    // promoting one into the cap-triggering set is a cmpscan-v2 decision made on that evidence, not on argument.
    private static readonly string[] DiagnosticOnlyPhrases =
    [
        "continuing operations",
        "sale of its",
        "sale of the",
        "sold its",
    ];

    /// <summary>
    /// Scans <paramref name="body"/> (the full stripped EX-99.1 text) and returns the matched cap-triggering and
    /// diagnostic-only phrases, each ordered by table order and distinct. Empty lists = scanned clean.
    /// </summary>
    public static ComparabilityMarkers Scan(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ComparabilityMarkers([], []);
        }

        var normalized = NormalizeWhitespace(body);
        return new ComparabilityMarkers(
            Match(normalized, CapTriggeringPhrases),
            Match(normalized, DiagnosticOnlyPhrases));
    }

    /// <summary>
    /// The canonical comparability POLICY string a cache record is stamped with:
    /// <c>"{Version};cap={G29 of cap}"</c> (InvariantCulture <c>G29</c> — injective over [0,1], the same
    /// discipline as the descriptor's <c>minconf</c>). One composition site so the cache stamp and the lookup
    /// comparison can never drift.
    /// </summary>
    public static string Policy(decimal comparabilityConfidenceCap) =>
        Version + ";cap=" + comparabilityConfidenceCap.ToString("G29", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Match(string normalizedBody, string[] phrases)
    {
        List<string>? matched = null;
        foreach (var phrase in phrases)
        {
            if (normalizedBody.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                (matched ??= []).Add(phrase);
            }
        }

        return matched ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Collapses every run of whitespace (spaces, newlines, tabs — whatever the HTML strip left) to a single
    /// space so a phrase like "one time" or "litigation settlement" matches across a line break.
    /// </summary>
    private static string NormalizeWhitespace(string body)
    {
        var sb = new StringBuilder(body.Length);
        var pendingSpace = false;
        foreach (var ch in body)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
