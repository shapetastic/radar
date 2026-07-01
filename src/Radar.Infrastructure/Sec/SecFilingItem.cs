namespace Radar.Infrastructure.Sec;

/// <summary>
/// A single parsed SEC EDGAR filing from a company's submissions JSON (the columnar
/// <c>filings.recent</c> arrays flattened to one record per index). Raw metadata only — the collector
/// synthesizes evidence Title/RawText from these real fields and never fabricates filing body text.
/// <see cref="IndexUrl"/> is the stable landing page for the filing (provenance).
/// </summary>
internal sealed record SecFilingItem(
    string Form,
    string FilingDate,
    string? ReportDate,
    DateTimeOffset AcceptanceDateTimeUtc,
    string Accession,
    string? PrimaryDocument,
    string? PrimaryDocDescription,
    string? Items,
    string IndexUrl);
