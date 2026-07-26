using Radar.Application.Scoring;

namespace Radar.Application.Replay;

/// <summary>
/// The replay-scoped <see cref="IScoringStrategyFactory"/> (spec 139): the SAME strategy set, configured the
/// SAME way — same weights, same fingerprints, same signal-type filters — but with every strategy's engine
/// bound to a replay-scoped <see cref="Radar.Application.Abstractions.Persistence.IScoreRepository"/> instead
/// of the shared one the weekly report renders.
/// <para>
/// This exists ONLY as a resolution marker; it deliberately adds no members. Replay must call the live
/// scoring seam, not a second copy of it (a forked engine would drift and silently invalidate the
/// replay⊆forward invariant), so the type identity is the whole point: it lets a composition root register
/// replay engines alongside the live ones without either resolving the other's score repository.
/// </para>
/// </summary>
public interface IReplayScoringStrategyFactory : IScoringStrategyFactory;
