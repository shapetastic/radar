using Radar.Application.Reporting;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed class WeeklyReportActionPolicyV1Tests
{
    private static readonly RadarReportAction[] AllowedActions =
    [
        RadarReportAction.Investigate,
        RadarReportAction.Watch,
        RadarReportAction.Ignore,
        RadarReportAction.NeedsMoreEvidence,
        RadarReportAction.ThesisImproving,
        RadarReportAction.ThesisDeteriorating
    ];

    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    private static WeeklyReportActionPolicyV1 CreatePolicy() => new();

    [Fact]
    public void Version_Is_Stable_Identifier()
    {
        Assert.Equal("weekly-report-action-v2", CreatePolicy().Version);
    }

    public static IEnumerable<object?[]> RepresentativeMatrix()
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
                        yield return [t, o, e, p];
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
    public void Adequate_Evidence_Below_Watch_Threshold_Yields_Ignore()
    {
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(39)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
        Assert.Contains("39", result.Rationale);
        Assert.Contains("40", result.Rationale);
    }

    [Fact]
    public void Thin_Evidence_Below_Watch_Threshold_Still_Yields_NeedsMoreEvidence()
    {
        // Thin evidence (below the floor) must win over the low-opportunity Ignore rule:
        // an insufficiently-evidenced company is "needs more evidence", not silently ignored.
        var current = new ScoreSnapshotBuilder()
            .WithOpportunityScore(20)
            .WithEvidenceConfidenceScore(34)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.NeedsMoreEvidence, result.Action);
        Assert.NotEqual(RadarReportAction.Ignore, result.Action);
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

    [Fact]
    public void Incomparable_Previous_Never_Yields_Deteriorating()
    {
        // A prior snapshot exists but was produced by a different scoring generation. Even though the
        // trajectory dropped 60 → 40 (which would normally deteriorate), the incomparable previous must
        // fall through to the steady-state branch — Investigate on opportunity 90.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(40)
            .WithOpportunityScore(90)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(60).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous, PreviousComparable: false));

        Assert.NotEqual(RadarReportAction.ThesisDeteriorating, result.Action);
        Assert.NotEqual(RadarReportAction.ThesisImproving, result.Action);
        Assert.Equal(RadarReportAction.Investigate, result.Action);
    }

    [Fact]
    public void Incomparable_Previous_Never_Yields_Improving()
    {
        // A prior snapshot exists but is incomparable. A rise 50 → 60 must not yield ThesisImproving;
        // it falls through to the steady-state Watch on opportunity 55.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(55)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, previous, PreviousComparable: false));

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

    // ---- Corroboration floor (v2) -------------------------------------------------------------

    private static ReportSignalRef SignalRef(SignalType type, SignalDirection direction) =>
        new(Guid.NewGuid(), type, direction, $"{type} ({direction}).");

    // Two independent positive axes agreeing — the corroborated set the floor is meant to catch.
    private static IReadOnlyList<ReportSignalRef> CorroboratedSignals() =>
    [
        SignalRef(SignalType.CustomerWin, SignalDirection.Positive),
        SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive),
    ];

    [Theory]
    [InlineData(FollowingTier.Small)]
    [InlineData(FollowingTier.Mid)]
    public void UnderFollowed_Corroborated_SubWatch_Opportunity_Is_Floored_To_Watch(FollowingTier tier)
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(50)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: tier));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Contains("corroborating positive signal types", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("2", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("30", result.Rationale, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FollowingTier.Large)]
    [InlineData(FollowingTier.Mega)]
    public void Already_Followed_Company_Is_Not_Floored(FollowingTier tier)
    {
        // Tier gate: a well-followed name with the same corroborated set still falls to Ignore, so the
        // spec-117 notedness posture (noticed mega-caps stay low) is preserved.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(50)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: tier));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
    }

    [Fact]
    public void Single_Positive_Signal_Type_Does_Not_Floor()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current,
            null,
            ContributingSignals: [SignalRef(SignalType.CustomerWin, SignalDirection.Positive)],
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
    }

    [Fact]
    public void Two_Rows_Of_Same_Positive_Type_Do_Not_Floor()
    {
        // Corroboration is measured in DISTINCT types: the same phrase matched twice is one axis.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current,
            null,
            ContributingSignals:
            [
                SignalRef(SignalType.CustomerWin, SignalDirection.Positive),
                SignalRef(SignalType.CustomerWin, SignalDirection.Positive),
            ],
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
    }

    [Theory]
    [InlineData(SignalDirection.Neutral)]
    [InlineData(SignalDirection.Negative)]
    public void NonPositive_Signal_Directions_Do_Not_Corroborate(SignalDirection direction)
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current,
            null,
            ContributingSignals:
            [
                SignalRef(SignalType.CustomerWin, direction),
                SignalRef(SignalType.StrategicPartnership, direction),
            ],
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
    }

    [Fact]
    public void Below_Neutral_Trajectory_Is_Not_Floored()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(49)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
    }

    [Fact]
    public void Empty_Contributing_Signal_Set_Is_Not_Floored()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(current, null));

        Assert.Equal(RadarReportAction.Ignore, result.Action);
        Assert.Empty(new ReportActionContext(current, null).ContributingSignals);
    }

    [Theory]
    [InlineData(40, RadarReportAction.Watch)]
    [InlineData(60, RadarReportAction.Investigate)]
    public void Floor_Does_Not_Fire_At_Or_Above_The_Normal_Thresholds(
        int opportunity, RadarReportAction expected)
    {
        // At/above the Watch line the normal branch already decides; the floor must not restate it and
        // must never lift an Investigate-grade company anywhere.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(opportunity)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: FollowingTier.Small));

        Assert.Equal(expected, result.Action);
        Assert.DoesNotContain("corroborating", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Floor_Never_Overrides_Thin_Evidence()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(34)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.NeedsMoreEvidence, result.Action);
    }

    [Fact]
    public void Floor_Never_Overrides_Deteriorating_Thesis()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(55)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(70).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, previous, ContributingSignals: CorroboratedSignals(),
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.ThesisDeteriorating, result.Action);
    }

    [Fact]
    public void Floor_Never_Overrides_Improving_Thesis()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(60)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, previous, ContributingSignals: CorroboratedSignals(),
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.ThesisImproving, result.Action);
    }

    [Fact]
    public void Floored_Rationale_Is_Free_Of_Advice_Language()
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(50)
            .WithOpportunityScore(12)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: CorroboratedSignals(), FollowingTier: FollowingTier.Small));

        Assert.Contains(result.Action, AllowedActions);
        Assert.False(string.IsNullOrWhiteSpace(result.Rationale));
        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, result.Rationale, StringComparison.OrdinalIgnoreCase);
        }
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
