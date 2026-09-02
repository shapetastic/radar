using Radar.Application.Storage;

namespace Radar.Application.Reporting;

/// <summary>
/// Writes a rendered daily news report's markdown to local storage. Best-effort like the weekly report
/// writer (AD-8): a disk failure logs and never throws, and the returned <see cref="DurableWriteResult"/>
/// carries the attempted path plus whether the markdown reached it. A report is a derived view, not
/// immutable evidence, so a same-day re-run overwriting the file is allowed.
/// </summary>
public interface IDailyNewsReportWriter
{
    Task<DurableWriteResult> WriteAsync(DateTimeOffset generatedAtUtc, string markdown, CancellationToken ct);
}
