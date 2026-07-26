using System.Collections.Concurrent;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.Persistence.InMemory;

/// <summary>
/// The default <see cref="IScoreRepositoryFactory"/> (spec 137): the <b>primary</b> strategy writes into the
/// registered, shared <see cref="IScoreRepository"/> singleton — byte-identical to the single-strategy
/// composition, and the instance the weekly report reads — while every <b>non-primary</b> strategy gets its
/// own <see cref="InMemoryScoreRepository"/>.
/// <para>
/// This isolation is load-bearing, not tidiness: the report ranks each company's latest in-period snapshot
/// from the shared repository, so letting a second strategy write there would make the report rank a mixture
/// of strategies for the same company with no way to tell which produced which.
/// </para>
/// <para>
/// Non-primary repositories are cached per strategy name (case-insensitive, matching
/// <see cref="ScoringStrategySet"/>'s uniqueness rule) so a strategy's snapshots and its evidence links
/// always land in the SAME store — provenance would break if a second call handed back a fresh repository.
/// </para>
/// </summary>
public sealed class StrategyScopedScoreRepositoryFactory : IScoreRepositoryFactory
{
    private readonly IScoreRepository _primary;

    private readonly ConcurrentDictionary<string, IScoreRepository> _byStrategy =
        new(StringComparer.OrdinalIgnoreCase);

    public StrategyScopedScoreRepositoryFactory(IScoreRepository primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        _primary = primary;
    }

    public IScoreRepository ForStrategy(ScoringStrategyDefinition strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return strategy.IsPrimary
            ? _primary
            : _byStrategy.GetOrAdd(strategy.Name, static _ => new InMemoryScoreRepository());
    }
}
