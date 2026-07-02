using Radar.Application.Collectors;
using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
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
        IReadOnlyList<ReportEvidenceRef>? evidence = null,
        IReadOnlyList<ReportSignalRef>? signals = null,
        int? previousOpportunity = null,
        int? previousTrajectory = null,
        bool previousScoringChanged = false)
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
            Evidence: evidence ?? [],
            Signals: signals ?? [],
            PreviousOpportunityScore: previousOpportunity,
            PreviousTrajectoryScore: previousTrajectory,
            PreviousScoringChanged: previousScoringChanged);
    }

    private static WeeklyReportModel CreateModel(
        IReadOnlyList<WeeklyReportEntry>? entries = null,
        IReadOnlyList<NeedsReviewSignalRef>? signalsNeedingReview = null,
        CollectionSummary? collection = null,
        IReadOnlyList<RecentRunSummary>? recentRuns = null) =>
        new(
            Title: "Radar Weekly",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: GeneratedAt,
            Entries: entries ?? [],
            SignalsNeedingReview: signalsNeedingReview ?? [],
            Collection: collection,
            RecentRuns: recentRuns);

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
    [InlineData(RadarReportAction.Ignore, "Ignore")]
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
    public void Render_Ignore_Label_Renders_Under_Low_Signal_Section()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Ignore, companyName: "Low Co", ticker: "LOW")]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("- Label: Ignore", output);
        Assert.Contains("## Ignore / Low signal", output);
        Assert.Contains("- Low Co (LOW) (#1)", output);
    }

    [Fact]
    public void Render_Watch_Label_Renders_Under_Watch_Section()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Watch, companyName: "Watch Co", ticker: "WCH")]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Watch", output);
        Assert.Contains("- Watch Co (WCH) (#1)", output);
    }

    [Fact]
    public void Render_Watch_Section_Omitted_When_No_Watch_Entries()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate)]);

        var output = CreateRenderer().Render(model);

        Assert.DoesNotContain("## Watch", output);
    }

    [Fact]
    public void Render_Watch_Section_Ordered_After_Thesis_And_Before_Ignore()
    {
        var improving = CreateEntry(RadarReportAction.ThesisImproving, rank: 1, companyName: "Up Co", ticker: "UP");
        var watch = CreateEntry(RadarReportAction.Watch, rank: 2, companyName: "Watch Co", ticker: "WCH");
        var ignore = CreateEntry(RadarReportAction.Ignore, rank: 3, companyName: "Low Co", ticker: "LOW");
        var model = CreateModel([improving, watch, ignore]);

        var output = CreateRenderer().Render(model);

        var thesisIndex = output.IndexOf("## Thesis improving", StringComparison.Ordinal);
        var watchIndex = output.IndexOf("## Watch", StringComparison.Ordinal);
        var ignoreIndex = output.IndexOf("## Ignore / Low signal", StringComparison.Ordinal);

        Assert.True(thesisIndex >= 0 && watchIndex >= 0 && ignoreIndex >= 0);
        Assert.True(thesisIndex < watchIndex, "Watch section should appear after the thesis sections.");
        Assert.True(watchIndex < ignoreIndex, "Watch section should appear before the Ignore section.");
    }

    [Fact]
    public void Render_Watch_Entry_Appears_In_Both_Detail_And_RollUp()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Watch, companyName: "Watch Co", ticker: "WCH")]);

        var output = CreateRenderer().Render(model);

        // Detailed lead block (every entry) shows the entry with its label.
        var detailIndex = output.IndexOf("### 1. Watch Co (WCH)", StringComparison.Ordinal);
        Assert.True(detailIndex >= 0, "Watch entry should appear in the detailed Highest opportunity block.");
        Assert.Contains("- Label: Watch", output);

        // Roll-up section also lists the entry, after the detailed block.
        var rollUpIndex = output.IndexOf("## Watch", StringComparison.Ordinal);
        Assert.True(rollUpIndex > detailIndex, "Watch roll-up should appear after the detailed block.");
        Assert.Contains("- Watch Co (WCH) (#1)", output);
    }

    [Fact]
    public void Render_Ignore_Section_Omitted_When_No_Ignore_Entries()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate)]);

        var output = CreateRenderer().Render(model);

        Assert.DoesNotContain("## Ignore / Low signal", output);
    }

    [Fact]
    public void Render_Disallowed_Label_Throws()
    {
        // An enum value outside the six AD-9 labels must still be rejected.
        var model = CreateModel([CreateEntry((RadarReportAction)999)]);

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
            Evidence: [],
            Signals: []);
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
            Evidence: [],
            Signals: []);
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
    public void Render_Score_Line_Shows_Positive_Deltas_When_Scores_Rose()
    {
        var snap = new ScoreSnapshotBuilder()
            .WithOpportunityScore(80)
            .WithTrajectoryScore(75)
            .Build();
        var model = CreateModel(
        [
            CreateEntry(
                RadarReportAction.Investigate,
                snapshot: snap,
                previousOpportunity: 61,
                previousTrajectory: 56),
        ]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(Opportunity +19, Trajectory +19 vs last run)\n", output);
    }

    [Fact]
    public void Render_Score_Line_Shows_Negative_Deltas_When_Scores_Fell()
    {
        var snap = new ScoreSnapshotBuilder()
            .WithOpportunityScore(61)
            .WithTrajectoryScore(56)
            .Build();
        var model = CreateModel(
        [
            CreateEntry(
                RadarReportAction.ThesisDeteriorating,
                snapshot: snap,
                previousOpportunity: 80,
                previousTrajectory: 75),
        ]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(Opportunity -19, Trajectory -19 vs last run)\n", output);
    }

    [Fact]
    public void Render_Score_Line_Shows_No_Change_When_Deltas_Zero()
    {
        var snap = new ScoreSnapshotBuilder()
            .WithOpportunityScore(70)
            .WithTrajectoryScore(65)
            .Build();
        var model = CreateModel(
        [
            CreateEntry(
                RadarReportAction.Watch,
                snapshot: snap,
                previousOpportunity: 70,
                previousTrajectory: 65),
        ]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(no change vs last run)\n", output);
        Assert.DoesNotContain("vs last run)", output[..output.IndexOf("(no change", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Render_Score_Line_Shows_First_Snapshot_When_No_Previous()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate)]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(first snapshot)\n", output);
    }

    [Fact]
    public void Render_Score_Line_Shows_Scoring_Updated_When_Previous_Incomparable()
    {
        // A prior snapshot exists but was produced by a different (incomparable) scoring generation.
        // The renderer must say "(scoring updated)" instead of a numeric delta or "(first snapshot)".
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, previousScoringChanged: true)]);

        var output = CreateRenderer().Render(model);

        Assert.Contains(" (scoring updated)\n", output);
        Assert.DoesNotContain("vs last run)", output);
        Assert.DoesNotContain("(first snapshot)", output);
    }

    [Theory]
    [InlineData(61, null)]
    [InlineData(null, 56)]
    public void Render_Score_Line_Shows_First_Snapshot_When_Previous_Partially_Present(
        int? previousOpportunity, int? previousTrajectory)
    {
        // Previous scores are populated-or-null together; a single-null entry has no prior
        // snapshot to compare, so the movement clause must not fabricate a "+0" delta.
        var model = CreateModel(
        [
            CreateEntry(
                RadarReportAction.Investigate,
                previousOpportunity: previousOpportunity,
                previousTrajectory: previousTrajectory),
        ]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(first snapshot)\n", output);
        Assert.DoesNotContain("vs last run)", output);
    }

    [Fact]
    public void Render_Delta_Clause_Contains_No_Advice_Language()
    {
        var snap = new ScoreSnapshotBuilder()
            .WithOpportunityScore(80)
            .WithTrajectoryScore(50)
            .Build();
        var model = CreateModel(
        [
            CreateEntry(
                RadarReportAction.Investigate,
                snapshot: snap,
                previousOpportunity: 61,
                previousTrajectory: 75),
        ]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("(Opportunity +19, Trajectory -25 vs last run)\n", output);
        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        }
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
            Summary: "ambiguous mention",
            ReviewReason: "EscalateToHuman: Unresolved company mention");
        var model = CreateModel(signalsNeedingReview: [review]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Signals needing review", output);
        Assert.Contains(
            $"- Beta Inc: ambiguous mention — EscalateToHuman: Unresolved company mention (signal {signalId})",
            output);
    }

    [Fact]
    public void Render_NeedsReview_Bullet_Places_ReviewReason_Between_Summary_And_SignalId()
    {
        var signalId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var review = new NeedsReviewSignalRef(
            SignalId: signalId,
            EvidenceId: Guid.NewGuid(),
            CompanyMention: "Acme",
            Summary: "Matched phrase 'x'",
            ReviewReason: "EscalateToHuman: Unresolved company mention");
        var model = CreateModel(signalsNeedingReview: [review]);

        var output = CreateRenderer().Render(model);

        Assert.Contains(
            $"- Acme: Matched phrase 'x' — EscalateToHuman: Unresolved company mention (signal {signalId})",
            output);
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
            ],
            signals:
            [
                new ReportSignalRef(
                    Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive, "customer win detected"),
            ]);
        var review = new NeedsReviewSignalRef(
            Guid.NewGuid(), Guid.NewGuid(), "Mention", "summary", "EscalateToHuman: review reason");
        var model = CreateModel([entry], [review]);

        var output = CreateRenderer().Render(model);

        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Render_Signals_Renders_WhyNoticed_Block_In_Model_Order()
    {
        var first = new ReportSignalRef(
            Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive, "Matched phrase 'multi-year deal'.");
        var second = new ReportSignalRef(
            Guid.NewGuid(), SignalType.GovernmentContract, SignalDirection.Positive, "Matched phrase 'nasa'.");
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, signals: [first, second])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("- Why noticed:", output);
        Assert.Contains("  - CustomerWin (Positive): Matched phrase 'multi-year deal'.", output);
        Assert.Contains("  - GovernmentContract (Positive): Matched phrase 'nasa'.", output);

        // Renderer is a pure formatter: bullets appear in model-supplied order, no re-sorting.
        var firstIndex = output.IndexOf("CustomerWin (Positive)", StringComparison.Ordinal);
        var secondIndex = output.IndexOf("GovernmentContract (Positive)", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex >= 0);
        Assert.True(firstIndex < secondIndex, "Signals should render in model-supplied order.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_Signal_With_Empty_Reason_Omits_Trailing_Colon(string reason)
    {
        var signal = new ReportSignalRef(
            Guid.NewGuid(), SignalType.ExecutiveHire, SignalDirection.Neutral, reason);
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, signals: [signal])]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("  - ExecutiveHire (Neutral)\n", output);
        Assert.DoesNotContain("ExecutiveHire (Neutral):", output);
    }

    [Fact]
    public void Render_No_Signals_Renders_No_WhyNoticed_Block()
    {
        var model = CreateModel([CreateEntry(RadarReportAction.Investigate, signals: [])]);

        var output = CreateRenderer().Render(model);

        Assert.DoesNotContain("- Why noticed:", output);
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
            ],
            signals:
            [
                new ReportSignalRef(
                    Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    SignalType.CustomerWin,
                    SignalDirection.Positive,
                    "reason for signal"),
            ]);
        var review = new NeedsReviewSignalRef(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Mention",
            "summary",
            "EscalateToHuman: review reason");
        var model = CreateModel([entry], [review]);

        var renderer = CreateRenderer();
        var first = renderer.Render(model);
        var second = renderer.Render(model);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Render_CollectionSummary_All_Read_Renders_Count_Line_No_Bullets()
    {
        var summary = new CollectionSummary(
            SourcesChecked: 5,
            SourcesSucceeded: 5,
            SourcesFailed: 0,
            ItemsCollected: 12,
            Failures: []);
        var model = CreateModel(collection: summary);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Collection summary", output);
        Assert.Contains("Radar checked 5 source(s) this run; 0 could not be read.", output);
        Assert.DoesNotContain("\n- ", output[output.IndexOf("## Collection summary", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Render_CollectionSummary_Some_Failed_Renders_One_Bullet_Per_Failure_In_Order()
    {
        var summary = new CollectionSummary(
            SourcesChecked: 3,
            SourcesSucceeded: 1,
            SourcesFailed: 2,
            ItemsCollected: 4,
            Failures:
            [
                new SourceFailure("Acme Feed", "https://acme.example/rss", "HTTP 503"),
                new SourceFailure("Local File", null, "File not found"),
            ]);
        var model = CreateModel(collection: summary);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Collection summary", output);
        Assert.Contains("Radar checked 3 source(s) this run; 2 could not be read.", output);
        Assert.Contains("- Acme Feed (https://acme.example/rss): HTTP 503", output);
        Assert.Contains("- Local File: File not found", output);

        // URL is omitted when absent.
        Assert.DoesNotContain("- Local File (", output);

        // Failures render in summary order.
        var firstIndex = output.IndexOf("- Acme Feed", StringComparison.Ordinal);
        var secondIndex = output.IndexOf("- Local File", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex >= 0);
        Assert.True(firstIndex < secondIndex, "Failures should render in summary order.");
    }

    [Fact]
    public void Render_CollectionSummary_Omitted_When_Null()
    {
        var output = CreateRenderer().Render(CreateModel(collection: null));

        Assert.DoesNotContain("## Collection summary", output);
    }

    [Fact]
    public void Render_CollectionSummary_Appears_After_Signals_Needing_Review()
    {
        var review = new NeedsReviewSignalRef(
            SignalId: Guid.NewGuid(),
            EvidenceId: Guid.NewGuid(),
            CompanyMention: "Beta Inc",
            Summary: "ambiguous mention",
            ReviewReason: "EscalateToHuman: Unresolved company mention");
        var summary = new CollectionSummary(2, 2, 0, 3, []);
        var model = CreateModel(signalsNeedingReview: [review], collection: summary);

        var output = CreateRenderer().Render(model);

        var reviewIndex = output.IndexOf("## Signals needing review", StringComparison.Ordinal);
        var summaryIndex = output.IndexOf("## Collection summary", StringComparison.Ordinal);

        Assert.True(reviewIndex >= 0 && summaryIndex >= 0);
        Assert.True(reviewIndex < summaryIndex, "Collection summary should appear after Signals needing review.");
    }

    [Fact]
    public void Render_CollectionSummary_Is_Deterministic_ByteIdentical()
    {
        var summary = new CollectionSummary(
            SourcesChecked: 3,
            SourcesSucceeded: 1,
            SourcesFailed: 2,
            ItemsCollected: 4,
            Failures:
            [
                new SourceFailure("Acme Feed", "https://acme.example/rss", "HTTP 503"),
                new SourceFailure("Local File", null, "File not found"),
            ]);
        var model = CreateModel(collection: summary);

        var renderer = CreateRenderer();
        var first = renderer.Render(model);
        var second = renderer.Render(model);

        Assert.Equal(first, second);
    }

    private static readonly DateTimeOffset RunNewer = new(2026, 6, 28, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RunOlder = new(2026, 6, 27, 9, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Render_RecentRuns_Renders_Section_With_Bullets_Newest_First()
    {
        var newer = new RecentRunSummary(
            CreatedAtUtc: RunNewer,
            Collectors: ["rss", "sec"],
            EvidenceNew: 12,
            SignalsApproved: 7,
            CompaniesScored: 6,
            SourcesChecked: 14,
            SourcesFailed: 1);
        var older = new RecentRunSummary(
            CreatedAtUtc: RunOlder,
            Collectors: ["rss"],
            EvidenceNew: 3,
            SignalsApproved: 2,
            CompaniesScored: 4,
            SourcesChecked: 5,
            SourcesFailed: 0);
        var model = CreateModel(recentRuns: [newer, older]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("## Recent runs", output);
        Assert.Contains(
            "- 2026-06-28 14:00Z — collectors: rss, sec — new evidence 12 · approved 7 · companies 6 · sources 14/1 failed",
            output);
        Assert.Contains(
            "- 2026-06-27 09:05Z — collectors: rss — new evidence 3 · approved 2 · companies 4 · sources 5/0 failed",
            output);

        // Model order preserved (newest-first as supplied).
        var newerIndex = output.IndexOf("2026-06-28 14:00Z", StringComparison.Ordinal);
        var olderIndex = output.IndexOf("2026-06-27 09:05Z", StringComparison.Ordinal);
        Assert.True(newerIndex >= 0 && olderIndex >= 0);
        Assert.True(newerIndex < olderIndex, "Recent runs should render in model-supplied (newest-first) order.");
    }

    [Fact]
    public void Render_RecentRuns_Section_Omitted_When_Null()
    {
        var output = CreateRenderer().Render(CreateModel(recentRuns: null));

        Assert.DoesNotContain("## Recent runs", output);
    }

    [Fact]
    public void Render_RecentRuns_Section_Omitted_When_Empty()
    {
        var output = CreateRenderer().Render(CreateModel(recentRuns: []));

        Assert.DoesNotContain("## Recent runs", output);
    }

    [Fact]
    public void Render_RecentRuns_Empty_Collectors_Renders_None()
    {
        var run = new RecentRunSummary(
            CreatedAtUtc: RunNewer,
            Collectors: [],
            EvidenceNew: 0,
            SignalsApproved: 0,
            CompaniesScored: 0,
            SourcesChecked: 0,
            SourcesFailed: 0);
        var model = CreateModel(recentRuns: [run]);

        var output = CreateRenderer().Render(model);

        Assert.Contains("collectors: (none)", output);
    }

    [Fact]
    public void Render_RecentRuns_Appears_After_Collection_Summary()
    {
        var summary = new CollectionSummary(2, 2, 0, 3, []);
        var run = new RecentRunSummary(RunNewer, ["rss"], 1, 1, 1, 1, 0);
        var model = CreateModel(collection: summary, recentRuns: [run]);

        var output = CreateRenderer().Render(model);

        var summaryIndex = output.IndexOf("## Collection summary", StringComparison.Ordinal);
        var runsIndex = output.IndexOf("## Recent runs", StringComparison.Ordinal);

        Assert.True(summaryIndex >= 0 && runsIndex >= 0);
        Assert.True(summaryIndex < runsIndex, "Recent runs should appear after the Collection summary.");
    }

    [Fact]
    public void Render_RecentRuns_Contains_No_Advice_Language()
    {
        var run = new RecentRunSummary(RunNewer, ["rss", "sec"], 12, 7, 6, 14, 1);
        var model = CreateModel(recentRuns: [run]);

        var output = CreateRenderer().Render(model);

        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        }
    }
}
