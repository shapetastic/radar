using System.Globalization;

using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// SPEC 220 §1, extended by SPEC 221 §1 — the ONE definition of the order in which a company's canonical fact
/// families fill the judge's family budget (<c>family-ordering-v3</c> since spec 221; spec 220 shipped
/// <c>family-ordering-v2</c>). Both cohorts read it through <see cref="NewsJudgmentInputBuilder"/>; there is
/// no second ordering anywhere.
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
/// role in it moves <see cref="Version"/>, which joins <see cref="NewsJudgmentContract.CohortKey"/>, because
/// the order decides WHICH facts a bounded judge sees. (Spec 220's text here named that next move
/// <c>family-ordering-v3</c>; spec 221 is that move — see below.)
/// </para>
/// <para>
/// <b>SPEC 221 §1 — <c>family-ordering-v3</c>: a stock-price move is not a business trajectory.</b> The
/// classifier answers a LINGUISTIC question (does the statement carry a comparison marker or an event term)
/// and "the stock hit an all-time high" does. Under v2 such a family ranked with the revenue comparisons, so
/// on 2026-09-09 the judge was handed share-price moves, analyst labels and listicles as its "directional"
/// facts — and rejected them, correctly, in 28 of 47 <c>Unknown</c> rationales (SENEA: "the only supplied
/// fact with a StatedComparison is the 3.9% price increase"). Radar already knew those facts were unusable
/// in three places — prompt rule (5), the validator's <c>trajectory-non-business-context-only</c> gate, and
/// the judge itself — and the selector was the only layer that never asked. v3 asks, through the EXISTING
/// <see cref="NewsJudgmentContextOnlyEventTypes.IsConfinedTo"/> (REUSED, never re-declared): a family
/// confined to the context-only event types is NON-BUSINESS and ranks <see cref="NonBusinessRank"/>, after
/// every business class, WHATEVER its basis. The order is therefore business
/// <c>StatedComparison</c>/<c>Event</c> → business <c>LevelOnly</c> → business <c>NotQuantified</c> →
/// non-business, and within every class the v2 order (<c>MemberCount</c> → <c>DistinctPublisherCount</c> →
/// <c>FamilyId</c>) is byte-identical. Demoted, never dropped: a company whose whole supply is market
/// reaction still fills its budget, and the judge still sees it.
/// <list type="bullet">
/// <item>A family with an EMPTY event-type list is NOT demoted — <see cref="NewsJudgmentContextOnlyEventTypes"/>'s
/// own carve-out ("we cannot tell" must not read as "we can reject"). It is counted instead
/// (<see cref="NewsJudgmentInputBundle.FamiliesWithNoEventTypesAvailable"/>).</item>
/// <item>A mixed family (<c>MarketReaction</c> + <c>EarningsOrGuidance</c>) is BUSINESS: one non-context
/// type is enough, and that is usually where the real content is.</item>
/// <item>The <c>ComparisonBasis</c> LINE rendered to the judge is untouched — spec 221 changes which
/// families are picked, never what the judge is told about them.</item>
/// </list>
/// </para>
/// </summary>
public static class NewsJudgmentFamilyOrdering
{
    /// <summary>
    /// The ordering's version token — joins <see cref="NewsJudgmentContract.CohortKey"/> after <c>references=</c>.
    /// <c>family-ordering-v2</c> (spec 220, basis first) is HISTORY: spec 221 moved it to v3 (non-business last).
    /// </summary>
    public const string Version = "family-ordering-v3";

    /// <summary>
    /// SPEC 221 §1 — the rank of a NON-BUSINESS family (confined to
    /// <see cref="NewsJudgmentContextOnlyEventTypes"/>): after every business basis class, whatever its own basis.
    /// </summary>
    public const int NonBusinessRank = 3;

    /// <summary>
    /// The basis rank (lower fills the budget first) — the v2 table, unchanged, and still the rank of every
    /// BUSINESS family under v3. A value outside the four defined members THROWS rather than falling into a
    /// default rank: a silently-ranked undefined basis would be an unrecorded decision about what the judge sees.
    /// </summary>
    public static int BasisRank(NewsFactComparisonBasis basis) => basis switch
    {
        NewsFactComparisonBasis.StatedComparison => 0,
        NewsFactComparisonBasis.Event => 0,
        NewsFactComparisonBasis.LevelOnly => 1,
        NewsFactComparisonBasis.NotQuantified => 2,
        _ => throw new ArgumentOutOfRangeException(
            nameof(basis), basis, "Undefined NewsFactComparisonBasis; the family ordering ranks only the four defined members."),
    };

