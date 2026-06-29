namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>Writes a built weekly report's markdown to local storage. Returns the written path.</summary>
public interface IReportFileWriter
{
    Task<string> WriteAsync(RadarReport report, CancellationToken ct);
}
