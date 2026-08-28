using System.Collections.Frozen;

using Radar.Application.NewsRisk.Judgment;

namespace Radar.Application.News;

/// <summary>
/// The CLOSED vocabulary of reasons one judgment produced no directional signal (spec 194 §1.2). Every
/// member names a SPECIFIC missing precondition rather than a generic "skipped": the whole point of the
/// correction is that a direction Radar cannot ground is not created, so the reason it could not be
/// grounded is the finding.
/// <para>
/// <b>These are not failures in equal measure.</b> <see cref="NotPresentationCohort"/>,
/// <see cref="NotJudged"/> and <see cref="NonDirectionalTrajectory"/> are the ORDINARY path — most
/// judgments in a run are one of them, and a <c>Mixed</c>/<c>Unknown</c> trajectory materializing nothing is
/// the system working exactly as designed. The provenance members
/// (<see cref="UnresolvedFact"/> … <see cref="ExcerptNotInEvidence"/>) are the ones worth watching: they
/// mean a real directional judgment existed and Radar declined to score it because the chain back to the
/// article it cited was incomplete.
/// </para>
/// </summary>
public enum NewsJudgmentSignalSkipReason
{
    /// <summary>The record belongs to a cohort other than the prospectively designated presentation cohort.</summary>
    NotPresentationCohort = 0,

    /// <summary>The attempt did not complete as <see cref="NewsJudgmentStatus.Judged"/> (a failure, an exhaustion marker, or no facts).</summary>
    NotJudged,

    /// <summary>The judged trajectory is <c>Mixed</c>, <c>Unknown</c> or absent — an honest non-direction, not a defect.</summary>
    NonDirectionalTrajectory,

    /// <summary>A directional verdict that cited no fact at all. Under <c>news-judgment-v2</c> that cannot happen; a <c>v1</c> record simply never recorded them.</summary>
    NoTrajectoryFactIds,

    /// <summary>A cited fact id does not resolve in the presentation cohort's stage-1 fact index.</summary>
    UnresolvedFact,

    /// <summary>
    /// RETAINED FOR ACCRUED ARTIFACTS ONLY — spec 197 §1.2 split this generic reason into
    /// <see cref="ObservationNoMatch"/> and <see cref="ObservationAmbiguous"/>, because "Radar collected no
    /// evidence for the cited article" and "Radar deliberately refused an ambiguous identity" are different
    /// facts and only the second is a policy decision. It is never newly produced; it stays declared so a
    /// pre-197 live artifact carrying the token still deserializes (the spec-189 <c>Failed</c> precedent).
    /// </summary>
    UnresolvedObservation,

    /// <summary>A cited fact or its joined evidence belongs to a DIFFERENT company than the judgment — Radar never attaches one company's verdict to another's article.</summary>
    CompanyMismatch,

    /// <summary>No cited text could be located verbatim in the primary anchor evidence, so the signal would carry an unverifiable excerpt.</summary>
    ExcerptNotInEvidence,

    /// <summary>The designated presentation cohort could not be resolved this run, so NO record is eligible.</summary>
    PresentationCohortUnresolved,

    /// <summary>An unexpected error occurred for this company. Counted rather than thrown, so one bad record cannot silence the rest of the pass.</summary>
    UnexpectedFailure,

    /// <summary>
    /// SPEC 197 §1.2 — a cited fact's source observation matched NO evidence at any tier of the join's
    /// ladder. Radar holds no news evidence for that article: a collection/coverage gap, not an identity
    /// decision.
    /// </summary>
    ObservationNoMatch,

    /// <summary>
    /// SPEC 197 §1.2 — a cited fact's source observation matched two or more evidence items, or a key two
    /// or more companies claim, at the strongest tier that matched at all. Radar REFUSED the identity rather
    /// than guessing which article the judgment cited; the ladder never falls through to a weaker key to
    /// make ambiguity disappear.
    /// </summary>
    ObservationAmbiguous,

    /// <summary>
    /// Defence in depth: the observation joined, but the evidence id it joined to is absent from the
    /// evidence this pass read. Impossible by construction (the join is built FROM that same list), and
    /// counted on its own axis rather than folded into <see cref="ObservationNoMatch"/> so that an
    /// impossible state can never be read as an ordinary coverage gap.
    /// </summary>
    JoinedEvidenceMissing,
}

