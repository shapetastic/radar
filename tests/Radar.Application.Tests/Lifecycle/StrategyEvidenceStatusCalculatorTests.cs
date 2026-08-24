using Radar.Application.Efficacy.Claims;
using Radar.Application.Lifecycle;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Lifecycle;

/// <summary>
/// Spec 184 §1: the pure facts→status mapping. Noise is never converted into pass/fail ahead of the
/// precommitted gate; Ranked always carries its numbers; unreadable evidence degrades the display.
/// </summary>
public sealed class StrategyEvidenceStatusCalculatorTests
{
    private const string VerdictId = "a1b2c3d4e5f6";

    private static ScoringStrategyDefinition Strategy(string name, bool isPrimary = false) =>
        new(name, name, new ScoringWeights(), isPrimary);

    private static readonly IReadOnlyList<ScoringStrategyDefinition> Strategies =
    [
        Strategy("default", isPrimary: true),
        Strategy("alpha"),
    ];

    private static PairedGateFact Gate(
        string primary = "alpha",
        bool predeclared = true,
        bool boundary = true,
        bool qualifies = false,
        string reasons = "",
        string verdictId = VerdictId) =>
        new(primary, predeclared, boundary, qualifies, reasons, verdictId);

    [Fact]
    public void NoReadableArtifacts_DegradeToAccruingEvidenceUnavailable_ForEveryArm()
    {
        var statuses = StrategyEvidenceStatusCalculator.Compute(EfficacyEvidenceFacts.Unavailable, Strategies);

        Assert.All(Strategies, s =>
        {
            var status = statuses[s.Name];
            Assert.Equal(StrategyEvidenceStatusKind.Accruing, status.Kind);
            Assert.True(status.EvidenceUnavailable);
        });
    }

    [Fact]
    public void RankedRow_YieldsRankedStatus_WithItsNumbers()
    {
        var numbers = new RankedEvidence(1, -0.05, -0.30, 0.20, 72);
        var facts = new EfficacyEvidenceFacts(
            true,
            [new LeaderboardStrategyFact("default", true, numbers, null)],
            false,
            null);

        var statuses = StrategyEvidenceStatusCalculator.Compute(facts, Strategies);

        Assert.Equal(StrategyEvidenceStatusKind.Ranked, statuses["default"].Kind);
        Assert.Same(numbers, statuses["default"].Ranked);
        Assert.True(numbers.CiSpansZero);

        // alpha has no row at all → Accruing (a dropped/absent arm is accruing, not hidden).
        Assert.Equal(StrategyEvidenceStatusKind.Accruing, statuses["alpha"].Kind);
        Assert.False(statuses["alpha"].EvidenceUnavailable);
    }

    [Fact]
    public void DroppedRow_YieldsAccruing_WithTheDropReason()
    {
        var facts = new EfficacyEvidenceFacts(
            true,
            [new LeaderboardStrategyFact("alpha", false, null, "insufficient-in-sample-observations")],
            false,
            null);

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.Accruing, status.Kind);
        Assert.Equal("insufficient-in-sample-observations", status.Detail);
    }

    [Fact]
    public void QualifyingCompositeGate_YieldsGatePassed_ForThePredeclaredPrimaryOnly()
    {
        var facts = new EfficacyEvidenceFacts(false, [], true, Gate(qualifies: true));

        var statuses = StrategyEvidenceStatusCalculator.Compute(facts, Strategies);

        Assert.Equal(StrategyEvidenceStatusKind.GatePassed, statuses["alpha"].Kind);
        // default is NOT the arm under confirmatory test → leaderboard-derived status (here unavailable).
        Assert.Equal(StrategyEvidenceStatusKind.Accruing, statuses["default"].Kind);
    }

    [Fact]
    public void MeritOnlyReasons_YieldGateFailed()
    {
        var reasons = "baseline 'baseline-x': " + Ad15GateReasonCodes.MedianPairedDeltaNotPositive
            + "; baseline 'baseline-y': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive;
        var facts = new EfficacyEvidenceFacts(false, [], true, Gate(reasons: reasons));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GateFailed, status.Kind);
        Assert.Contains(Ad15GateReasonCodes.MedianPairedDeltaNotPositive, status.Detail);
    }

    [Fact]
    public void AccrualOrPrerequisiteReasons_YieldGatePending_NeverFailed()
    {
        // A merit reason BESIDE an accrual reason is still pending: the gate has not evaluated everywhere,
        // and "not enough data yet" must never read as a negative result (spec 184 §1).
        var reasons = "baseline 'baseline-x': " + Ad15GateReasonCodes.MedianPairedDeltaNotPositive
            + "; baseline 'baseline-y': " + Ad15GateReasonCodes.InsufficientPurgedBlocks
            + " (admitted 4, need at least 6 at 95%)";
        var facts = new EfficacyEvidenceFacts(false, [], true, Gate(reasons: reasons));

        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"].Kind);
    }

    [Fact]
    public void NotPredeclared_OrNoBoundary_MeansNoGateStatusAtAll()
    {
        var notPredeclared = new EfficacyEvidenceFacts(false, [], true, Gate(predeclared: false));
        var noBoundary = new EfficacyEvidenceFacts(false, [], true, Gate(boundary: false));

        Assert.Equal(
            StrategyEvidenceStatusKind.Accruing,
            StrategyEvidenceStatusCalculator.Compute(notPredeclared, Strategies)["alpha"].Kind);
        Assert.Equal(
            StrategyEvidenceStatusKind.Accruing,
            StrategyEvidenceStatusCalculator.Compute(noBoundary, Strategies)["alpha"].Kind);
    }

    [Fact]
    public void GateStatus_CarriesTheDescriptiveRankedNumbersBesideIt()
    {
        var numbers = new RankedEvidence(2, 0.10, -0.05, 0.25, 40);
        var facts = new EfficacyEvidenceFacts(
            true,
            [new LeaderboardStrategyFact("alpha", true, numbers, null)],
            true,
            Gate(reasons: Ad15GateReasonCodes.NoEligibleBlocks));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GatePending, status.Kind);
        Assert.Same(numbers, status.Ranked); // descriptive and confirmatory facts are orthogonal
    }

    [Fact]
    public void GateVerdicts_ExistOnlyForPassedOrFailed_CarryingTheSemanticVerdictId()
    {
        var pending = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: Ad15GateReasonCodes.Ad16ScreenPending));
        Assert.Empty(StrategyEvidenceStatusCalculator.GateVerdicts(pending, Strategies));

        var passed = new EfficacyEvidenceFacts(false, [], true, Gate(qualifies: true));
        var verdict = Assert.Single(StrategyEvidenceStatusCalculator.GateVerdicts(passed, Strategies));
        Assert.Equal("alpha", verdict.StrategyName);
        Assert.True(verdict.Passed);
        Assert.Equal(VerdictId, verdict.VerdictId);

        var failed = new EfficacyEvidenceFacts(
            false, [], true,
            Gate(reasons: "baseline 'x': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive));
        var failedVerdict = Assert.Single(StrategyEvidenceStatusCalculator.GateVerdicts(failed, Strategies));
        Assert.False(failedVerdict.Passed);
    }

    [Fact]
    public void RankedStatus_CannotBeConstructedWithoutItsNumbers()
    {
        Assert.Throws<ArgumentNullException>(() => StrategyEvidenceStatus.RankedStatus(null!));
    }
}
