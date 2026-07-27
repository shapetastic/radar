namespace Radar.Application.Efficacy;

/// <summary>The written efficacy-artifact paths (best-effort; returned even when a write degraded).</summary>
public sealed record EfficacyArtifactPaths(string SvgPath, string CsvPath);

/// <summary>The written strategy-leaderboard paths (best-effort; returned even when a write degraded).</summary>
public sealed record StrategyLeaderboardPaths(string CsvPath, string MarkdownPath);

/// <summary>
/// The persistence seam for the per-company efficacy artifacts (AD-14 read side): writes the SVG + CSV under
/// <c>data/efficacy/{ticker}.{svg,csv}</c>. Best-effort (AD-8): a disk failure logs and returns the attempted
/// path(s) rather than throwing. It writes ONLY efficacy artifacts — never evidence/signal/score.
/// </summary>
public interface IEfficacyArtifactStore
{
    Task<EfficacyArtifactPaths> WriteAsync(string ticker, string svg, string csv, CancellationToken ct);

    /// <summary>
    /// Writes the spec-140 strategy-vs-price leaderboard to <c>data/efficacy/strategy-leaderboard.{csv,md}</c>
    /// — ONE artifact per run (it compares strategies, not companies), alongside the per-company files rather
    /// than replacing any of them. Same best-effort posture as <see cref="WriteAsync"/> (AD-8): a disk failure
    /// logs and returns the attempted paths rather than throwing.
    /// </summary>
    Task<StrategyLeaderboardPaths> WriteLeaderboardAsync(string csv, string markdown, CancellationToken ct);
}
