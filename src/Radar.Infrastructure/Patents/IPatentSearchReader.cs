namespace Radar.Infrastructure.Patents;

/// <summary>
/// Infrastructure-internal abstraction over the USPTO ODP PFW Search API POST + parse so the collector is
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
    /// The USPTO ODP PFW Search endpoint URL — used as the evidence <c>SourceUrl</c> provenance link. The
    /// request is a POST with the query in the body, so this is the constant search endpoint (the assignee +
    /// grant floor are recorded in the evidence metadata). The parameters are kept on the signature so one
    /// builder produces both the fetched target and this link and they can never disagree.
    /// </summary>
    string QueryUrl(string assigneeName, DateOnly grantFloor);
}