    /// <summary>
    /// SPEC 221 §1 — whether a family is NON-BUSINESS: the ONE rule, delegated verbatim to
    /// <see cref="NewsJudgmentContextOnlyEventTypes.IsConfinedTo"/> (at least one declared type, every declared
    /// type context-only). An empty list is NOT non-business.
    /// </summary>
    public static bool IsNonBusiness(IReadOnlyList<NewsEventType> eventTypes) =>
        NewsJudgmentContextOnlyEventTypes.IsConfinedTo(eventTypes);

    /// <summary>
    /// SPEC 221 §1 — the <c>family-ordering-v3</c> class rank (lower fills the budget first):
    /// <see cref="NonBusinessRank"/> for a non-business family, otherwise <see cref="BasisRank"/>. The basis is
    /// validated FIRST, so an undefined basis throws even on a non-business family — demotion never launders
    /// an unrecorded value.
    /// </summary>
    public static int ClassRank(NewsFactComparisonBasis basis, IReadOnlyList<NewsEventType> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var basisRank = BasisRank(basis);
        return IsNonBusiness(eventTypes) ? NonBusinessRank : basisRank;
    }

    /// <summary>Whether a basis can carry a direction (<c>StatedComparison</c> or <c>Event</c>).</summary>
    public static bool IsDirectionalBasis(NewsFactComparisonBasis basis) =>
        basis is NewsFactComparisonBasis.StatedComparison or NewsFactComparisonBasis.Event;
}

/// <summary>
/// SPEC 221 §2a — what Radar HANDED the judge, derived from supply only: the supplied families counted by
/// comparison basis, split business / non-business (<see cref="NewsJudgmentFamilyOrdering.IsNonBusiness"/>),
/// plus how many supplied families declared no event type at all. It audits RADAR's own behaviour — did we
/// hand the judge anything to work with — and cannot be gamed by the model. It is NOT the source of any
/// verdict: <c>NoBusinessSignal</c> is the judge's own answer (spec 221 §2b), and the disagreement between
/// the two is the diagnostic (<see cref="NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal"/>).
/// <para>
/// <see cref="WithNoEventTypes"/> is a SUBSET of <see cref="Business"/> (an untyped family is treated as
/// business, never rejected), broken out so a stage-1 labelling gap is visible rather than silently absorbed.
/// Deliberately NO computed properties — the <see cref="NewsJudgmentBasisCounts"/> precedent: the file store
/// serializes public properties, and a derived value on disk could only ever disagree with its inputs.
/// </para>
/// </summary>
public sealed record NewsJudgmentSuppliedBasisProfile(
    NewsJudgmentBasisCounts Business,
    NewsJudgmentBasisCounts NonBusiness,
    int WithNoEventTypes)
{
    /// <summary>
    /// Profiles a supplied family list. Pure; an undefined basis throws (via
    /// <see cref="NewsJudgmentBasisCounts.Of"/>) rather than being dropped.
    /// </summary>
    public static NewsJudgmentSuppliedBasisProfile Of(IReadOnlyList<NewsJudgmentInputFamily> suppliedFamilies)
    {
        ArgumentNullException.ThrowIfNull(suppliedFamilies);
        return new NewsJudgmentSuppliedBasisProfile(
            Business: NewsJudgmentBasisCounts.Of(suppliedFamilies
                .Where(f => !NewsJudgmentFamilyOrdering.IsNonBusiness(f.EventTypes))
                .Select(f => f.ComparisonBasis)),
            NonBusiness: NewsJudgmentBasisCounts.Of(suppliedFamilies
                .Where(f => NewsJudgmentFamilyOrdering.IsNonBusiness(f.EventTypes))
                .Select(f => f.ComparisonBasis)),
            WithNoEventTypes: suppliedFamilies.Count(f => f.EventTypes.Count == 0));
    }
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
