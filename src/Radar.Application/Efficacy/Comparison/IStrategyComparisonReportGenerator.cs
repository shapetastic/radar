using Radar.Application.Efficacy.Claims;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The opt-in strategy-vs-price comparison step (spec 140, AD-14 read side): reads each configured strategy's
/// persisted score series, joins it to price, ranks the strategies with a chronological hold-out, and writes
/// the leaderboard CSV + markdown. Runs as a Worker step DISTINCT from and OUTSIDE <c>IRadarPipeline</c>, after
/// the per-company efficacy render. It reads score history + price and writes only efficacy artifacts; it never
/// feeds evidence, signals, or scoring, and it promotes nothing — ranking is not acting.
/// </summary>
public interface IStrategyComparisonReportGenerator
{
    /// <summary>
    /// Builds and writes the leaderboard (and, when configured, the spec-155 paired comparison judged by the
    /// spec-170 composite AD-15 gate); returns the leaderboard so a caller can assert on the result.
    /// </summary>
    /// <param name="attentionPrerequisite">
    /// The neutral projection of AD-16's attention-arrival screen outcome, mapped by the Worker BEFORE this
    /// step runs. Nullable, and <c>null</c> fails CLOSED: it means the screen was not run at all
    /// (<c>ad16-screen-not-calculated</c>), so the composite AD-15 gate can never qualify. This type is in
    /// <c>Efficacy.Claims</c>, never an Attention type — Comparison → Attention stays forbidden.
    /// </param>
    Task<StrategyLeaderboard> GenerateAsync(
        Ad15AttentionPrerequisite? attentionPrerequisite, CancellationToken ct);

    /// <summary>
    /// Convenience overload for a composition with no attention screen at all: identical to passing
    /// <c>null</c> — the prerequisite is NOT calculated and the claim path is closed.
    /// </summary>
    Task<StrategyLeaderboard> GenerateAsync(CancellationToken ct) =>
        GenerateAsync(attentionPrerequisite: null, ct);
}
