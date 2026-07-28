using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 140's causality guarantee: a score at D is judged ONLY against price strictly after D. These tests
/// poison every bar at-or-before D with values that would change the answer if any of them were read.
/// <para>
/// Spec 152 added the exit-coverage rule, so every call now states the tolerance it will accept. The tolerance
/// each test passes is chosen to keep that test asserting what it was written to assert: the ones about the
/// horizon bound, bar ordering and the price fallback pass a tolerance generous enough for their own fixtures,
/// so they cannot start failing for a partial-window reason instead of the reason they exist for.
/// </para>
/// </summary>
public sealed class ForwardReturnTests
{
    private static PriceBar Bar(int year, int month, int day, decimal adjClose, decimal? close = null) =>
        new(
            new DateOnly(year, month, day),
            Open: adjClose,
            High: adjClose,
            Low: adjClose,
            Close: close ?? adjClose,
            AdjClose: adjClose,
            Volume: 1000);

    [Fact]
    public void TryCompute_UsesOnlyBarsStrictlyAfterAsOf()
    {
        var asOf = new DateOnly(2026, 1, 5);

        // Entry 100 → exit 110 over (2026-01-05, 2026-01-26] ⇒ exactly +10%.
        PriceBar[] Series(decimal poisonEarly, decimal poisonOnAsOf) =>
        [
            Bar(2026, 1, 1, poisonEarly),
            Bar(2026, 1, 5, poisonOnAsOf),   // exactly D — a lookahead-free metric must never read this
            Bar(2026, 1, 6, 100m),
            Bar(2026, 1, 20, 110m),
        ];

        // The exit bar is 6 days short of D+21, so this fixture needs a tolerance of at least 6 to stay a
        // question about the ENTRY rule rather than about coverage.
        var a = ForwardReturn.TryCompute(
            Series(1_000_000m, 999_999m), asOf, horizonDays: 21, exitToleranceDays: 6);
        var b = ForwardReturn.TryCompute(
            Series(0.0001m, 0.0002m), asOf, horizonDays: 21, exitToleranceDays: 6);

        Assert.True(a.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 6), a.EntryDate);
        Assert.Equal(new DateOnly(2026, 1, 20), a.ExitDate);
        Assert.Equal(0.10, a.Value, 12);

