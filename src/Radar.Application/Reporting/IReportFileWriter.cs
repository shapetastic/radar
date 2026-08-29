namespace Radar.Application.Reporting;

using Radar.Application.Storage;
using Radar.Domain.Reports;

/// <summary>
/// Writes a built weekly report's markdown to local storage. Best-effort (AD-8): a disk failure logs and
/// never throws, and since spec 201 §1 the outcome is REPORTED — the returned
/// <see cref="DurableWriteResult"/> carries the attempted path plus whether the markdown reached it.
/// </summary>
public interface IReportFileWriter
{
    Task<DurableWriteResult> WriteAsync(RadarReport report, CancellationToken ct);
}
