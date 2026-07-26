using Radar.Application.Replay;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.Replay;

/// <summary>
/// The <see cref="IReplayScoringStrategyFactory"/> marker, implemented as a THIN delegating wrapper over an
/// inner <see cref="IScoringStrategyFactory"/> (spec 139).
/// <para>
/// A wrapper rather than a second implementation on purpose. <see cref="ScoringStrategyFactory"/> is sealed,
/// and copying it would mean two places that build engines — the exact drift that would let a replay engine
/// end up configured differently from the live one (different weights, a different fingerprint, a different
/// signal-type filter) and silently break the replay⊆forward invariant. Wrapping keeps ONE engine-building
/// implementation; the only thing that varies is the <see cref="IScoreRepositoryFactory"/> the inner factory
/// was constructed with (a replay-scoped one), which is precisely the isolation replay needs.
/// </para>
/// </summary>
public sealed class ReplayScoringStrategyFactory : IReplayScoringStrategyFactory
{
    private readonly IScoringStrategyFactory _inner;

    public ReplayScoringStrategyFactory(IScoringStrategyFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public IReadOnlyList<ScoringStrategyRuntime> Runtimes => _inner.Runtimes;

    public ScoringStrategyRuntime Primary => _inner.Primary;
}