/// <summary>
/// What one judgment-signal materialization pass did (spec 194 §1.2). Attached as a trailing nullable member
/// of <see cref="NewsJudgmentRunResult"/>, logged once, and rendered in the live news-risk artifact.
/// <para>
/// The counters answer four different questions and are deliberately not collapsed into one:
/// <see cref="Eligible"/> is how many judgments carried a real direction worth grounding;
/// <see cref="Materialized"/> is how many became a DURABLE signal; <see cref="AlreadyMaterialized"/> is the
/// idempotent no-op (the deterministic id already existed, so nothing was reviewed and nothing was
/// overwritten); and the rest are the named ways an eligible judgment did not produce one.
/// <see cref="Materialized"/> + <see cref="AlreadyMaterialized"/> + <see cref="PriorVersionOccupied"/> +
/// <see cref="ValidationRejected"/> + <see cref="WriteFailed"/> + the per-record skips
/// (<see cref="NewsJudgmentSignalSkipReason.UnresolvedFact"/>,
/// <see cref="NewsJudgmentSignalSkipReason.ObservationNoMatch"/>,
/// <see cref="NewsJudgmentSignalSkipReason.ObservationAmbiguous"/>,
/// <see cref="NewsJudgmentSignalSkipReason.JoinedEvidenceMissing"/>,
/// <see cref="NewsJudgmentSignalSkipReason.CompanyMismatch"/>,
/// <see cref="NewsJudgmentSignalSkipReason.ExcerptNotInEvidence"/> and
/// <see cref="NewsJudgmentSignalSkipReason.UnexpectedFailure"/>) equals <see cref="Eligible"/> on EVERY
/// path, because the eligibility gates are evaluated before any of those can occur.
/// </para>
/// <para>
/// The four PER-RECORD gate reasons (<see cref="NewsJudgmentSignalSkipReason.NotPresentationCohort"/>,
/// <see cref="NewsJudgmentSignalSkipReason.NotJudged"/>,
/// <see cref="NewsJudgmentSignalSkipReason.NonDirectionalTrajectory"/> and
/// <see cref="NewsJudgmentSignalSkipReason.NoTrajectoryFactIds"/>) sum, with <see cref="Eligible"/>, to
/// <see cref="JudgmentsConsidered"/> — with ONE named exception:
/// <see cref="NewsJudgmentSignalSkipReason.PresentationCohortUnresolved"/>. That reason is a PASS-level
/// fact, counted exactly ONCE per pass rather than once per record (one configuration condition is not N
/// provenance failures), and the pass returns before any record is examined. On that path
/// <see cref="JudgmentsConsidered"/> is the whole judgment count while <see cref="Eligible"/> and all four
/// gates are zero, so this second identity deliberately does not hold; the first one still does, trivially.
/// Both are pinned by <c>NewsJudgmentSignalMaterializerTests</c> — the mixed pass and the unresolved-cohort
/// pass respectively.
/// </para>
/// <para>
/// <see cref="WriteFailed"/> follows spec 193's truthful-outcome rule: a signal whose durable write failed
/// is COUNTED, never reported as materialized. It is in this process's in-memory index (matching what the
/// collection pass does with its own signals) but not on disk, so the accrued history does not contain it
/// and a later process may safely retry — there is no durable signal with that id to collide with, and no
/// retry queue is introduced.
/// </para>
/// </summary>
/// <param name="PriorVersionOccupied">
/// SPEC 197 §1.3 — eligible judgments that already hold a structurally valid signal under the RETIRED
/// <c>news-judgment-signal-v1</c> identity, so no v2 duplicate was minted. Counted on its OWN axis rather
/// than inside <see cref="AlreadyMaterialized"/> or a generic skip: it measures the one-time migration
/// across the version fork, and it must drain rather than persist. A MISSING or MALFORMED record at the v1
/// id is NOT occupancy and never lands here — it lets an honest v2 retry proceed.
/// </param>
/// <param name="JoinCounts">
/// SPEC 197 §1.2 — this run's observation→evidence ladder measurement, TRAILING and NULLABLE. <c>null</c>
/// means the join was NOT ATTEMPTED (the presentation cohort was unresolvable, or no judgment survived the
/// cheap gates, so neither store was read); a measured zero stays zero. Current-run diagnostic provenance
/// only: it enters no bundle hash, cache key, cohort key, judgment, signal or score.
/// </param>
public sealed record NewsJudgmentSignalMaterializationSummary(
    int JudgmentsConsidered,
    int Eligible,
    int Materialized,
    int AlreadyMaterialized,
    int ValidationRejected,
    int WriteFailed,
    IReadOnlyDictionary<NewsJudgmentSignalSkipReason, int> Skips,
    int PriorVersionOccupied = 0,
    NewsObservationEvidenceJoinCounts? JoinCounts = null)
{
    /// <summary>The empty summary — a pass that considered nothing. Shared, and frozen so it cannot be mutated through a cast.</summary>
    public static readonly NewsJudgmentSignalMaterializationSummary Empty = new(
        0, 0, 0, 0, 0, 0, FrozenDictionary<NewsJudgmentSignalSkipReason, int>.Empty);

    /// <summary>The count for one reason, or zero. A reason that never occurred is absent from the map rather than stored as a zero.</summary>
    public int SkipCount(NewsJudgmentSignalSkipReason reason) => Skips.GetValueOrDefault(reason);

    /// <summary>
    /// The skip counts as a deterministic, human-readable list — enum-declaration order, zero counts
    /// omitted, kebab tokens composed from the enum member names through the SAME
    /// <c>NewsJudgmentMarkerPolicy.KebabToken</c> the marker vocabulary uses, so a reason token and a
    /// marker token cannot spell the same concept two ways. Empty when nothing was skipped.
    /// </summary>
    public string DescribeSkips() => string.Join(
        ", ",
        Enum.GetValues<NewsJudgmentSignalSkipReason>()
            .Where(r => SkipCount(r) > 0)
            .Select(r => $"{NewsJudgmentMarkerPolicy.KebabToken(r.ToString())} {SkipCount(r)}"));
}
