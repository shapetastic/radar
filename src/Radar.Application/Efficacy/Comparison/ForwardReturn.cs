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

    /// <summary>
    /// A forward pair EXISTS, but the exit bar falls short of the horizon end by more than the caller's
    /// tolerance — "we had some price, but not the horizon you asked for" (spec 152). Distinct from
    /// <see cref="NoForwardBar"/> on purpose: a four-day return inside a twenty-one-day window is not a
    /// missing observation, it is a mislabelled one, and pooling it with complete windows is what made every
    /// previously published leaderboard number measure something other than its stated horizon.
    /// <para>
    /// Appended LAST so no existing member's value moves.
    /// </para>
    /// </summary>
    PartialWindow,
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
/// before D, so the property survives future edits rather than depending on someone remembering it. Spec 152
/// tightened the EXIT rule only; the entry rule above is untouched and still the single admission filter.
/// </para>
/// <para>
/// <b>The window must actually reach the horizon (spec 152).</b> Selecting the latest bar inside
/// <c>(D, D+h]</c> says nothing about how far that bar got. Four days of price inside a twenty-one-day window
/// used to yield a four-day return reported as a twenty-one-day forward return. So the exit bar must satisfy
/// <c>exit.Date &gt;= D.AddDays(h - exitToleranceDays)</c>; otherwise the observation is
/// <see cref="ForwardReturnUnavailableReason.PartialWindow"/> and the caller must exclude rather than relabel
/// it. The tolerance is REQUIRED, with no default — a silent default is exactly how the mislabelled number
/// slipped through the first time, so every call site states the coverage it will accept.
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
    /// <param name="bars">The company's price series; only bars strictly after <paramref name="asOf"/> are read.</param>
    /// <param name="asOf">D — the instant the score being judged could see up to.</param>
    /// <param name="horizonDays">h, in calendar days: the forward window is <c>(D, D+h]</c>.</param>
    /// <param name="exitToleranceDays">
    /// How many calendar days short of <c>D+h</c> the latest bar may fall and still count as a full-horizon
    /// window (weekends and holidays mean the last bar is rarely on the bound itself). REQUIRED — there is
    /// deliberately no default, so no caller can accidentally accept a four-day return as an h-day one.
    /// <para>
    /// Only non-negativity is checked here. The composite invariant
    /// <c>exitToleranceDays &lt; horizonDays</c> is enforced by <see cref="StrategyComparisonOptions"/>, the
    /// only production call path, because that is where both numbers are resolved from config together. A
    /// direct caller passing a tolerance at or above the horizon gets a VACUOUS check — the minimum exit date
    /// falls at or before D, so every bar in the window qualifies and nothing is ever classified
    /// <see cref="ForwardReturnUnavailableReason.PartialWindow"/>.
    /// </para>
    /// </param>
    public static ForwardReturnResult TryCompute(
        IReadOnlyList<PriceBar> bars, DateOnly asOf, int horizonDays, int exitToleranceDays)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonDays, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(exitToleranceDays);

        var exitBound = asOf.AddDays(horizonDays);

        // The earliest exit date that still counts as covering the horizon. Computed from `asOf` once, so the
        // bound and the tolerance cannot drift apart in two expressions.
        var minimumExitDate = asOf.AddDays(horizonDays - exitToleranceDays);

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

        // BEFORE the price check, deliberately. Window coverage is a property of the data the caller supplied
        // — "you asked for h days and this series only reaches part way" — and is the more informative
        // classification of the two, so an observation that is both partial AND price-defective is reported as
        // PartialWindow. It is also the cheaper truth: fixing the price of a bar that is not there is not a fix.
        if (exit.Date < minimumExitDate)
        {
            return ForwardReturnResult.Unavailable(ForwardReturnUnavailableReason.PartialWindow);
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
