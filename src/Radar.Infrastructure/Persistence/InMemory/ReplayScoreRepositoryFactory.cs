using System.Collections.Concurrent;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.Persistence.InMemory;

/// <summary>
/// The replay-scoped <see cref="IScoreRepositoryFactory"/> (spec 139): EVERY strategy — the primary
/// included — gets its own fresh <see cref="InMemoryScoreRepository"/>.
/// <para>
/// This is the load-bearing difference from <see cref="StrategyScopedScoreRepositoryFactory"/>, which hands
/// the PRIMARY strategy the shared registered repository (the instance the weekly report renders). A replay
/// is a hypothesis about the past, not this run's result: if replayed primary snapshots landed in that shared
/// repository, the report would rank a mixture of "what Radar thinks now" and "what Radar would have thought
/// 40 days ago" for the same company, with no way to tell them apart. Isolating every strategy makes that
/// impossible by construction rather than by discipline.
/// </para>
/// <para>
/// The repositories are cached per strategy name (case-insensitive, matching
/// <see cref="ScoringStrategySet"/>'s uniqueness rule) so a strategy's snapshots and its evidence links
/// always land in the SAME store — provenance would break if a second call handed back a fresh repository.
/// </para>
/// </summary>
public sealed class ReplayScoreRepositoryFactory : IScoreRepositoryFactory
{
    private readonly ConcurrentDictionary<string, IScoreRepository> _byStrategy =
        new(StringComparer.OrdinalIgnoreCase);

    public IScoreRepository ForStrategy(ScoringStrategyDefinition strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return _byStrategy.GetOrAdd(strategy.Name, static _ => new InMemoryScoreRepository());
    }
}
