using Radar.Application.Acquisitions;
using Radar.TestSupport;

namespace Radar.Application.Tests.Acquisitions;

/// <summary>
/// SPEC 227 §1 — <c>acqscan-v2</c>: each leg must be about THE deal. The two REAL bodies
/// (<see cref="RealAcquisitionFilingBodies"/>) are the ground truth — SHOO's false positive and HZO's wrong
/// facts under <c>acqscan-v1</c> — and each hole v2 closes also has its own small synthetic case, so a
/// regression names the rule it broke. (The IntegrationTests project pins that the frozen v1 control DID make
/// both mistakes on these same excerpts, so the fixtures demonstrably reproduce the defect.)
/// </summary>
public sealed class AcquisitionAgreementScanV2Tests
{
    private static readonly string[] SteveMadden = ["Steven Madden, Ltd.", "Steven Madden"];

    private static readonly string[] MarineMax = ["MarineMax, Inc.", "MarineMax"];

    private static readonly string[] Example = ["Example Industries, Inc.", "Example Industries"];

    // ---- the real bodies ------------------------------------------------------------------------------------

    [Fact]
    public void RealSteveMadden8K_IsNotRecognised_ItIsNoMergerAgreement()
    {
        // SHOO 0001641172-25-008949: Q1 results + an amended and restated credit agreement + a dividend + the
        // completion of SHOO's OWN acquisition of Kurt Geiger. It lands on NoMergerAgreement, and why matters:
        //   - the body carries no "agreement and plan of merger" / "merger agreement" / "plan of merger" at all,
        //     only the credit agreement's "definitive agreement(s)" boilerplate, which counts only BESIDE a
        //     target clause;
        //   - there is no target clause: "~" splits the heading "Announces Completion of Acquisition of Kurt
        //     Geiger" from the dateline, and even without the split "Steven Madden" is not the DIRECT object of
        //     "acquisition of" (and no "by {acquirer}" follows it);
        //   - it is not CompanyIsAcquirer because the heading's subject is "Steve\nMadden", which is not a seeded
        //     mention, and the "~" separates it from "Announces Completion of Acquisition of" — the new veto has
        //     no company to govern. Reporting "not a merger filing" is the honest answer for this document.
        var result = AcquisitionAgreementScan.Scan(
            RealAcquisitionFilingBodies.SteveMaddenQ1ResultsAndCreditAgreement, SteveMadden);

        Assert.Equal(AcquisitionScanOutcome.NoMergerAgreement, result.Outcome);
        Assert.Null(result.AcquirerName);
        Assert.Null(result.ConsiderationPerShare);
    }

    [Fact]
    public void RealMarineMaxMerger_IsRecognised_AtTheMergerConsideration_WithTheEntityNamedForParent()
    {
        // HZO 0001193125-26-341302. v1 recorded "Parent" at $0.001 (the par value in the WHEREAS clause). v2
        // skips the par value, keeps scanning, and reaches §2.01(c): "the right to receive an amount in cash
        // equal to $53.00 per share, without interest (the “ Merger Consideration ”)". The acquirer is the
        // entity the agreement defines as Parent — "SHM Holdco, LLC, a Delaware limited liability company
        // (“ Parent ”)" (Safe Harbor Marinas' acquisition vehicle) — never the role word.
        var body = RealAcquisitionFilingBodies.MarineMaxMergerAgreement;

        var result = AcquisitionAgreementScan.Scan(body, MarineMax);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("53.00", result.ConsiderationPerShare);
        Assert.Equal("$", result.ConsiderationCurrency);
        Assert.Equal(AcquisitionConsiderationKind.Cash, result.ConsiderationKind);
        Assert.Equal("SHM Holdco, LLC", result.AcquirerName);

        Assert.Contains(result.ConsiderationQuote!, body, StringComparison.Ordinal);
        Assert.Contains(result.TargetQuote!, body, StringComparison.Ordinal);
        Assert.Contains("Merger Consideration", result.ConsiderationQuote!, StringComparison.Ordinal);
        Assert.DoesNotContain("par value", result.ConsiderationQuote!, StringComparison.Ordinal);
        Assert.Contains("to be Acquired by", result.TargetQuote!, StringComparison.Ordinal);

        // The clause split: the target quote no longer drags in the merger agreement's signature pages.
        Assert.DoesNotContain("/s/", result.TargetQuote!, StringComparison.Ordinal);
    }

    // ---- leg (b): the amount must be the merger consideration -----------------------------------------------

    [Fact]
    public void AParValueOnlySentence_IsNotAConsideration()
    {
        var text = MergerFrame(
            "Each share of common stock, par value $0.01 per share, of the Company outstanding immediately prior "
            + "to the Effective Time will be converted into the right to receive the Merger Consideration.");

        Assert.Equal(AcquisitionScanOutcome.NoStatedConsideration, AcquisitionAgreementScan.Scan(text, Example).Outcome);
    }

