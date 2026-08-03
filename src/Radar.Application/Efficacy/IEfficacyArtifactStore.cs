namespace Radar.Application.Efficacy;

/// <summary>The written efficacy-artifact paths (best-effort; returned even when a write degraded).</summary>
public sealed record EfficacyArtifactPaths(string SvgPath, string CsvPath);

/// <summary>The written strategy-leaderboard paths (best-effort; returned even when a write degraded).</summary>
public sealed record StrategyLeaderboardPaths(string CsvPath, string MarkdownPath);

/// <summary>The written paired-comparison paths (best-effort; returned even when a write degraded).</summary>
public sealed record PairedComparisonPaths(string CsvPath, string MarkdownPath);

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

    /// <summary>
    /// Writes the spec-155 paired, purged strategy comparison to
    /// <c>data/efficacy/strategy-paired-comparison.{csv,md}</c> — a SEPARATE artifact pair from the
    /// leaderboard, because the leaderboard is descriptive and this is the only result that can support the
    /// amended AD-15 claim; sharing a file would let one overwrite the other's meaning. Same best-effort
    /// posture as <see cref="WriteAsync"/> (AD-8): a disk failure logs and returns the attempted paths rather
    /// than throwing.
    /// </summary>
    Task<PairedComparisonPaths> WritePairedComparisonAsync(string csv, string markdown, CancellationToken ct);
}
