using Radar.Application.Acquisitions;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.Acquisitions;

/// <summary>SPEC 217 §2 — the shared run-time projection every consumer reads.</summary>
public sealed class PendingAcquisitionsTests
{
    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset Announced =
        new(2026, 8, 10, 8, 0, 12, TimeSpan.Zero);

    internal static PendingAcquisitionRecord Record(
        Guid? companyId = null,
        DateTimeOffset? announced = null,
        string accession = "0001193125-26-341302",
        Guid? evidenceId = null) =>
        new(
            Id: Guid.NewGuid(),
            CompanyId: companyId ?? Company,
            Accession: accession,
            EvidenceId: evidenceId ?? Guid.Parse("db2132fb-c4b5-d2e7-6449-4716a6186a83"),
            AnnouncedOnUtc: announced ?? Announced,
            AcquirerName: "Safe Harbor Marinas, LLC",
            ConsiderationPerShare: "53.00",
            ConsiderationCurrency: "$",
            ConsiderationKind: AcquisitionConsiderationKind.Cash,
            ConsiderationQuote: "…will be converted into the right to receive $53.00 in cash…",
            TargetQuote: "…Merger Sub will merge with and into MarineMax, Inc.…",
            ScanVersion: AcquisitionAgreementScan.Version,
            Verification: AcquisitionVerification.Verbatim);

    internal static PendingAcquisitions From(params PendingAcquisitionRecord[] records) =>
        new(new AcquisitionStoreReadResult(records, 0));

    [Fact]
    public void None_IsNotAMeasuredEmpty()
    {
        // The distinction the whole "nothing discarded without being counted" rule turns on: the inert
        // projection has NOT looked, so an absence must never render as a measured zero.
        Assert.False(PendingAcquisitions.None.RecognitionAvailable);
        Assert.Empty(PendingAcquisitions.None.Records);
        Assert.True(From().RecognitionAvailable);
    }

    [Fact]
    public void StatusAt_IsPendingFromTheAnnouncement_AndNullBeforeIt()
    {
        var acquisitions = From(Record());

        Assert.Null(acquisitions.StatusAt(Company, Announced.AddSeconds(-1)));
        Assert.Equal(CompanyStatus.PendingAcquisition, acquisitions.StatusAt(Company, Announced));
        Assert.Equal(
            CompanyStatus.PendingAcquisition, acquisitions.StatusAt(Company, Announced.AddDays(30)));

        // Null means NOT RECORDED, never Active: the curated seed status is a different fact.
        Assert.Null(acquisitions.StatusAt(Guid.NewGuid(), Announced.AddDays(30)));
    }

    [Fact]
    public void EarliestAnnouncementWins_SoALaterAmendmentCannotReopenAPinnedWindow()
    {
        var early = Record(announced: Announced, accession: "aaa");
        var late = Record(announced: Announced.AddDays(20), accession: "bbb");

        Assert.Equal("aaa", From(late, early).For(Company)!.Accession);
        Assert.Equal("aaa", From(early, late).For(Company)!.Accession);   // order-independent (AD-3)
    }

    [Fact]
    public void RecognisedEvidenceIds_CarriesEveryRecordsEvidence_NotJustTheGoverningOne()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var acquisitions = From(
            Record(announced: Announced, accession: "aaa", evidenceId: a),
            Record(announced: Announced.AddDays(1), accession: "bbb", evidenceId: b));

        Assert.Contains(a, acquisitions.RecognisedEvidenceIds);
        Assert.Contains(b, acquisitions.RecognisedEvidenceIds);
    }

    [Fact]
    public void AnnouncedOn_IsTheUtcCalendarDate_TheEfficacyExclusionCompares()
    {
        Assert.Equal(new DateOnly(2026, 8, 10), From(Record()).AnnouncedOn(Company));
        Assert.Null(From(Record()).AnnouncedOn(Guid.NewGuid()));
    }

    [Fact]
    public void Unreadable_TravelsWithTheProjection()
    {
        // A store that could not read a file may be leaving a closed thesis open; the count must reach the
        // consumer, never only a log.
        var projection = new PendingAcquisitions(new AcquisitionStoreReadResult([], 3));

        Assert.Equal(3, projection.Unreadable);
    }

    [Fact]
    public void AnUNREADABLEStore_IsNotAMeasuredEmptyEither()
    {
        // SPEC 217, closing a hole the first cut left: a store whose ROOT could not be enumerated returns
        // no records, and treating that as a measured zero would let the weekly report print
        // "no company is under a recognised pending acquisition" over a store it could not open. That is a
        // defaulted zero rendering as a measured zero — the exact failure this slice exists to prevent, and
        // a Warning in a log is not a substitute for the report staying silent.
        var unavailable = new PendingAcquisitions(AcquisitionStoreReadResult.Unavailable);

        Assert.False(unavailable.RecognitionAvailable);
        Assert.Empty(unavailable.Records);

        // ...and it stays false even when a caller asserts availability: the read itself failed.
        Assert.False(
            new PendingAcquisitions(AcquisitionStoreReadResult.Unavailable, recognitionAvailable: true)
                .RecognitionAvailable);

        // The three states are distinct: not composed / read-and-empty / read-failed.
        Assert.False(PendingAcquisitions.None.RecognitionAvailable);
        Assert.True(new PendingAcquisitions(AcquisitionStoreReadResult.Empty).RecognitionAvailable);
        Assert.True(AcquisitionStoreReadResult.Empty.Readable);
        Assert.False(AcquisitionStoreReadResult.Unavailable.Readable);
    }

    [Fact]
    public void UnreadableFilesAreADifferentFactFromAnUnreadableStore()
    {
        // "Some records may be missing from this answer" (Unreadable > 0, still Readable) and "there is no
        // answer" (Readable == false) must never collapse into one number: the first still lets the report
        // state what it DID find, the second must not state an absence at all.
        var partial = new PendingAcquisitions(new AcquisitionStoreReadResult([Record()], 2));

        Assert.True(partial.RecognitionAvailable);
        Assert.Equal(2, partial.Unreadable);
        Assert.Single(partial.Records);
    }
}
