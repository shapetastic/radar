namespace Radar.Application.Prices;

/// <summary>
/// A ticker's daily price history as persisted to <c>data/prices/{ticker}.json</c> — reference/validation
/// data (AD-14). Bars are ordered ascending by <see cref="PriceBar.Date"/> and deduped by <c>Date</c>.
/// Carries the <see cref="Source"/> + <see cref="RetrievedAtUtc"/> fetch instant for provenance of the
/// <em>reference dataset</em>. This provenance is deliberately DISCONNECTED from the evidence provenance
/// chain — price is not evidence, so it has its own source/instant and never links to an
/// <c>EvidenceItem</c> / <c>Signal</c> / <c>CompanyScoreSnapshot</c>.
/// </summary>
public sealed record PriceHistory(
    string Ticker,
    string Source,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<PriceBar> Bars);
