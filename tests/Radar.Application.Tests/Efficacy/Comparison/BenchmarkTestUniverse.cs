using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The ONE test-side constructor of a frozen-universe benchmark (reuse-over-copy): fixed member order,
/// content hash computed through the SAME shared <see cref="BenchmarkUniverseContentHash"/> definition the
/// artifact reader verifies, price series keyed by ticker. No clock, no randomness (AD-3).
/// </summary>
/// <summary>A provider handing out one fixed (possibly null) benchmark — the test seam.</summary>
internal sealed class FixedUniverseBenchmarkProvider(UniverseBenchmark? benchmark) : IUniverseBenchmarkProvider
{
    public Task<UniverseBenchmark?> GetAsync(CancellationToken ct) => Task.FromResult(benchmark);
}

internal static class BenchmarkTestUniverse
{
    public const string SchemaVersion = "benchmark-universe-schema-v1";

    public static UniverseBenchmark Of(
        string universeVersion,
        DateTimeOffset frozenAtUtc,
        IReadOnlyList<(Guid Id, string Ticker, IReadOnlyList<PriceBar> Bars)> members)
    {
        var memberRecords = members
            .Select(m => new BenchmarkUniverseMember(m.Id, m.Ticker, "TEST", m.Ticker))
            .ToList();
        var universe = new BenchmarkUniverse(
            SchemaVersion,
            universeVersion,
            frozenAtUtc,
            SourceSeedHash: "test-seed-hash",
            BenchmarkUniverseContentHash.Compute(universeVersion, memberRecords),
            memberRecords);
        var bars = members.ToDictionary(
            m => m.Ticker, m => m.Bars, StringComparer.Ordinal);
        return new UniverseBenchmark(universe, bars);
    }

    /// <summary>A deterministic peer id: <c>99999999-9999-9999-9999-{index:D12}</c>.</summary>
    public static Guid PeerId(int index) => new($"99999999-9999-9999-9999-{index:D12}");

    /// <summary>Daily flat bars at 100 — a peer whose forward return is exactly 0 on every covered date.</summary>
    public static IReadOnlyList<PriceBar> FlatBars(DateOnly first, int days)
    {
        var bars = new List<PriceBar>(days);
        for (var t = 0; t < days; t++)
        {
            bars.Add(new PriceBar(
                first.AddDays(t), Open: 100m, High: 100m, Low: 100m, Close: 100m, AdjClose: 100m, Volume: 1000));
        }

        return bars;
    }
}
