namespace Radar.Application.Prices;

/// <summary>
/// Configuration for the price-history acquisition step (AD-14). Provider-neutral (no evidence/scoring type):
/// carries only the polite inter-request pace between per-ticker reads.
/// </summary>
public sealed class PriceAcquisitionOptions
{
    /// <summary>
    /// Pause between successive per-ticker reads (mirrors the News collector's polite pacing). Defaults to 1s.
    /// Must not be negative.
    /// </summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(1);
}
