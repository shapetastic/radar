using System.Globalization;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// SPEC 220 §1 — the ONE definition of the order in which a company's canonical fact families fill the
/// judge's family budget (<c>family-ordering-v2</c>). Both cohorts read it through
/// <see cref="NewsJudgmentInputBuilder"/>; there is no second ordering anywhere.
/// <para>
/// <b>Why it exists.</b> Under the implicit <c>family-ordering-v1</c> (every judgment before spec 220) the
/// primary key was <c>MemberCount</c> — SYNDICATION VOLUME. What gets syndicated is boilerplate ("Q2 results
/// released", "CEO to present at conference"), while the fact that can carry a direction (a revenue
/// comparison, a contract award) often sits in a one- or two-member family. At the spec-219 breadth budget of
/// five families that ordering handed the judge its least informative facts: on 2026-09-09, 46 of 83 breadth
/// judgments were correct <c>Unknown</c> abstentions over five <c>NotQuantified</c> families, and on the full
/// cohort 7 of 17 cited trajectory facts sat OUTSIDE the top five by member count (AEHR's at rank 28 of 50).
/// </para>
/// <para>
/// <b>The order, and why the rank follows the enum's OWN documented semantics rather than intuition</b>:
/// <list type="number">
/// <item><see cref="NewsFactComparisonBasis.StatedComparison"/> ("states a comparison or trend") and
/// <see cref="NewsFactComparisonBasis.Event"/> ("carries direction without needing a comparison") TIE at rank
/// 0 — both can establish a direction, so both lead, interleaved by member count;</item>
/// <item><see cref="NewsFactComparisonBasis.LevelOnly"/> is rank 1 despite being quantified, because a level
/// "establishes no direction by itself" (spec 214: a level-only trajectory mints nothing; it is usable only
/// beside a cited reference value, spec 215's <c>ReferenceSupported</c>). Ranking it above
/// <see cref="NewsFactComparisonBasis.Event"/> would promote exactly the facts spec 214 says mint nothing;</item>
/// <item><see cref="NewsFactComparisonBasis.NotQuantified"/> is rank 2, at the back.</item>
/// </list>
/// Within a rank the pre-220 order is byte-identical: <c>MemberCount</c> descending, then (spec 219 §2)
/// <c>DistinctPublisherCount</c> descending, then <c>FamilyId</c> ascending, so the order is TOTAL and two
/// runs over one store produce one order (AD-3). Syndication volume stops being the primary key and becomes
/// the tie-break.
/// </para>
/// <para>
/// <b>The basis is the EXISTING <see cref="StatementComparisonClassifier"/> read</b> — no phrase table is
/// reimplemented here and no model call is added. Changing the rank table, the key order, or the classifier's
/// role in it is <c>family-ordering-v3</c>: <see cref="Version"/> joins
/// <see cref="NewsJudgmentContract.CohortKey"/>, because the order decides WHICH facts a bounded judge sees.
/// </para>
/// </summary>
public static class NewsJudgmentFamilyOrdering
{
    /// <summary>The ordering's version token — joins <see cref="NewsJudgmentContract.CohortKey"/> after <c>references=</c>.</summary>
    public const string Version = "family-ordering-v2";

    /// <summary>
    /// The basis rank (lower fills the budget first). A value outside the four defined members THROWS rather
    /// than falling into a default rank: a silently-ranked undefined basis would be an unrecorded decision
    /// about what the judge sees.
    /// </summary>
    public static int BasisRank(NewsFactComparisonBasis basis) => basis switch
    {
        NewsFactComparisonBasis.StatedComparison => 0,
        NewsFactComparisonBasis.Event => 0,
        NewsFactComparisonBasis.LevelOnly => 1,
        NewsFactComparisonBasis.NotQuantified => 2,
        _ => throw new ArgumentOutOfRangeException(
            nameof(basis), basis, "Undefined NewsFactComparisonBasis; family-ordering-v2 ranks only the four defined members."),
    };
}

/// <summary>
/// SPEC 220 §3 — a count of fact families per <see cref="NewsFactComparisonBasis"/> class. Persisted on the
/// judgment record (<see cref="NewsJudgmentRecord.FamiliesAvailableByBasis"/>) and aggregated into the
/// per-pass basis line, so "did the reordering change what the judge saw" is answerable from durable data.
/// Four ints and nothing else: it can carry no prose, score, rank or price. Deliberately NO computed
/// properties — the file store serializes public properties, and a derived value on disk could only ever
/// disagree with the four it is derived from.
/// </summary>
public sealed record NewsJudgmentBasisCounts(int StatedComparison, int Event, int LevelOnly, int NotQuantified)
{
    /// <summary>A MEASURED zero in every class — never a stand-in for "not recorded", which is <c>null</c>.</summary>
    public static NewsJudgmentBasisCounts Zero { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Counts a sequence of bases. An undefined enum value THROWS rather than being dropped or binned into a
    /// class: an uncounted family is exactly what these counts exist to prevent.
    /// </summary>
    public static NewsJudgmentBasisCounts Of(IEnumerable<NewsFactComparisonBasis> bases)
    {
        ArgumentNullException.ThrowIfNull(bases);

        int stated = 0, evt = 0, level = 0, notQuantified = 0;
        foreach (var basis in bases)
        {
            switch (basis)
            {
                case NewsFactComparisonBasis.StatedComparison:
                    stated++;
                    break;
                case NewsFactComparisonBasis.Event:
                    evt++;
                    break;
                case NewsFactComparisonBasis.LevelOnly:
                    level++;
                    break;
                case NewsFactComparisonBasis.NotQuantified:
                    notQuantified++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(bases), basis, "Undefined NewsFactComparisonBasis cannot be counted.");
            }
        }

        return new NewsJudgmentBasisCounts(stated, evt, level, notQuantified);
    }

    /// <summary>The sum over all four classes.</summary>
    public int Sum() => StatedComparison + Event + LevelOnly + NotQuantified;

    /// <summary>Class-wise addition, for aggregating across judgments.</summary>
    public NewsJudgmentBasisCounts Plus(NewsJudgmentBasisCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            StatedComparison + other.StatedComparison,
            Event + other.Event,
            LevelOnly + other.LevelOnly,
            NotQuantified + other.NotQuantified);
    }

    /// <summary>Class-wise subtraction (available − supplied = withheld per class).</summary>
    public NewsJudgmentBasisCounts Minus(NewsJudgmentBasisCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            StatedComparison - other.StatedComparison,
            Event - other.Event,
            LevelOnly - other.LevelOnly,
            NotQuantified - other.NotQuantified);
    }

    /// <summary>The fixed-order rendering used in the aggregated log line — every class named, zeros included.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"StatedComparison {StatedComparison} / Event {Event} / LevelOnly {LevelOnly} / NotQuantified {NotQuantified}");
}
