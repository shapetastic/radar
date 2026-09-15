using Radar.Application.Acquisitions;
using Radar.TestSupport;

using Cases = Radar.TestSupport.AcquisitionScanV4ConstructedCases;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 229 — proof that the fixtures the <c>acqscan-v4</c> unit tests (<c>AcquisitionAgreementScanV4Tests</c>) pin
/// exercise a CHANGED rule: the frozen <see cref="AcqScanV3HistoricalControl"/> answers each of them as the spec
/// describes <c>acqscan-v3</c> did — otherwise a v4 test that passes would prove nothing. Where v3 already gave v4's
/// answer (AEHR; a dividend in a separate sentence) the test says so, because the spec asked for those cases anyway.
/// Offline, deterministic, never skipped.
/// </summary>
public sealed class AcqScanV3HistoricalControlFixtureTests
{
    [Fact]
    public void TheV3Control_CallsOtterTailsNotePurchase_CompanyIsAcquirer()
    {
        var result = AcqScanV3HistoricalControl.Scan(
            RealAcquisitionFilingBodies.OtterTailPowerNotePurchase, ["Otter Tail Corporation", "Otter Tail Corp", "Otter Tail"]);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
        Assert.Contains("a wholly owned subsidiary of Otter Tail Corporation", result.DecidingQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheV3Control_CallsEssentialUtilities_TheTargetOfAMerger_CompanyIsAcquirer()
    {
        var result = AcqScanV3HistoricalControl.Scan(
            RealAcquisitionFilingBodies.EssentialUtilitiesMergerWithAmericanWater, ["Essential Utilities, Inc.", "Essential Utilities"]);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
    }

    [Fact]
    public void TheV3Control_AlreadyCalledAehrsIncalPurchase_CompanyIsAcquirer()
    {
        // Not a v3 defect — the label was right. Rule 1 must KEEP it while removing OTTR's.
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcqScanV3HistoricalControl.Scan(RealAcquisitionFilingBodies.AehrIncalStockPurchase, ["Aehr Test Systems"]).Outcome);
    }

    [Fact]
    public void TheV3Control_MissesTheSpecsMarineMaxSentence_AndVetoesTheShortOne()
    {
        // The spec's own sentence: "MarineMax" sits 99 characters before "agreed to acquire", beyond the 90-character
        // proximity, so v3 did not VETO it — it had no target form for "Parent agreed to acquire the Company" at all.
        Assert.Equal(
            AcquisitionScanOutcome.CompanyNotTarget,
            AcqScanV3HistoricalControl.Scan(Cases.MarineMaxPursuantToWhichParentAgreedToAcquire, Cases.MarineMaxMentions).Outcome);

        // Within the proximity the veto the spec describes fires: acquirer-side, a real takeover unrecognised.
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcqScanV3HistoricalControl.Scan(Cases.MarineMaxShortPursuantToWhichParentAgreedToAcquire, Cases.MarineMaxMentions).Outcome);
    }

    [Fact]
    public void TheV3Control_CannotNameATitleCaseAcquiredByAcquirer()
    {
        Assert.Equal(
            AcquisitionScanOutcome.AcquirerNotNamed,
            AcqScanV3HistoricalControl.Scan(Cases.TitleCaseAcquiredByHeadline, Cases.ExampleMentions).Outcome);
        Assert.Equal(
            AcquisitionScanOutcome.AcquirerNotNamed,
            AcqScanV3HistoricalControl.Scan(Cases.UpperCaseAcquiredByHeadline, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void TheV3Control_ExcludesAConsiderationNearAnUngoverningDividend_ButNotOneInTheNextSentence()
    {
        Assert.Equal(
            AcquisitionScanOutcome.NoStatedConsideration,
            AcqScanV3HistoricalControl.Scan(Cases.DividendInClauseNotGoverningTheAmount, Cases.ExampleMentions).Outcome);

        // Already right under v3: its look-behind never crossed a sentence boundary.
        Assert.Equal(
            AcquisitionScanOutcome.Recognised,
            AcqScanV3HistoricalControl.Scan(Cases.DividendSentenceThenConsiderationSentence, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void TheV3Control_VetoesAnUngovernedCompletionOfTheAcquisitionOfTheCompany()
    {
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcqScanV3HistoricalControl.Scan(Cases.UngovernedCompletionOfTheAcquisitionOf, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void TheV3Control_RecognisesATargetClauseWithNoMergerContext()
    {
        // v3 let the heading's "Material Definitive Agreement" stand in for merger context anywhere in the body.
        Assert.Equal(
            AcquisitionScanOutcome.Recognised,
            AcqScanV3HistoricalControl.Scan(Cases.TargetClauseWithoutMergerContext, Cases.ExampleMentions).Outcome);
    }

    [Theory]
    [InlineData(nameof(Cases.BuyerSideParentAgreedToAcquireTheDefinedCompany))]
    [InlineData(nameof(Cases.BuyerSideUndefinedBuyerAgreedToAcquireTheDefinedCompany))]
    [InlineData(nameof(Cases.BuyerSideSubsidiaryPurchaserAgreedToAcquireTheDefinedCompany))]
    [InlineData(nameof(Cases.BuyerSideParentFarFromTheVerb))]
    public void TheV3Control_DidNotRecogniseTheBuyerSideCases(string caseName)
    {
        // The review cases are a regression v4's new target form must not introduce: v3 had no such form and did not
        // recognise them.
        var body = (string)typeof(Cases).GetField(caseName)!.GetValue(null)!;

        Assert.False(AcqScanV3HistoricalControl.Scan(body, Cases.ExampleMentions).IsRecognised);
    }

    [Theory]
    [InlineData(nameof(Cases.BuyerSideVerbBeforeTheDefinedCompany))]
    [InlineData(nameof(Cases.BuyerSideHeadingAcquiresTheDefinedCompany))]
    [InlineData(nameof(Cases.BuyerSideVerbBeforeTheUnquotedCompany))]
    public void TheV3Control_CalledTheVerbBeforeCompanyProbes_CompanyIsAcquirer(string caseName)
    {
        // Review probes M3, M2 and O: v3 answered all three correctly (acquirer-side, on its proximity veto), so v4's
        // target form must not turn them into recognitions.
        var body = (string)typeof(Cases).GetField(caseName)!.GetValue(null)!;

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, AcqScanV3HistoricalControl.Scan(body, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void TheV3Control_IsStillAcqScanV3()
    {
        // The control must never track production: if this ever reads the current version the "v3" column of the
        // spec-229 §2 measurement would silently mean something else.
        Assert.Equal("acqscan-v3", AcqScanV3HistoricalControl.Version);
        Assert.NotEqual(AcquisitionAgreementScan.Version, AcqScanV3HistoricalControl.Version);
    }
}
