namespace Radar.Application.Collectors;

/// <summary>
/// A single source (one RSS feed, one local file, …) that could not be read, parsed, or validated
/// during a collection run, with a short human-readable reason. Observational only — carries no
/// labels, scores, or advice language.
/// </summary>
public sealed record SourceFailure(string SourceName, string? SourceUrl, string Reason);

/// <summary>
/// Observational collection-health metadata for one collector run: how many sources were checked,
/// how many succeeded/failed, how many items were collected, and which sources failed. This is
/// <b>not</b> a score or signal — it carries no labels and no advice language; provenance still
/// lives in the persisted evidence/signals/snapshots/report. <see cref="Failures"/> is in stable
/// source-processing order.
/// </summary>
public sealed record CollectionSummary(
    int SourcesChecked,
    int SourcesSucceeded,
    int SourcesFailed,
    int ItemsCollected,
    IReadOnlyList<SourceFailure> Failures)
{
    public static CollectionSummary Empty { get; } = new(0, 0, 0, 0, []);
}
