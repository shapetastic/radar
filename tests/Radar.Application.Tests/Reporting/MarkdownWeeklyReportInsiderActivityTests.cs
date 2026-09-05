using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 209 — the weekly report's insider-channel presentation: the display-only relabel of the stored
/// <see cref="SignalType.InsiderBuying"/> member as <c>InsiderActivity</c> over BOTH render paths (the
/// renderer-owned type site AND the stored provenance text of evidence-link reasons and signal reasons),
/// the one legend line, and the ONE structured insider-activity line whose every clause states what the
/// store captured and says "not captured" for the rest — never 0.
/// </summary>
public sealed class MarkdownWeeklyReportInsiderActivityTests
{
    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    // Must stay in sync with MarkdownWeeklyReportRenderer.AppendDisclaimers. Deliberately names no stored
    // token: the report-language rule forbids the substring the stored member contains.
    private const string LegendLine =
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine "
        + "or planned filing, not a discretionary transaction.";

    // The exact NWPX shape from the audit (docs/cohorts/insider-flow-audit-2026-09.md), pinned byte-exact.
    private const string NwpxLine =
        "- Insider activity (Form 4, this window): 11 filings; 11 planned-disposition filings across 29 days; "
        + "transaction value not captured";

    // The stored provenance shape RadarScoreFormulaV8 authors at scoring time for the insider channel.
    private const string StoredInsiderReason = "InsiderBuying (Neutral), strength 3, novelty 4";

    private static InsiderActivitySummary Summary(
        int filings = 0,
        int plans = 0,
        int? planSpan = null,
        int planUndated = 0,
        int purchases = 0,
        decimal? purchaseValue = null,
        int purchasesNotCaptured = 0,
        int sales = 0,
        decimal? saleValue = null,
        int salesNotCaptured = 0,
        int mixed = 0,
        int noDiscretionary = 0,
        int unknown = 0,
        int unrecognised = 0,
        int outside = 0) =>
        new(
            FilingCount: filings,
            PlannedDispositionCount: plans,
            PlannedDispositionFirstFilingDate: plans > 0 ? new DateOnly(2026, 8, 5) : null,
            PlannedDispositionLastFilingDate: plans > 0 ? new DateOnly(2026, 9, 3) : null,
            PlannedDispositionSpanDays: planSpan,
            PlannedDispositionUndatedCount: planUndated,
            DiscretionaryPurchaseCount: purchases,
            DiscretionaryPurchaseValue: purchaseValue,
            DiscretionaryPurchaseValueNotCapturedCount: purchasesNotCaptured,
            DiscretionarySaleCount: sales,
            DiscretionarySaleValue: saleValue,
            DiscretionarySaleValueNotCapturedCount: salesNotCaptured,
            MixedCount: mixed,
            NoDiscretionaryTransactionsCount: noDiscretionary,
            UnknownClassificationCount: unknown,
            UnrecognisedClassificationCount: unrecognised,
            OutsideWindowCount: outside);

    private static readonly InsiderActivitySummary NwpxSummary = Summary(filings: 11, plans: 11, planSpan: 29);

