using Radar.Application.Scoring;

namespace Radar.Application.Abstractions.Persistence;

/// <summary>
/// Hands each scoring strategy the <see cref="IScoreRepository"/> it must write into (spec 137).
/// <para>
/// This seam exists because the weekly report reads the <b>shared</b> score repository. If every strategy's
/// engine wrote into that one instance, the report would silently rank a mixture of strategies' snapshots
/// for the same company. Implementations therefore return:
/// </para>
/// <list type="bullet">
/// <item>for the <b>primary</b> strategy — the shared, registered repository (byte-identical to today);</item>
/// <item>for every <b>non-primary</b> strategy — a repository scoped to that strategy alone.</item>
/// </list>
/// Repeated calls for the same strategy must return the SAME instance (a strategy's snapshots and its
/// evidence links have to land together for provenance to hold).
/// </summary>
public interface IScoreRepositoryFactory
{
    IScoreRepository ForStrategy(ScoringStrategyDefinition strategy);
}
