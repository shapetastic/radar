using System.Collections.Frozen;

namespace Radar.Infrastructure.Prices;

/// <summary>
/// Options for <see cref="HttpPriceHistoryReader"/>: the daily <see cref="Range"/> window (validated against
/// the Yahoo <c>chart</c> endpoint's known <c>validRanges</c>) and an optional endpoint template override
/// (a <c>string.Format</c> template with <c>{0}</c> = URL-encoded ticker, <c>{1}</c> = range) for testing.
/// <c>internal</c> so the Yahoo specifics stay confined to Infrastructure (AD-5); the Infrastructure test
/// project sees it via the existing <c>InternalsVisibleTo</c>.
/// </summary>
internal sealed class PriceReaderOptions
{
    /// <summary>The Yahoo <c>chart</c> endpoint's documented <c>validRanges</c> set.</summary>
    public static readonly FrozenSet<string> ValidRanges = new[]
    {
        "1d", "5d", "1mo", "3mo", "6mo", "1y", "2y", "5y", "10y", "ytd", "max",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The daily-bar range window. Defaults to "1y". Validated against <see cref="ValidRanges"/>.</summary>
    public string Range { get; init; } = "1y";

    /// <summary>
    /// Optional endpoint template override (<c>{0}</c> = URL-encoded ticker, <c>{1}</c> = range). Null uses
    /// the verified keyless Yahoo <c>chart</c> endpoint. Only used to point tests/experiments elsewhere.
    /// </summary>
    public string? EndpointTemplate { get; init; }

    /// <summary>
    /// Fails fast (throws <see cref="InvalidOperationException"/>) when <see cref="Range"/> is not one of the
    /// Yahoo <c>validRanges</c> — an unknown range would otherwise silently return nothing.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Range) || !ValidRanges.Contains(Range.Trim()))
        {
            throw new InvalidOperationException(
                $"Price reader Range '{Range}' is not a valid Yahoo chart range; configure Radar:Prices:Range "
                    + "to one of: " + string.Join(", ", ValidRanges) + " (default 1y).");
        }
    }
}
