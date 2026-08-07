namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>The written audit-artifact paths (best-effort; returned even when a write degraded).</summary>
public sealed record DenominatorAuditPaths(string CsvPath, string MarkdownPath);

/// <summary>
/// The persistence seam for the spec-172 audit artifacts: writes the CSV + markdown pair to
/// <c>data/audits/score-move-denominator.{csv,md}</c> — a NEW directory, so no existing efficacy artifact can
/// be overwritten (the spec-161 sibling hazard). Best-effort (AD-8): a disk failure logs and returns the
/// attempted paths rather than throwing. It writes ONLY audit artifacts — never evidence/signal/score — and
/// the directory is created only when the audit actually writes (default-off ⇒ no directory).
/// </summary>
public interface IDenominatorAuditArtifactStore
{
    Task<DenominatorAuditPaths> WriteAsync(string csv, string markdown, CancellationToken ct);
}
