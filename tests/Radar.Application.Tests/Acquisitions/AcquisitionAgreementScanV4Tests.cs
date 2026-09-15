using Radar.Application.Acquisitions;
using Radar.TestSupport;

using Cases = Radar.TestSupport.AcquisitionScanV4ConstructedCases;

namespace Radar.Application.Tests.Acquisitions;

/// <summary>
/// SPEC 229 §1 — <c>acqscan-v4</c>: every not-recognised outcome is a TRUE stated reason. Each rule is pinned on a REAL
/// passage where the live store holds one (<see cref="RealAcquisitionFilingBodies"/>: OTTR, AEHR, WTRG — trimmed, with
/// provenance) and otherwise on a minimal constructed body (<see cref="AcquisitionScanV4ConstructedCases"/>). The
/// IntegrationTests project pins what the frozen <c>acqscan-v3</c> control answered on the same strings, so each case
/// demonstrably exercises a changed rule (or, where v3 already agreed, says so).
/// </summary>
public sealed class AcquisitionAgreementScanV4Tests
{
    private static readonly string[] OtterTail = ["Otter Tail Corporation", "Otter Tail Corp", "Otter Tail"];

    private static readonly string[] Aehr = ["Aehr Test Systems"];

    private static readonly string[] Essential = ["Essential Utilities, Inc.", "Essential Utilities"];

    // ---- rule 1: the outcome precedence is honest ------------------------------------------------------------

    [Fact]
    public void RealOtterTailNotePurchase_IsNoMergerAgreement_NotCompanyIsAcquirer()
    {
        // OTTR 0001466593-25-000054: "Otter Tail Power Company (the “Company”), a wholly owned subsidiary of Otter Tail
        // Corporation (“OTC”), entered into a Note Purchase Agreement". A debt issue. No explicit merger phrase, so the
        // subsidiary-of veto (a merger-STRUCTURE test) does not apply, and no clause has the filer acquiring a named
        // party: the honest answer is that this is not a merger filing.
        var result = AcquisitionAgreementScan.Scan(RealAcquisitionFilingBodies.OtterTailPowerNotePurchase, OtterTail);

        Assert.Equal(AcquisitionScanOutcome.NoMergerAgreement, result.Outcome);
        Assert.Null(result.DecidingQuote);
    }

