namespace Radar.Infrastructure.Patents;

/// <summary>
/// Infrastructure-internal abstraction over the PatentsView Search API GET + parse so the collector is
/// fully offline-testable (tests supply fixture grants; the real reader uses <c>HttpClient</c> +
/// <c>System.Text.Json</c>). An assignee with no recent grants, an unreachable endpoint, or a blank API
/// key each reports its mode via the returned <see cref="PatentSearchReadResult"/> rather than swallowing
/// it; caller-requested cancellation still throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IPatentSearchReader
{
    /// <summary>
    /// Reads granted patents whose assignee organization contains <paramref name="assigneeName"/> and whose
    /// grant date is on or after <paramref name="grantFloor"/> (a bounded single page).
    /// </summary>
    Task<PatentSearchReadResult> ReadAsync(string assigneeName, DateOnly grantFloor, CancellationToken ct);

    /// <summary>
    /// The human-viewable PatentsView query URL for the same assignee + grant floor — used as the evidence
    /// <c>SourceUrl</c> provenance link (there is no stable per-assignee landing page). One builder produces
    /// both the fetched URL and this link so they can never disagree.
    /// </summary>
    string QueryUrl(string assigneeName, DateOnly grantFloor);
}
