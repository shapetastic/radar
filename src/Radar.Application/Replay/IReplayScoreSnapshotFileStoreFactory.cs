using Radar.Application.Scoring;

namespace Radar.Application.Replay;

/// <summary>
/// Hands each replayed strategy the <see cref="IScoreSnapshotFileStore"/> its snapshots are written to
/// (spec 139), scoped by BOTH the replay run label and the strategy.
/// <para>
/// Deliberately a SEPARATE seam from <see cref="IScoreSnapshotFileStoreFactory"/> rather than an overload of
/// it. That factory's contract includes "the primary strategy gets the registered store at the live scores
/// root" — which is precisely what a replay must never touch, since the forward efficacy series is history
/// and a replay is a hypothesis. Keeping the seams distinct makes "replay never writes into the live scores
/// directory" a structural property of the type graph instead of a rule someone has to remember.
/// </para>
/// <para>
/// Repeated calls for the same (label, strategy) pair must return the SAME store: a run's snapshots have to
/// land together for the output to be a coherent series.
/// </para>
/// </summary>
public interface IReplayScoreSnapshotFileStoreFactory
{
    /// <summary>
    /// The store for <paramref name="strategy"/>'s snapshots within the replay run named
    /// <paramref name="runLabel"/>.
    /// </summary>
    IScoreSnapshotFileStore ForStrategy(string runLabel, ScoringStrategyDefinition strategy);
}
