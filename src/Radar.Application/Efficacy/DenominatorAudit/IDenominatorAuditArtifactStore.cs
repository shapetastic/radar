using Radar.Application.Storage;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// The per-file outcomes of the audit pair (spec 201 §1): each member is the shared
/// <see cref="DurableWriteResult"/> — the attempted path plus whether the content reached it. The
/// <c>*Path</c> projections keep the pre-201 shape; a path is never evidence that the file exists.
/// </summary>
public sealed record DenominatorAuditPaths(DurableWriteResult Csv, DurableWriteResult Markdown)
{
    public string CsvPath => Csv.Path;

    public string MarkdownPath => Markdown.Path;

    public int NotPersistedCount => (Csv.Written ? 0 : 1) + (Markdown.Written ? 0 : 1);
}

/// <summary>
/// The persistence seam for the spec-172 audit artifacts: writes the CSV + markdown pair to
/// <c>data/audits/score-move-denominator.{csv,md}</c> — a NEW directory, so no existing efficacy artifact can
/// be overwritten (the spec-161 sibling hazard). Best-effort (AD-8): a disk failure logs, never throws, and is
/// reported on the returned per-file outcomes. It writes ONLY audit artifacts — never evidence/signal/score — and
/// the directory is created only when the audit actually writes (default-off ⇒ no directory).
/// </summary>
public interface IDenominatorAuditArtifactStore
{
    Task<DenominatorAuditPaths> WriteAsync(string csv, string markdown, CancellationToken ct);
}
