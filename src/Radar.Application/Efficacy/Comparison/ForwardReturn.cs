using Radar.Application.Prices;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>Why a forward return could not be computed for one (company, as-of) observation.</summary>
public enum ForwardReturnUnavailableReason
{
    /// <summary>The return WAS computed; not a drop.</summary>
    None = 0,

    /// <summary>No price bar exists strictly after the as-of date and within the horizon.</summary>
    NoForwardBar,

    /// <summary>Exactly one bar exists in the forward window, so entry and exit are the same bar.</summary>
    SingleForwardBar,

    /// <summary>The entry bar's price is not positive, so a relative return is undefined.</summary>
    NonPositiveEntryPrice,
}

/// <summary>The outcome of a forward-return computation: a value, or a named reason it does not exist.</summary>
public sealed record ForwardReturnResult(
    bool IsDefined,
    double Value,
    ForwardReturnUnavailableReason Reason,
    DateOnly? EntryDate,
    DateOnly? ExitDate)
{
    public static ForwardReturnResult Unavailable(ForwardReturnUnavailableReason reason) =>
        new(IsDefined: false, Value: 0.0, Reason: reason, EntryDate: null, ExitDate: null);
}

/// <summary>
/// The causality primitive of spec 140: price movement over the OPEN-LEFT interval <c>(D, D+h]</c> for a score
/// observed at <c>D</c>.
/// <para>
/// <b>No look-ahead, structurally.</b> This is the mirror image of spec 136's hindsight leak, on the price
/// side: relating a score at D to a price at-or-before D would let the price the score was already contemporary
/// with masquerade as a prediction. The guarantee is not a convention here — the ONLY place this type touches
/// the caller's bar list is a single admission filter whose predicate is <c>bar.Date &gt; asOf</c>, and every
/// later step reads exclusively from the resulting window. There is no code path that can reach a bar at or
/// before D, so the property survives future edits rather than depending on someone remembering it.
/// </para>
/// <para>
/// <b>Which price.</b> Adjusted close, matching what the per-company efficacy SVG already plots as "price (adj
/// close)" — a split or dividend inside the horizon would otherwise show up as a fabricated return. The price
/// reader never fabricates <c>AdjClose</c> (it skips a bar with any null field), so the non-positive fallback
/// to <c>Close</c> exists only for a pathological/zero-adjusted bar; when neither is positive the observation
/// is dropped rather than divided by.
/// </para>
/// <para>
/// Pure and deterministic (AD-3): no clock, no randomness, no sort dependency — entry/exit are chosen by a
/// strict date comparison scan, so a duplicated date resolves to the first occurrence in the caller's already
/// deterministic list.
/// </para>
/// </summary>
public static class ForwardReturn
{
    public static ForwardReturnResult TryCompute(
        IReadOnlyList<PriceBar> bars, DateOnly asOf, int horizonDays)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonDays, 1);

        var exitBound = asOf.AddDays(horizonDays);

        // THE single admission filter. `bar.Date > asOf` is the whole no-look-ahead guarantee; nothing below
        // this loop looks at `bars` again.
        PriceBar? entry = null;
        PriceBar? exit = null;
        foreach (var bar in bars)
        {
            if (bar.Date <= asOf || bar.Date > exitBound)
            {
                continue;
            }

            if (entry is null || bar.Date < entry.Date)
            {
                entry = bar;
            }

            if (exit is null || bar.Date > exit.Date)
            {
                exit = bar;
            }
        }

        if (entry is null || exit is null)
        {
            return ForwardReturnResult.Unavailable(ForwardReturnUnavailableReason.NoForwardBar);
        }

        if (entry.Date == exit.Date)
        {
            return ForwardReturnResult.Unavailable(ForwardReturnUnavailableReason.SingleForwardBar);
        }

        var entryPrice = Price(entry);
        var exitPrice = Price(exit);
        if (entryPrice <= 0m)
        {
            return ForwardReturnResult.Unavailable(ForwardReturnUnavailableReason.NonPositiveEntryPrice);
        }

        return new ForwardReturnResult(
            IsDefined: true,
            Value: (double)((exitPrice - entryPrice) / entryPrice),
            Reason: ForwardReturnUnavailableReason.None,
            EntryDate: entry.Date,
            ExitDate: exit.Date);
    }

    /// <summary>Adjusted close, falling back to close only when the adjusted value is not usable.</summary>
    private static decimal Price(PriceBar bar) => bar.AdjClose > 0m ? bar.AdjClose : bar.Close;
}