    private static WeeklyReportModel Model(
        InsiderActivitySummary? insider,
        IReadOnlyList<ReportEvidenceRef>? evidence = null,
        IReadOnlyList<ReportSignalRef>? signals = null)
    {
        var snap = new ScoreSnapshotBuilder().Build();
        var entry = new WeeklyReportEntry(
            CompanyId: snap.CompanyId,
            CompanyName: "Northwest Pipe",
            Ticker: "NWPX",
            ScoreSnapshotId: snap.Id,
            Snapshot: snap,
            Action: RadarReportAction.Watch,
            Rationale: "Deterministic rationale.",
            Rank: 1,
            Evidence: evidence ?? [],
            Signals: signals ?? [],
            InsiderActivity: insider);
        return new WeeklyReportModel(
            Title: "Radar Weekly",
            PeriodStartUtc: new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
            PeriodEndUtc: new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero),
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 7, 9, 30, 0, TimeSpan.Zero),
            Entries: [entry],
            SignalsNeedingReview: []);
    }

    private static string RenderLine(InsiderActivitySummary insider)
    {
        var output = new MarkdownWeeklyReportRenderer().Render(Model(insider));
        var line = output.Split('\n').Single(l => l.StartsWith("- Insider activity", StringComparison.Ordinal));
        AssertNoForbiddenWords(output);
        return line;
    }

    private static void AssertNoForbiddenWords(string output)
    {
        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- (a) the NWPX planned-disposition stream, over both render paths ---

    [Fact]
    public void PlannedDispositionStream_RendersNwpxLineByteExact_AndNeverTheWordBuying()
    {
        var signalId = Guid.NewGuid();
        var evidence = new List<ReportEvidenceRef>
        {
            new(
                EvidenceId: Guid.NewGuid(),
                SignalId: signalId,
                SourceName: "SEC EDGAR Form 4",
                SourceUrl: "https://sec.example/form4",
                Title: "Form 4 insider filing: routine",
                ContributionReason: StoredInsiderReason),
        };
        var signals = new List<ReportSignalRef>
        {
            new(signalId, SignalType.InsiderBuying, SignalDirection.Neutral,
                "Insider stock transaction (routine)"),
        };

        var output = new MarkdownWeeklyReportRenderer().Render(Model(NwpxSummary, evidence, signals));

        Assert.Contains(NwpxLine + "\n", output, StringComparison.Ordinal);
        // Path 1: the renderer-owned type site.
        Assert.Contains(
            "  - InsiderActivity (Neutral): Insider stock transaction (routine)\n", output,
            StringComparison.Ordinal);
        // Path 2: the stored evidence-link provenance text, token swapped, every other byte verbatim.
        Assert.Contains(
            "  - [Form 4 insider filing: routine](https://sec.example/form4) — SEC EDGAR Form 4: "
                + "InsiderActivity (Neutral), strength 3, novelty 4\n",
            output, StringComparison.Ordinal);
        Assert.DoesNotContain("Buying", output, StringComparison.Ordinal);
        AssertNoForbiddenWords(output);
    }

    [Fact]
    public void InsiderLine_RendersAfterNotednessAndBeforeWhy()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(Model(NwpxSummary));

        var notedness = output.IndexOf("- **Notedness:**", StringComparison.Ordinal);
        var insider = output.IndexOf("- Insider activity", StringComparison.Ordinal);
        var why = output.IndexOf("- Why:", StringComparison.Ordinal);

        Assert.True(notedness >= 0 && insider > notedness, "Insider line follows Notedness.");
        Assert.True(why > insider, "Insider line precedes Why.");
    }

    // --- (b) GuidanceChange stored text is unchanged: the seam rewrites ONE token only ---

    [Fact]
    public void GuidanceChangeStoredText_StillRendersByteVerbatim()
    {
        const string storedGuidance = "GuidanceChange (Positive), strength 8, confidence 0.90";
        var signalId = Guid.NewGuid();
        var evidence = new List<ReportEvidenceRef>
        {
            new(Guid.NewGuid(), signalId, "SEC EDGAR", "https://sec.example/8-k", "Q2 earnings 8-K",
                storedGuidance),
        };
        var signals = new List<ReportSignalRef>
        {
            new(signalId, SignalType.GuidanceChange, SignalDirection.Positive,
                "Directional earnings read mentioning GuidanceChange verbatim."),
        };

        var output = new MarkdownWeeklyReportRenderer().Render(Model(null, evidence, signals));

        Assert.Contains("SEC EDGAR: " + storedGuidance + "\n", output, StringComparison.Ordinal);
        Assert.Contains(
            "  - EarningsTrajectory (Positive): Directional earnings read mentioning GuidanceChange verbatim.\n",
            output, StringComparison.Ordinal);
    }

    // --- (c) whole-token only ---

    [Theory]
    [InlineData("NotInsiderBuyingX (Neutral)", "NotInsiderBuyingX (Neutral)")]
    [InlineData("InsiderBuyingX", "InsiderBuyingX")]
    [InlineData("XInsiderBuying", "XInsiderBuying")]
    [InlineData("insiderbuying (Neutral)", "insiderbuying (Neutral)")]
    [InlineData("InsiderBuying (Neutral); InsiderBuying again", "InsiderActivity (Neutral); InsiderActivity again")]
    [InlineData("(InsiderBuying)", "(InsiderActivity)")]
    public void StoredProvenanceSeam_RewritesWholeTokenOnly(string stored, string expected)
    {
        var signalId = Guid.NewGuid();
        var evidence = new List<ReportEvidenceRef>
        {
            new(Guid.NewGuid(), signalId, "Src", null, "Title", stored),
        };
        var signals = new List<ReportSignalRef>
        {
            new(signalId, SignalType.CustomerWin, SignalDirection.Positive, stored),
        };

        var output = new MarkdownWeeklyReportRenderer().Render(Model(null, evidence, signals));

        Assert.Contains("  - Title — Src: " + expected + "\n", output, StringComparison.Ordinal);
        Assert.Contains("  - CustomerWin (Positive): " + expected + "\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOtherSignalType_RendersItsOwnName()
    {
        foreach (var type in Enum.GetValues<SignalType>()
                     .Where(t => t is not SignalType.InsiderBuying and not SignalType.GuidanceChange))
        {
            var output = new MarkdownWeeklyReportRenderer().Render(Model(
                null, signals: [new ReportSignalRef(Guid.NewGuid(), type, SignalDirection.Positive, "r")]));

            Assert.Contains($"  - {type} (Positive): r\n", output, StringComparison.Ordinal);
        }
    }

    // --- (d) null summary renders nothing ---

    [Fact]
    public void NullSummary_RendersNoInsiderLine()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(Model(null));

        Assert.DoesNotContain("Insider activity", output, StringComparison.Ordinal);
    }

    // --- the legend line ---

    [Fact]
    public void LegendLine_IsPresentExactlyOnce_DirectlyAfterTheGuidanceChangeLegend()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(Model(null));

        Assert.Equal(1, output.Split(LegendLine, StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "changed guidance.\n" + LegendLine + "\n\n## ", output, StringComparison.Ordinal);
        AssertNoForbiddenWords(output);
    }

    // --- (e) every clause, with "not captured" semantics ---

    [Fact]
    public void NwpxSummary_LineIsByteExact()
    {
        Assert.Equal(NwpxLine, RenderLine(NwpxSummary));
    }

    [Fact]
    public void SinglePlanFiling_UsesSingularAndNoSpan()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 1 filing; 1 planned-disposition filing; "
                + "transaction value not captured",
            RenderLine(Summary(filings: 1, plans: 1)));
    }

    [Fact]
    public void PlanFilingsWithAnUndatedOne_SaySpanNotEstablished()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 3 filings; 3 planned-disposition filings "
                + "(span not established: 1 undated); transaction value not captured",
            RenderLine(Summary(filings: 3, plans: 3, planUndated: 1)));
    }

    [Fact]
    public void TwoSameDayPlanFilings_RenderZeroDaySpan()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 2 filings; 2 planned-disposition filings across 0 days; "
                + "transaction value not captured",
            RenderLine(Summary(filings: 2, plans: 2, planSpan: 0)));
    }

    [Fact]
    public void PurchaseAndSale_WithCapturedValues_RenderInvariantN0()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 3 filings; 2 discretionary purchase filings, "
                + "purchase value $75,000; 1 discretionary sale filing, sale value $3,313,222",
            RenderLine(Summary(
                filings: 3, purchases: 2, purchaseValue: 75000m, sales: 1, saleValue: 3313222m)));
    }

    [Fact]
    public void CapturedValue_SubDollarFraction_RoundsToWholeDollars()
    {
        // "N0" rounds half away from zero; a captured $75,000.50 renders as $75,001, never as a fraction.
        Assert.Equal(
            "- Insider activity (Form 4, this window): 1 filing; 1 discretionary purchase filing, "
                + "purchase value $75,001",
            RenderLine(Summary(filings: 1, purchases: 1, purchaseValue: 75000.5m)));
    }

    [Fact]
    public void PurchaseAndSale_WithNoCapturedValue_SayNotCaptured_NeverZero()
    {
        var line = RenderLine(Summary(
            filings: 3, purchases: 2, purchasesNotCaptured: 2, sales: 1, salesNotCaptured: 1));

        Assert.Equal(
            "- Insider activity (Form 4, this window): 3 filings; 2 discretionary purchase filings, "
                + "purchase value not captured; 1 discretionary sale filing, sale value not captured",
            line);
        Assert.DoesNotContain("$0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PurchaseAndSale_PartiallyCaptured_StateTheUncapturedCount()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 5 filings; 3 discretionary purchase filings, "
                + "purchase value $50,000 (2 not captured); 2 discretionary sale filings, "
                + "sale value $1,000 (1 not captured)",
            RenderLine(Summary(
                filings: 5, purchases: 3, purchaseValue: 50000m, purchasesNotCaptured: 2,
                sales: 2, saleValue: 1000m, salesNotCaptured: 1)));
    }

    [Fact]
    public void Mixed_RendersCountOnly_WithSplitAndTotalNotCaptured()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 2 filings; 2 mixed purchase-and-sale filings; "
                + "split and total not captured",
            RenderLine(Summary(filings: 2, mixed: 2)));
        Assert.Equal(
            "- Insider activity (Form 4, this window): 1 filing; 1 mixed purchase-and-sale filing; "
                + "split and total not captured",
            RenderLine(Summary(filings: 1, mixed: 1)));
    }

    [Fact]
    public void NoDiscretionary_Unknown_Unrecognised_AndOutsideWindow_EachRender()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 6 filings; 3 with no discretionary transactions; "
                + "2 with classification not captured; 1 with unrecognised classification; 4 outside the window",
            RenderLine(Summary(filings: 6, noDiscretionary: 3, unknown: 2, unrecognised: 1, outside: 4)));
    }

    [Fact]
    public void AllForm4OutsideTheWindow_RendersZeroFilingsAndTheOutsideCount()
    {
        Assert.Equal(
            "- Insider activity (Form 4, this window): 0 filings; 2 outside the window",
            RenderLine(Summary(filings: 0, outside: 2)));
    }

    [Fact]
    public void EveryBucket_RendersInTheFixedOrder()
    {
        var line = RenderLine(Summary(
            filings: 9, plans: 2, planSpan: 7, purchases: 1, purchaseValue: 10m, sales: 1, saleValue: 20m,
            mixed: 1, noDiscretionary: 1, unknown: 1, unrecognised: 1, outside: 1));

        Assert.Equal(
            "- Insider activity (Form 4, this window): 9 filings; 2 planned-disposition filings across 7 days; "
                + "transaction value not captured; 1 discretionary purchase filing, purchase value $10; "
                + "1 discretionary sale filing, sale value $20; 1 mixed purchase-and-sale filing; "
                + "split and total not captured; 1 with no discretionary transactions; "
                + "1 with classification not captured; 1 with unrecognised classification; 1 outside the window",
            line);
    }
}
