namespace Radar.Infrastructure.Fda;

/// <summary>
/// Infrastructure-internal abstraction over the openFDA device-clearance (510(k) + PMA) GET + parse so the
/// collector is fully offline-testable (tests supply fixture clearances; the real reader uses
/// <c>HttpClient</c> + <c>System.Text.Json</c>). An applicant with no recent clearances (including openFDA's
/// documented empty-search 404), an unreachable endpoint, or a malformed response each reports its mode via
/// the returned <see cref="FdaClearanceReadResult"/> rather than swallowing it; caller-requested cancellation
/// still throws <see cref="OperationCanceledException"/>. openFDA needs no API key.
/// </summary>
internal interface IFdaClearanceReader
{
    /// <summary>
    /// Reads device clearances/approvals whose applicant matches <paramref name="applicantName"/> and whose
    /// decision date is on or after <paramref name="decisionFloor"/> (a bounded single page per endpoint,
    /// merged across 510(k) and PMA).
    /// </summary>
    Task<FdaClearanceReadResult> ReadAsync(string applicantName, DateOnly decisionFloor, CancellationToken ct);

    /// <summary>
    /// The human-viewable openFDA query URL for the same applicant + decision floor — used as the evidence
    /// <c>SourceUrl</c> provenance link (there is no stable per-applicant landing page; the 510(k) query URL
    /// is returned). One builder produces both the fetched URL and this link so they can never disagree.
    /// </summary>
    string QueryUrl(string applicantName, DateOnly decisionFloor);
}