        // Two wildly different poison sets, byte-identical answer: the at-or-before bars are unreachable.
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryCompute_TheEntryRuleIsUnchangedEvenForAPartialWindow()
    {
        // Spec 152 tightened the EXIT rule only. The at-or-before bars must remain unreachable in every branch,
        // including the new one — a PartialWindow classification computed from a poisoned bar at D would be a
        // look-ahead regression hiding inside a drop reason.
        var asOf = new DateOnly(2026, 1, 5);

        PriceBar[] Series(decimal poisonEarly, decimal poisonOnAsOf) =>
        [
            Bar(2026, 1, 1, poisonEarly),
            Bar(2026, 1, 5, poisonOnAsOf),   // exactly D
            Bar(2026, 1, 6, 100m),
            Bar(2026, 1, 8, 110m),           // 13 days short of D+21 ⇒ partial at tolerance 4
        ];

        var a = ForwardReturn.TryCompute(
            Series(1_000_000m, 999_999m), asOf, horizonDays: 21, exitToleranceDays: 4);
        var b = ForwardReturn.TryCompute(
            Series(0.0001m, 0.0002m), asOf, horizonDays: 21, exitToleranceDays: 4);

        Assert.False(a.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, a.Reason);
        Assert.Equal(a, b);

        // …and the entry bar the coverage rule sat in front of is still the first bar STRICTLY AFTER D: widen
        // the tolerance past the fixture's 18-day shortfall (01-26 − 01-08) and the same fixtures produce +10%
        // from 01-06 (100 → 110), not something computed from the poisoned bar at D. Two wildly different poison
        // sets, one answer, on both sides of the new branch.
        var widenedA = ForwardReturn.TryCompute(
            Series(1_000_000m, 999_999m), asOf, horizonDays: 21, exitToleranceDays: 18);
        var widenedB = ForwardReturn.TryCompute(
            Series(0.0001m, 0.0002m), asOf, horizonDays: 21, exitToleranceDays: 18);

        Assert.True(widenedA.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 6), widenedA.EntryDate);
        Assert.Equal(0.10, widenedA.Value, 12);
        Assert.Equal(widenedA, widenedB);
    }

    [Fact]
    public void TryCompute_HorizonBoundIsInclusiveAtDPlusH_AndExclusiveBeyondIt()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars =
        [
            Bar(2026, 1, 6, 100m),
            Bar(2026, 1, 15, 110m),   // == D + 10, inside a 10-day horizon
            Bar(2026, 1, 16, 900m),   // == D + 11, outside it
        ];

        // Tolerance 0 for the 10-day horizon (the exit bar lands exactly on the bound) and 1 for the 11-day one
        // (its latest admissible bar is D+11 = 01-16, which is on the bound, so 0 would also do — 1 keeps the
        // assertion about the BOUND rather than about coverage if the fixture is ever nudged).
        var inside = ForwardReturn.TryCompute(bars, asOf, horizonDays: 10, exitToleranceDays: 0);
        Assert.True(inside.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 15), inside.ExitDate);
        Assert.Equal(0.10, inside.Value, 12);

        var wider = ForwardReturn.TryCompute(bars, asOf, horizonDays: 11, exitToleranceDays: 1);
        Assert.True(wider.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 16), wider.ExitDate);
        Assert.Equal(8.0, wider.Value, 12);
    }

    [Fact]
    public void TryCompute_DropsWhenNoBarFollowsAsOfWithinTheHorizon()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] onlyBefore = [Bar(2026, 1, 1, 100m), Bar(2026, 1, 5, 200m)];

        var none = ForwardReturn.TryCompute(onlyBefore, asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.False(none.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, none.Reason);
        Assert.Null(none.EntryDate);

        // A bar exists after D but beyond the horizon ⇒ still no observation inside (D, D+h].
        PriceBar[] beyondHorizon = [Bar(2026, 1, 1, 100m), Bar(2026, 3, 1, 200m)];
        var beyond = ForwardReturn.TryCompute(beyondHorizon, asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.False(beyond.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, beyond.Reason);
    }

    [Fact]
    public void TryCompute_DistinguishesAnEmptyWindowFromAPartialOne()
    {
        // Two different facts, two different reasons: "we had no price at all in (D, D+h]" is NOT the same as
        // "we had price, but it stopped well before D+h". Conflating them is what spec 152 exists to undo.
        var asOf = new DateOnly(2026, 1, 5);

        var empty = ForwardReturn.TryCompute(
            [Bar(2026, 1, 1, 100m)], asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, empty.Reason);

        var partial = ForwardReturn.TryCompute(
            [Bar(2026, 1, 6, 100m), Bar(2026, 1, 9, 110m)], asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, partial.Reason);
    }

    [Fact]
    public void TryCompute_DropsAnExitBarThatFallsShortOfTheHorizonByMoreThanTheTolerance()
    {
        // 4 days of price inside a 21-day window: the pre-152 behaviour returned this as a 21-day forward
        // return of +10%.
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars =
        [
            Bar(2026, 1, 6, 100m),
            Bar(2026, 1, 7, 105m),
            Bar(2026, 1, 9, 110m),   // 17 days short of D+21
        ];

        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 4);

        Assert.False(result.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, result.Reason);
        Assert.Equal(0.0, result.Value);
        Assert.Null(result.EntryDate);
        Assert.Null(result.ExitDate);
    }

    [Fact]
    public void TryCompute_TheToleranceBoundaryIsInclusive_AndOneDayFurtherIsPartial()
    {
        // D + 21 = 2026-01-26. At tolerance 4 the earliest acceptable exit is 2026-01-22 (a shortfall of
        // exactly 4); 2026-01-21 (a shortfall of 5) is one day too far.
        var asOf = new DateOnly(2026, 1, 5);

        PriceBar[] AtExactlyTheTolerance() => [Bar(2026, 1, 6, 100m), Bar(2026, 1, 22, 110m)];
        PriceBar[] OneDayBeyondIt() => [Bar(2026, 1, 6, 100m), Bar(2026, 1, 21, 110m)];

        var onTheBoundary = ForwardReturn.TryCompute(
            AtExactlyTheTolerance(), asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.True(onTheBoundary.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.None, onTheBoundary.Reason);
        Assert.Equal(new DateOnly(2026, 1, 22), onTheBoundary.ExitDate);
        Assert.Equal(0.10, onTheBoundary.Value, 12);

        var justPastIt = ForwardReturn.TryCompute(
            OneDayBeyondIt(), asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.False(justPastIt.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, justPastIt.Reason);

        // …and the same shortfall of 5 IS accepted once the tolerance is widened to 5, so the boundary moves
        // with the knob rather than being baked in.
        var widened = ForwardReturn.TryCompute(
            OneDayBeyondIt(), asOf, horizonDays: 21, exitToleranceDays: 5);
        Assert.True(widened.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 21), widened.ExitDate);
    }

    [Fact]
    public void TryCompute_WithZeroToleranceRequiresTheExitBarExactlyOnTheBound()
    {
        var asOf = new DateOnly(2026, 1, 5);

        var onTheBound = ForwardReturn.TryCompute(
            [Bar(2026, 1, 6, 100m), Bar(2026, 1, 26, 110m)],   // == D + 21
            asOf,
            horizonDays: 21,
            exitToleranceDays: 0);
        Assert.True(onTheBound.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 26), onTheBound.ExitDate);

        var oneDayShort = ForwardReturn.TryCompute(
            [Bar(2026, 1, 6, 100m), Bar(2026, 1, 25, 110m)],   // == D + 20
            asOf,
            horizonDays: 21,
            exitToleranceDays: 0);
        Assert.False(oneDayShort.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, oneDayShort.Reason);
    }

    [Fact]
    public void TryCompute_ReportsAPartialWindowAheadOfANonPositiveEntryPrice()
    {
        // Both defects at once. Window coverage is the property of the data the CALLER supplied and the more
        // informative classification, so the documented precedence is PartialWindow.
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars = [Bar(2026, 1, 6, 0m, close: 0m), Bar(2026, 1, 9, 120m)];

        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 4);

        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, result.Reason);
    }

    [Fact]
    public void TryCompute_DropsWhenEntryAndExitWouldBeTheSameBar()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars = [Bar(2026, 1, 1, 100m), Bar(2026, 1, 6, 150m)];

        // A generous tolerance so the single-bar rule is what fires, not the coverage rule — and this is also
        // the documented precedence: SingleForwardBar is checked BEFORE PartialWindow.
        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 20);

        Assert.False(result.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.SingleForwardBar, result.Reason);

        // The precedence, asserted: the same lone bar under a tight tolerance is STILL SingleForwardBar.
        var tight = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 4);
        Assert.Equal(ForwardReturnUnavailableReason.SingleForwardBar, tight.Reason);
    }

    [Fact]
    public void TryCompute_FallsBackToCloseOnlyWhenAdjustedCloseIsUnusable()
    {
        var asOf = new DateOnly(2026, 1, 5);

        // AdjClose 0 on both ends ⇒ Close (100 → 120) is used. Tolerance 6 covers the 01-20 exit bar so this
        // stays a question about WHICH price is read.
        PriceBar[] withFallback = [Bar(2026, 1, 6, 0m, close: 100m), Bar(2026, 1, 20, 0m, close: 120m)];
        var fallback = ForwardReturn.TryCompute(withFallback, asOf, horizonDays: 21, exitToleranceDays: 6);
        Assert.True(fallback.IsDefined);
        Assert.Equal(0.20, fallback.Value, 12);

        // AdjClose present ⇒ it wins, and the divergent Close is ignored.
        PriceBar[] adjustedWins = [Bar(2026, 1, 6, 100m, close: 1m), Bar(2026, 1, 20, 110m, close: 999m)];
        var adjusted = ForwardReturn.TryCompute(adjustedWins, asOf, horizonDays: 21, exitToleranceDays: 6);
        Assert.True(adjusted.IsDefined);
        Assert.Equal(0.10, adjusted.Value, 12);
    }

    [Fact]
    public void TryCompute_DropsWhenTheEntryPriceIsNotPositive()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars = [Bar(2026, 1, 6, 0m, close: 0m), Bar(2026, 1, 20, 120m)];

        // Tolerance 6 admits the 01-20 exit bar, so the window is complete and the PRICE is the only defect.
        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 6);

        Assert.False(result.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NonPositiveEntryPrice, result.Reason);
    }

    [Fact]
    public void TryCompute_IsIndependentOfBarOrdering()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] ascending = [Bar(2026, 1, 4, 5m), Bar(2026, 1, 6, 100m), Bar(2026, 1, 20, 110m)];
        PriceBar[] shuffled = [Bar(2026, 1, 20, 110m), Bar(2026, 1, 4, 5m), Bar(2026, 1, 6, 100m)];

        // Tolerance 6 keeps both sides DEFINED, so this compares the selected entry/exit rather than two
        // identically-empty drop results.
        var a = ForwardReturn.TryCompute(ascending, asOf, 21, exitToleranceDays: 6);
        var b = ForwardReturn.TryCompute(shuffled, asOf, 21, exitToleranceDays: 6);

        Assert.True(a.IsDefined);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryCompute_RejectsANonPositiveHorizon()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForwardReturn.TryCompute([], new DateOnly(2026, 1, 5), horizonDays: 0, exitToleranceDays: 0));
    }

    [Fact]
    public void TryCompute_RejectsANegativeTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForwardReturn.TryCompute([], new DateOnly(2026, 1, 5), horizonDays: 21, exitToleranceDays: -1));
    }

    [Fact]
    public void TryCompute_RejectsAToleranceThatWouldMakeTheCoverageCheckVacuous()
    {
        var asOf = new DateOnly(2026, 1, 5);

        // A tolerance AT the horizon puts the minimum exit date on `asOf` itself, so every admitted bar
        // qualifies and nothing could ever be PartialWindow again. Refused at the boundary rather than left to
        // the one production caller that resolves both numbers together.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForwardReturn.TryCompute([], asOf, horizonDays: 21, exitToleranceDays: 21));

        // And beyond it, where the minimum exit date would fall BEFORE the as-of date.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForwardReturn.TryCompute([], asOf, horizonDays: 21, exitToleranceDays: 22));

        // The largest legal tolerance is h-1, and it still computes: the boundary is strict, not off by one.
        // h-1 puts the minimum exit date one day after D, so with the two-distinct-bar rule the tightest
        // window it can admit is entry at D+1 and exit at D+2.
        PriceBar[] bars = [Bar(2026, 1, 6, 100m), Bar(2026, 1, 7, 110m)];
        var atTheLimit = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21, exitToleranceDays: 20);
        Assert.True(atTheLimit.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.None, atTheLimit.Reason);
    }
}
