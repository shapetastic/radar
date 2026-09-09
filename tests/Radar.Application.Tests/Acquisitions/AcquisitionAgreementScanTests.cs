using Radar.Application.Acquisitions;

namespace Radar.Application.Tests.Acquisitions;

/// <summary>
/// SPEC 217 §1 — <c>acqscan-v1</c>. The scan is the whole slice's gate: a false positive CLOSES A LIVE
/// THESIS, so every test here is about failing closed and counting the reason.
/// </summary>
public sealed class AcquisitionAgreementScanTests
{
    private static readonly string[] MarineMax = ["MarineMax, Inc.", "MarineMax"];

    private static readonly string[] Stereotaxis = ["Stereotaxis, Inc.", "Stereotaxis"];

    private static readonly string[] ExampleIndustries = ["Example Industries, Inc.", "Example Industries"];

    [Fact]
    public void Version_IsTheDeclaredScanIdentity()
    {
        // Pinned because it is part of every record's content-derived id AND of the hashed acq= descriptor
        // field: a silent rename would re-mint every record and move every fingerprint.
        Assert.Equal("acqscan-v1", AcquisitionAgreementScan.Version);
    }

    [Fact]
    public void MarineMaxMerger_IsRecognised_WithBothLegsVerbatim()
    {
        // THE recognition case: HZO accession 0001193125-26-341302, filed 2026-08-10.
        var result = AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, MarineMax);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("Safe Harbor Marinas, LLC", result.AcquirerName);
        Assert.Equal("53.00", result.ConsiderationPerShare);
        Assert.Equal("$", result.ConsiderationCurrency);
        Assert.Equal(AcquisitionConsiderationKind.Cash, result.ConsiderationKind);

