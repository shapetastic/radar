namespace Radar.Application.Scoring;

/// <summary>
/// Hands each scoring strategy the <see cref="IScoreSnapshotFileStore"/> it must mirror its snapshots to
/// (spec 137). The <b>primary</b> strategy gets the registered store at the existing scores root — unchanged,
/// so the spec-101/108 efficacy read, the weekly report's "vs previous run" read and all accrued history keep
/// working with no migration and no path-fallback logic. Every <b>non-primary</b> strategy gets a
/// strategy-scoped store so the two series can never collide.
/// <para>
/// Repeated calls for the same strategy return the same store.
/// </para>
/// </summary>
public interface IScoreSnapshotFileStoreFactory
{
    IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy);
}
