using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed class MarkdownWeeklyReportRendererTests
{
    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    private static readonly DateTimeOffset PeriodStart = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 6, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 6, 8, 9, 30, 0, TimeSpan.Zero);

    private static MarkdownWeeklyReportRenderer CreateRenderer() => new();

    private static WeeklyReportEntry CreateEntry(
        RadarReportAction action,
        int rank = 1,
        string companyName = "Acme Corp",
        string? ticker = "ACME",
        CompanyScoreSnapshot? snapshot = null,
        IReadOnlyList<ReportEvidenceRef>? evidence = null)
    {
        var snap = snapshot ?? new ScoreSnapshotBuilder().Build();
        return new WeeklyReportEntry(
            CompanyId: snap.CompanyId,
            CompanyName: companyName,
            Ticker: ticker,
            ScoreSnapshotId: snap.Id,
            Snapshot: snap,
            Action: action,
            Rationale: "Deterministic rationale.",
            Rank: rank,
            Evidence: evidence ?? []);
    }

    private static WeeklyReportModel CreateModel(
        IReadOnlyList<WeeklyReportEntry>? entries = null,
        IReadOnlyList<NeedsReviewSignalRef>? signalsNeedingReview = null) =>
        new(
            Title: "Radar Weekly",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: GeneratedAt,
            Entries: entries ?? [],
            SignalsNeedingReview: signalsNeedingReview ?? []);

    [Fact]
    public void Render_Null_Model_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreateRenderer().Render(null!));
    }

    [Fact]
    public void Render_Contains_All_Three_Disclaimers()
    {
        var output = CreateRenderer().Render(CreateModel());

        Assert.Contains("> Not financial advice.", output);
        Assert.Contains("> For research only.", output);
        Assert.Contains("> Human review required.", output);
    }

    [Fact]
    public void Render_Contains_Heading_And_Period()
    {
        var output = CreateRenderer().Render(CreateModel());

        Assert.Contains("# Radar Weekly", output);
        Assert.Contains("Period: 2026-06-01 → 2026-06-07 (UTC)", output);
        Assert.Contains("Generated: 2026-06-08 09:30Z", output);
    }

    [Theory]
    [InlineData(RadarReportAction.Investigate, "Investigate")]
    [InlineData(RadarReportAction.Watch, "Watch")]
    [InlineData(RadarReportAction.NeedsMoreEvidence, "Needs more evidence")]
    [InlineData(RadarReportAction.ThesisImproving, "Thesis improving")]
    [InlineData(RadarReportAction.ThesisDeteriorating, "Thesis deteriorating")]
    public void Render_Allowed_Label_Uses_Display_String(RadarReportAction action, string display)
    {
        var model = CreateModel([CreateEntry(action)]);

        var output = CreateRenderer().Render(model);

        Assert.Contains($"- Label: {display}", output);
    }

    [Fact]
    public void Render_Disallowed_Label_Throws()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Ignore)]);

        Assert.Throws<InvalidOperationException>(() => CreateRenderer().Render(model));
    }

    [Fact]
    public void Render_SnapshotId_Mismatch_Throws()
    {
        var snap = new ScoreSnapshotBuilder().Build();
        var entry = new WeeklyReportEntry(
            CompanyId: snap.CompanyId,
            CompanyName: "Acme Corp",
            Ticker: "ACME",
            ScoreSnapshotId: Guid.NewGuid(), // does not match snap.Id
            Snapshot: snap,
            Action: RadarReportAction.Investigate,
            Rationale: "Deterministic rationale.",
            Rank: 1,
            Evidence: []);
        var model = CreateModel([entry]);

        Assert.Throws<InvalidOperationException>(() => CreateRenderer().Render(model));
    }

    [Fact]
    public void Render_CompanyId_Mismatch_Throws()
    {
        var snap = new ScoreSnapshotBuilder().Build();
        var entry = new WeeklyReportEntry(
            CompanyId: Guid.NewGuid(), // does not match snap.CompanyId
            CompanyName: "Acme Corp",
            Ticker: "ACME",
            ScoreSnapshotId: snap.Id,
            Snapshot: snap,
            Action: RadarReportAction.Investigate,
            Rationale: "Deterministic rationale.",
            Rank: 1,
            Evidence: []);
        var model = CreateModel([entry]);

        Assert.Throws<InvalidOperationException>(() => CreateRenderer().Render(model));
    }

    [Fact]
    public void Render_Entry_Shows_Score_Components_And_Snapshot_Id()
    {
        var snap = new ScoreSnapshotBuilder()
            .WithOpportunityScore(61)
            .WithTrajectoryScore(62)
            .WithAttentionScore(63)
            .WithEvidenceConfidenceScore(64)
            .WithSignalVelocityScore(65)
            .Build();
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, snapshot: snap)]);

        var output = CreateRenderer().Render(model);

        Assert.Contains(
            "- Opportunity 61 · Trajectory 62 · Attention 63 · Evidence 64 · Velocity 65",
            output);
        Assert.Contains($"- Score snapshot: {snap.Id}", output);
    }

    [Fact]
    public void Render_Evidence_With_Url_Renders_Markdown_Link_With_Provenance()
    {
        var evidence = new ReportEvidenceRef(
            EvidenceId: Guid.NewGuid(),
            SignalId: Guid.NewGuid(),
            SourceName: "Example Wire",
            SourceUrl: "https://example.com/article",
            Title: "Quarterly bookings up",
            ContributionReason: "supports trajectory");
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, evidence: [evidence])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains(
            "  - [Quarterly bookings up](https://example.com/article) — Example Wire: supports trajectory",
            output);
    }

    [Fact]
    public void Render_Evidence_With_Null_Url_Renders_No_Broken_Link()
    {
        var evidence = new ReportEvidenceRef(
            EvidenceId: Guid.NewGuid(),
            SignalId: Guid.NewGuid(),
            SourceName: "Filing",
            SourceUrl: null,
            Title: "10-K excerpt",
            ContributionReason: "supports evidence confidence");
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, evidence: [evidence])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("  - 10-K excerpt — Filing: supports evidence confidence", output);
        Assert.DoesNotContain("[10-K excerpt]", output);
        Assert.DoesNotContain("()", output);
    }

    [Fact]
    public void Render_Evidence_With_Empty_Url_Renders_No_Broken_Link()
    {
        var evidence = new ReportEvidenceRef(
            EvidenceId: Guid.NewGuid(),
            SignalId: Guid.NewGuid(),
            SourceName: "Filing",
            SourceUrl: "",
            Title: "10-K excerpt",
            ContributionReason: "supports evidence confidence");
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, evidence: [evidence])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("  - 10-K excerpt — Filing: supports evidence confidence", output);
        Assert.DoesNotContain("()", output);
    }

    [Fact]
    public void Render_Entry_Without_Evidence_Renders_Placeholder()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Watch, evidence: [])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("  - (no linked evidence)", output);
    }

    [Fact]
    public void Render_NeedsReview_Section_Present_When_NonEmpty()
    {
        var signalId = Guid.NewGuid();
        var review = new NeedsReviewSignalRef(
            SignalId: signalId,
            EvidenceId: Guid.NewGuid(),
            CompanyMention: "Beta Inc",
            Summary: "ambiguous mention");
        var model = CreateModel(signalsNeedingReview: [review]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Signals needing review", output);
        Assert.Contains($"- Beta Inc: ambiguous mention (signal {signalId})", output);
    }

    [Fact]
    public void Render_NeedsReview_Section_Omitted_When_Empty()
    {
        var output = CreateRenderer().Render(CreateModel());

        Assert.DoesNotContain("## Signals needing review", output);
    }

    [Fact]
    public void Render_Thesis_Sections_Omitted_When_Empty()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate)]);

        var output = CreateRenderer().Render(model);

        Assert.DoesNotContain("## Thesis improving", output);
        Assert.DoesNotContain("## Thesis deteriorating", output);
    }

    [Fact]
    public void Render_Thesis_Sections_Present_When_Matching_Entries_Exist()
    {
        var improving = CreateEntry(RadarReportAction.ThesisImproving, rank: 1, companyName: "Up Co", ticker: "UP");
        var deteriorating = CreateEntry(RadarReportAction.ThesisDeteriorating, rank: 2, companyName: "Down Co", ticker: "DN");
        var model = CreateModel([improving, deteriorating]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Thesis improving", output);
        Assert.Contains("- Up Co (UP) (#1)", output);
        Assert.Contains("## Thesis deteriorating", output);
        Assert.Contains("- Down Co (DN) (#2)", output);
    }

    [Fact]
    public void Render_Empty_Report_Has_Heading_And_Disclaimers_And_Does_Not_Throw()
    {
        var output = CreateRenderer().Render(CreateModel());

        Assert.Contains("# Radar Weekly", output);
        Assert.Contains("> Not financial advice.", output);
        Assert.Contains("> For research only.", output);
        Assert.Contains("> Human review required.", output);
    }

    [Fact]
    public void Render_Scaffolding_Contains_No_Advice_Language()
    {
        // Model-supplied text is deliberately clean here so the assertion targets generated scaffolding.
        var entry = CreateEntry(
            RadarReportAction.Investigate,
            evidence:
            [
                new ReportEvidenceRef(
                    Guid.NewGuid(), Guid.NewGuid(), "Source", "https://example.com", "Title", "reason"),
            ]);
        var review = new NeedsReviewSignalRef(Guid.NewGuid(), Guid.NewGuid(), "Mention", "summary");
        var model = CreateModel([entry], [review]);

        var output = CreateRenderer().Render(model);

        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Render_Is_Deterministic_ByteIdentical()
    {
        var snap = new ScoreSnapshotBuilder().WithOpportunityScore(70).Build();
        var entry = CreateEntry(
            RadarReportAction.ThesisImproving,
            snapshot: snap,
            evidence:
            [
                new ReportEvidenceRef(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "Example Wire",
                    "https://example.com/a",
                    "Title A",
                    "reason A"),
            ]);
        var review = new NeedsReviewSignalRef(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Mention",
            "summary");
        var model = CreateModel([entry], [review]);

        var renderer = CreateRenderer();
        var first = renderer.Render(model);
        var second = renderer.Render(model);

        Assert.Equal(first, second);
    }
}
