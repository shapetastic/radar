namespace Radar.Application.News;

/// <summary>
/// The create-once <c>boundary.json</c> shape (spec 177 §5): the whole-universe prospective-capture start
/// (<see cref="FirstProspectiveCaptureAsOfUtc"/> comes from the actual establishing run, never a document
/// date) plus the explicit batch association. Public since spec 179 so the read-only news-risk evaluator can
/// gate its clean prospective table on "assessment at/after the boundary" — the file implementation
/// serializes exactly this ONE record type.
/// </summary>
public sealed record NewsObservationBoundary(
    string SchemaVersion,
    DateTimeOffset FirstProspectiveCaptureAsOfUtc,
    Guid EstablishedByBatchId);

/// <summary>
/// Read seam over the create-once prospective boundary (spec 179 §9). <c>null</c> means no boundary has
/// been established (or the file is unreadable) — which the evaluator must treat as "no clean prospective
/// cohort exists yet" (fail closed), never as "everything is prospective".
/// </summary>
public interface INewsProspectiveBoundaryReader
{
    /// <summary>The established boundary, or <c>null</c>. Never throws for a read failure; caller cancellation propagates.</summary>
    Task<NewsObservationBoundary?> ReadBoundaryAsync(CancellationToken ct);
}
