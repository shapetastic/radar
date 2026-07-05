namespace Radar.Application.Prices;

/// <summary>
/// A ticker's daily price history as persisted to <c>data/prices/{ticker}.json</c> — reference / validation
/// data (AD-14). Bars are ordered ascending by <see cref="PriceBar.Date"/> and deduped by date. Carries the
/// <see cref="Source"/> + <see cref="RetrievedAtUtc"/> for provenance of the <i>reference dataset</i>; this
/// provenance is deliberately DISCONNECTED from the evidence provenance chain — price is not evidence, is never
/// extracted into a signal, and is never an input to scoring.
/// </summary>
public sealed record PriceHistory(
    string Ticker,
    string Source,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<PriceBar> Bars);
