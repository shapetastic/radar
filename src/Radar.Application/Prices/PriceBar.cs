namespace Radar.Application.Prices;

/// <summary>
/// One daily price bar — raw factual market data (UTC trading date, decimal OHLC + adjusted close + volume).
/// REFERENCE / VALIDATION data only: never evidence, never a signal, never a scoring input (AD-14). This type
/// deliberately lives in <see cref="Radar.Application.Prices"/>, NOT in <c>Radar.Domain</c>, so price is not a
/// domain aggregate and cannot be mistaken for part of the evidence → signal → score model.
/// </summary>
public sealed record PriceBar(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjClose,
    long Volume);
