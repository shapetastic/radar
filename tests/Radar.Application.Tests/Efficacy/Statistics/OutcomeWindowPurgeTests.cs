using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Statistics;

public sealed class OutcomeWindowPurgeTests
{
    private static OutcomeWindowBlock Nominal(DateOnly date, int horizonDays) =>
        new(date, date, date.AddDays(horizonDays));

    [Fact]
    public void Purge_DenseDailyDates_AdmitsGreedilyAscendingAtLeastAHorizonApart()
    {
        var first = new DateOnly(2026, 1, 1);
        var candidates = Enumerable.Range(0, 60)
            .Select(d => Nominal(first.AddDays(d), 21))
            .ToList();

        var result = OutcomeWindowPurge.Purge(candidates);

        // Earliest-first: day 0, then the first date whose window starts at/after day 0's end, etc.
        Assert.Equal(
            [first, first.AddDays(21), first.AddDays(42)],
            result.Admitted.Select(a => a.Date).ToList());

        // Every skipped date is accounted for — 60 candidates, 3 admitted, 57 skipped.
        Assert.Equal(57, result.Skipped.Count);
        Assert.Equal(candidates.Count, result.Admitted.Count + result.Skipped.Count);

        // Admitted dates are at least the horizon apart.
        for (var i = 1; i < result.Admitted.Count; i++)
        {
            Assert.True(result.Admitted[i].Date.DayNumber - result.Admitted[i - 1].Date.DayNumber >= 21);
        }
    }

    [Fact]
    public void Purge_AdjacentWindowsMayTouchButNotOverlap()
    {
        var first = new DateOnly(2026, 1, 1);

        // (d, d+21] and (d+21, d+42] touch at the boundary: open-left/closed-right means no shared day.
        var touching = OutcomeWindowPurge.Purge(
            [Nominal(first, 21), Nominal(first.AddDays(21), 21)]);
        Assert.Equal(2, touching.Admitted.Count);
        Assert.Empty(touching.Skipped);

        var overlapping = OutcomeWindowPurge.Purge(
            [Nominal(first, 21), Nominal(first.AddDays(20), 21)]);
        Assert.Single(overlapping.Admitted);
        Assert.Single(overlapping.Skipped);
        Assert.Equal(first.AddDays(20), overlapping.Skipped[0].Date);
    }

    [Fact]
    public void Purge_SupportsCallerSuppliedExactEndpoints()
    {
        // Outcome-agnostic: a non-price outcome supplies its own endpoints, which need not be date+horizon.
        var result = OutcomeWindowPurge.Purge(
        [
            new OutcomeWindowBlock(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 10)),
            new OutcomeWindowBlock(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6), new DateOnly(2026, 1, 12)),
            new OutcomeWindowBlock(new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 20)),
        ]);

        Assert.Equal(
            [new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 9)],
            result.Admitted.Select(a => a.Date).ToList());
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Purge_RejectsUnsortedInputAsADefect()
    {
        var first = new DateOnly(2026, 1, 1);
        Assert.Throws<ArgumentException>(() => OutcomeWindowPurge.Purge(
            [Nominal(first.AddDays(5), 21), Nominal(first, 21)]));
    }

    [Fact]
    public void Purge_RejectsAnInvertedInterval()
    {
        Assert.Throws<ArgumentException>(() => OutcomeWindowPurge.Purge(
            [new OutcomeWindowBlock(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 1))]));
    }
}
