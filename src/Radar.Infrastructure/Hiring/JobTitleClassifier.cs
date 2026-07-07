namespace Radar.Infrastructure.Hiring;

/// <summary>
/// Deterministic, pure job-title bucketing for the hiring collector (NO AI): case-insensitive SUBSTRING
/// matches against two small fixed keyword sets, tallied over a title list. The buckets are independent
/// tallies, not a partition — a single title (e.g. "VP of Engineering") may count toward BOTH. A title
/// matching neither set still counts toward the board's total roles (the tallies only enrich the evidence
/// metadata / synthesized counts; they never gate a role out). AI role-function classification beyond
/// these buckets is a deferred future <c>Microsoft.Extensions.AI</c> slice.
/// </summary>
internal static class JobTitleClassifier
{
    // Senior/leadership cues. "VP" is matched as a plain substring (like every other keyword) — titles
    // such as "VP, Strategic Partnerships" or "SVP Sales" both count as senior.
    private static readonly string[] SeniorKeywords =
        ["VP", "Vice President", "Chief", "Head of", "Director", "Principal"];

    // Engineering/R&D cues. "Engineer" already covers "Engineering" as a substring; both are kept so the
    // keyword set reads as the deliberate, fixed vocabulary it is.
    private static readonly string[] EngineeringKeywords =
        ["Engineer", "Engineering", "R&D", "Research", "Scientist"];

    /// <summary>
    /// Tallies how many of <paramref name="titles"/> read as senior/leadership roles and how many as
    /// engineering/R&amp;D roles (independently — a title may count toward both, or neither).
    /// </summary>
    public static (int Senior, int Engineering) Classify(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);

        var senior = 0;
        var engineering = 0;

        foreach (var title in titles)
        {
            if (ContainsAny(title, SeniorKeywords))
            {
                senior++;
            }

            if (ContainsAny(title, EngineeringKeywords))
            {
                engineering++;
            }
        }

        return (senior, engineering);
    }

    private static bool ContainsAny(string title, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
