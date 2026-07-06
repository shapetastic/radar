namespace Radar.Infrastructure.Sec;

/// <summary>
/// One parsed + classified SEC Schedule 13D/13G beneficial-ownership filing. v1 is metadata-only: the reader
/// classifies the filing by form string (<see cref="Category"/>) and carries the submissions-row fields — it
/// does NOT fetch or parse the free-form filing body, so there is no % of class, filer name, or 13D Item 4
/// intent here (all deferred). The collector synthesizes an advice-free evidence phrase from these real fields
/// (the fixed spec-99 ownership phrases) and never fabricates filing body text. <see cref="IndexUrl"/> is the
/// stable filing landing page (provenance). <see cref="Form"/> is the raw EDGAR form string (e.g. "SC 13D").
/// </summary>
internal sealed record Sec13DGFiling(
    string Accession,
    string FilingDate,
    DateTimeOffset AcceptanceDateTimeUtc,
    string IndexUrl,
    string Form,
    Sec13DGCategory Category);
