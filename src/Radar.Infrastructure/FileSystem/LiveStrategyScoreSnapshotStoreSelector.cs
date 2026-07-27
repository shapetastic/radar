using Radar.Application.Efficacy.Comparison;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// The default <see cref="IStrategyScoreSnapshotStoreSelector"/> (spec 140): the comparison reads the LIVE
/// forward series by delegating straight to the existing <see cref="IScoreSnapshotFileStoreFactory"/>.
/// <para>
/// That delegation is the whole point — it is the same factory the scoring pass writes through, so the primary
/// strategy resolves to the registered store at the scores root (byte-for-byte the series the spec-101/108
/// per-company efficacy read already uses) and each non-primary strategy to its own
/// <c>strategies/{name}/</c> scope. There is no second path resolution and therefore nothing that can drift.
/// </para>
/// </summary>
public sealed class LiveStrategyScoreSnapshotStoreSelector : IStrategyScoreSnapshotStoreSelector
{
    private readonly IScoreSnapshotFileStoreFactory _factory;

    public LiveStrategyScoreSnapshotStoreSelector(IScoreSnapshotFileStoreFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public string SeriesDescription => "the live forward score series";

    public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) =>
        _factory.ForStrategy(strategy);
}
