namespace Radar.Application.Reporting;

using Radar.Domain.Reports;
using Radar.Domain.Scoring;

/// <summary>
/// Maps a company's score snapshot onto one of the five ALLOWED weekly-report labels. The mapping is a
/// versioned product decision; implementations must emit only Investigate / Watch / NeedsMoreEvidence /
/// ThesisImproving / ThesisDeteriorating and must never produce financial-advice language.
/// </summary>
public interface IReportActionPolicy
{
    string Version { get; }
    ReportActionResult Decide(ReportActionContext context);
}
