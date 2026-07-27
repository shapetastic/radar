using Radar.Application.Efficacy.Comparison;
using Radar.Application.Replay;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// The <see cref="IStrategyScoreSnapshotStoreSelector"/> that points the spec-140 comparison at ONE spec-139
/// replay run's output (<c>Radar:Efficacy:Comparison:ReplayLabel</c>), by delegating to the existing
/// <see cref="IReplayScoreSnapshotFileStoreFactory"/> with the label pinned.
/// <para>
/// This is what makes the comparison usable before a long forward multi-strategy history has accrued: a replay
/// produces a dense per-strategy as-of series over stored signals, and this selector reads it. Note the
/// two-process workflow it implies — a replay run REPLACES the pipeline run (spec 139) and never renders
/// efficacy, so the sequence is "run the replay, then run a pass with the comparison pointed at its label".
/// </para>
/// <para>
/// Read-only in both directions: it hands out the same <see cref="IScoreSnapshotFileStore"/> type the live
/// selector does, the comparison only ever calls the read method on it, and the replay root is its own
/// directory outside the live scores root — so nothing here can touch the forward series.
/// </para>
/// </summary>
public sealed class ReplayLabelStrategyScoreSnapshotStoreSelector : IStrategyScoreSnapshotStoreSelector
{
    private readonly IReplayScoreSnapshotFileStoreFactory _factory;
    private readonly string _runLabel;

    public ReplayLabelStrategyScoreSnapshotStoreSelector(
        IReplayScoreSnapshotFileStoreFactory factory, string runLabel)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runLabel);
        _factory = factory;
        _runLabel = runLabel;
    }

    public string SeriesDescription => $"replay run '{_runLabel}'";

    public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) =>
        _factory.ForStrategy(_runLabel, strategy);
}
