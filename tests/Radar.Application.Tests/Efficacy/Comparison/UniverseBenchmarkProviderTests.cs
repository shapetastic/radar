using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The provider seam: one lazy load, price series resolved through the artifact's own priceSeriesKey via the
/// EXISTING price store (no second price source), a missing member series staying in the denominator, and a
/// null universe propagating as null (the consumers' BenchmarkUnavailable state).
/// </summary>
public sealed class UniverseBenchmarkProviderTests
{
    private sealed class FixedSource(BenchmarkUniverse? universe) : IBenchmarkUniverseSource
    {
        public int Reads { get; private set; }

        public Task<BenchmarkUniverse?> ReadAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(universe);
        }
    }

    private static readonly DateOnly AsOf = new(2026, 3, 2);

    private static BenchmarkUniverse Universe(params (Guid Id, string Key)[] members)
    {
        var records = members
            .Select(m => new BenchmarkUniverseMember(m.Id, m.Key, "TEST", m.Key))
            .ToList();
        return new BenchmarkUniverse(
            "benchmark-universe-schema-v1",
            "benchmark-universe-v1",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            "seed-hash",
            BenchmarkUniverseContentHash.Compute("benchmark-universe-v1", records),
            records);
    }

    private static IReadOnlyList<PriceBar> TwoBar(decimal entry, decimal exit) =>
    [
        new(AsOf.AddDays(1), entry, entry, entry, entry, entry, 1000),
        new(AsOf.AddDays(21), exit, exit, exit, exit, exit, 1000),
    ];

    [Fact]
    public async Task GetAsync_ResolvesMembersThroughTheirPriceSeriesKey_AndLoadsOnce()
    {
        var a = new Guid("11111111-0000-0000-0000-000000000001");
        var b = new Guid("11111111-0000-0000-0000-000000000002");
        var source = new FixedSource(Universe((a, "AAA"), (b, "BBB")));
        var prices = new FakePriceHistoryStore();
        prices.With("AAA", [.. TwoBar(100m, 110m)]);
        prices.With("BBB", [.. TwoBar(100m, 105m)]);

        var provider = new UniverseBenchmarkProvider(
            source, prices, NullLogger<UniverseBenchmarkProvider>.Instance);

        var benchmark = await provider.GetAsync(CancellationToken.None);
        var again = await provider.GetAsync(CancellationToken.None);

        Assert.NotNull(benchmark);
        Assert.Same(benchmark, again);
        Assert.Equal(1, source.Reads);                       // loaded once, shared by every consumer

        var day = benchmark!.DayAt(AsOf, 21, 4);
        Assert.Equal(2, day.ResolvedCount);
        Assert.Equal(0.10, day.Members.Single(m => m.CompanyId == a).ForwardReturnValue, 12);
        Assert.Equal(0.05, day.Members.Single(m => m.CompanyId == b).ForwardReturnValue, 12);
    }

    [Fact]
    public async Task GetAsync_MemberWithoutAPriceSeries_StaysInTheDenominatorUnresolved()
    {
        var a = new Guid("11111111-0000-0000-0000-000000000001");
        var missing = new Guid("11111111-0000-0000-0000-000000000009");
        var source = new FixedSource(Universe((a, "AAA"), (missing, "GONE")));
        var prices = new FakePriceHistoryStore();
        prices.With("AAA", [.. TwoBar(100m, 110m)]);

        var provider = new UniverseBenchmarkProvider(
            source, prices, NullLogger<UniverseBenchmarkProvider>.Instance);

        var benchmark = await provider.GetAsync(CancellationToken.None);
        var day = benchmark!.DayAt(AsOf, 21, 4);

        Assert.Equal(2, day.MemberCount);                    // the frozen pond, not "whatever resolved"
        Assert.Equal(1, day.ResolvedCount);
        var unresolved = Assert.Single(day.Unresolved);
        Assert.Equal("GONE", unresolved.Ticker);
        Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, unresolved.Reason);
    }

    [Fact]
    public async Task GetAsync_NullUniverse_PropagatesNull_TheConsumersBenchmarkUnavailableState()
    {
        var source = new FixedSource(universe: null);
        var provider = new UniverseBenchmarkProvider(
            source, new FakePriceHistoryStore(), NullLogger<UniverseBenchmarkProvider>.Instance);

        Assert.Null(await provider.GetAsync(CancellationToken.None));
        Assert.Null(await provider.GetAsync(CancellationToken.None));
        Assert.Equal(1, source.Reads);                       // the failure is cached too — no retry storm
    }
}
