namespace Radar.Application.Filings;

/// <summary>
/// The outcome of the deterministic spec-160 comparability scan of an earnings release body, carried on the
/// analyzed-filing cache record and the filing-read debug record so the scan's verdict is auditable per filing.
/// <para>
/// <see cref="CapTriggering"/> holds the matched phrases that declare a comparability break (they bound the AI
/// read's persisted confidence via the configured cap); <see cref="DiagnosticOnly"/> holds the matched phrases
/// that are recorded for hit-rate measurement but NEVER cap (over-broad prose correlates, demoted per the
/// spec-160 review — promoting one into the cap-triggering set is a <c>cmpscan-v2</c> decision made on this
/// accrued data, not on argument). Both lists are ordered (scanner table order) and distinct. Two EMPTY lists
/// mean "scanned clean", which is distinct from "not scanned" — an absent (null) markers value on a legacy
/// record means the record predates spec 160 and no scan happened.
/// </para>
/// </summary>
/// <param name="CapTriggering">Matched cap-triggering phrases (ordered, distinct; empty = none matched).</param>
/// <param name="DiagnosticOnly">Matched diagnostic-only phrases (ordered, distinct; empty = none matched).</param>
public sealed record ComparabilityMarkers(
    IReadOnlyList<string> CapTriggering,
    IReadOnlyList<string> DiagnosticOnly);
