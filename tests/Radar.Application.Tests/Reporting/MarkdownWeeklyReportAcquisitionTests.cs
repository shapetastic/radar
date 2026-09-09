using Radar.Application.Acquisitions;
using Radar.Application.Reporting;
using Radar.Application.Tests.Acquisitions;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// SPEC 217 §2 — how a pending acquisition RENDERS: the one-line banner under the label, the
/// <c>## Acquisitions pending</c> section after <c>## Ignore / Low signal</c>, and the per-strategy
/// exclusion footer. Every one of them exists so a removal is COUNTED, never silent.
/// </summary>
public sealed class MarkdownWeeklyReportAcquisitionTests
{
    private static readonly DateTimeOffset PeriodStart = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly PendingAcquisitionRecord Marinemax = PendingAcquisitionsTests.Record();

    private static MarkdownWeeklyReportRenderer Renderer() => new();

    private static WeeklyReportEntry Entry(
        CompanyScoreSnapshot snapshot, PendingAcquisitionRecord? acquisition) =>
        new(
            CompanyId: snapshot.CompanyId,
            CompanyName: "MarineMax, Inc.",
            Ticker: "HZO",
            ScoreSnapshotId: snapshot.Id,
            Snapshot: snapshot,
            Action: RadarReportAction.Ignore,
            Rationale: "Acquisition pending: Safe Harbor Marinas, LLC at $53.00 per share in cash announced "
                + "2026-08-10; thesis closed — trajectory and opportunity are not the driver of this price.",
            Rank: 1,
            Evidence: [],
            Signals: [],
            PendingAcquisition: acquisition);

    private static WeeklyReportModel Model(
        IReadOnlyList<WeeklyReportEntry> entries,
        IReadOnlyList<AcquisitionPendingReportRow>? pending,
        IReadOnlyList<StrategyReportSection>? strategies = null) =>
        new(
            Title: "Radar Weekly",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: GeneratedAt,
            Entries: entries,
            SignalsNeedingReview: [],
            Strategies: strategies,
            AcquisitionsPending: pending);

    private static AcquisitionPendingReportRow Row(int daysPending = 22) =>
        new("MarineMax, Inc.", "HZO", Marinemax, daysPending);

