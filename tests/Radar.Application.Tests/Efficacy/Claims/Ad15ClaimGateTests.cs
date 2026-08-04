using Radar.Application.Efficacy.Claims;

namespace Radar.Application.Tests.Efficacy.Claims;

/// <summary>
/// The spec-170 composite AD-15 gate: total over the outcome vocabulary, fail-closed on absence and on every
/// uninterpretable state, and structurally unable to qualify from the price side alone.
/// </summary>
public sealed class Ad15ClaimGateTests
{
    private static readonly IReadOnlyList<Ad15GateReason> NoPriceReasons = [];

    private static readonly IReadOnlyList<Ad15GateReason> OnePriceReason =
        [new Ad15GateReason(Ad15GateReasonCodes.NoPrecommittedBoundary)];

    // ---------------------------------------------------------------------------- absence fails closed

    [Fact]
    public void Evaluate_NullPrerequisite_CanNeverQualify_EvenWithAPassingPriceGate()
    {
        var verdict = Ad15ClaimGate.Evaluate(
            satisfiesPriceGate: true, NoPriceReasons, attentionPrerequisite: null);

        Assert.False(verdict.Qualifies);
        Assert.True(verdict.SatisfiesPriceGate);
        Assert.Equal(Ad16ScreenOutcome.NotCalculated, verdict.Prerequisite.Outcome);
        Assert.False(verdict.Prerequisite.WasCalculated);
        var reason = Assert.Single(verdict.Reasons);
        Assert.Equal(Ad15GateReasonCodes.Ad16ScreenNotCalculated, reason.Code);
    }

    // ------------------------------------------------------- the outcome → satisfied/reason state machine

    [Theory]
    [InlineData(Ad16ScreenOutcome.NotCalculated, Ad15GateReasonCodes.Ad16ScreenNotCalculated)]
    [InlineData(Ad16ScreenOutcome.Unavailable, Ad15GateReasonCodes.Ad16ScreenUnavailable)]
    [InlineData(Ad16ScreenOutcome.Pending, Ad15GateReasonCodes.Ad16ScreenPending)]
    [InlineData(Ad16ScreenOutcome.Invalid, Ad15GateReasonCodes.Ad16ScreenInvalid)]
    public void Evaluate_UnmetOutcomes_RefuseTheClaimWithTheirOwnCode(
        Ad16ScreenOutcome outcome, string expectedCode)
    {
        var verdict = Ad15ClaimGate.Evaluate(
            satisfiesPriceGate: true, NoPriceReasons, Ad15AttentionPrerequisite.For(outcome));

        Assert.False(verdict.Qualifies);
        Assert.True(verdict.SatisfiesPriceGate);
        Assert.Equal(expectedCode, Assert.Single(verdict.Reasons).Code);
    }

    [Theory]
    [InlineData(Ad16ScreenOutcome.Miss)]
    [InlineData(Ad16ScreenOutcome.ClearsNecessaryScreen)]
    public void Evaluate_CalculatedOutcomes_SatisfyThePrerequisite_MissIncluded(Ad16ScreenOutcome outcome)
    {
        // AD-15 requires the screen to be CALCULATED, not passed (spec 170's recorded judgement call): a
        // Miss satisfies the prerequisite — the RENDERER is what must state the Miss beside the licence.
        var verdict = Ad15ClaimGate.Evaluate(
            satisfiesPriceGate: true, NoPriceReasons, Ad15AttentionPrerequisite.For(outcome));

        Assert.True(verdict.Qualifies);
        Assert.Empty(verdict.Reasons);
        Assert.True(verdict.Prerequisite.WasCalculated);
    }

