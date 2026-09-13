using Radar.Application.Efficacy.EvidenceConfidence;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// <c>evidence-confidence-verdict-v2</c>, one branch per test: the rule is deterministic, its thresholds ride on
/// the section, and the sentence names which of (a)/(b)/(c) the data supports. The v1 branches are unchanged;
/// v2 adds the evidence-type axis to (c).
/// </summary>
public sealed class EvidenceConfidenceVerdictRuleTests
{
    [Fact]
    public void NoCounterfactual_IsNotDetermined_WithTheReason()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(false, "formula is not v8", null, 0.3, 0.9);

        Assert.Equal(EvidenceConfidenceVerdict.NotDetermined, v.Verdict);
        Assert.Equal("formula is not v8", v.NotDeterminedReason);
        Assert.Contains("NOT DETERMINED", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("formula is not v8", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void FewRankChanges_IsAFlatTax_EvenWhenValuesVary()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.25, 0.10, 0.9);

        Assert.Equal(EvidenceConfidenceVerdict.FlatTax, v.Verdict);
        Assert.Contains("(b)", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("FLAT TAX", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void ADominantModalValue_IsAFlatTax_EvenWhenRanksMove()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.9, 0.50, 0.1);

        Assert.Equal(EvidenceConfidenceVerdict.FlatTax, v.Verdict);
    }

    [Fact]
    public void VariesButTracksDistinctSourceTypes_IsTheWrongThing()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.6, 0.2, -0.5);

        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, v.Verdict);
        Assert.Contains("(c)", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void VariesAndDoesNotTrackCollectorCount_Discriminates()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.6, 0.2, 0.49);

        Assert.Equal(EvidenceConfidenceVerdict.Discriminates, v.Verdict);
        Assert.Contains("(a)", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndefinedRho_CannotSatisfyTheWrongThingBranch()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.6, 0.2, null);

        Assert.Equal(EvidenceConfidenceVerdict.Discriminates, v.Verdict);
        Assert.Contains("undefined", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThresholdsRideOnTheSection_AsTheRulesOwn()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.6, 0.2, 0.1, EvidenceType());

        Assert.Equal("evidence-confidence-verdict-v2", v.RuleVersion);
        Assert.Equal(0.25, v.RankChangedShareAtOrBelowIsFlatTax);
        Assert.Equal(0.50, v.ModalShareAtOrAboveIsFlatTax);
        Assert.Equal(0.5, v.AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing);
        Assert.Equal(0.75, v.DominantTermLogVarianceShareAtOrAboveIsSingleTerm);
        Assert.Equal(0.75, v.AboveMedianModalSignalTypeShareAtOrAboveIsSingleType);
        Assert.Equal(0.6, v.RankChangedShare);
        Assert.Equal(0.2, v.ModalShare);
        Assert.Equal(0.1, v.RhoEvidenceConfidenceVsDistinctSourceTypes);
        Assert.Equal(EvidenceConfidenceDistributionReporter.TermBestConfidence, v.DominantTerm);
        Assert.Equal(0.9, v.DominantTermLogVarianceShare);
        Assert.Equal(28, v.AboveMedianCompanies);
        Assert.Equal(0.39, v.RhoEvidenceConfidenceVsTrajectory);
    }

    // ---- v2: the evidence-type axis ----

    private static EvidenceConfidenceEvidenceTypeInputs EvidenceType(
        string? term = EvidenceConfidenceDistributionReporter.TermBestConfidence,
        double? termShare = 0.9,
        double? typeShare = 0.8,
        string producer = nameof(SignalProducer.AiEarningsReadDirectional)) =>
        new(
            DominantTerm: term,
            DominantTermLogVarianceShare: termShare,
            AboveMedianCompanies: 28,
            AboveMedianModalSignalType: "GuidanceChange",
            AboveMedianModalSignalTypeCompanies: 23,
            AboveMedianModalSignalTypeShare: typeShare,
            AboveMedianModalProducer: producer,
            AboveMedianModalProducerCompanies: 23,
            RhoEvidenceConfidenceVsTrajectory: 0.39);

    [Fact]
    public void OneTermAndOneSignalType_CarryingTheSpread_IsTheWrongThingOnTheEvidenceTypeAxis()
    {
        // ρ(EC, DistinctSourceTypes) 0.35 does NOT fire the collector axis — v1 would have read (a) here.
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.93, 0.3, 0.35, EvidenceType());

        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, v.Verdict);
        Assert.Equal([EvidenceConfidenceVerdictRule.AxisEvidenceType], v.WrongThingAxes);
        Assert.Contains("(c)", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("evidence-TYPE axis", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("23 of 28 above-median companies take it from a GuidanceChange signal", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("the AI earnings read and how confident that read reported itself to be", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("ρ(EvidenceConfidence, Trajectory) = 0.39", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("cannot be read as independent corroboration", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAxes_AreNamed_WhenBothFire()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.93, 0.3, 0.6, EvidenceType());

        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, v.Verdict);
        Assert.Equal(
            [EvidenceConfidenceVerdictRule.AxisCollectorCount, EvidenceConfidenceVerdictRule.AxisEvidenceType],
            v.WrongThingAxes);
        Assert.Contains("(and the collector-count axis)", v.Sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvidenceConfidenceDistributionReporter.TermDiversityFactor, 0.9, 0.8)]   // the dominant term is not bestConfidence
    [InlineData(EvidenceConfidenceDistributionReporter.TermBestConfidence, 0.74, 0.8)]  // term share below the line
    [InlineData(EvidenceConfidenceDistributionReporter.TermBestConfidence, 0.9, 0.74)]  // type share below the line
    public void TheEvidenceTypeAxis_NeedsEveryLeg(string term, double termShare, double typeShare)
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.93, 0.3, 0.35, EvidenceType(term, termShare, typeShare));

        Assert.Equal(EvidenceConfidenceVerdict.Discriminates, v.Verdict);
        Assert.Empty(v.WrongThingAxes);
    }

    [Fact]
    public void UndefinedEvidenceTypeInputs_CannotSatisfyTheAxis()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.93, 0.3, 0.35, EvidenceType(termShare: null, typeShare: null));

        Assert.Equal(EvidenceConfidenceVerdict.Discriminates, v.Verdict);
        Assert.Contains("undefined", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatTax_StillTakesPrecedence_OverTheEvidenceTypeAxis()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(true, null, 0.2, 0.3, 0.35, EvidenceType());

        Assert.Equal(EvidenceConfidenceVerdict.FlatTax, v.Verdict);
        Assert.Empty(v.WrongThingAxes);
    }

    [Fact]
    public void AKeywordProducer_IsNotDescribedAsTheAiRead()
    {
        var v = EvidenceConfidenceVerdictRule.Evaluate(
            true, null, 0.93, 0.3, 0.35, EvidenceType(producer: nameof(SignalProducer.KeywordPhraseRule)));

        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, v.Verdict);
        Assert.DoesNotContain("AI earnings read", v.Sentence, StringComparison.Ordinal);
        Assert.Contains("which keyword rule fired", v.Sentence, StringComparison.Ordinal);
    }
}
