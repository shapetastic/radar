using Radar.Application.Acquisitions;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// SPEC 217 §3 — the versioned identity of the OBSERVATION-ELIGIBILITY rule set, stamped on both efficacy
/// artifacts and on the Lead-call evidence lines the weekly report prints, so a reader can tell which
/// admission rules produced a number without reading the code.
/// <para>
/// v1 (implicit, specs 152/183) admitted an observation when a full-horizon forward return existed and, for
/// the pooled series, an excess return existed: the axes were <c>WithoutForwardPrice</c>,
/// <c>PartialForwardWindow</c>, <c>BenchmarkUnavailable</c> and <c>NotInBenchmarkUniverse</c>.
/// </para>
/// <para>
/// <b>v2 adds <c>CorporateActionInWindow</c></b>: an observation whose outcome NO STRATEGY COULD HAVE
/// EARNED is excluded and counted. MarineMax's +46.1% gap on 2026-08-10 sat inside the 21-day forward
/// window of every score from 2026-07-20 onward, and from the announcement its price has been pinned at the
/// $53.00 bid — zero variance, still scored. Both halves are the same fact: the outcome is a deal, not a
/// trajectory.
/// </para>
/// </summary>
public static class ObservationEligibility
{
    /// <summary>The current rule-set identity. Bump it when an admission AXIS is added or changes meaning.</summary>
    public const string Version = "observation-eligibility-v2";

    /// <summary>
    /// True when the observation at <paramref name="asOf"/> for a company whose recognised acquisition was
    /// announced on <paramref name="announcedOn"/> must be excluded.
    /// <para>
    /// ONE predicate covers both halves of the rule and that is arithmetic, not a shortcut. "The
    /// announcement falls inside the forward window <c>(D, D+h]</c>" is <c>D &lt; A ≤ D+h</c>; "D is on or
    /// after the announcement" is <c>A ≤ D</c>. Their union is exactly <c>A ≤ D+h</c>.
    /// </para>
    /// </summary>
    public static bool IsCorporateActionInWindow(DateOnly asOf, DateOnly announcedOn, int forwardHorizonDays) =>
        announcedOn <= asOf.AddDays(forwardHorizonDays);

    /// <summary>
    /// The same predicate against the shared run-time projection: false when the company has no recognised
    /// acquisition at all. The ONE place the two consumers (the observation builder and the benchmark) ask
    /// the question, so they can never disagree about a company.
    /// </summary>
    public static bool IsCorporateActionInWindow(
        PendingAcquisitions acquisitions, Guid companyId, DateOnly asOf, int forwardHorizonDays)
    {
        ArgumentNullException.ThrowIfNull(acquisitions);

        return acquisitions.AnnouncedOn(companyId) is { } announced
            && IsCorporateActionInWindow(asOf, announced, forwardHorizonDays);
    }
}
