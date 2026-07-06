namespace Radar.Application.Efficacy;

/// <summary>The written efficacy-artifact paths (best-effort; returned even when a write degraded).</summary>
public sealed record EfficacyArtifactPaths(string SvgPath, string CsvPath);

/// <summary>
/// The persistence seam for the per-company efficacy artifacts (AD-14 read side): writes the SVG + CSV under
/// <c>data/efficacy/{ticker}.{svg,csv}</c>. Best-effort (AD-8): a disk failure logs and returns the attempted
/// path(s) rather than throwing. It writes ONLY efficacy artifacts — never evidence/signal/score.
/// </summary>
public interface IEfficacyArtifactStore
{
    Task<EfficacyArtifactPaths> WriteAsync(string ticker, string svg, string csv, CancellationToken ct);
}
