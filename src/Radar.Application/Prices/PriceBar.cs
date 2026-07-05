namespace Radar.Application.Prices;

/// <summary>
/// One daily price bar — raw factual market data (UTC trading date, decimal OHLC + adjusted close +
/// volume). REFERENCE / VALIDATION data only: never evidence, never a signal, never a scoring input
/// (AD-14). Deliberately an Application-layer record, not a Domain aggregate, to reinforce that price is
/// not part of the evidence→signal→score domain model.
/// </summary>
public sealed record PriceBar(
    DateOnly Date,          // UTC trading date (from FromUnixTimeSeconds(ts).UtcDateTime)
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjClose,
    long Volume);
