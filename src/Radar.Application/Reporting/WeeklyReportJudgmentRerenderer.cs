namespace Radar.Application.Reporting;

using Microsoft.Extensions.Logging;
using Radar.Domain.Reports;

/// <summary>
/// The spec-185 §5 report re-render seam: the weekly report is rendered and written INSIDE the pipeline,
/// while the judgment step runs AFTER it in the Worker — so the first render carries
/// <c>? unassessed (judgment-pending)</c> markers, and once the judgment pass completes the Worker
/// re-renders the SAME captured model with the policy-derived marker map and overwrites the SAME report
/// file (the writer keys the path on the report's period end, and a report is a derived view — AD-1 governs
/// evidence only). Registered ONLY when the judgment step is registered; its mere presence is what tells
/// the report builder to render the pending markers on the first pass.
/// </summary>
public interface IWeeklyReportJudgmentRerenderer
{
    /// <summary>Captures the exact model/report pair the builder rendered, for a later marker re-render.</summary>
    void CaptureRendered(WeeklyReportModel model, RadarReport report);

    /// <summary>
    /// Re-renders the captured model with <paramref name="markers"/> and overwrites the report file.
    /// Returns <c>false</c> (logged, never thrown) when no render was captured this run — the honest
    /// pending markers then stand. Cancellation propagates.
    /// </summary>
    Task<bool> RerenderAsync(NewsJudgmentMarkerReportModel markers, CancellationToken ct);
}

/// <summary>
/// Holds the latest rendered (model, report) pair and re-renders it through the SAME renderer and file
/// writer the pipeline used — one rendering code path, one file path, no second route. The re-render
/// changes ONLY the marker source on the model: every score, rank, ordering, label and snapshot citation is
/// byte-identical to the first render (the marker column is display metadata, spec 185 §4).
/// </summary>
public sealed class WeeklyReportJudgmentRerenderer : IWeeklyReportJudgmentRerenderer
{
    private readonly IWeeklyReportRenderer _renderer;
    private readonly IReportFileWriter _fileWriter;
    private readonly ILogger<WeeklyReportJudgmentRerenderer> _logger;
    private readonly Lock _gate = new();
    private (WeeklyReportModel Model, RadarReport Report)? _captured;

    public WeeklyReportJudgmentRerenderer(
        IWeeklyReportRenderer renderer,
        IReportFileWriter fileWriter,
        ILogger<WeeklyReportJudgmentRerenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(fileWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _renderer = renderer;
        _fileWriter = fileWriter;
        _logger = logger;
    }

    public void CaptureRendered(WeeklyReportModel model, RadarReport report)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            _captured = (model, report);
        }
    }

    public async Task<bool> RerenderAsync(NewsJudgmentMarkerReportModel markers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(markers);

        (WeeklyReportModel Model, RadarReport Report) captured;
        lock (_gate)
        {
            if (_captured is not { } value)
            {
                _logger.LogWarning(
                    "No rendered weekly report was captured this run; the judgment markers cannot be "
                        + "re-rendered and the report's pending markers stand.");
                return false;
            }

            captured = value;
            _captured = null; // one capture, one re-render — a marker map never applies to a stale model
        }

        var markdown = _renderer.Render(captured.Model with { NewsJudgment = markers });
        var write = await _fileWriter
            .WriteAsync(captured.Report with { MarkdownContent = markdown }, ct)
            .ConfigureAwait(false);
        if (!write.Written)
        {
            // Spec 201 §1: a re-render whose file never landed is not a re-render. The pending markers in
            // whatever file exists at that path (if any) stand, and the caller's bool says so.
            _logger.LogWarning(
                "Re-rendered weekly report {ReportId} with the news-judgment markers, but the write to "
                    + "{Path} degraded gracefully: the file on disk (if any) still carries the pending "
                    + "markers.",
                captured.Report.Id,
                write.Path);
            return false;
        }

        _logger.LogInformation(
            "Re-rendered weekly report {ReportId} with the news-judgment semantic-read markers.",
            captured.Report.Id);
        return true;
    }
}
