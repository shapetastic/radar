using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 183's artifact obligations on the leaderboard: renamed excess columns, the benchmark provenance
/// preamble (version + hash + freeze + coverage rule), the schema bump, the retrospective label for
/// pre-freeze dates, the raw-series incomparability statement, and the loud (never silent) rendering of an
/// unavailable universe.
/// </summary>
public sealed class BenchmarkLeaderboardRenderingTests
{
    private static readonly StrategyLeaderboardRenderer Renderer = new();
    private static readonly StrategyComparisonHarness Harness = new();

    private static StrategyLeaderboard Compare(UniverseBenchmark? benchmark) => Harness.Compare(
        [
            ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
            ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
        ],
        ComparisonFixtures.Options(),
        benchmark);

    [Fact]
    public void RenderMarkdown_CarriesTheBenchmarkProvenancePreamble()
    {
        var benchmark = ComparisonFixtures.Benchmark();
        var markdown = Renderer.RenderMarkdown(Compare(benchmark));

        Assert.Contains("## Benchmark (excess-vs-universe-v1)", markdown, StringComparison.Ordinal);
        Assert.Contains("benchmark-universe-v1", markdown, StringComparison.Ordinal);
        Assert.Contains(benchmark.Universe.ContentHash, markdown, StringComparison.Ordinal);
        Assert.Contains("48 member(s)", markdown, StringComparison.Ordinal);
        Assert.Contains("frozen at 2026-01-01T00:00:00Z", markdown, StringComparison.Ordinal);
        Assert.Contains("max(40, ceil(90% of eligible peers))", markdown, StringComparison.Ordinal);
        Assert.Contains(StrategyLeaderboardRenderer.RawSeriesNotComparable, markdown, StringComparison.Ordinal);

        // Excess-vs-universe columns, named in the table.
        Assert.Contains("in-sample rho (excess-vs-universe-v1)", markdown, StringComparison.Ordinal);
        Assert.Contains("out-of-sample rho (excess-vs-universe-v1)", markdown, StringComparison.Ordinal);
        Assert.Contains("observations excluded: benchmark unavailable", markdown, StringComparison.Ordinal);
        Assert.Contains("observations excluded: not in benchmark universe", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_LabelsPreFreezeDatesRetrospective()
    {
        // Freeze AFTER day 9: days 0..9 predate it and must carry the retrospective/descriptive label.
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var c = 0; c < ComparisonFixtures.CompanyIds.Length; c++)
        {
            members.Add((
                ComparisonFixtures.CompanyIds[c],
                ComparisonFixtures.Tickers[c],
                ComparisonFixtures.Bars(c)));
        }

        for (var p = 0; p < 44; p++)
        {
            members.Add((
                BenchmarkTestUniverse.PeerId(p),
                $"PR{p:D2}",
                BenchmarkTestUniverse.FlatBars(ComparisonFixtures.FirstAsOf, 91)));
        }

        var lateFreeze = BenchmarkTestUniverse.Of(
            "benchmark-universe-v1",
            new DateTimeOffset(
                ComparisonFixtures.AsOf(10).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            members);

        var leaderboard = Compare(lateFreeze);
        Assert.NotNull(leaderboard.Benchmark);
        Assert.Equal(10, leaderboard.Benchmark!.PreFreezeAsOfDates);

        var markdown = Renderer.RenderMarkdown(leaderboard);
        Assert.Contains("RETROSPECTIVE span: 10 of 30 as-of date(s) predate the freeze", markdown, StringComparison.Ordinal);
        Assert.Contains("prices were backfilled", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_PostFreezeSeries_SaysSoInsteadOfTheRetrospectiveLabel()
    {
        var markdown = Renderer.RenderMarkdown(Compare(ComparisonFixtures.Benchmark()));

        Assert.DoesNotContain("RETROSPECTIVE span", markdown, StringComparison.Ordinal);
        Assert.Contains("No as-of date predates the freeze", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableUniverse_IsStatedLoudly_AndRanksNothing()
    {
        var leaderboard = Compare(benchmark: null);

        // Every raw-usable observation is an EXCLUSION with the counted reason — never a raw fallback.
        Assert.Empty(leaderboard.Rows);
        Assert.Null(leaderboard.Benchmark);
        Assert.Equal(2, leaderboard.DroppedStrategies.Count);

        var markdown = Renderer.RenderMarkdown(leaderboard);
        Assert.Contains("Benchmark universe UNAVAILABLE", markdown, StringComparison.Ordinal);
        Assert.Contains("BenchmarkUnavailable", markdown, StringComparison.Ordinal);
        Assert.Contains("No strategy could be ranked", markdown, StringComparison.Ordinal);

        var csv = Renderer.RenderCsv(leaderboard);
        var header = csv.Split('\n')[0].Split(',');
        Assert.Contains("benchmarkUniverseVersion", header);
        Assert.Contains("benchmarkUniverseContentHash", header);
    }

    [Fact]
    public void RenderCsv_CarriesTheBenchmarkColumnsAndTheSchemaVersionOnEveryRow()
    {
        var benchmark = ComparisonFixtures.Benchmark();
        var csv = Renderer.RenderCsv(Compare(benchmark));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');

        var versionIndex = Array.IndexOf(header, "benchmarkUniverseVersion");
        var hashIndex = Array.IndexOf(header, "benchmarkUniverseContentHash");
        var unavailableIndex = Array.IndexOf(header, "observationsBenchmarkUnavailable");
        var notInIndex = Array.IndexOf(header, "observationsNotInBenchmarkUniverse");
        Assert.True(versionIndex >= 0 && hashIndex >= 0 && unavailableIndex >= 0 && notInIndex >= 0);

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            Assert.Equal(StrategyLeaderboardRenderer.CsvSchemaVersion, cells[0]);
            Assert.Equal("benchmark-universe-v1", cells[versionIndex]);
            Assert.Equal(benchmark.Universe.ContentHash, cells[hashIndex]);
        }

        // Full coverage in this fixture: zero exclusions, stated as zeros rather than blanks on ranked rows.
        var ranked = lines.Where(l => l.Contains(",ranked,", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.All(ranked, l =>
        {
            var cells = l.Split(',');
            Assert.Equal("0", cells[unavailableIndex]);
            Assert.Equal("0", cells[notInIndex]);
        });
    }

    [Fact]
    public void PerDayCoverageGaps_AreListedWithEveryUnresolvedMemberAndItsReason()
    {
        // One peer with NO price at all: it stays in the denominator on every day, and the artifact lists
        // it per day with its reason (full provenance — coverage is measured against the frozen pond).
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var c = 0; c < ComparisonFixtures.CompanyIds.Length; c++)
        {
            members.Add((
                ComparisonFixtures.CompanyIds[c],
                ComparisonFixtures.Tickers[c],
                ComparisonFixtures.Bars(c)));
        }

        for (var p = 0; p < 44; p++)
        {
            members.Add((
                BenchmarkTestUniverse.PeerId(p),
                $"PR{p:D2}",
                BenchmarkTestUniverse.FlatBars(ComparisonFixtures.FirstAsOf, 91)));
        }

        members.Add((new Guid("eeeeeeee-0000-0000-0000-000000000001"), "GONE", []));

        var universe = BenchmarkTestUniverse.Of(
            "benchmark-universe-v1", ComparisonFixtures.BenchmarkFrozenAtUtc, members);

        var leaderboard = Compare(universe);
        var markdown = Renderer.RenderMarkdown(leaderboard);

        Assert.Contains("| as-of date | resolved / members | unresolved members (reason) |", markdown, StringComparison.Ordinal);
        Assert.Contains("GONE (no-forward-bar)", markdown, StringComparison.Ordinal);
        Assert.Contains("48 / 49", markdown, StringComparison.Ordinal);
    }
}
