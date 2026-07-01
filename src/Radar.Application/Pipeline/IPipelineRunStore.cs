namespace Radar.Application.Pipeline;

/// <summary>
/// Persists and reads the append-only pipeline run log (AD-8). Each completed run writes one
/// <see cref="PipelineRunRecord"/>; the store is the durable history of when runs happened, which
/// collectors ran, and how the run's counts compared to prior runs. All file I/O stays behind this
/// interface in Infrastructure (AD-5); the Application layer never touches the disk directly.
/// </summary>
public interface IPipelineRunStore
{
    /// <summary>
    /// Persists <paramref name="record"/> to the run log and returns the written path. Best-effort:
    /// disk failures degrade gracefully (the record is not lost from the returned in-memory result) and
    /// never abort the run.
    /// </summary>
    Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct);

    /// <summary>
    /// Returns up to <paramref name="count"/> most-recent run records, newest-first, ordered by
    /// <see cref="PipelineRunRecord.CreatedAtUtc"/> descending then <see cref="PipelineRunRecord.Id"/>
    /// descending (AD-3 determinism). A non-positive <paramref name="count"/> returns an empty list.
    /// </summary>
    Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct);
}
