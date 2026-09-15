using Radar.Application.Acquisitions;
using Radar.TestSupport;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 227 — proof that the trimmed REAL-body fixtures (<see cref="RealAcquisitionFilingBodies"/>) reproduce
/// the defect they exist to pin. The frozen <see cref="AcqScanV1HistoricalControl"/> must make on these excerpts
/// exactly the two mistakes the live <c>acqscan-v1</c> made on the full bodies — otherwise a v2 test that
/// passes on the excerpts would prove nothing. Offline, deterministic, never skipped.
/// </summary>
public sealed class AcqScanV1HistoricalControlFixtureTests
{
    [Fact]
    public void TheV1Control_RecognisesTheSteveMaddenExcerpt_OnTheDividendAndARole()
    {
        var result = AcqScanV1HistoricalControl.Scan(
            RealAcquisitionFilingBodies.SteveMaddenQ1ResultsAndCreditAgreement, ["Steven Madden, Ltd.", "Steven Madden"]);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("Lead Borrower", result.AcquirerName);
        Assert.Equal("0.21", result.ConsiderationPerShare);
    }

    [Fact]
    public void TheV1Control_RecordsTheMarineMaxExcerpt_AtTheParValue_WithTheRoleWord()
    {
        var result = AcqScanV1HistoricalControl.Scan(
            RealAcquisitionFilingBodies.MarineMaxMergerAgreement, ["MarineMax, Inc.", "MarineMax"]);

        Assert.Equal(AcquisitionScanOutcome.Recognised, result.Outcome);
        Assert.Equal("Parent", result.AcquirerName);
        Assert.Equal("0.001", result.ConsiderationPerShare);
    }

    [Fact]
    public void TheV1Control_IsStillAcqScanV1()
    {
        // The control must never track production: if this ever reads the current version the "v1" column of
        // the spec-227 §3 measurement would silently mean something else.
        Assert.Equal("acqscan-v1", AcqScanV1HistoricalControl.Version);
        Assert.NotEqual(AcquisitionAgreementScan.Version, AcqScanV1HistoricalControl.Version);
    }
}
