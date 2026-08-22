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
        Assert.Contains("## Dropped from efficacy ranking (2)", markdown, StringComparison.Ordinal);

        // J, named, each with its machine-readable reason.
        Assert.Contains("| thin-in-sample | insufficient-in-sample-observations |", markdown, StringComparison.Ordinal);
        Assert.Contains("| thin-out-of-sample | insufficient-out-of-sample-observations |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_DroppedHeading_SaysDroppedFromRanking_NeverThatTheStrategyFailedToScore()
    {
        // Spec 176 §1: a strategy dropped from EFFICACY RANKING may still be scoring every company live, and
        // the old heading read as if the strategy itself had been dropped. The heading is pinned EXACTLY
        // (count preserved) and the clarifying sentence sits immediately below it.
        var markdown = Renderer.RenderMarkdown(FourStrategiesTwoDropped());

        Assert.DoesNotContain("## Dropped strategies", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "## Dropped from efficacy ranking (2)\n\n"
                + "A strategy listed here may still be scoring every company live; this section means only "
                + "that its declared forward-outcome sample cannot yet be ranked.\n\n",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Spec176Relabel_ChangesNoStatisticNoCountAndNoDropReason()
    {
        // "No statistical rule, exclusion count or dropped-strategy reason changes" — the CSV (the machine
        // artifact, which never carried the heading) is a convenient whole-file witness: every numeric field,
        // status token and reason must be exactly what the pre-176 renderer emitted for this fixture.
        var leaderboard = FourStrategiesTwoDropped();
        var csv = Renderer.RenderCsv(leaderboard);

        Assert.DoesNotContain("Dropped from efficacy ranking", csv, StringComparison.Ordinal);
        Assert.Equal(2, leaderboard.DroppedStrategies.Count);

        // The markdown drop TABLE (names, reasons, observation counts, metric detail) is untouched too.
        var markdown = Renderer.RenderMarkdown(leaderboard);
        Assert.Contains(
            "| strategy | reason | in-sample obs | out-of-sample obs | metric detail |",
            markdown, StringComparison.Ordinal);
        foreach (var drop in leaderboard.DroppedStrategies)
        {
            Assert.Contains(
                $"| {drop.StrategyName} | ", markdown, StringComparison.Ordinal);
            Assert.Contains(
                $" | {drop.InSampleObservations} | {drop.OutOfSampleObservations} | ",
                markdown, StringComparison.Ordinal);
        }
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
    public void RenderCsv_CarriesThePartialWindowCountAsItsOwnColumnInEveryRowShape()
    {
        var csv = Renderer.RenderCsv(FourStrategiesTwoDropped());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');

        // A distinct column, immediately after the missing-price one — never a widening of it.
        var missingIndex = Array.IndexOf(header, "observationsWithoutForwardPrice");
        var partialIndex = Array.IndexOf(header, "observationsWithPartialWindow");
        Assert.True(missingIndex >= 0);
        Assert.Equal(missingIndex + 1, partialIndex);

        // Both ranked and dropped rows carry the field — the dropped ones as an EMPTY value — so every row has
        // the same column count and the file stays parseable.
        foreach (var line in lines)
        {
            Assert.Equal(header.Length, line.Split(',').Length);
        }

        var ranked = lines.Where(l => l.StartsWith("ranked,", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.All(ranked, l => Assert.Equal("0", l.Split(',')[partialIndex]));

        var dropped = lines.Where(l => l.StartsWith("dropped,", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, dropped.Count);
        Assert.All(dropped, l => Assert.Equal(string.Empty, l.Split(',')[partialIndex]));
    }

    [Fact]
    public void RenderMarkdown_GivesThePartialWindowCountItsOwnAlignedColumn()
    {
        var markdown = Renderer.RenderMarkdown(FourStrategiesTwoDropped());
        var lines = markdown.Split('\n');

        var headerIndex = Array.FindIndex(
            lines, l => l.StartsWith("| rank | strategy |", StringComparison.Ordinal));
        Assert.True(headerIndex >= 0);

        var headerCells = SplitRow(lines[headerIndex]);
        Assert.Contains("observations without a forward price", headerCells);
        Assert.Contains("observations with a partial forward window", headerCells);

        // Header, separator and every body row must agree on the column count — an alignment row one marker
        // short silently stops rendering as a table.
        Assert.Equal(headerCells.Length, SplitRow(lines[headerIndex + 1]).Length);
        for (var i = headerIndex + 2; i < lines.Length && lines[i].StartsWith("| ", StringComparison.Ordinal); i++)
        {
            Assert.Equal(headerCells.Length, SplitRow(lines[i]).Length);
        }
    }

    [Fact]
    public void RenderMarkdown_StatesTheExitToleranceAndWhatAPartialWindowMeans()
    {
        var markdown = Renderer.RenderMarkdown(FourStrategiesTwoDropped());

        Assert.Contains("Exit tolerance: 4 calendar day(s).", markdown, StringComparison.Ordinal);
        Assert.Contains("falls on or after D+17", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "excluded from the correlation rather than reported as a full 21-day return",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("markets close at weekends and holidays", markdown, StringComparison.Ordinal);

        // …and that the two counts are not the same fact.
        Assert.Contains(
            "no price at all in the window, versus some price that does not reach the horizon",
            markdown,
            StringComparison.Ordinal);
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
    public void RenderMarkdown_LabelsTheMarginalRankingDescriptiveAndPointsAtThePairedArtifact()
    {
        // Spec 155: the marginal leaderboard stays, but it is DESCRIPTIVE — it answers whether a strategy
        // tracked its outcome at all, not whether it beat a comparator, and it cannot support the amended
        // AD-15 claim. The label points at the artifact that can.
        var markdown = Renderer.RenderMarkdown(FourStrategiesTwoDropped());

        Assert.Contains(StrategyLeaderboardRenderer.DescriptiveScope, markdown, StringComparison.Ordinal);
        Assert.Contains("Descriptive only", markdown, StringComparison.Ordinal);
        Assert.Contains("strategy-paired-comparison.md", markdown, StringComparison.Ordinal);
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

    /// <summary>
    /// The cells of one markdown table row, ignoring the leading and trailing pipe. Cell VALUES never contain an
    /// unescaped <c>|</c> (the renderer escapes it), so a plain split is exact here.
    /// </summary>
    private static string[] SplitRow(string line) =>
        [.. line.Trim().Trim('|').Split('|', StringSplitOptions.None).Select(c => c.Trim())];

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
