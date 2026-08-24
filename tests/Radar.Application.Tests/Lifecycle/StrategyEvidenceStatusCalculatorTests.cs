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

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 §5 — a CURRENT artifact's structured verdict identity outranks its rendered reason text
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void CurrentArtifact_FailedVerdictId_YieldsGateFailed_EvenWhenABaselineNameEmbedsANonMeritToken()
    {
        // THE REGRESSION (spec 187 §5). The baseline is literally named `no-eligible-blocks-baseline`, so
        // the rendered reason text CONTAINS the non-merit token `no-eligible-blocks` — and the pre-187
        // substring rule therefore read this as "the gate could not evaluate" and returned GatePending,
        // while GateVerdicts(...) carried the artifact's real FAILED verdict id for the same facts. The
        // writer only emits an id once the composite gate has reached a merit verdict, so the structured
        // fields decide and the prose is display detail.
        var reasons = "baseline 'no-eligible-blocks-baseline': "
            + Ad15GateReasonCodes.MedianPairedDeltaNotPositive;
        var facts = new EfficacyEvidenceFacts(false, [], true, Gate(qualifies: false, reasons: reasons));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GateFailed, status.Kind);
        Assert.Equal(reasons, status.Detail); // the reasons still ride along, as DISPLAY detail

        // …and the verdict carried out of the same facts is the very id the decision was taken from.
        var verdict = Assert.Single(StrategyEvidenceStatusCalculator.GateVerdicts(facts, Strategies));
        Assert.Equal("alpha", verdict.StrategyName);
        Assert.False(verdict.Passed);
        Assert.Equal(VerdictId, verdict.VerdictId);
    }

    [Fact]
    public void CurrentArtifact_PassedVerdictId_YieldsGatePassed_EvenWhenTheReasonTextLooksLikeAFailure()
    {
        // The symmetric case: a qualifying verdict whose (residual, informational) reason text quotes
        // merit-code-looking noise must still read as GatePassed, carrying the same id.
        var facts = new EfficacyEvidenceFacts(
            false,
            [],
            true,
            Gate(
                qualifies: true,
                reasons: "baseline '" + Ad15GateReasonCodes.MedianPairedDeltaNotPositive + "-control': "
                    + Ad15GateReasonCodes.IntervalLowerBoundNotPositive));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GatePassed, status.Kind);

        var verdict = Assert.Single(StrategyEvidenceStatusCalculator.GateVerdicts(facts, Strategies));
        Assert.True(verdict.Passed);
        Assert.Equal(VerdictId, verdict.VerdictId);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 §5 — the LEGACY (pre-186, no-id) path: exact reason CODES, fail closed, no fabricated id
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LegacyArtifact_MeritOnlyReasons_YieldGateFailed_WithTheEmptyVerdictId()
    {
        var reasons = "baseline 'baseline-x': " + Ad15GateReasonCodes.MedianPairedDeltaNotPositive
            + "; baseline 'baseline-y': " + Ad15GateReasonCodes.IntervalLowerBoundNotPositive
            + " (admitted 7, need at least 6 at 95%)";
        var facts = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: reasons, verdictId: string.Empty));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GateFailed, status.Kind);

        // No id is fabricated for a pre-186 artifact: the verdict exists but can never match an override.
        var verdict = Assert.Single(StrategyEvidenceStatusCalculator.GateVerdicts(facts, Strategies));
        Assert.False(verdict.Passed);
        Assert.Equal(string.Empty, verdict.VerdictId);
    }

    [Fact]
    public void LegacyArtifact_AccrualOrPrerequisiteReasons_YieldGatePending_NeverFailed()
    {
        // A merit reason BESIDE an accrual reason is still pending: the gate has not evaluated everywhere,
        // and "not enough data yet" must never read as a negative result (spec 184 §1).
        var reasons = "baseline 'baseline-x': " + Ad15GateReasonCodes.MedianPairedDeltaNotPositive
            + "; baseline 'baseline-y': " + Ad15GateReasonCodes.InsufficientPurgedBlocks
            + " (admitted 4, need at least 6 at 95%)";
        var facts = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: reasons, verdictId: string.Empty));

        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"].Kind);
        Assert.Empty(StrategyEvidenceStatusCalculator.GateVerdicts(facts, Strategies));
    }

    [Fact]
    public void LegacyArtifact_BlankOrUnparseableReasons_StayPending_NeverFailed()
    {
        // Fail CLOSED: an absent explanation, and a segment that is not a code at all, are both "cannot
        // tell" — which must never render as a negative RESULT.
        var blank = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: string.Empty, verdictId: string.Empty));
        var noise = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: "the gate could not be evaluated", verdictId: string.Empty));

        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(blank, Strategies)["alpha"].Kind);
        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(noise, Strategies)["alpha"].Kind);
    }

    [Fact]
    public void LegacyArtifact_BaselineNameEmbeddingACodeToken_IsStrippedBeforeTheCodeIsCompared()
    {
        // The prefix strip is what makes the legacy path exact rather than substring-based. Both fixtures
        // carry a baseline NAME containing a reason-code token; only the parsed CODES may decide.
        var meritOnly = new EfficacyEvidenceFacts(
            false,
            [],
            true,
            Gate(
                reasons: "baseline 'no-eligible-blocks-baseline': "
                    + Ad15GateReasonCodes.MedianPairedDeltaNotPositive,
                verdictId: string.Empty));

        var spoofed = new EfficacyEvidenceFacts(
            false,
            [],
            true,
            Gate(
                reasons: "baseline '" + Ad15GateReasonCodes.MedianPairedDeltaNotPositive + "-baseline': "
                    + Ad15GateReasonCodes.NoEligibleBlocks,
                verdictId: string.Empty));

        // Merit-only DESPITE the non-merit token in the name → failed (pre-187 this read pending).
        Assert.Equal(
            StrategyEvidenceStatusKind.GateFailed,
            StrategyEvidenceStatusCalculator.Compute(meritOnly, Strategies)["alpha"].Kind);

        // …and the mirror image: the only CODE is an accrual reason, so the merit token planted in the
        // baseline name contributes nothing and the status stays pending.
        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(spoofed, Strategies)["alpha"].Kind);

        // The sharpest spoof, and the one the old rule genuinely got wrong: reason PROSE carrying a merit
        // token and no code at all. Substring matching read that as an evaluated failure; exact parsing
        // finds no code, so it can only hold at pending.
        var prose = new EfficacyEvidenceFacts(
            false,
            [],
            true,
            Gate(
                reasons: "baseline '" + Ad15GateReasonCodes.MedianPairedDeltaNotPositive
                    + "-baseline' was withdrawn before evaluation",
                verdictId: string.Empty));
        Assert.Equal(
            StrategyEvidenceStatusKind.GatePending,
            StrategyEvidenceStatusCalculator.Compute(prose, Strategies)["alpha"].Kind);
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
        // A PENDING gate carries no verdict identity by construction (GateVerdictIdentity.VerdictExists is
        // false for an accrual reason, so the artifact's id column is empty) — which is exactly the shape
        // this fixture uses.
        var numbers = new RankedEvidence(2, 0.10, -0.05, 0.25, 40);
        var facts = new EfficacyEvidenceFacts(
            true,
            [new LeaderboardStrategyFact("alpha", true, numbers, null)],
            true,
            Gate(reasons: Ad15GateReasonCodes.NoEligibleBlocks, verdictId: string.Empty));

        var status = StrategyEvidenceStatusCalculator.Compute(facts, Strategies)["alpha"];

        Assert.Equal(StrategyEvidenceStatusKind.GatePending, status.Kind);
        Assert.Same(numbers, status.Ranked); // descriptive and confirmatory facts are orthogonal
    }

    [Fact]
    public void GateVerdicts_ExistOnlyForPassedOrFailed_CarryingTheSemanticVerdictId()
    {
        // Pending ⇒ the artifact states no verdict, so its id column is empty (spec 186 §3's VerdictExists).
        var pending = new EfficacyEvidenceFacts(
            false, [], true, Gate(reasons: Ad15GateReasonCodes.Ad16ScreenPending, verdictId: string.Empty));
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
