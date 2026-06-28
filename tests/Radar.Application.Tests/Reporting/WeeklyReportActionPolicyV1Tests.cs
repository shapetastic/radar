using Radar.Application.Reporting;
using Radar.Domain.Reports;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed class WeeklyReportActionPolicyV1Tests
{
    private static readonly RadarReportAction[] AllowedActions =
    [
        RadarReportAction.Investigate,
        RadarReportAction.Watch,
        RadarReportAction.NeedsMoreEvidence,
        RadarReportAction.ThesisImproving,
        RadarReportAction.ThesisDeteriorating
    ];

    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    private static WeeklyReportActionPolicyV1 CreatePolicy() => new();

    [Fact]
    public void Version_Is_Stable_Identifier()
    {
        Assert.Equal("weekly-report-action-v1", CreatePolicy().Version);
    }

    public static IEnumerable<object[]> RepresentativeMatrix()
    {
        // current trajectory, current opportunity, current evidence, previous trajectory (nullable)
        var trajectories = new[] { 10, 45, 50, 60, 90 };
        var opportunities = new[] { 0, 40, 55, 60, 100 };
        var evidences = new[] { 0, 34, 35, 70 };
        var previousTrajectories = new int?[] { null, 50, 55, 90 };

        foreach (var t in trajectories)
        {
            foreach (var o in opportunities)
            {
                foreach (var e in evidences)
                {
                    foreach (var p in previousTrajectories)
                    {
                        yield return [t, o, e, p!];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RepresentativeMatrix))]
    public void Decide_Only_Emits_Allowed_Labels(int trajectory, int opportunity, int evidence, int? previousTrajectory)
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(trajectory)
            .WithOpportunityScore(opportunity)
            .WithEvidenceConfidenceScore(evidence)
            .Build();

        var previous = previousTrajectory is null
            ? null
            : new ScoreSnapshotBuilder().WithTrajectoryScore(previousTrajectory.Value).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.Contains(result.Action, AllowedActions);
        Assert.NotEqual(RadarReportAction.Ignore, result.Action);
    }

    [Fact]
    public void Thin_Evidence_Overrides_High_Opportunity()
    {
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(95)
            .WithEvidenceConfidenceScore(34)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.NeedsMoreEvidence, result.Action);
        Assert.Contains("34", result.Rationale);
        Assert.Contains("35", result.Rationale);
    }

    [Fact]
    public void Opportunity_AtOrAbove_Investigate_Threshold_Yields_Investigate()
    {
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(60)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.Investigate, result.Action);
        Assert.Contains("60", result.Rationale);
    }

    [Fact]
    public void Opportunity_Between_Watch_And_Investigate_Yields_Watch()
    {
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(40)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Contains("40", result.Rationale);
    }

    [Fact]
    public void Opportunity_Below_Watch_Threshold_Yields_NeedsMoreEvidence()
    {
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(39)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.NeedsMoreEvidence, result.Action);
        Assert.Contains("39", result.Rationale);
    }

    [Fact]
    public void Rising_Trajectory_Above_Neutral_Yields_ThesisImproving()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(90)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.Equal(RadarReportAction.ThesisImproving, result.Action);
        Assert.Contains("50", result.Rationale);
        Assert.Contains("60", result.Rationale);
        Assert.Contains("+10", result.Rationale);
    }

    [Fact]
    public void Falling_Trajectory_Yields_ThesisDeteriorating_Before_Opportunity()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(40)
            .WithOpportunityScore(90)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(60).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.Equal(RadarReportAction.ThesisDeteriorating, result.Action);
        Assert.Contains("60", result.Rationale);
        Assert.Contains("40", result.Rationale);
        Assert.Contains("-20", result.Rationale);
    }

    [Fact]
    public void SubThreshold_Trajectory_Change_Does_Not_Trigger_Improving_Or_Deteriorating()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(54)
            .WithOpportunityScore(70)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.NotEqual(RadarReportAction.ThesisImproving, result.Action);
        Assert.NotEqual(RadarReportAction.ThesisDeteriorating, result.Action);
        Assert.Equal(RadarReportAction.Investigate, result.Action);
    }

    [Fact]
    public void Rising_Trajectory_Below_Neutral_Does_Not_Yield_Improving()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(45)
            .WithOpportunityScore(70)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(38).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.NotEqual(RadarReportAction.ThesisImproving, result.Action);
        Assert.Equal(RadarReportAction.Investigate, result.Action);
    }

    [Fact]
    public void No_Previous_Snapshot_Never_Yields_Improving_Or_Deteriorating()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(90)
            .WithOpportunityScore(55)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.NotEqual(RadarReportAction.ThesisImproving, result.Action);
        Assert.NotEqual(RadarReportAction.ThesisDeteriorating, result.Action);
        Assert.Equal(RadarReportAction.Watch, result.Action);
    }

    [Theory]
    [MemberData(nameof(RepresentativeMatrix))]
    public void Rationale_Is_NonEmpty_And_Free_Of_Advice_Language(int trajectory, int opportunity, int evidence, int? previousTrajectory)
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(trajectory)
            .WithOpportunityScore(opportunity)
            .WithEvidenceConfidenceScore(evidence)
            .Build();

        var previous = previousTrajectory is null
            ? null
            : new ScoreSnapshotBuilder().WithTrajectoryScore(previousTrajectory.Value).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous));

        Assert.False(string.IsNullOrWhiteSpace(result.Rationale));
        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, result.Rationale, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Decide_Is_Deterministic_For_Same_Context()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(70)
            .WithOpportunityScore(80)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();
        var context = new ReportActionContext(current, previous);

        var policy = CreatePolicy();
        var first = policy.Decide(context);
        var second = policy.Decide(context);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Decide_Null_Context_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreatePolicy().Decide(null!));
    }

    [Fact]
    public void Decide_Null_Current_Throws()
    {
        var context = new ReportActionContext(null!, null);
        Assert.Throws<ArgumentNullException>(() => CreatePolicy().Decide(context));
    }
}