    [Fact]
    public void Evaluate_UnrecognisedOutcomeValue_IsCoercedToInvalid_AndNeverSatisfies()
    {
        var prerequisite = Ad15AttentionPrerequisite.For((Ad16ScreenOutcome)999);

        Assert.Equal(Ad16ScreenOutcome.Invalid, prerequisite.Outcome);
        Assert.False(prerequisite.WasCalculated);

        var verdict = Ad15ClaimGate.Evaluate(satisfiesPriceGate: true, NoPriceReasons, prerequisite);
        Assert.False(verdict.Qualifies);
        Assert.Equal(Ad15GateReasonCodes.Ad16ScreenInvalid, Assert.Single(verdict.Reasons).Code);
    }

    // -------------------------------------------------------------------- price side composes, unchanged

    [Fact]
    public void Evaluate_FailedPriceGate_KeepsThePriceReasonsFirst_AndAppendsThePrerequisiteReason()
    {
        var verdict = Ad15ClaimGate.Evaluate(
            satisfiesPriceGate: false, OnePriceReason, attentionPrerequisite: null);

        Assert.False(verdict.Qualifies);
        Assert.False(verdict.SatisfiesPriceGate);
        Assert.Equal(2, verdict.Reasons.Count);
        Assert.Equal(Ad15GateReasonCodes.NoPrecommittedBoundary, verdict.Reasons[0].Code);
        Assert.Equal(Ad15GateReasonCodes.Ad16ScreenNotCalculated, verdict.Reasons[1].Code);
    }

    [Fact]
    public void Evaluate_InconsistentPriceInputs_FailClosed()
    {
        // A true flag beside a non-empty reason list is a caller defect; it must not read as a claim.
        var verdict = Ad15ClaimGate.Evaluate(
            satisfiesPriceGate: true,
            OnePriceReason,
            Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.False(verdict.Qualifies);
    }

    // -------------------------------------------------------------------------------- structural pieces

    [Fact]
    public void Prerequisite_WasCalculated_IsDerivedStructurally_NeverFreelySettable()
    {
        // The invariant "WasCalculated ⇔ Miss|ClearsNecessaryScreen" holds for every constructible value.
        foreach (var outcome in Enum.GetValues<Ad16ScreenOutcome>())
        {
            var prerequisite = Ad15AttentionPrerequisite.For(outcome);
            Assert.Equal(
                outcome is Ad16ScreenOutcome.Miss or Ad16ScreenOutcome.ClearsNecessaryScreen,
                prerequisite.WasCalculated);
        }

        Assert.False(Ad15AttentionPrerequisite.NotCalculated.WasCalculated);
    }

    [Fact]
    public void GateReason_CodeVocabularyIsClosed_AnUnknownCodeIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Ad15GateReason("made-up-code"));
        Assert.Contains("CLOSED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("made-up-code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateReason_Render_ReproducesThePre170TextForEveryMigratedPriceShape()
    {
        Assert.Equal(
            "no-baselines", new Ad15GateReason(Ad15GateReasonCodes.NoBaselines).Render());
        Assert.Equal(
            "baseline 'baseline-x': median-paired-delta-not-positive",
            new Ad15GateReason(
                Ad15GateReasonCodes.MedianPairedDeltaNotPositive, "baseline-x").Render());
        Assert.Equal(
            "baseline 'baseline-x': insufficient-purged-blocks (admitted 4, need at least 6 at 95%)",
            new Ad15GateReason(
                Ad15GateReasonCodes.InsufficientPurgedBlocks,
                "baseline-x",
                "admitted 4, need at least 6 at 95%").Render());
    }

    [Fact]
    public void OutcomeToken_IsTotal_AndAnUnknownValueRendersAsInvalid()
    {
        Assert.Equal("not-calculated", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.NotCalculated));
        Assert.Equal("unavailable", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.Unavailable));
        Assert.Equal("pending", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.Pending));
        Assert.Equal("miss", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.Miss));
        Assert.Equal(
            "clears-necessary-screen", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.ClearsNecessaryScreen));
        Assert.Equal("invalid", Ad15ClaimGate.OutcomeToken(Ad16ScreenOutcome.Invalid));
        Assert.Equal("invalid", Ad15ClaimGate.OutcomeToken((Ad16ScreenOutcome)999));
    }
}
