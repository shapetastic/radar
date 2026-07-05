namespace Radar.Application.Prices;

/// <summary>
/// Options for <see cref="PriceHistoryAcquirer"/>. Only the polite inter-request pace is tunable here; the
/// source/range live behind the reader. Kept provider-neutral (no Yahoo/HTTP specifics leak into Application).
/// </summary>
public sealed class PriceHistoryAcquirerOptions
{
    /// <summary>Polite pause between successive per-ticker reads. Defaults to 1 second.</summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(1);
}
