namespace Radar.Application.Reporting;

public interface IWeeklyReportRenderer
{
    /// <summary>Renders the model to deterministic markdown. Pure: no clock, no I/O.</summary>
    string Render(WeeklyReportModel model);
}