    [Fact]
    public void ADividendOnlySentence_IsNotAConsideration_EvenBesideMergerWords()
    {
        // "transaction" and "per share" are both present; the amount is still a dividend.
        var text = MergerFrame(
            "Prior to the closing of the transaction the Company's Board of Directors approved a quarterly cash "
            + "dividend of $0.21 per share in cash for the merger period.");

        Assert.Equal(AcquisitionScanOutcome.NoStatedConsideration, AcquisitionAgreementScan.Scan(text, Example).Outcome);
    }

    [Fact]
    public void AnExerciseOrConversionOrOfferingPrice_IsNotAConsideration()
    {
        foreach (var sentence in new[]
        {
            "Each Company Option with an exercise price of $9.00 per share will be cancelled in the merger.",
            "The notes carry a conversion price of $15.00 per share and survive the merger consideration mechanics.",
            "The shares were sold in the offering at an offering price of $11.00 per share in cash in a transaction.",
            "The investors paid a purchase price of $7.50 per share in cash in the private placement transaction.",
        })
        {
            var result = AcquisitionAgreementScan.Scan(MergerFrame(sentence), Example);
            Assert.True(
                result.Outcome == AcquisitionScanOutcome.NoStatedConsideration,
                $"Expected no consideration for: {sentence} (was {result.Outcome}, amount {result.ConsiderationPerShare})");
        }
    }

    [Fact]
    public void AnExcludedAmount_IsSkipped_AndTheScanContinuesInTheSameSentence()
    {
        // HZO's shape in one sentence: the par value comes FIRST, the consideration after it.
        var text = MergerFrame(
            "Each share of common stock, par value $0.01 per share, of the Company will be converted into the "
            + "right to receive $12.00 in cash, without interest.");

        var result = AcquisitionAgreementScan.Scan(text, Example);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("12.00", result.ConsiderationPerShare);
    }

    [Fact]
    public void APerShareAmountWithoutMergerVocabulary_IsNotAConsideration()
    {
        // No "merger consideration", no "offer price", no "right to receive", no "per share in cash" beside a
        // deal word: a bare "$X per share" figure says nothing about the deal.
        var text = MergerFrame("Diluted earnings were $1.25 per share for the quarter.");

        Assert.Equal(AcquisitionScanOutcome.NoStatedConsideration, AcquisitionAgreementScan.Scan(text, Example).Outcome);
    }

    [Fact]
    public void PerShareInCash_BesideADealWord_IsAConsideration()
    {
        var text = MergerFrame("Acme Holdings will acquire all outstanding shares for $30.00 per share in cash.");

        var result = AcquisitionAgreementScan.Scan(text, Example);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("30.00", result.ConsiderationPerShare);
    }

    // ---- leg (a): the company must be the target of THIS clause ---------------------------------------------

