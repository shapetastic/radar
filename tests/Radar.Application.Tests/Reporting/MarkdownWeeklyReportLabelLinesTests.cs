using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 212 §4 — the ONE label-lines banner. Rendered from <see cref="WeeklyReportModel.Labels"/>, never from
/// a constant; absent when the model carries no lines (StopAll, or a direct-model caller predating it), so
/// every pre-212 whole-document pin still passes unmodified. Test (g): the golden fixture in the undeclared
/// state differs from its pre-212 pin by exactly the banner line.
/// </summary>
public sealed class MarkdownWeeklyReportLabelLinesTests
{
    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    private const string HonestySentence =
        "These are fixed operating thresholds set by prevalence, not validated evidence of opportunity; a "
            + "label is comparable only across reports labelled on the same arm and lines.";

    private const string LegendAnchor =
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine "
            + "or planned filing, not a discretionary transaction.\n";

    private static string Render(WeeklyReportModel model) => new MarkdownWeeklyReportRenderer().Render(model);

    private static ReportLabelLines Undeclared() =>
        new("default", LabelThresholds.Default, Explicit: false, LeadDeclared: false, "weekly-report-action-v5");

    [Fact]
    public void UndeclaredState_DiffersFromThePre212PinByTheBannerLineOnly()
    {
        // The pre-212 pin is MarkdownWeeklyReportStrategySectionTests.PreSpec150Golden, which the null-Labels
        // render still equals (asserted there). Insert the one banner line under the last legend line and
        // the labelled render must match exactly.
        var pre212 = Render(MarkdownWeeklyReportGoldenModel.Create(strategies: null));
        var labelled = Render(MarkdownWeeklyReportGoldenModel.Create(strategies: null) with { Labels = Undeclared() });

        var banner =
            "> Labels in this report follow default at Investigate ≥ 60 / Watch ≥ 40 (defaults (no operating "
                + "calls declared); weekly-report-action-v5). " + HonestySentence + "\n";
        var expected = pre212.Replace(LegendAnchor, LegendAnchor + banner, StringComparison.Ordinal);

        Assert.Contains(LegendAnchor, pre212, StringComparison.Ordinal);
        Assert.NotEqual(pre212, expected);
        Assert.Equal(expected, labelled);
    }

    [Fact]
    public void NullLabels_RendersNoBanner_SoEveryPre212PinHolds()
    {
        var markdown = Render(MarkdownWeeklyReportGoldenModel.Create(strategies: null));

        Assert.DoesNotContain("Labels in this report", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(HonestySentence, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void LeadState_RendersArmLinesExplicitnessAndVersion_Verbatim()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(strategies: null) with
        {
            Labels = new ReportLabelLines(
                "disclosure-led-v11", new LabelThresholds(20, 15), Explicit: true, LeadDeclared: true,
                "weekly-report-action-v5"),
        };

        var markdown = Render(model);

        Assert.Contains(
            "> Labels in this report follow disclosure-led-v11 at Investigate ≥ 20 / Watch ≥ 15 (explicit "
                + "lines on the Lead arm; weekly-report-action-v5). " + HonestySentence + "\n",
            markdown,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "defaults on the Lead arm")]
    [InlineData(false, true, "explicit lines on the storage primary; no operating call declared")]
    public void OtherStates_NameTheirBasis(bool leadDeclared, bool isExplicit, string basis)
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(strategies: null) with
        {
            Labels = new ReportLabelLines(
                "default", LabelThresholds.Default, Explicit: isExplicit, LeadDeclared: leadDeclared,
                "weekly-report-action-v5"),
        };

        var markdown = Render(model);

        Assert.Contains("(" + basis + "; weekly-report-action-v5)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_RendersOnce_BeforeTheFirstSection_AndCarriesNoAdviceLanguage()
    {
        var markdown = Render(MarkdownWeeklyReportGoldenModel.Create(strategies: null) with { Labels = Undeclared() });

        var first = markdown.IndexOf("> Labels in this report follow", StringComparison.Ordinal);
        var last = markdown.LastIndexOf("> Labels in this report follow", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(first, last);
        Assert.True(first < markdown.IndexOf("## Highest opportunity", StringComparison.Ordinal));

        var bannerLine = markdown[first..markdown.IndexOf('\n', first)];
        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, bannerLine, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Labelled_Render_IsDeterministic()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(strategies: null) with { Labels = Undeclared() };
        Assert.Equal(Render(model), Render(model));
    }

    [Fact]
    public void ReportLabelLines_RejectsBlankArmAndVersion()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReportLabelLines(" ", LabelThresholds.Default, false, false, "weekly-report-action-v5"));
        Assert.Throws<ArgumentException>(() =>
            new ReportLabelLines("default", LabelThresholds.Default, false, false, ""));
        Assert.Throws<ArgumentNullException>(() =>
            new ReportLabelLines("default", null!, false, false, "weekly-report-action-v5"));
    }
}
