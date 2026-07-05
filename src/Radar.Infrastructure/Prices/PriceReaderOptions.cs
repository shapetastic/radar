namespace Radar.Infrastructure.Prices;

/// <summary>
/// Options for <see cref="HttpPriceHistoryReader"/>: the Yahoo <c>chart</c> range window (validated against the
/// API's <c>validRanges</c>) and the endpoint template (<c>{0}</c> = URL-encoded ticker, <c>{1}</c> = range).
/// <c>internal</c> — Infrastructure-only, visible to the test project via
/// <c>InternalsVisibleTo</c> (like the SEC/News reader internals). The Worker never constructs this directly;
/// the <c>AddHttpPriceHistoryReader(range)</c> DI helper builds and validates it from a public <c>string</c>.
/// </summary>
internal sealed class PriceReaderOptions
{
    /// <summary>The Yahoo chart endpoint's accepted <c>range</c> tokens.</summary>
    public static readonly IReadOnlySet<string> ValidRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1d", "5d", "1mo", "3mo", "6mo", "1y", "2y", "5y", "10y", "ytd", "max",
    };

    /// <summary>The range window requested from the chart endpoint. Default <c>1y</c>.</summary>
    public string Range { get; init; } = "1y";

    /// <summary>The GET template: <c>{0}</c> is the URL-encoded ticker, <c>{1}</c> is the range.</summary>
    public string EndpointTemplate { get; init; } =
        "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range={1}";
}
