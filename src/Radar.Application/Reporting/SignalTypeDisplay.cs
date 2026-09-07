namespace Radar.Application.Reporting;

using System.Text.RegularExpressions;
using Radar.Domain.Signals;

/// <summary>
/// THE presentation mapping from a stored <see cref="SignalType"/> to the label the weekly report prints —
/// the single shared owner (spec 211) used by BOTH <see cref="MarkdownWeeklyReportRenderer"/> (the "Why
/// noticed" type site and the stored-provenance seam) and <see cref="WeeklyReportActionPolicyV1"/> (the
/// Watch-floor rationale). Presentation only: the stored enum, persisted JSON, signals, scores and the
/// fingerprint are untouched, and every consumer that COUNTS or GROUPS still does so on the stored enum.
/// <para>
/// Spec 167: display-only relabel of the stored <see cref="SignalType.GuidanceChange"/> member as
/// <c>EarningsTrajectory</c>. The stored token is a taxonomy misnomer (spec-75 lineage): the AI filing
/// reader classifies the business trajectory AS REPORTED and is never asked whether guidance changed, and
/// the deterministic spec-57 earnings-8-K signal carries the same member for a plain earnings FILING — so
/// printing "GuidanceChange" reads as a guidance event that never happened.
/// </para>
/// <para>
/// Spec 209 adds a second relabel, <see cref="SignalType.InsiderBuying"/> → <c>InsiderActivity</c>: the
/// stored member covers every Form 4 (a 10b5-1 plan filing stream, whose transaction direction is not read,
/// renders as Neutral rows of it), so the literal token is a directional label over filings whose direction
/// the store does not know. Spec 167's stance that this mapping "must NEVER be applied to stored provenance
/// text" is SUPERSEDED for that ONE exact token only: <see cref="RewriteStoredProvenance"/> rewrites the
/// whole-word <c>InsiderBuying</c> inside stored evidence-link reasons and signal reasons at render time
/// (both paths the token reaches the reader), because a legend cannot un-invert a label the reader sees
/// eleven times. The GuidanceChange stance is unchanged — its stored text still renders byte-verbatim and
/// the renderer's legend line explains the literal token where it appears.
/// </para>
/// <para>
/// Spec 211 made this the single shared owner: until then the mapping was private to the renderer, so the
/// spec-210 floor rationale printed the STORED names (<c>GuidanceChange</c> in 15 of 19 live floored lines,
/// <c>InsiderBuying</c> — the forbidden substring — in LBRT's). A second private copy of this mapping is
/// the failure mode; <c>SignalTypeDisplayGuardrailTests</c> fails the build on one.
/// </para>
/// </summary>
public static class SignalTypeDisplay
{
    /// <summary>
    /// The label the report prints for a stored signal type: the two relabels above, and the enum member's
    /// own name for everything else.
    /// </summary>
    public static string Label(SignalType type) =>
        type switch
        {
            SignalType.GuidanceChange => "EarningsTrajectory",
            SignalType.InsiderBuying => "InsiderActivity",
            _ => type.ToString(),
        };

    // Spec 209: the presentation-only seam over STORED text (evidence-link contribution reasons and signal
    // reasons, both authored at scoring time). Exactly one whole-word token is rewritten; every other byte
    // renders verbatim. The stored JSON is never touched — accrued signals keep deserializing unchanged.
    private static readonly Regex StoredInsiderTypeToken =
        new(@"\bInsiderBuying\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rewrites the whole-word stored token <c>InsiderBuying</c> to <c>InsiderActivity</c> inside stored
    /// provenance text; nothing else changes (<c>NotInsiderBuyingX</c> is not a whole word and stays;
    /// <c>GuidanceChange</c> inside stored text stays verbatim per spec 167).
    /// </summary>
    public static string RewriteStoredProvenance(string stored) =>
        StoredInsiderTypeToken.Replace(stored, "InsiderActivity");
}