    [Fact]
    public void AHeadingWithoutPunctuation_CannotBindTheDatelineCompanyToItsPhrase()
    {
        // The SHOO shape with NO "~" and NO dash: the heading's object is Widget Holdings, and the company in the
        // dateline 40 characters later is not a DIRECT object, so it is not the target. (Under v1 the
        // 90-character proximity made it one.)
        var text = Pad(
            "Agreement and Plan of Merger.\n"
            + "Acme Announces Completion of Acquisition of Widget Holdings\n"
            + "NEW YORK, N.Y., May 7, 2026 Example Industries, Inc. (Nasdaq: EXI) by Acme Holdings, Inc. today "
            + "reported results. Each share will be converted into the right to receive $12.00 in cash.");

        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, AcquisitionAgreementScan.Scan(text, Example).Outcome);
    }

    [Fact]
    public void AcquisitionOf_NeedsTheCompanyAsDirectObject_AndABy()
    {
        var withBy = Pad(
            "Agreement and Plan of Merger with Acme Holdings, Inc., a Delaware corporation (\"Parent\"). The Merger Agreement provides for "
            + "the acquisition of Example Industries, Inc. by Acme Holdings, Inc. for the Merger Consideration of "
            + "$12.00 per share.");
        var withoutBy = Pad(
            "Agreement and Plan of Merger. The Merger Agreement provides for the acquisition of Example "
            + "Industries, Inc. and the Merger Consideration of $12.00 per share.");

        Assert.Equal(AcquisitionScanOutcome.Recognised, AcquisitionAgreementScan.Scan(withBy, Example).Outcome);
        Assert.Equal("Acme Holdings, Inc", AcquisitionAgreementScan.Scan(withBy, Example).AcquirerName);
        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, AcquisitionAgreementScan.Scan(withoutBy, Example).Outcome);
    }

    [Fact]
    public void CompletionOfItsOwnAcquisition_IsAcquirerSide()
    {
        foreach (var heading in new[]
        {
            "Example Industries, Inc. Announces Completion of Acquisition of Widget Holdings.",
            "Example Industries, Inc. announced the completion of the acquisition of Widget Holdings.",
            "Example Industries, Inc. Completes Acquisition of Widget Holdings.",
            "Example Industries, Inc. completed acquisition of Widget Holdings.",
        })
        {
            var text = Pad("Agreement and Plan of Merger. " + heading);
            Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, AcquisitionAgreementScan.Scan(text, Example).Outcome);
        }
    }

    [Fact]
    public void SplitSentences_BreaksOnBlankLinesTildesAndSpacedDashes_ButNotOnASingleLineBreak()
    {
        var clauses = AcquisitionAgreementScan.SplitSentences(
            "Steve\nMadden Announces Results\n\n ~\n Announces Completion of Acquisition of Kurt Geiger ~\n\n LONG\n"
            + "ISLAND CITY, N.Y., May 7, 2025 – Steven Madden, Ltd. (Nasdaq: SHOO) announced results for 2025–2026.");

        Assert.Equal(
            [
                "Steve\nMadden Announces Results",
                "Announces Completion of Acquisition of Kurt Geiger",
                "LONG\nISLAND CITY, N.Y., May 7, 2025",
                "Steven Madden, Ltd. (Nasdaq: SHOO) announced results for 2025–2026.",
            ],
            clauses);
    }

    // ---- the acquirer must be a name ------------------------------------------------------------------------

    [Fact]
    public void ARoleOnlyAcquirer_IsAcquirerNotNamed_NeverTheRoleWord()
    {
        // Every form the scan reads yields only a defined-term role: "by and among Parent, …", "wholly owned
        // subsidiary of Parent". v1 would have recorded "Parent".
        var text = Pad(
            "On March 2, 2026, Example Industries, Inc. entered into an Agreement and Plan of Merger (the \"Merger "
            + "Agreement\") by and among Parent, Merger Sub and the Company. Merger Sub will merge with and into "
            + "Example Industries, Inc., with the Company surviving as a wholly owned subsidiary of Parent. Each "
            + "share will be converted into the right to receive $12.00 in cash (the \"Merger Consideration\").");

        var result = AcquisitionAgreementScan.Scan(text, Example);

        Assert.Equal(AcquisitionScanOutcome.AcquirerNotNamed, result.Outcome);
        Assert.Null(result.AcquirerName);
    }

    [Fact]
    public void ADefinedPartyWithSpacesInsideItsQuotes_YieldsTheEntity()
    {
        var text = Pad(
            "This AGREEMENT AND PLAN OF MERGER is by and among Acme Holdings, LLC, a Delaware limited liability "
            + "company (“ Parent ”), and Example Industries, Inc. (the “ Company ”). Merger Sub will merge "
            + "with and into Example Industries, Inc. Each share will be converted into the right to receive $12.00 "
            + "in cash.");

        Assert.Equal("Acme Holdings, LLC", AcquisitionAgreementScan.Scan(text, Example).AcquirerName);
    }

    [Theory]
    [InlineData("parent", true)]
    [InlineData("the lead borrower", true)]
    [InlineData("administrative agent", true)]
    [InlineData("merger sub", true)]
    [InlineData("shm holdco, llc", false)]
    [InlineData("parent holdings corp", false)]
    public void DefinedTermRoles_AreDataInOnePlace(string candidateLower, bool isRole)
    {
        Assert.Equal(isRole, AcquisitionAgreementScan.IsDefinedTermRole(candidateLower));
    }

    /// <summary>A named, target-side merger agreement around <paramref name="considerationSentence"/>.</summary>
    private static string MergerFrame(string considerationSentence) =>
        "Item 1.01 Entry into a Material Definitive Agreement. On March 2, 2026, Example Industries, Inc. (the "
        + "\"Company\") entered into an Agreement and Plan of Merger with Acme Holdings, Inc., a Delaware "
        + "corporation (\"Parent\"). Subject to its terms, Acme Merger Sub will merge with and into Example "
        + "Industries, Inc., with the Company surviving as a wholly owned subsidiary of Parent. "
        + considerationSentence;

    /// <summary>Pads a short synthetic body past the scan's degenerate-fetch floor without adding any rule-relevant word.</summary>
    private static string Pad(string text) =>
        text + "\n\nItem 9.01 Financial Statements and Exhibits. (d) Exhibits. 99.1 Press release, furnished herewith "
        + "and not filed for purposes of Section 18 of the Securities Exchange Act of 1934.";
}