    [Fact]
    public void Entry_CarriesTheOneLineBannerDirectlyUnderTheLabel()
    {
        var snapshot = new ScoreSnapshotBuilder().Build();
        var markdown = Renderer().Render(Model([Entry(snapshot, Marinemax)], [Row()]));

        var label = markdown.IndexOf("- Label: Ignore\n", StringComparison.Ordinal);
        var banner = markdown.IndexOf("- ⏸ Acquisition pending — ", StringComparison.Ordinal);

        Assert.True(label >= 0);
        Assert.True(banner > label, markdown);

        // The banner reads EVERY fact off the durable record; nothing is recomputed in the renderer.
        Assert.Contains("Safe Harbor Marinas, LLC", markdown, StringComparison.Ordinal);
        Assert.Contains("$53.00 per share in cash", markdown, StringComparison.Ordinal);
        Assert.Contains("announced 2026-08-10", markdown, StringComparison.Ordinal);
        Assert.Contains("accession 0001193125-26-341302", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Thesis closed: this price tracks the deal, not the business.",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntryWithoutAnAcquisition_RendersNoBanner()
    {
        var markdown = Renderer().Render(
            Model([Entry(new ScoreSnapshotBuilder().Build(), null)], []));

        Assert.DoesNotContain("Acquisition pending — ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_IsPlacedAfterIgnoreLowSignal_AndNamesEveryFiledFact()
    {
        var markdown = Renderer().Render(
            Model([Entry(new ScoreSnapshotBuilder().Build(), Marinemax)], [Row(daysPending: 22)]));

        var ignore = markdown.IndexOf("## Ignore / Low signal", StringComparison.Ordinal);
        var section = markdown.IndexOf("## Acquisitions pending", StringComparison.Ordinal);
        Assert.True(ignore >= 0);
        Assert.True(section > ignore, markdown);

        Assert.Contains(
            "| company | ticker | acquirer | consideration | announced | days pending | accession |",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            "| MarineMax, Inc. | HZO | Safe Harbor Marinas, LLC | $53.00 per share in cash | 2026-08-10 | 22 "
                + "| 0001193125-26-341302 |",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySection_IsAMeasuredZero_AndSaysSo()
    {
        // The store IS composed and nothing is pending: that is a fact worth stating, not an omission.
        var markdown = Renderer().Render(
            Model([Entry(new ScoreSnapshotBuilder().Build(), null)], []));

        Assert.Contains("## Acquisitions pending", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "_None — no company in the universe is under a recognised pending acquisition._",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NullSection_RendersNothing_BecauseNothingWasMeasured()
    {
        // No acquisitions store composed: "nothing is pending" was never measured, so the report must not
        // claim it. Byte-identical to pre-217.
        var markdown = Renderer().Render(
            Model([Entry(new ScoreSnapshotBuilder().Build(), null)], null));

        Assert.DoesNotContain("Acquisitions pending", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void StrategyTable_PrintsTheExclusionFooter_WhenARowWasRemoved()
    {
        var snapshot = new ScoreSnapshotBuilder().Build();
        var sections = new List<StrategyReportSection>
        {
            new(
                StrategyName: "default",
                FormulaVersion: "radar-formula-v8",
                ScoringConfigVersion: "radar-scoring-fp-test",
                IsPrimary: true,
                CompaniesScored: 3,
                CompaniesWithLinkedEvidence: 3,
                Rows:
                [
                    new StrategyReportRow(1, snapshot.CompanyId, "Other Co", "OTH", snapshot.Id, snapshot),
                ])
            {
                PendingAcquisitionsExcluded = 1,
            },
            new(
                StrategyName: "second",
                FormulaVersion: "radar-formula-v9",
                ScoringConfigVersion: "radar-scoring-fp-test2",
                IsPrimary: false,
                CompaniesScored: 3,
                CompaniesWithLinkedEvidence: 3,
                Rows:
                [
                    new StrategyReportRow(1, snapshot.CompanyId, "Other Co", "OTH", snapshot.Id, snapshot),
                ]),
        };

        var markdown = Renderer().Render(
            Model([Entry(snapshot, Marinemax)], [Row()], sections));

        Assert.Contains(
            "1 company excluded: pending acquisition — see Acquisitions pending",
            markdown,
            StringComparison.Ordinal);

        // A strategy with nothing excluded prints NO footer, so its table is byte-identical to pre-217.
        Assert.Equal(
            1,
            markdown.Split("excluded: pending acquisition", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Truncated_DiscountsTheExcludedRows_SoTheCapIsNotBlamedForTheExclusion()
    {
        // 3 with linked evidence, 1 excluded as pending, 2 rendered ⇒ the cap removed NOTHING.
        var snapshot = new ScoreSnapshotBuilder().Build();
        var section = new StrategyReportSection(
            "default",
            "radar-formula-v8",
            "radar-scoring-fp-test",
            IsPrimary: true,
            CompaniesScored: 3,
            CompaniesWithLinkedEvidence: 3,
            Rows:
            [
                new StrategyReportRow(1, snapshot.CompanyId, "A", "A", snapshot.Id, snapshot),
                new StrategyReportRow(2, snapshot.CompanyId, "B", "B", snapshot.Id, snapshot),
            ])
        {
            PendingAcquisitionsExcluded = 1,
        };

        Assert.False(section.Truncated);
        Assert.True(section with { PendingAcquisitionsExcluded = 0 } is { Truncated: true });
    }

    [Fact]
    public void TheRenderedAcquisitionText_CarriesNoAdviceLanguage()
    {
        var markdown = Renderer().Render(
            Model([Entry(new ScoreSnapshotBuilder().Build(), Marinemax)], [Row()]));

        foreach (var forbidden in new[] { "buy", "sell", "guaranteed upside", "safe bet" })
        {
            Assert.DoesNotContain(forbidden, markdown, StringComparison.OrdinalIgnoreCase);
        }
    }
}
