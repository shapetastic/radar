using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.DenominatorAudit;

/// <summary>
/// Pins the spec-172 observation-building rules: consecutive SNAPSHOT pairs (not consecutive calendar days),
/// later-minus-earlier deltas, later-snapshot link counts, and the "not Neutral" directional classification —
/// including the unparseable case, which counts as directional.
/// </summary>
public sealed class ScoreMoveDenominatorAuditBuilderTests
{
    private static readonly Guid CompanyId = Guid.NewGuid();

    private static ScoreSnapshotWithLinks Point(
        DateTimeOffset windowEnd,
        int opportunity,
        int trajectory,
        DateTimeOffset? createdAt = null,
        params string[] linkReasons)
    {
        var snapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(CompanyId)
            .WithOpportunityScore(opportunity)
            .WithTrajectoryScore(trajectory)
            .WithWindow(windowEnd.AddDays(-30), windowEnd)
            .WithCreatedAtUtc(createdAt ?? windowEnd)
            .Build();

        var links = linkReasons
            .Select(reason => new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshot.Id,
                SignalId: Guid.NewGuid(),
                EvidenceId: Guid.NewGuid(),
                ContributionReason: reason,
                ContributionWeight: 3))
            .ToList();

        return new ScoreSnapshotWithLinks(snapshot, links);
    }

    private static DateTimeOffset Day(int day) => new(2026, 7, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoSnapshots_OnePair_DeltasAreLaterMinusEarlier_CountsComeFromTheLaterSnapshot()
    {
        var series = new[]
        {
            Point(Day(1), opportunity: 40, trajectory: 55,
                linkReasons: ["MediaAttention (Neutral), strength 2, confidence 0.60"]),
            Point(Day(2), opportunity: 57, trajectory: 50, linkReasons:
            [
                "GuidanceChange (Positive), strength 8, confidence 0.90",
                "MediaAttention (Neutral), strength 2, confidence 0.60",
                "InsiderBuying (Negative), strength 4, confidence 0.70",
            ]),
        };

        var observation = Assert.Single(ScoreMoveDenominatorAudit.BuildObservations("default", series));

        Assert.Equal("default", observation.StrategyName);
        Assert.Equal(CompanyId, observation.CompanyId);
        Assert.Equal(new DateOnly(2026, 7, 2), observation.AsOfDate);
        Assert.Equal(17, observation.DeltaOpportunity);   // later minus earlier
        Assert.Equal(-5, observation.DeltaTrajectory);
        Assert.Equal(17, observation.AbsDeltaOpportunity);
        Assert.Equal(3, observation.LinkCount);           // the LATER snapshot's links
        Assert.Equal(2, observation.DirectionalCount);    // Positive + Negative; Neutral excluded
    }

    [Fact]
    public void SingleSnapshot_ContributesNoPair_AndIsNotAnError()
    {
        var series = new[] { Point(Day(1), 40, 50) };

        Assert.Empty(ScoreMoveDenominatorAudit.BuildObservations("default", series));
    }

    [Fact]
    public void EmptySeries_ContributesNoPair()
    {
        Assert.Empty(ScoreMoveDenominatorAudit.BuildObservations("default", []));
    }

    [Fact]
    public void GapInAsOfDates_StillPairsConsecutiveSnapshots_NotConsecutiveCalendarDays()
    {
        // Snapshots at day 1, day 2, day 11: the 9-day gap does NOT break the pairing — the rule is
        // consecutive SNAPSHOTS in as-of order, pinned here.
        var series = new[]
        {
            Point(Day(1), 40, 50),
            Point(Day(2), 45, 50),
            Point(Day(11), 60, 50),
        };

        var observations = ScoreMoveDenominatorAudit.BuildObservations("default", series);

        Assert.Equal(2, observations.Count);
        Assert.Equal(new DateOnly(2026, 7, 2), observations[0].AsOfDate);
        Assert.Equal(5, observations[0].DeltaOpportunity);
        Assert.Equal(new DateOnly(2026, 7, 11), observations[1].AsOfDate);
        Assert.Equal(15, observations[1].DeltaOpportunity); // 60 - 45, across the gap
    }

    [Fact]
    public void UnsortedInput_IsWalkedInAsOfOrder_AnchoredOnWindowEndUtc()
    {
        // The later-windowed snapshot deliberately carries the EARLIER CreatedAtUtc (a replay writes
        // history with the replaying process's wall clock): the as-of anchor is WindowEndUtc.
        var later = Point(Day(9), 70, 50, createdAt: Day(1));
        var earlier = Point(Day(3), 40, 50, createdAt: Day(2));

        var observation = Assert.Single(
            ScoreMoveDenominatorAudit.BuildObservations("default", [later, earlier]));

        Assert.Equal(30, observation.DeltaOpportunity); // 70 - 40, in WindowEndUtc order
        Assert.Equal(new DateOnly(2026, 7, 9), observation.AsOfDate);
    }

    [Fact]
    public void AllNeutralLinksOnTheLaterSnapshot_DirectionalCountIsZero_LinkCountIsNot()
    {
        var series = new[]
        {
            Point(Day(1), 40, 50),
            Point(Day(2), 45, 50, linkReasons:
            [
                "MediaAttention (Neutral), strength 2, confidence 0.60",
                "MediaAttention (Neutral), strength 1, confidence 0.50",
            ]),
        };

        var observation = Assert.Single(ScoreMoveDenominatorAudit.BuildObservations("default", series));

        Assert.Equal(2, observation.LinkCount);
        Assert.Equal(0, observation.DirectionalCount);
    }

    // ------------------------------------------------------------------------------------------------
    // The classification rule, pinned (spec 172): classify by the FIRST parenthesised direction token;
    // "(Neutral)" is neutral, anything else — Positive, Negative, Mixed, and an UNPARSEABLE reason —
    // counts as directional, per the spec's "not Neutral" wording.
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("MediaAttention (Neutral), strength 2, confidence 0.60", false)]
    [InlineData("GuidanceChange (Positive), strength 8, confidence 0.90", true)]
    [InlineData("InsiderBuying (Negative), strength 4, confidence 0.70", true)]
    [InlineData("CustomerWin (Mixed), strength 3, confidence 0.50", true)]
    // Spec-109/151 suffixes ride AFTER the direction token; the first parenthesised token still decides.
    [InlineData("MediaAttention (Neutral), strength 2, confidence 0.60 — via channel rss (collector attribution inferred)", false)]
    [InlineData("GuidanceChange (Positive), strength 8, confidence 0.90 (corroborated by 3 duplicates)", true)]
    // A bare token with surrounding whitespace still parses.
    [InlineData("( Neutral )", false)]
    public void IsDirectional_ClassifiesByTheFirstParenthesisedDirectionToken(string reason, bool expected)
    {
        Assert.Equal(expected, ScoreMoveDenominatorAudit.IsDirectional(reason));
    }

    [Theory]
    [InlineData("no direction token here")]     // no parentheses at all
    [InlineData("broken (Neutral")]             // opening but no closing parenthesis
    [InlineData("")]                            // empty
    [InlineData(null)]                          // null
    public void IsDirectional_UnparseableReason_CountsAsDirectional(string? reason)
    {
        Assert.True(ScoreMoveDenominatorAudit.IsDirectional(reason));
    }
}
