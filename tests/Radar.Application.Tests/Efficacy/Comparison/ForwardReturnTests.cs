using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 140's causality guarantee: a score at D is judged ONLY against price strictly after D. These tests
/// poison every bar at-or-before D with values that would change the answer if any of them were read.
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

        var a = ForwardReturn.TryCompute(Series(1_000_000m, 999_999m), asOf, horizonDays: 21);
        var b = ForwardReturn.TryCompute(Series(0.0001m, 0.0002m), asOf, horizonDays: 21);

        Assert.True(a.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 6), a.EntryDate);
        Assert.Equal(new DateOnly(2026, 1, 20), a.ExitDate);
        Assert.Equal(0.10, a.Value, 12);

        // Two wildly different poison sets, byte-identical answer: the at-or-before bars are unreachable.
        Assert.Equal(a, b);
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

        var inside = ForwardReturn.TryCompute(bars, asOf, horizonDays: 10);
        Assert.True(inside.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 15), inside.ExitDate);
        Assert.Equal(0.10, inside.Value, 12);

        var wider = ForwardReturn.TryCompute(bars, asOf, horizonDays: 11);
        Assert.True(wider.IsDefined);
        Assert.Equal(new DateOnly(2026, 1, 16), wider.ExitDate);
        Assert.Equal(8.0, wider.Value, 12);
    }

    [Fact]
    public void TryCompute_DropsWhenNoBarFollowsAsOfWithinTheHorizon()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] onlyBefore = [Bar(2026, 1, 1, 100m), Bar(2026, 1, 5, 200m)];

        var none = ForwardReturn.TryCompute(onlyBefore, asOf, horizonDays: 21);
        Assert.False(none.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, none.Reason);
        Assert.Null(none.EntryDate);

        // A bar exists after D but beyond the horizon ⇒ still no observation inside (D, D+h].
        PriceBar[] beyondHorizon = [Bar(2026, 1, 1, 100m), Bar(2026, 3, 1, 200m)];
        var beyond = ForwardReturn.TryCompute(beyondHorizon, asOf, horizonDays: 21);
        Assert.False(beyond.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, beyond.Reason);
    }

    [Fact]
    public void TryCompute_DropsWhenEntryAndExitWouldBeTheSameBar()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars = [Bar(2026, 1, 1, 100m), Bar(2026, 1, 6, 150m)];

        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21);

        Assert.False(result.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.SingleForwardBar, result.Reason);
    }

    [Fact]
    public void TryCompute_FallsBackToCloseOnlyWhenAdjustedCloseIsUnusable()
    {
        var asOf = new DateOnly(2026, 1, 5);

        // AdjClose 0 on both ends ⇒ Close (100 → 120) is used.
        PriceBar[] withFallback = [Bar(2026, 1, 6, 0m, close: 100m), Bar(2026, 1, 20, 0m, close: 120m)];
        var fallback = ForwardReturn.TryCompute(withFallback, asOf, horizonDays: 21);
        Assert.True(fallback.IsDefined);
        Assert.Equal(0.20, fallback.Value, 12);

        // AdjClose present ⇒ it wins, and the divergent Close is ignored.
        PriceBar[] adjustedWins = [Bar(2026, 1, 6, 100m, close: 1m), Bar(2026, 1, 20, 110m, close: 999m)];
        var adjusted = ForwardReturn.TryCompute(adjustedWins, asOf, horizonDays: 21);
        Assert.True(adjusted.IsDefined);
        Assert.Equal(0.10, adjusted.Value, 12);
    }

    [Fact]
    public void TryCompute_DropsWhenTheEntryPriceIsNotPositive()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] bars = [Bar(2026, 1, 6, 0m, close: 0m), Bar(2026, 1, 20, 120m)];

        var result = ForwardReturn.TryCompute(bars, asOf, horizonDays: 21);

        Assert.False(result.IsDefined);
        Assert.Equal(ForwardReturnUnavailableReason.NonPositiveEntryPrice, result.Reason);
    }

    [Fact]
    public void TryCompute_IsIndependentOfBarOrdering()
    {
        var asOf = new DateOnly(2026, 1, 5);
        PriceBar[] ascending = [Bar(2026, 1, 4, 5m), Bar(2026, 1, 6, 100m), Bar(2026, 1, 20, 110m)];
        PriceBar[] shuffled = [Bar(2026, 1, 20, 110m), Bar(2026, 1, 4, 5m), Bar(2026, 1, 6, 100m)];

        Assert.Equal(
            ForwardReturn.TryCompute(ascending, asOf, 21),
            ForwardReturn.TryCompute(shuffled, asOf, 21));
    }

    [Fact]
    public void TryCompute_RejectsANonPositiveHorizon()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ForwardReturn.TryCompute([], new DateOnly(2026, 1, 5), horizonDays: 0));
    }
}
