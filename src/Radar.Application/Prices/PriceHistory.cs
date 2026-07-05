namespace Radar.Application.Prices;

/// <summary>
/// A ticker's daily price history as persisted to <c>data/prices/{ticker}.json</c> — reference/validation
/// data (AD-14). Bars are ordered ascending by <see cref="PriceBar.Date"/> and deduped by Date. Carries the
/// source + fetch instant for provenance of the <i>reference dataset</i>; this provenance is deliberately
/// DISCONNECTED from the evidence provenance chain — price is not evidence.
/// </summary>
public sealed record PriceHistory(
    string Ticker,
    string Source,                  // e.g. "yahoo-chart-v8"
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<PriceBar> Bars);
