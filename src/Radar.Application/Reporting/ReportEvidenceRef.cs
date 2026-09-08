namespace Radar.Application.Reporting;

/// <summary>
/// One company-reported figure as the weekly report displays it beside an earnings-8-K evidence line
/// (spec 215 §4): the metric's display name (<c>ReportedMetricDisplay.NameOf</c>, lower-case words), the
/// value and unit AS STATED, and the period in parentheses. No direction word, no arithmetic — the clause
/// states what the company said, nothing more.
/// </summary>
public sealed record ReportedMetricLine(string Metric, string Value, string Unit, string Period);

/// <summary>One piece of evidence behind a company entry (provenance for display).</summary>
/// <param name="ReportedMetrics">
/// Spec 215 §4 (trailing + nullable): the reported-metrics ledger records whose <c>EvidenceId</c> is this
/// evidence item, projected to display lines in ledger order. <c>null</c> = the ledger holds nothing for
/// this evidence (or no ledger is registered), and the renderer prints nothing — never an empty
/// "reported:" clause.
/// </param>
public sealed record ReportEvidenceRef(
    Guid EvidenceId,
    Guid SignalId,
    string SourceName,
    string? SourceUrl,
    string Title,
    string ContributionReason,
    IReadOnlyList<ReportedMetricLine>? ReportedMetrics = null);
