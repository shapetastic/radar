using Radar.Application.Collectors;
using Radar.Application.Efficacy.Attention;
using Radar.Application.Pipeline;

namespace Radar.Application.Tests.Efficacy.Attention;

/// <summary>
/// AD-16 §5 as corrected by its 2026-08-03 amendment (spec 169). Every test here defends the same property:
/// a failed, capped, partial or unrecorded collection window must NEVER be able to produce a publisher count
/// of zero, because that is indistinguishable from the genuine negative case the sample is required to keep.
/// </summary>
public sealed class AttentionCoverageEvaluatorTests
{
    private static readonly Guid CompanyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherCompanyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private static AttentionCoverageEvaluator Create() => new(AttentionTestFakes.Options());

    /// <summary>Daily complete checkpoints from a day before the start to a day after the end.</summary>
    private static List<PipelineRunRecord> DailyCompleteRuns(params Guid[] companyIds)
    {
        var runs = new List<PipelineRunRecord>();
        for (var instant = Start.AddDays(-1); instant <= End.AddDays(1); instant = instant.AddDays(1))
        {
            runs.Add(AttentionTestFakes.CompleteCheckpoint(instant, companyIds));
        }

        return runs;
    }

    private static AttentionCoverageResult Evaluate(IReadOnlyList<PipelineRunRecord> runs) =>
        Create().Evaluate(CompanyId, Start, End, runs);

    [Fact]
    public void DailyCompleteRuns_CoverTheInterval()
    {
        var result = Evaluate(DailyCompleteRuns(CompanyId, OtherCompanyId));

        Assert.True(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.Complete, result.Reason);
        Assert.Equal(AttentionCheckpointDisqualification.None, result.Disqualification);
    }