        // VERBATIM (the spec-215/216 rule): both quotes are ordinal substrings of the filing text, and each
        // contains the token the record claims it does.
        Assert.Contains(
            result.TargetQuote!, AcquisitionFilingFixtures.MarineMaxMerger, StringComparison.Ordinal);
        Assert.Contains(
            result.ConsiderationQuote!, AcquisitionFilingFixtures.MarineMaxMerger, StringComparison.Ordinal);
        Assert.Contains("merge with and into", result.TargetQuote!, StringComparison.Ordinal);
        Assert.Contains("$53.00", result.ConsiderationQuote!, StringComparison.Ordinal);
    }

    [Fact]
    public void MarineMaxCreditAgreement_IsNotRecognised_AndTheFailedLegIsNamed()
    {
        // THE fail-closed case: HZO accession 0001193125-26-290439, filed 2026-06-30. It carries item 1.01
        // and the company's own name throughout — exactly the shape a title-only rule fires on — and it is a
        // credit agreement. It must be COUNTED, with the leg that failed named.
        var result = AcquisitionAgreementScan.Scan(
            AcquisitionFilingFixtures.MarineMaxCreditAgreement, MarineMax);

        Assert.Equal(AcquisitionScanOutcome.NoMergerAgreement, result.Outcome);
        Assert.Null(result.AcquirerName);
        Assert.Null(result.ConsiderationPerShare);
        Assert.Null(result.TargetQuote);
        Assert.Null(result.ConsiderationQuote);
    }

    [Fact]
    public void AcquirerSideFiling_IsCountedSeparately_NotRecognised()
    {
        // The company is the BUYER. Every merger word is present, so an unordered co-occurrence test would
        // recognise it and close the wrong company's thesis. It fails leg (a) by construction and gets its
        // OWN counted outcome — a different fact from "no target sentence", not a weaker version of it.
        var result = AcquisitionAgreementScan.Scan(
            AcquisitionFilingFixtures.AcquirerSideMerger, Stereotaxis);

        Assert.Equal(AcquisitionScanOutcome.CompanyIsAcquirer, result.Outcome);
    }

    [Fact]
    public void MergerWithoutStatedConsideration_FailsLegB_AndIsCounted()
    {
        var result = AcquisitionAgreementScan.Scan(
            AcquisitionFilingFixtures.MergerWithoutStatedConsideration, ExampleIndustries);

        Assert.Equal(AcquisitionScanOutcome.NoStatedConsideration, result.Outcome);
    }

    [Fact]
    public void UnrelatedCompany_IsNeverRecognisedFromAnothersMerger()
    {
        // The single most dangerous false positive: a company that merely APPEARS in someone else's merger
        // filing must never have its own thesis closed.
        var result = AcquisitionAgreementScan.Scan(
            AcquisitionFilingFixtures.MarineMaxMerger, ["Some Other Company, Inc."]);

        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, result.Outcome);
    }

    [Fact]
    public void EmptyOrShortBody_IsNotAnAnswer_ItIsEmptyBody()
    {
        // The spec-114 rule: a degenerate fetch is NOT a "no acquisition" answer. The caller must be able to
        // tell the two apart, because one is cached forever and the other is re-attempted.
        Assert.Equal(
            AcquisitionScanOutcome.EmptyBody,
            AcquisitionAgreementScan.Scan(string.Empty, MarineMax).Outcome);
        Assert.Equal(
            AcquisitionScanOutcome.EmptyBody,
            AcquisitionAgreementScan.Scan("Item 1.01 Entry into a Material Definitive Agreement.", MarineMax)
                .Outcome);
    }

    [Fact]
    public void NoCompanyMentions_FailsClosed()
    {
        // A company Radar cannot locate in the text cannot be shown to be the target.
        var result = AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, []);

        Assert.Equal(AcquisitionScanOutcome.CompanyNotTarget, result.Outcome);
    }

    [Fact]
    public void Scan_IsDeterministic_ForTheSameInputs()
    {
        // AD-3: no clock, no state, no randomness — the answer is a function of (text, mentions) alone.
        var a = AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, MarineMax);
        var b = AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, MarineMax);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SentenceSplit_DoesNotBreakOnADecimalPointOrALegalAbbreviation()
    {
        // The spec-216 decimal rule, plus the abbreviation rule filings force on us: without either, the
        // consideration sentence splits at "$53.00" and the target sentence at "MarineMax, Inc." — and both
        // legs fail on a filing that plainly states them.
        var sentences = AcquisitionAgreementScan.SplitSentences(
            "MarineMax, Inc. entered into a merger agreement. Each share converts into $53.00 in cash. Done.");

        Assert.Equal(3, sentences.Count);
        Assert.Contains("MarineMax, Inc. entered into a merger agreement.", sentences);
        Assert.Contains("Each share converts into $53.00 in cash.", sentences);
    }

    [Fact]
    public void AVerbatimCheckFailure_HasItsOwnBucket_NotALegsBucket()
    {
        // SPEC 217 §1 requires the not-recognised tally to be split by WHICH LEG failed, so the final
        // verbatim re-check must never borrow a leg's bucket: it used to return NoStatedConsideration even
        // when it was the leg-(a) TARGET quote that failed. It is now its own outcome, because a failure
        // there is a DEFECT IN THE SCAN (every quote is a slice of the body being checked), not a fact
        // about the filing — attributing it to a leg would hide a bug inside an expected number.
        Assert.Contains(AcquisitionScanOutcome.VerbatimCheckFailed, AcquisitionAgreementScan.AllOutcomes);
        Assert.Equal(
            "verbatim-check-failed",
            AcquisitionAgreementScan.Token(AcquisitionScanOutcome.VerbatimCheckFailed));

        // And the leg buckets keep their exact meanings: NoStatedConsideration is returned ONLY for a
        // genuine leg-(b) failure, and CompanyNotTarget / CompanyIsAcquirer only for leg (a).
        Assert.Equal(
            AcquisitionScanOutcome.NoStatedConsideration,
            AcquisitionAgreementScan.Scan(
                AcquisitionFilingFixtures.MergerWithoutStatedConsideration, ExampleIndustries).Outcome);
        Assert.Equal(
            AcquisitionScanOutcome.CompanyNotTarget,
            AcquisitionAgreementScan.Scan(
                AcquisitionFilingFixtures.MarineMaxMerger, ["Some Other Company, Inc."]).Outcome);
        Assert.Equal(
            AcquisitionScanOutcome.CompanyIsAcquirer,
            AcquisitionAgreementScan.Scan(
                AcquisitionFilingFixtures.AcquirerSideMerger, Stereotaxis).Outcome);

        // The recognised path is unaffected: both quotes verify, so neither branch is reachable.
        Assert.True(
            AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, MarineMax)
                .IsRecognised);
    }

    [Fact]
    public void EveryDefinedOutcome_HasAStableToken()
    {
        // The tokens are the vocabulary the pass's aggregated log line and the live harness both print, so a
        // renamed member must break loudly here rather than silently changing an artifact.
        foreach (var outcome in AcquisitionAgreementScan.AllOutcomes)
        {
            var token = AcquisitionAgreementScan.Token(outcome);
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.DoesNotContain(' ', token);
        }

        Assert.Equal(
            AcquisitionAgreementScan.AllOutcomes.Count,
            AcquisitionAgreementScan.AllOutcomes.Select(AcquisitionAgreementScan.Token).Distinct().Count());
    }

    [Fact]
    public void RecognisedRecord_DescribesItsConsiderationWithoutAdviceLanguage()
    {
        var result = AcquisitionAgreementScan.Scan(AcquisitionFilingFixtures.MarineMaxMerger, MarineMax);
        var record = new PendingAcquisitionRecord(
            Id: Guid.NewGuid(),
            CompanyId: Guid.NewGuid(),
            Accession: "0001193125-26-341302",
            EvidenceId: Guid.NewGuid(),
            AnnouncedOnUtc: new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero),
            AcquirerName: result.AcquirerName!,
            ConsiderationPerShare: result.ConsiderationPerShare!,
            ConsiderationCurrency: result.ConsiderationCurrency!,
            ConsiderationKind: result.ConsiderationKind!.Value,
            ConsiderationQuote: result.ConsiderationQuote!,
            TargetQuote: result.TargetQuote!,
            ScanVersion: AcquisitionAgreementScan.Version,
            Verification: AcquisitionVerification.Verbatim);

        Assert.Equal("$53.00 per share in cash", record.DescribeConsideration());
        foreach (var forbidden in new[] { "buy", "sell", "guaranteed upside", "safe bet" })
        {
            Assert.DoesNotContain(forbidden, record.DescribeConsideration(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void IdentityFor_IsContentDerived_AndVersionScoped()
    {
        var company = Guid.NewGuid();

        Assert.Equal(
            PendingAcquisitionRecord.IdentityFor("acqscan-v1", company, "0001193125-26-341302"),
            PendingAcquisitionRecord.IdentityFor("acqscan-v1", company, "0001193125-26-341302"));
        Assert.NotEqual(
            PendingAcquisitionRecord.IdentityFor("acqscan-v1", company, "0001193125-26-341302"),
            PendingAcquisitionRecord.IdentityFor("acqscan-v2", company, "0001193125-26-341302"));
        Assert.NotEqual(
            PendingAcquisitionRecord.IdentityFor("acqscan-v1", company, "0001193125-26-341302"),
            PendingAcquisitionRecord.IdentityFor("acqscan-v1", company, "0001193125-26-290439"));
    }
}
