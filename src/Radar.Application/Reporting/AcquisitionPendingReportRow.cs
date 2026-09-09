using Radar.Application.Acquisitions;

namespace Radar.Application.Reporting;

/// <summary>
/// SPEC 217 §2 — one row of the weekly report's <c>## Acquisitions pending</c> section: a company whose
/// thesis is closed because a recognised agreement to acquire it exists.
/// <para>
/// It carries the whole <see cref="PendingAcquisitionRecord"/> rather than copied strings, for the reason
/// <see cref="StrategyReportRow"/> carries its whole snapshot: every rendered fact is read straight off the
/// durable record the recognition wrote, so a row can never state an acquirer or a consideration that no
/// stored record produced (provenance is sacred), and a reader can walk report → acquisition record →
/// evidence.
/// </para>
/// </summary>
/// <param name="CompanyName">Display name, straight from the company record.</param>
/// <param name="Ticker">Display ticker, or null when the company record has none.</param>
/// <param name="Record">The recognised acquisition every rendered fact is read from.</param>
/// <param name="DaysPending">
/// Whole days from the announcement to the report period's end. Derived from the model's own period, never
/// from a wall clock, so a re-render of the same model is byte-identical (AD-3). Never negative: a record
/// announced after the period end is not rendered at all.
/// </param>
public sealed record AcquisitionPendingReportRow(
    string CompanyName,
    string? Ticker,
    PendingAcquisitionRecord Record,
    int DaysPending);