    [Fact]
    public void NoRunsAtAll_IsNotCoverage()
    {
        var result = Evaluate([]);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.NoRunRecords, result.Reason);
    }

    [Fact]
    public void NoCheckpointWithin36HoursBeforeTheStart_BreaksTheChain()
    {
        var runs = DailyCompleteRuns(CompanyId);
        // Drop everything at or before the start: the opening checkpoint is gone.
        runs.RemoveAll(r => r.CreatedAtUtc <= Start);

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.NoCheckpointBeforeStart, result.Reason);
    }

    [Fact]
    public void NoCheckpointWithin36HoursAfterTheEnd_BreaksTheChain()
    {
        var runs = DailyCompleteRuns(CompanyId);
        runs.RemoveAll(r => r.CreatedAtUtc >= End);

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.NoCheckpointAfterEnd, result.Reason);
    }

    [Fact]
    public void AGapWiderThan36Hours_BreaksTheChain()
    {
        var runs = DailyCompleteRuns(CompanyId);
        // Remove two consecutive mid-interval days ⇒ a 72-hour gap between the survivors either side.
        runs.RemoveAll(r => r.CreatedAtUtc == Start.AddDays(5) || r.CreatedAtUtc == Start.AddDays(6));

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.CheckpointGapExceeded, result.Reason);
    }

    [Fact]
    public void AGapOfExactly36Hours_IsStillCovered()
    {
        // The tolerance accommodates ordinary drift in a once-daily job; exactly 36 hours is inside it.
        var runs = new List<PipelineRunRecord>
        {
            AttentionTestFakes.CompleteCheckpoint(Start, CompanyId),
            AttentionTestFakes.CompleteCheckpoint(Start.AddHours(36), CompanyId),
        };

        var mid = Start.AddHours(36);
        var result = Create().Evaluate(CompanyId, Start, mid, runs);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void PartialCollectionRuns_CannotSupplyACheckpoint()
    {
        // Spec 161's company-FILTERED collect pass looked at part of the universe. Even for a company it DID
        // cover, letting it certify a screen AD-16 pairs across "exactly the same eligible companies" would
        // be wrong.
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => r with { CompanyFilter = ["MRCY"] })
            .ToList();

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCheckpointDisqualification.PartialCollectionRun, result.Disqualification);
    }

    [Fact]
    public void ScoreOnlyRuns_CannotSupplyACheckpoint()
    {
        // A score pass collects nothing (spec 144): no collectors, and CollectorRuns null. It observed
        // nothing, so it can prove nothing.
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => r with { Collectors = [], CollectorRuns = null })
            .ToList();

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(
            AttentionCheckpointDisqualification.ScoreOnlyRunWithoutCollection, result.Disqualification);
    }

    [Fact]
    public void LegacyRunsWithoutCollectorRuns_ReadAsUnproven_NeverAsClean()
    {
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => r with { CollectorRuns = null })
            .ToList();

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(
            AttentionCheckpointDisqualification.LegacyCheckpointWithoutCollectorRuns,
            result.Disqualification);
    }

    [Fact]
    public void ARunWhereTheAttentionCollectorDidNotRun_CannotSupplyACheckpoint()
    {
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => r with
            {
                CollectorRuns = [new CollectorRunRecord("sec-edgar", 1, 1, 0, 3, [], null)],
            })
            .ToList();

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(
            AttentionCheckpointDisqualification.AttentionCollectorDidNotRun, result.Disqualification);
    }

    [Fact]
    public void ARunWithNoRecordedCompanyCoverage_CannotSupplyACheckpoint()
    {
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => r with
            {
                CollectorRuns =
                    [new CollectorRunRecord(AttentionTestFakes.NewsSearch, 1, 1, 0, 3, [], null)],
            })
            .ToList();

        Assert.Equal(
            AttentionCheckpointDisqualification.CompanyCoverageNotRecorded,
            Evaluate(runs).Disqualification);
    }

    [Fact]
    public void ACompanyWithNoRowInThatPass_CannotSupplyACheckpoint()
    {
        // Coverage was recorded, but for OTHER companies only: this company was not in that pass's universe.
        var runs = DailyCompleteRuns(OtherCompanyId);

        Assert.Equal(
            AttentionCheckpointDisqualification.CompanyNotInCollectionPass,
            Evaluate(runs).Disqualification);
    }

    [Theory]
    [InlineData(CollectionCoverageIssues.MissingFeed, AttentionCheckpointDisqualification.CompanyFeedMissing)]
    [InlineData(CollectionCoverageIssues.SourceFailure, AttentionCheckpointDisqualification.CompanyFeedFailed)]
    [InlineData(CollectionCoverageIssues.ResultLimitReached, AttentionCheckpointDisqualification.CompanyFeedCapped)]
    [InlineData(
        CollectionCoverageIssues.CollectionHealthMismatch,
        AttentionCheckpointDisqualification.CollectionHealthMismatch)]
    public void EveryIssueToken_BreaksTheChainWithItsOwnReason(
        string issue, AttentionCheckpointDisqualification expected)
    {
        var expectedFeeds = issue == CollectionCoverageIssues.MissingFeed ? 0 : 1;
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => AttentionTestFakes.Checkpoint(
                r.CreatedAtUtc,
                [new CollectorCompanyCoverage(CompanyId, expectedFeeds, expectedFeeds, false, [issue])]))
            .ToList();

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(expected, result.Disqualification);
    }

    [Fact]
    public void ACappedResultIsIncomplete_EvenWithNoIssueTokenRecorded()
    {
        // Defensive: a future collector that forgets the token still fails CLOSED. The flag and the tokens
        // are both consulted, and neither alone can certify a window.
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => AttentionTestFakes.Checkpoint(
                r.CreatedAtUtc,
                [new CollectorCompanyCoverage(CompanyId, 1, 1, HitEffectiveResultLimit: true, Issues: [])]))
            .ToList();

        Assert.Equal(AttentionCheckpointDisqualification.CompanyFeedCapped, Evaluate(runs).Disqualification);
    }

    [Fact]
    public void AFailedFeedIsIncomplete_EvenWithNoIssueTokenRecorded()
    {
        var runs = DailyCompleteRuns(CompanyId)
            .Select(r => AttentionTestFakes.Checkpoint(
                r.CreatedAtUtc,
                [new CollectorCompanyCoverage(CompanyId, 2, 1, false, [])]))
            .ToList();

        Assert.Equal(AttentionCheckpointDisqualification.CompanyFeedFailed, Evaluate(runs).Disqualification);
    }

    [Fact]
    public void OneBrokenMidIntervalCheckpoint_BreaksTheChain_AndNamesTheSpecificCause()
    {
        var runs = DailyCompleteRuns(CompanyId);
        var brokenAt = Start.AddDays(5);
        // Two consecutive broken days, so the surviving complete checkpoints straddle a 72-hour gap.
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].CreatedAtUtc == brokenAt || runs[i].CreatedAtUtc == brokenAt.AddDays(1))
            {
                runs[i] = AttentionTestFakes.Checkpoint(
                    runs[i].CreatedAtUtc,
                    [
                        new CollectorCompanyCoverage(
                            CompanyId, 1, 0, false, [CollectionCoverageIssues.SourceFailure]),
                    ]);
            }
        }

        var result = Evaluate(runs);

        Assert.False(result.IsComplete);
        Assert.Equal(AttentionCoverageReason.CheckpointGapExceeded, result.Reason);
        // The chain reason says WHERE it broke; the disqualification says WHY the run that should have
        // closed the gap could not.
        Assert.Equal(AttentionCheckpointDisqualification.CompanyFeedFailed, result.Disqualification);
    }
}
