using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 215 §4 — the weekly report's two reference-value surfaces: the evidence line's
/// <c>— reported: …</c> clause (values as stated, period in parentheses, no direction word, printed only
/// when the ledger has records for that evidence) and the judgment appendix row's <c>· references: …</c>
/// after the basis.
/// </summary>
public sealed class MarkdownWeeklyReportReportedMetricsTests
{
    private static readonly DateTimeOffset PeriodStart = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 6, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 6, 8, 9, 30, 0, TimeSpan.Zero);

    private static ReportEvidenceRef Evidence(IReadOnlyList<ReportedMetricLine>? reported) => new(
        EvidenceId: Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        SignalId: Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
        SourceName: "Argan — SEC",
        SourceUrl: "https://www.sec.gov/Archives/edgar/data/100591/000010059126000011/0000100591-26-000011-index.htm",
        Title: "8-K — Report (2026-09-03) [items: 2.02,9.01]",
        ContributionReason: "GuidanceChange (Positive), strength 8, novelty 6",
        ReportedMetrics: reported);

    private static string Render(ReportEvidenceRef evidence)
    {
        var snap = new ScoreSnapshotBuilder().Build();
        var entry = new WeeklyReportEntry(
            CompanyId: snap.CompanyId,
            CompanyName: "Argan",
            Ticker: "AGX",
            ScoreSnapshotId: snap.Id,
            Snapshot: snap,
            Action: RadarReportAction.Investigate,
            Rationale: "Deterministic rationale.",
            Rank: 1,
            Evidence: [evidence],
            Signals: []);
        return new MarkdownWeeklyReportRenderer().Render(new WeeklyReportModel(
            Title: "Radar Weekly",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: GeneratedAt,
            Entries: [entry],
            SignalsNeedingReview: [],
            Collection: null,
            RecentRuns: null,
            Health: null));
    }

    [Fact]
    public void EvidenceLine_AppendsTheReportedClause_ValuesAsStated_NoDirectionWord()
    {
        var markdown = Render(Evidence(
        [
            new ReportedMetricLine("revenue", "384.0", "million", "Q2 FY27"),
            new ReportedMetricLine("backlog", "2.518", "billion", "as of 2026-07-31"),
            new ReportedMetricLine("gross margin", "24", "%", "Q2 FY27"),
            new ReportedMetricLine("diluted eps", "1.12", "", "Q2 FY27"),
        ]));

        Assert.Contains(
            "— Argan — SEC: GuidanceChange (Positive), strength 8, novelty 6 — reported: revenue 384.0 million "
                + "(Q2 FY27), backlog 2.518 billion (as of 2026-07-31), gross margin 24 % (Q2 FY27), diluted eps 1.12 (Q2 FY27)\n",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceLine_PrintsNothing_WhenTheLedgerHoldsNothingForTheEvidence()
    {
        Assert.DoesNotContain("reported:", Render(Evidence(null)), StringComparison.Ordinal);
        Assert.DoesNotContain("reported:", Render(Evidence([])), StringComparison.Ordinal);
    }
}
