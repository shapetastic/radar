using System.Globalization;

namespace Radar.Infrastructure.Prices;

/// <summary>
/// Infrastructure-internal configuration for <see cref="HttpPriceHistoryReader"/>: the daily-bar
/// <see cref="Range"/> (validated against the Yahoo chart endpoint's known <c>validRanges</c> set) and the
/// endpoint template. <c>internal</c> so it stays behind the Application seam (AD-5); the Infrastructure test
/// project sees it via the existing <c>InternalsVisibleTo</c>.
/// </summary>
internal sealed class PriceReaderOptions
{
    /// <summary>
    /// The Yahoo chart <c>validRanges</c> set (verified 2026-07-04). A range outside this set is rejected at
    /// registration so a typo fails fast rather than silently returning an empty series.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1d", "5d", "1mo", "3mo", "6mo", "1y", "2y", "5y", "10y", "ytd", "max",
    };

    /// <summary>The daily-bar window (one of <see cref="ValidRanges"/>). Defaults to <c>"1y"</c>.</summary>
    public string Range { get; init; } = "1y";

    /// <summary>
    /// The endpoint template — <c>{0}</c> is the URL-encoded ticker, <c>{1}</c> is the range. Defaults to the
    /// verified keyless Yahoo chart v8 endpoint. Overridable for tests; production never changes it.
    /// </summary>
    public string EndpointTemplate { get; init; } =
        "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range={1}";

    /// <summary>Throws <see cref="InvalidOperationException"/> when <see cref="Range"/> is not a known valid range.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Range) || !ValidRanges.Contains(Range.Trim()))
        {
            throw new InvalidOperationException(
                $"Radar:Prices:Range '{Range}' is not a valid Yahoo chart range; configure one of "
                    + $"{string.Join(", ", ValidRanges)} (default 1y).");
        }
    }

    /// <summary>Builds the request URL for a ticker: URL-encodes the ticker, formats the template invariantly.</summary>
    public string BuildRequestUrl(string ticker) =>
        string.Format(
            CultureInfo.InvariantCulture,
            EndpointTemplate,
            Uri.EscapeDataString(ticker.Trim()),
            Range.Trim());
}
