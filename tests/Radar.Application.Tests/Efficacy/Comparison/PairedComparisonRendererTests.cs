using System.Globalization;

using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The rendered paired comparison must state the model limitation BESIDE every interval, disclose the
/// boundary (or its absence), the supports, the dropped dates and the arms considered, stay AD-9-clean, and
/// be byte-stable.
/// </summary>
public sealed class PairedComparisonRendererTests
{
    private static readonly PairedComparisonRenderer Renderer = new();
    private static readonly PairedComparisonHarness Harness = new();

    /// <summary>The hard-rule forbidden terms (CLAUDE.md "Output language"), plus obvious near-misses.</summary>
    private static readonly string[] ForbiddenTerms =
    [
        "buy", "sell", "guaranteed upside", "safe bet", "guaranteed", "outperform", "price target",
    ];

    private static PairedStrategyComparison GateTrue() => Harness.Compare(
        [
            PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
            PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
        ],
        "primary",
        primaryWasPredeclared: true,
        PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

    private static PairedStrategyComparison Exploratory() => Harness.Compare(
        [
            PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Daily(30)),
            PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Daily(30)),
        ],
        "primary",
        primaryWasPredeclared: false,
        PairedFixtures.Options(configuredPrimary: ""));

    private static PairedStrategyComparison NoBaselines() => Harness.Compare(
        [PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(3))],
        "primary",
        primaryWasPredeclared: true,
        PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

    [Fact]
    public void RenderMarkdown_StatesTheLimitationBesideEveryInterval_NotOnlyInAFootnote()
    {
        var markdown = Renderer.RenderMarkdown(GateTrue());
        var lines = markdown.Split('\n');

        // Every line that prints an order-statistic interval carries the conditional-model limitation ON
        // THAT LINE — quoting the interval without it is impossible.
        var intervalLines = lines
            .Where(l => l.Contains("order-statistic interval", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("- ", StringComparison.Ordinal))   // exclude the how-to-read bullet
            .ToList();
        Assert.Equal(2, intervalLines.Count);                            // one per baseline
        Assert.All(intervalLines, l =>
            Assert.Contains(
                "cannot prove independence or stationarity across market regimes",
                l,
                StringComparison.Ordinal));
        Assert.All(intervalLines, l =>
            Assert.Contains("ties make the order-statistic interval conservative", l, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderMarkdown_DisclosesBoundarySupportsDroppedDatesBlockCountAndArmsConsidered()
    {
        var result = GateTrue();
        var markdown = Renderer.RenderMarkdown(result);

        Assert.Contains("Precommitted first eligible as-of date: **2026-01-01**", markdown, StringComparison.Ordinal);
        Assert.Contains("Arms considered: 3", markdown, StringComparison.Ordinal);
        Assert.Contains("baselines compared: 2", markdown, StringComparison.Ordinal);
        Assert.Contains("Joint intersection across the primary and every baseline", markdown, StringComparison.Ordinal);
        Assert.Contains("## Purged blocks (7 admitted)", markdown, StringComparison.Ordinal);
        Assert.Contains("| primary |", markdown, StringComparison.Ordinal);        // marginal support table
        Assert.Contains("Pairwise primary∩baseline intersections", markdown, StringComparison.Ordinal);
        Assert.Contains("Companies are never pooled across dates", markdown, StringComparison.Ordinal);
        Assert.Contains("Daily candidate dates are NOT independent", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_MissingBoundaryAndPredeclaration_AreNamedAndTheResultIsLabelledExploratory()
    {
        var markdown = Renderer.RenderMarkdown(Exploratory());

        Assert.Contains("Status: EXPLORATORY", markdown, StringComparison.Ordinal);
        Assert.Contains("No primary was predeclared", markdown, StringComparison.Ordinal);
        Assert.Contains("no-precommitted-evaluation-boundary", markdown, StringComparison.Ordinal);
        Assert.Contains("Qualifies under AD-15's amended gate: no.", markdown, StringComparison.Ordinal);

        // Dropped dates are rendered with their machine tokens (the dense fixture purges most dates).
        Assert.Contains("overlapping-outcome-window", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_NoBaselines_SaysSoAndClaimsNothing()
    {
        var markdown = Renderer.RenderMarkdown(NoBaselines());

        Assert.Contains("no-baselines", markdown, StringComparison.Ordinal);
        Assert.Contains("nothing is being claimed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_GateOutcome_IsAboutRadarScoringNeverAboutAnyAction()
    {
        var markdown = Renderer.RenderMarkdown(GateTrue());

        Assert.Contains("Qualifies under AD-15's amended gate: yes.", markdown, StringComparison.Ordinal);
        Assert.Contains("adding value relative to these baselines under AD-15's gate", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "a statement about Radar's scoring, never about any company, security or action",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_PointsAtTheDescriptiveMarginalLeaderboard()
    {
        var markdown = Renderer.RenderMarkdown(GateTrue());

        Assert.Contains("strategy-leaderboard.md", markdown, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTIVE", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "only result that can support the amended AD-15 claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCsv_OneRowPerBaselineWithAlignedColumns_AndSignTestLabelledByItsOwnColumns()
    {
        var csv = Renderer.RenderCsv(GateTrue());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("status,primaryStrategy,primaryPredeclared,firstEligibleAsOf,", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length);                                   // header + 2 baselines

        var header = lines[0].Split(',');
        foreach (var line in lines)
        {
            Assert.Equal(header.Length, SplitCsv(line));
        }

        Assert.Contains("signTestP", lines[0], StringComparison.Ordinal);
        Assert.Contains("signTestZeroDeltasDropped", lines[0], StringComparison.Ordinal);
        Assert.Contains("qualifiesUnderAd15", lines[0], StringComparison.Ordinal);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("baseline,", l, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderCsv_NoBaselines_StillOneParseableRowWithItsStatus()
    {
        var csv = Renderer.RenderCsv(NoBaselines());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("no-baselines,", lines[1], StringComparison.Ordinal);
        Assert.Equal(lines[0].Split(',').Length, SplitCsv(lines[1]));
    }

    [Fact]
    public void RenderedOutput_ContainsNoFinancialAdviceLanguage()
    {
        foreach (var result in new[] { GateTrue(), Exploratory(), NoBaselines() })
        {
            foreach (var text in new[] { Renderer.RenderMarkdown(result), Renderer.RenderCsv(result) })
            {
                foreach (var term in ForbiddenTerms)
                {
                    Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        Assert.Contains(PairedComparisonRenderer.Framing, Renderer.RenderMarkdown(GateTrue()), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IsCultureInvariantAndByteStable()
    {
        var result = GateTrue();

        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var deCsv = Renderer.RenderCsv(result);
            var deMarkdown = Renderer.RenderMarkdown(result);

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(Renderer.RenderCsv(result), deCsv);
            Assert.Equal(Renderer.RenderMarkdown(result), deMarkdown);

            Assert.DoesNotContain(";", deCsv.Split('\n')[1], StringComparison.Ordinal);
            Assert.Contains(".", deCsv, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>Column count of one CSV line, respecting quoted fields (the shared CsvField rule).</summary>
    private static int SplitCsv(string line)
    {
        var inQuotes = false;
        var count = 1;
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
