namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// One company declared excluded from AD-16 §7's binding primary screen by a committed cohort file.
/// <para>
/// Both identifiers are carried because the cohort file declares both and they check each other: the ticker is
/// what resolves against Radar's watch universe, and the CIK is what proves the resolution is the company the
/// cohort meant. A cohort that names <c>FR</c> and a universe whose <c>FR</c> is a different registrant would
/// otherwise silently exclude the wrong company — or, worse, silently fail to exclude the right one.
/// </para>
/// </summary>
public sealed record ExcludedCohortMember(string Cohort, string Ticker, string Cik);

/// <summary>
/// The loaded exclusion cohorts, or the fact that they could not be loaded (spec 169).
/// <para>
/// <b>"Unavailable" is a first-class state, not an empty list.</b> AD-16's 2026-07-31 amendment makes the
/// exclusion binding: an evaluator that silently included all companies because a file was missing would
/// produce a primary screen that quietly violates an accepted amendment while looking entirely normal. So a
/// missing directory, an unreadable or malformed file, or a contradictory member suppresses the primary status
/// under <c>CohortConfigurationUnavailable</c> instead.
/// </para>
/// </summary>
public sealed record ExcludedCohortSet(
    bool IsAvailable, string? UnavailableDetail, IReadOnlyList<ExcludedCohortMember> Members)
{
    public static ExcludedCohortSet Available(IReadOnlyList<ExcludedCohortMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return new ExcludedCohortSet(IsAvailable: true, UnavailableDetail: null, Members: members);
    }

    public static ExcludedCohortSet Unavailable(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new ExcludedCohortSet(IsAvailable: false, UnavailableDetail: detail, Members: []);
    }
}

/// <summary>
/// Reads the committed cohort declarations under <c>docs/cohorts/</c> — the machine-readable source AD-16's
/// 2026-07-31 amendment points the evaluator at ("the evaluator reads the file, never git history").
/// <para>
/// Application-side abstraction with its file implementation in Infrastructure (AD-5): no file I/O crosses
/// into <c>Radar.Application</c>. Read-only; it never writes a cohort file.
/// </para>
/// </summary>
public interface IExcludedCohortStore
{
    /// <summary>
    /// Loads every cohort declared <c>"excludeFromPrimaryScreen": true</c>, with members ordered
    /// deterministically (AD-3). Never throws for a data condition: a missing directory, an unreadable file or
    /// malformed JSON returns <see cref="ExcludedCohortSet.Unavailable"/>. Cancellation propagates.
    /// </summary>
    Task<ExcludedCohortSet> LoadAsync(CancellationToken ct);
}
