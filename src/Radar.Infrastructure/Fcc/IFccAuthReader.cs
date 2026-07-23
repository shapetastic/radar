namespace Radar.Infrastructure.Fcc;

/// <summary>
/// Infrastructure-internal abstraction over the FCC OET EAS GenericSearch GET + CSV parse so the collector is
/// fully offline-testable (tests supply fixture CSV; the real reader uses <c>HttpClient</c> + a small CSV
/// parser). A grantee with no recent authorizations, an unreachable/Akamai-blocked endpoint, or an unexpected
/// response each reports its mode via the returned <see cref="FccAuthReadResult"/> rather than swallowing it;
/// caller-requested cancellation still throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IFccAuthReader
{
    /// <summary>
    /// Reads FCC equipment authorizations whose applicant/grantee name matches <paramref name="granteeName"/>
    /// and whose grant date is on or after <paramref name="grantFloor"/> (a bounded single page).
    /// </summary>
    Task<FccAuthReadResult> ReadAsync(string granteeName, DateOnly grantFloor, CancellationToken ct);

    /// <summary>
    /// The human-viewable EAS GenericSearch query URL for the same grantee + grant floor — used as the
    /// evidence <c>SourceUrl</c> provenance link (there is no stable per-grantee landing page). One builder
    /// produces both the fetched URL and this link so they can never disagree.
    /// </summary>
    string QueryUrl(string granteeName, DateOnly grantFloor);
}
