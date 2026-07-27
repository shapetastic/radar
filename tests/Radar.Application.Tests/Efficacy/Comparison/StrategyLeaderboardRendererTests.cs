using System.Globalization;

using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The rendered leaderboard must state the honest N, name every dropped strategy with its reason, stay
/// AD-9-clean, and be byte-stable.
/// </summary>
public sealed class StrategyLeaderboardRendererTests
{
    private static readonly StrategyLeaderboardRenderer Renderer = new();
    private static readonly StrategyComparisonHarness Harness = new();

    /// <summary>The hard-rule forbidden terms (CLAUDE.md "Output language"), plus obvious near-misses.</summary>
    private static readonly string[] ForbiddenTerms =
    [
        "buy", "sell", "guaranteed upside", "safe bet", "guaranteed", "outperform", "price target",
    ];

    private static StrategyLeaderboard FourStrategiesTwoDropped() => Harness.Compare(
        [
            ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
            ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
            ComparisonFixtures.Strategy(
                "thin-in-sample",
                ComparisonFixtures.AlignedThroughout,
                dateIndexes: [0, 1],
                companyIndexes: [0]),
            ComparisonFixtures.Strategy(
                "thin-out-of-sample",
                ComparisonFixtures.AlignedThroughout,
                dateIndexes: [.. Enumerable.Range(0, ComparisonFixtures.InSampleDateCount), 25]),
        ],
        ComparisonFixtures.Options());

    [Fact]
    public void RenderMarkdown_StatesTheHonestNAndNamesEveryDroppedStrategyWithItsReason()
    {
        var markdown = Renderer.RenderMarkdown(FourStrategiesTwoDropped());

        // K, stated in the rendered text — not merely present on the object.
        Assert.Contains("Strategies compared (ranked): 2", markdown, StringComparison.Ordinal);
        Assert.Contains("Strategies considered: 4; dropped: 2", markdown, StringComparison.Ordinal);
        Assert.Contains("## Dropped strategies (2)", markdown, StringComparison.Ordinal);

        // J, named, each with its machine-readable reason.
        Assert.Contains("| thin-in-sample | insufficient-in-sample-observations |", markdown, StringComparison.Ordinal);
        Assert.Contains("| thin-out-of-sample | insufficient-out-of-sample-observations |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCsv_CarriesEveryStrategyWithItsStatusAndTheHonestN()
    {
        var csv = Renderer.RenderCsv(FourStrategiesTwoDropped());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("status,rank,strategy,strategiesCompared,strategiesConsidered,", lines[0], StringComparison.Ordinal);

        // 1 header + 2 ranked + 2 dropped.
        Assert.Equal(5, lines.Length);

        var columnCount = lines[0].Split(',').Length;
        foreach (var line in lines)
        {
            Assert.Equal(columnCount, line.Split(',').Length);
        }

        var ranked = lines.Where(l => l.StartsWith("ranked,", StringComparison.Ordinal)).ToList();
        var dropped = lines.Where(l => l.StartsWith("dropped,", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal(2, dropped.Count);

        // Every row carries N so the CSV alone cannot be read without it.
        Assert.All(ranked.Concat(dropped), l => Assert.Contains(",2,4,", l, StringComparison.Ordinal));

        Assert.Contains(dropped, l => l.Contains("thin-in-sample", StringComparison.Ordinal)
            && l.Contains("insufficient-in-sample-observations", StringComparison.Ordinal));
        Assert.Contains(dropped, l => l.Contains("thin-out-of-sample", StringComparison.Ordinal)
            && l.Contains("insufficient-out-of-sample-observations", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderMarkdown_ReportsTheOutOfSampleHeadlineWithItsDispersionAndCoverage()
    {
        var leaderboard = Harness.Compare(
            [
                ComparisonFixtures.Strategy("overfit", ComparisonFixtures.AlignedThenReversed),
                ComparisonFixtures.Strategy("late-bloomer", ComparisonFixtures.WeakThenAligned),
            ],
            ComparisonFixtures.Options());

        var markdown = Renderer.RenderMarkdown(leaderboard);
        var headline = leaderboard.Headline!;

        Assert.Contains("## Headline (out-of-sample)", markdown, StringComparison.Ordinal);

        // The number printed under the headline is the OUT-OF-SAMPLE rho, not the in-sample one.
        var outOfSample = headline.OutOfSample.Correlation.Rho.ToString("0.0000", CultureInfo.InvariantCulture);
        var inSample = headline.InSample.Correlation.Rho.ToString("0.0000", CultureInfo.InvariantCulture);
        Assert.Contains($"**overfit** — out-of-sample rho {outOfSample} (95% CI ", markdown, StringComparison.Ordinal);
        Assert.NotEqual(inSample, outOfSample);

        // Effect AND dispersion, never a point estimate alone.
        Assert.Contains(" to ", markdown, StringComparison.Ordinal);
        Assert.Contains("40 observation(s), 4 compan(ies), 10 as-of date(s)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_WithNothingRankableSaysSoInsteadOfClaimingAWinner()
    {
        var markdown = Renderer.RenderMarkdown(Harness.Compare([], ComparisonFixtures.Options()));

        Assert.Contains("Strategies compared (ranked): 0", markdown, StringComparison.Ordinal);
        Assert.Contains("No strategy could be ranked", markdown, StringComparison.Ordinal);
        Assert.Contains("Nothing is being claimed.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedOutput_ContainsNoFinancialAdviceLanguage()
    {
        var leaderboard = FourStrategiesTwoDropped();
        var markdown = Renderer.RenderMarkdown(leaderboard);
        var csv = Renderer.RenderCsv(leaderboard);
        var empty = Renderer.RenderMarkdown(Harness.Compare([], ComparisonFixtures.Options()));

        foreach (var text in new[] { markdown, csv, empty })
        {
            foreach (var term in ForbiddenTerms)
            {
                Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // …and the framing sentence is present verbatim, so the artifact says what it is not.
        Assert.Contains(StrategyLeaderboardRenderer.Framing, markdown, StringComparison.Ordinal);
        Assert.Contains("not financial advice", markdown, StringComparison.Ordinal);
        Assert.Contains("Radar ranks; a human decides.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IsCultureInvariantAndByteStable()
    {
        var leaderboard = FourStrategiesTwoDropped();

        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var deCsv = Renderer.RenderCsv(leaderboard);
            var deMarkdown = Renderer.RenderMarkdown(leaderboard);

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(Renderer.RenderCsv(leaderboard), deCsv);
            Assert.Equal(Renderer.RenderMarkdown(leaderboard), deMarkdown);

            // A decimal comma would be a comma-separated-values catastrophe; assert the point separator.
            Assert.DoesNotContain(";", deCsv, StringComparison.Ordinal);
            Assert.Contains(".", deCsv, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Render_EscapesAStrategyNameThatWouldOtherwiseBreakTheFormat()
    {
        var awkward = Harness.Compare(
            [
                ComparisonFixtures.Strategy("momentum, v2 |\"x\"", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
            ],
            ComparisonFixtures.Options());

        var csv = Renderer.RenderCsv(awkward);
        var header = csv.Split('\n')[0];
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // The quoted field keeps the row's column count intact (the shared CsvField rule).
            Assert.Equal(header.Split(',').Length, CountUnquotedCommas(line) + 1);
        }

        Assert.Contains("\"momentum, v2 |\"\"x\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("momentum, v2 \\|\"x\"", Renderer.RenderMarkdown(awkward), StringComparison.Ordinal);
    }

    private static int CountUnquotedCommas(string line)
    {
        var inQuotes = false;
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
