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

    /// <summary>
    /// How many snapshot writes into (<paramref name="runLabel"/>, <paramref name="strategy"/>) have
    /// REPLACED a file that was already on disk (spec 148). Replay names its files by AS-OF instant so that
    /// a re-run is idempotent — which also means a re-run under the same label silently replaces a series
    /// that may already have been ranked. Only the layer that computes the target path can tell, so it is
    /// counted there and surfaced here.
    /// <para>
    /// The counter is MONOTONIC for the lifetime of the factory instance and is never reset, so a caller
    /// spanning several runs takes a before/after difference rather than reading an absolute. Returns 0 for
    /// a pair that has never been written. It is pure bookkeeping: it changes no path, no file content and
    /// no score, and it is hashed into nothing.
    /// </para>
    /// </summary>
    int OverwrittenCount(string runLabel, ScoringStrategyDefinition strategy);
}
