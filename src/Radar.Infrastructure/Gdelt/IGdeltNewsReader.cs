namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// Infrastructure-internal abstraction over the GDELT DOC 2.0 <c>ArtList</c> GET + parse so the collector is
/// fully offline-testable (tests supply fixture articles; the real reader uses <c>HttpClient</c> +
/// <c>System.Text.Json</c>). A company with no recent coverage, an unreachable endpoint, the request's own
/// timeout, malformed JSON, and the operationally-dominant HTTP 429 rate-limit each report their mode via
/// the returned <see cref="GdeltReadResult"/> rather than swallowing it; caller-requested cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IGdeltNewsReader
{
    Task<GdeltReadResult> ReadAsync(GdeltNewsQuery query, CancellationToken ct);
}
