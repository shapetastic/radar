namespace Radar.Application.News;

/// <summary>
/// Read seam over the spec-177 per-pass batch manifests (spec 179 §4). The shadow news-risk step needs the
/// EXACT run's coverage/capture provenance — resolved by explicit batch id (the run record carries
/// <c>NewsObservationBatchId</c>), never by a nearest-time join — to decide whether a company's input
/// bundle can honestly be called complete. Read-only; the archive stays insert-only.
/// </summary>
public interface INewsObservationBatchReader
{
    /// <summary>
    /// The persisted batch manifest with <paramref name="batchId"/>, or <c>null</c> when none exists or it
    /// is unreadable — which callers must treat as UNPROVEN capture (fail closed), never as a clean batch.
    /// Never throws for a read failure; caller cancellation propagates.
    /// </summary>
    Task<NewsObservationBatch?> GetBatchAsync(Guid batchId, CancellationToken ct);
}
