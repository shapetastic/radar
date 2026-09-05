using Radar.Application.Reporting;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
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
        // v3 (spec 210): the Watch-floor rationale names each counted type's support tuples; labels, the
        // count and the threshold are byte-identical to v2.
        Assert.Equal("weekly-report-action-v3", CreatePolicy().Version);
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

    // ---- Spec 210: the floor NAMES what it counted (v3) --------------------------------------------
    //
    // Labels, the count and the threshold are byte-identical to v2 (proven by the sweep at the end);
    // only the rationale contract moved. The stored SignalType token is rendered — the renderer's
    // display relabel (GuidanceChange -> EarningsTrajectory, spec 167) never leaks into a rationale.

    private static readonly DateTimeOffset Sep2 = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sep2Later = new(2026, 9, 2, 21, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sep3 = new(2026, 9, 3, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Aug1 = new(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Aug15 = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);

    private const string FloorPrefix =
        "Opportunity 30 below 40 but 2 corroborating positive signal types across an under-followed name; floored to Watch (not Ignore): ";

    private static ReportSignalRef SignalRef(
        SignalType type,
        SignalDirection direction,
        DateTimeOffset? observedAtUtc,
        EvidenceSourceType? sourceType,
        bool? isJudgmentDerived) =>
        new(Guid.NewGuid(), type, direction, $"{type} ({direction}).", observedAtUtc, sourceType, isJudgmentDerived);

    private static ReportActionResult DecideFloorCandidate(params ReportSignalRef[] signals)
    {
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(50)
            .WithOpportunityScore(30)
            .WithEvidenceConfidenceScore(70)
            .Build();

        return CreatePolicy().Decide(new ReportActionContext(
            current, null, ContributingSignals: signals, FollowingTier: FollowingTier.Small));
    }

    [Fact]
    public void Floor_Rationale_Makes_A_SameDay_CrossExtractor_Pair_Visible()
    {
        // The hypothesized echo: one announcement wearing two extractors' clothes — a filing-typed
        // positive plus a judgment-derived MediaAttention citing SAME-DAY coverage. Two distinct types
        // satisfy the count exactly as before; the rationale must put both same-day tuples on one line.
        var result = DecideFloorCandidate(
            SignalRef(SignalType.GuidanceChange, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Equal(
            FloorPrefix + "GuidanceChange (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment).",
            result.Rationale);
        Assert.Contains("2 corroborating positive signal types", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Floor_Rationale_Makes_Live_Ooma_2026_08_26_Same_Day_Echo_Visible()
    {
        // LIVE case (spec 210 §3 audit, radar-weekly-2026-08-30): Ooma (OOMA) was floored on exactly two
        // positive types — GuidanceChange from the 2026-08-26 earnings 8-K (filing read, not judgment-
        // derived) and a judgment-derived MediaAttention from SAME-DAY coverage of that same release. The
        // count fires on two distinct types; the v3 rationale puts both 2026-08-26 tuples on one line so
        // the reader can see it is one event echoed through two extractors. The label is unchanged (Watch);
        // whether that floor SHOULD hold is the maintainer's call with this line in hand, not this spec's.
        var oomaAug26 = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(52)
            .WithOpportunityScore(33)
            .WithEvidenceConfidenceScore(48)
            .Build();

        var result = CreatePolicy().Decide(new ReportActionContext(
            current,
            null,
            ContributingSignals:
            [
                SignalRef(SignalType.GuidanceChange, SignalDirection.Positive, oomaAug26, EvidenceSourceType.Filing, false),
                SignalRef(SignalType.MediaAttention, SignalDirection.Positive, oomaAug26, EvidenceSourceType.NewsArticle, true),
            ],
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Contains(
            "GuidanceChange (filing 2026-08-26) + MediaAttention (news 2026-08-26, judgment)",
            result.Rationale,
            StringComparison.Ordinal);
        Assert.Contains("2 corroborating positive signal types", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Floor_Rationale_Renders_Every_Distinct_Tuple_Of_A_Type_In_Date_Order()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep3, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment; news 2026-09-03, judgment).",
            result.Rationale);
    }

    [Fact]
    public void Two_Positive_Signals_Of_One_Type_From_The_Same_Source_Date_And_Flag_Render_One_Tuple()
    {
        // Distinct SUPPORT, not row count: two same-day judgment-derived news signals of one type are one
        // tuple (a different time of day is the same date).
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2Later, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment).",
            result.Rationale);
    }

    [Fact]
    public void Same_Date_Tuples_Order_By_Source_Class_Then_Judgment_Flag()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.PressRelease, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, false));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (news 2026-09-02; news 2026-09-02, judgment; press release 2026-09-02).",
            result.Rationale);
    }

    [Fact]
    public void Null_Provenance_Renders_As_Unknown_Never_As_False_Or_Absent()
    {
        // A ref built without provenance (every pre-210 construction site) still floors, and every
        // missing member is SAID to be unknown — never rendered as "not judgment-derived" or dropped.
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, null, null, null),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, null, null, null));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Equal(
            FloorPrefix + "CustomerWin (source unknown date unknown, judgment unknown) + StrategicPartnership (source unknown date unknown, judgment unknown).",
            result.Rationale);
    }

    [Fact]
    public void Null_Judgment_Flag_Renders_Unknown_While_False_Renders_Nothing()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, null),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, Sep2, EvidenceSourceType.PressRelease, false));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02, judgment unknown) + StrategicPartnership (press release 2026-09-02).",
            result.Rationale);
    }

    [Fact]
    public void Unknown_Dates_Sort_After_Known_Dates_Within_A_Type()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, null, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep3, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, Sep2, EvidenceSourceType.PressRelease, false));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-03; filing date unknown) + StrategicPartnership (press release 2026-09-02).",
            result.Rationale);
    }

    [Fact]
    public void Exactly_At_The_Cap_Every_Tuple_Is_Still_Rendered()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug1, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug15, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (news 2026-08-01, judgment; news 2026-08-15, judgment; news 2026-09-02, judgment).",
            result.Rationale);
    }

    [Fact]
    public void Above_The_Cap_A_Type_Renders_Its_Date_Range_Unknown_Date_Count_And_Tuple_Count()
    {
        // Five distinct tuples (> cap of 3): three distinct known dates, one unknown-date tuple. The
        // summary must state the range AND the unknown count AND the tuple count — never a chosen subset.
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug1, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug1, EvidenceSourceType.PressRelease, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug15, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, null, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (3 distinct dates 2026-08-01–2026-09-02 (+1 date unknown), 5 support tuples).",
            result.Rationale);
    }

    [Fact]
    public void Above_The_Cap_Without_Unknown_Dates_Omits_The_Parenthetical()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug1, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug1, EvidenceSourceType.PressRelease, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Aug15, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (3 distinct dates 2026-08-01–2026-09-02, 4 support tuples).",
            result.Rationale);
    }

    [Fact]
    public void Above_The_Cap_On_One_Date_Renders_The_Singular_Date_Form()
    {
        // Four sources on one day: a range "d–d" would read as two dates, so the single date is stated.
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.PressRelease, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.RssFeed, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.CompanyBlog, true));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (1 distinct date 2026-09-02, 4 support tuples).",
            result.Rationale);
    }

    [Fact]
    public void Every_Defined_Source_Type_Maps_To_A_Named_Class_And_Only_Unknowns_Fall_Back()
    {
        foreach (var sourceType in Enum.GetValues<EvidenceSourceType>())
        {
            var described = WeeklyReportActionPolicyV1.DescribeSourceClass(sourceType);
            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.NotEqual("source unknown", described);
        }

        Assert.Equal("source unknown", WeeklyReportActionPolicyV1.DescribeSourceClass(null));
        Assert.Equal("source unknown", WeeklyReportActionPolicyV1.DescribeSourceClass((EvidenceSourceType)999));
    }

    [Fact]
    public void Counted_Types_Are_Named_In_Enum_Order_Regardless_Of_Input_Order()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, Sep2, EvidenceSourceType.PressRelease, false),
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep3, EvidenceSourceType.Filing, false));

        Assert.Equal(RadarReportAction.Watch, result.Action);
        Assert.EndsWith(
            "but 3 corroborating positive signal types across an under-followed name; floored to Watch (not Ignore): CustomerWin (filing 2026-09-03) + StrategicPartnership (press release 2026-09-02) + MediaAttention (news 2026-09-02, judgment).",
            result.Rationale,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_And_Neutral_Signals_Of_A_Counted_Type_Never_Appear_As_Support()
    {
        var result = DecideFloorCandidate(
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.CustomerWin, SignalDirection.Negative, Sep3, EvidenceSourceType.NewsArticle, false),
            SignalRef(SignalType.MediaAttention, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
            SignalRef(SignalType.MediaAttention, SignalDirection.Neutral, Sep3, EvidenceSourceType.NewsArticle, false));

        Assert.Equal(
            FloorPrefix + "CustomerWin (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment).",
            result.Rationale);
    }

    [Theory]
    [MemberData(nameof(RepresentativeMatrix))]
    public void Provenance_Changes_Only_The_Rationale_Never_The_Label(
        int trajectory, int opportunity, int evidence, int? previousTrajectory)
    {
        // The v2 -> v3 contract: for the same context, refs WITH provenance and refs WITHOUT decide the
        // same label on every tier; the rationale differs only where the floor fired (it now names the
        // support) and is byte-identical everywhere else.
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(trajectory)
            .WithOpportunityScore(opportunity)
            .WithEvidenceConfidenceScore(evidence)
            .Build();
        var previous = previousTrajectory is null
            ? null
            : new ScoreSnapshotBuilder().WithTrajectoryScore(previousTrajectory.Value).Build();

        IReadOnlyList<ReportSignalRef> bare =
        [
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, null, null, null),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, null, null, null),
        ];
        IReadOnlyList<ReportSignalRef> populated =
        [
            SignalRef(SignalType.CustomerWin, SignalDirection.Positive, Sep2, EvidenceSourceType.Filing, false),
            SignalRef(SignalType.StrategicPartnership, SignalDirection.Positive, Sep2, EvidenceSourceType.NewsArticle, true),
        ];

        foreach (var tier in Enum.GetValues<FollowingTier>())
        {
            var withoutProvenance = CreatePolicy().Decide(new ReportActionContext(
                current, previous, ContributingSignals: bare, FollowingTier: tier));
            var withProvenance = CreatePolicy().Decide(new ReportActionContext(
                current, previous, ContributingSignals: populated, FollowingTier: tier));

            Assert.Equal(withoutProvenance.Action, withProvenance.Action);
            if (withoutProvenance.Rationale.Contains("corroborating", StringComparison.Ordinal))
            {
                Assert.Equal(RadarReportAction.Watch, withProvenance.Action);
                Assert.NotEqual(withoutProvenance.Rationale, withProvenance.Rationale);
                Assert.Contains("source unknown date unknown, judgment unknown", withoutProvenance.Rationale, StringComparison.Ordinal);
                Assert.Contains("CustomerWin (filing 2026-09-02) + StrategicPartnership (news 2026-09-02, judgment)", withProvenance.Rationale, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(withoutProvenance.Rationale, withProvenance.Rationale);
            }
        }
    }
}
