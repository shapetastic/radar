namespace Radar.Application.Prices;

/// <summary>
/// Options for <see cref="PriceHistoryAcquirer"/>: the polite inter-request pace between tickers and the
/// <see cref="Source"/> label stamped on each persisted <see cref="PriceHistory"/> for reference-dataset
/// provenance (AD-14). Not a scoring input — these tune only the acquisition of the reference dataset.
/// </summary>
public sealed class PriceAcquisitionOptions
{
    /// <summary>Pause between successive per-ticker reads. Defaults to 1s (the endpoint is not per-IP throttled).</summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The reference-dataset source label recorded on each <see cref="PriceHistory.Source"/>.</summary>
    public string Source { get; init; } = "yahoo-chart-v8";
}