    [Fact]
    public void RealAehrIncalPurchase_IsCompanyIsAcquirer_OnTheFilerGoverningAcquireOverANamedObject()
    {
        // AEHR 0001654954-24-009008: no merger phrase, but the filer governs an acquisition verb over a NAMED party —
        // the EX-99.1 headline "Aehr Test Systems to Acquire Incal Technology". That clause is acquisition context in
        // itself. (The Item 1.01 sentence "pursuant to which the Company agreed to acquire … Incal" does not decide it:
        // "the Company" is not a seeded mention and "Aehr Test Systems" sits beyond the 90-character subject proximity.)
        var body = RealAcquisitionFilingBodies.AehrIncalStockPurchase;

        var result = AcquisitionAgreementScan.Scan(body, Aehr);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
        Assert.Contains("Aehr Test Systems to Acquire Incal Technology", result.DecidingQuote!, StringComparison.Ordinal);
        Assert.Contains(result.DecidingQuote!, body, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEssentialUtilitiesMerger_IsNotCompanyIsAcquirer_TheSubsidiaryOfVetoBindsADirectObject()
    {
        // WTRG 0001552781-25-000341: Essential Utilities is the TARGET of American Water's all-stock merger. v3 read
        // "a direct wholly owned subsidiary of Parent (“Merger Sub”), and Essential Utilities, Inc." as "subsidiary of
        // {company}" because the mention sat within 90 characters. v4 binds that veto to its DIRECT object ("Parent").
        // It is still not recognised — no clause the scan reads puts "Essential Utilities" in a target position (the
        // filing says "merge with and into Essential") — so the honest answer is company-not-target, deciding clause the
        // first explicit merger phrase. (A recall finding, recorded in spec 229 §2: a missed takeover, not fixed here.)
        var body = RealAcquisitionFilingBodies.EssentialUtilitiesMergerWithAmericanWater;

        var result = AcquisitionAgreementScan.Scan(body, Essential);

        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, result.Outcome);
        Assert.Contains("Plan of Merger", result.DecidingQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetClauseWithoutAnyMergerContext_IsNoMergerAgreement()
    {
        // Every item-1.01 body carries "Material Definitive Agreement" in its heading, so "definitive agreement" counts
        // only inside the target clause itself; without it and without an explicit merger phrase, this is not a merger.
        Assert.Equal(
            AcquisitionScanOutcome.NoMergerAgreement,
            AcquisitionAgreementScan.Scan(Cases.TargetClauseWithoutMergerContext, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void AnAcquireVerbOverAnUnnamedObject_WithoutAMergerPhrase_IsNoMergerAgreement()
    {
        // "Graham to acquire 599,808 shares (5%) of Graham common stock" (GHM's PIPE release, reworded): a share count is
        // not a named party, so without a merger phrase there is no acquisition context.
        var text = Pad(
            "Item 1.01 Entry into a Material Definitive Agreement.\n\nInvestors will invest $50 million in Example "
            + "Industries to acquire 599,808 shares (5%) of Example Industries Common Stock at $83.36 per share.");

        Assert.Equal(AcquisitionScanOutcome.NoMergerAgreement, AcquisitionAgreementScan.Scan(text, Cases.ExampleMentions).Outcome);
    }

    // ---- rule 2: the acquirer veto binds to the verb's subject ------------------------------------------------

    [Fact]
    public void TheSpecsMarineMaxSentence_PursuantToWhichParentAgreedToAcquireTheCompany_IsATargetClause()
    {
        var body = Cases.MarineMaxPursuantToWhichParentAgreedToAcquire;

        var result = AcquisitionAgreementScan.Scan(body, Cases.MarineMaxMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Contains("pursuant to which Parent agreed to acquire the Company", result.TargetQuote!, StringComparison.Ordinal);
        Assert.Equal("Safe Harbor Marinas, LLC", result.AcquirerName);
        Assert.Equal("53.00", result.ConsiderationPerShare);
    }

    [Fact]
    public void AShortPursuantToWhichParentSentence_WithinTheProximity_IsStillATargetClause()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.MarineMaxShortPursuantToWhichParentAgreedToAcquire, Cases.MarineMaxMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Contains("pursuant to which Parent agreed to acquire the Company", result.TargetQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void PursuantToWhichTheCompanyAgreedToAcquire_StillVetoes()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.PursuantToWhichCompanyAgreedToAcquire, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
        Assert.Contains("pursuant to which Example Industries agreed to acquire Widget Holdings", result.DecidingQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompanyDefinedAsParent_GovernsItsOwnPursuantToWhichParentClause()
    {
        // The acquirer-side 8-K shape: the filer IS "Parent", so "pursuant to which Parent agreed to acquire the Company"
        // names the filer buying the Company — never the filer as target.
        var text = Pad(
            "Item 1.01 Entry into a Material Definitive Agreement.\n\nExample Industries, Inc. (\"Parent\") signed a merger "
            + "agreement, pursuant to which Parent agreed to acquire the Company, Widget Holdings, Inc.");

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, AcquisitionAgreementScan.Scan(text, Cases.ExampleMentions).Outcome);
    }

    [Theory]
    [InlineData(nameof(Cases.BuyerSideParentAgreedToAcquireTheDefinedCompany), AcquisitionScanOutcome.CompanyIsAcquirer)]
    [InlineData(nameof(Cases.BuyerSideUndefinedBuyerAgreedToAcquireTheDefinedCompany), AcquisitionScanOutcome.CompanyNotTarget)]
    [InlineData(nameof(Cases.BuyerSideSubsidiaryPurchaserAgreedToAcquireTheDefinedCompany), AcquisitionScanOutcome.CompanyNotTarget)]
    [InlineData(nameof(Cases.BuyerSideParentFarFromTheVerb), AcquisitionScanOutcome.CompanyIsAcquirer)]
    public void ABuyerSideMerger8K_WhereAnotherPartyIsTheCompanyOrTheFilerIsTheSubject_IsNeverATargetClause(
        string caseName, AcquisitionScanOutcome expected)
    {
        // Review cases: "the Company" is the filer only when every (the "Company") definition directly follows a filer
        // mention with nothing but whitespace or an entity apposition between, and a role the filer is defined as
        // anywhere in the clause is the filer, however far from the verb. The EXACT outcome is
        // pinned, not merely "not recognised": reading either clause as a target yields acquirer-not-named on these
        // bodies (no nameable buyer), which "not recognised" would let through — and a body with a nameable acquirer
        // would then be recognised, closing the BUYER's thesis.
        var body = (string)typeof(Cases).GetField(caseName)!.GetValue(null)!;

        var result = AcquisitionAgreementScan.Scan(body, Cases.ExampleMentions);

        Assert.False(result.IsRecognised, $"{caseName} was recognised (acquirer {result.AcquirerName}).");
        Assert.Equal(expected, result.Outcome);
    }

    [Theory]
    [InlineData(nameof(Cases.BuyerSideVerbBeforeTheDefinedCompany), AcquisitionScanOutcome.CompanyIsAcquirer)]
    [InlineData(nameof(Cases.BuyerSideHeadingAcquiresTheDefinedCompany), AcquisitionScanOutcome.CompanyNotTarget)]
    [InlineData(nameof(Cases.BuyerSideVerbBeforeTheUnquotedCompany), AcquisitionScanOutcome.CompanyIsAcquirer)]
    public void ACompanyDefinitionAfterAVerb_BelongsToAnotherParty_SoTheCompanyIsNotTheFiler(
        string caseName, AcquisitionScanOutcome expected)
    {
        // Review probes M3, M2 and O: a (the "Company") definition is the filer's only when nothing but whitespace or an
        // entity apposition separates it from the filer's name. "agreed to acquire Widget Holdings, Inc." and "Acquires
        // Widget Holdings, Inc." are not appositions, so the definition names the TARGET and "the Company" is not the filer.
        // Unguarded, each is recognised as a takeover of the BUYER (acquirer "Widget Holdings, Inc").
        var body = (string)typeof(Cases).GetField(caseName)!.GetValue(null)!;

        var result = AcquisitionAgreementScan.Scan(body, Cases.ExampleMentions);

        Assert.False(result.IsRecognised, $"{caseName} was recognised (acquirer {result.AcquirerName}).");
        Assert.Equal(expected, result.Outcome);
    }

    [Theory]
    [InlineData(nameof(Cases.TargetSideCompanyDefinedRightAfterTheFilersName))]
    [InlineData(nameof(Cases.TargetSidePartyListWithApposition))]
    [InlineData(nameof(Cases.TargetSideCompanyPartiesBesideTheFilersName))]
    [InlineData(nameof(Cases.MarineMaxPursuantToWhichParentAgreedToAcquire))]
    public void GenuineTargetShapes_WithTheFilersOwnCompanyDefinition_AreStillRecognised(string caseName)
    {
        var body = (string)typeof(Cases).GetField(caseName)!.GetValue(null)!;
        var mentions = caseName.StartsWith("MarineMax", StringComparison.Ordinal) ? Cases.MarineMaxMentions : Cases.ExampleMentions;

        var result = AcquisitionAgreementScan.Scan(body, mentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Contains("agreed to acquire the Company", result.TargetQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilerDefinedAsParent_FarFromTheVerb_IsStillTheBuyer()
    {
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcquisitionAgreementScan.Scan(Cases.BuyerSideParentFarFromTheVerb, Cases.ExampleMentions).Outcome);
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcquisitionAgreementScan.Scan(Cases.BuyerSideParentAgreedToAcquireTheDefinedCompany, Cases.ExampleMentions).Outcome);
    }

    [Fact]
    public void AnAcquirerCandidateSpanningASentenceBoundary_IsNotAName()
    {
        // Defence in depth: the defined-party capture can run back across "Agreement. On March 2", and the comma trim
        // would otherwise leave that heading fragment as the "name".
        var text = Pad(
            "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Acme Holdings, Inc., a Delaware "
            + "corporation (\"Parent\"), signed the merger agreement. Merger Sub will merge with and into Example "
            + "Industries, Inc., and each share will be converted into the right to receive $12.00 in cash.");

        var result = AcquisitionAgreementScan.Scan(text, Cases.ExampleMentions);

        Assert.DoesNotContain("Item 1.01", result.AcquirerName ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- rule 3: governed "completion of the acquisition of" --------------------------------------------------

    [Fact]
    public void AGovernedCompletionOfTheAcquisitionOf_IsAcquirerSide()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.GovernedCompletionOfTheAcquisitionOf, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
        Assert.Contains("the Company's completion of the acquisition of Widget Holdings", result.DecidingQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUngovernedCompletionOfTheAcquisitionOfTheCompany_IsNotAcquirerSide()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.UngovernedCompletionOfTheAcquisitionOf, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, result.Outcome);
    }

    // ---- rule 4: case-insensitive acquirer extraction ---------------------------------------------------------

    [Fact]
    public void ATitleCaseAcquiredByHeadline_NamesTheAcquirer()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.TitleCaseAcquiredByHeadline, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("Acme Holdings, Inc", result.AcquirerName);
        Assert.Equal("12.00", result.ConsiderationPerShare);
    }

    [Fact]
    public void AnUpperCaseAcquiredByHeadline_NamesTheAcquirer_InTheFilingsOwnCase()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.UpperCaseAcquiredByHeadline, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("ACME HOLDINGS, INC", result.AcquirerName);
    }

    [Fact]
    public void TheCaseInsensitiveKeyword_NeverLetsALowerCaseWordBecomeTheName()
    {
        // The name group stays case-SENSITIVE: "acquired by the Company" must not capture "the Company…" as a name.
        var text = Pad(
            "Item 1.01 Entry into a Material Definitive Agreement.\n\nUnder the merger agreement Example Industries, Inc. "
            + "will be acquired by the purchaser, and each share will be converted into the right to receive $12.00 in cash.");

        Assert.Equal(AcquisitionScanOutcome.AcquirerNotNamed, AcquisitionAgreementScan.Scan(text, Cases.ExampleMentions).Outcome);
    }

    // ---- rule 5: consideration exclusions govern their own amount --------------------------------------------

    [Fact]
    public void ADividendSentence_ThenASeparateConsiderationSentence_KeepsTheConsideration()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.DividendSentenceThenConsiderationSentence, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("53.00", result.ConsiderationPerShare);
        Assert.DoesNotContain("dividend", result.ConsiderationQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADividendInTheSameClause_ThatDoesNotGovernTheAmount_KeepsTheConsideration()
    {
        var result = AcquisitionAgreementScan.Scan(Cases.DividendInClauseNotGoverningTheAmount, Cases.ExampleMentions);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("53.00", result.ConsiderationPerShare);
    }

    [Theory]
    [InlineData("The board declared dividends of $0.21 per share in cash for the merger period.")]
    [InlineData("The board declared a quarterly cash dividend in the amount of $0.21 per share in cash in the merger.")]
    [InlineData("Holders received $0.21 per share quarterly cash dividend, paid per share in cash before the merger.")]
    [InlineData("Each Company Option with a per share exercise price of $9.00 per share in cash will be cancelled in the merger.")]
    [InlineData("Each Company Option with an exercise price per share (the \"Exercise Price\") of $9.00 per share in cash will be cancelled in the merger.")]
    public void AGoverningExclusion_StillExcludesItsAmount(string sentence)
    {
        var text = Pad(
            "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc. (the "
            + "\"Company\") entered into an Agreement and Plan of Merger with Acme Holdings, Inc., a Delaware corporation "
            + "(\"Parent\"). Acme Merger Sub will merge with and into Example Industries, Inc. " + sentence);

        Assert.Equal(AcquisitionScanOutcome.NoStatedConsideration, AcquisitionAgreementScan.Scan(text, Cases.ExampleMentions).Outcome);
    }

    // ---- the deciding clause ----------------------------------------------------------------------------------

    [Fact]
    public void EveryClauseDecidedOutcome_CarriesItsDecidingClause_VerbatimFromTheBody()
    {
        foreach (var (body, mentions) in new[]
                 {
                     (RealAcquisitionFilingBodies.AehrIncalStockPurchase, Aehr),
                     (RealAcquisitionFilingBodies.EssentialUtilitiesMergerWithAmericanWater, Essential),
                     (Cases.PursuantToWhichCompanyAgreedToAcquire, Cases.ExampleMentions),
                 })
        {
            var result = AcquisitionAgreementScan.Scan(body, mentions);
            Assert.NotNull(result.DecidingQuote);
            Assert.Contains(result.DecidingQuote!, body, StringComparison.Ordinal);
        }

        Assert.Null(AcquisitionAgreementScan.Scan(RealAcquisitionFilingBodies.MarineMaxMergerAgreement, ["MarineMax, Inc.", "MarineMax"]).DecidingQuote);
    }

    /// <summary>Pads a short constructed body past the scan's degenerate-fetch floor without adding any rule-relevant word.</summary>
    private static string Pad(string text) =>
        text + "\n\nItem 9.01 Financial Statements and Exhibits. (d) Exhibits. 99.1 Press release, furnished herewith "
        + "and not filed for purposes of Section 18 of the Securities Exchange Act of 1934.";
}
