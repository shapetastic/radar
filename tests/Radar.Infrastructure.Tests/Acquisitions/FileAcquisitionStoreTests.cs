using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Acquisitions;
using Radar.Application.Storage;
using Radar.Infrastructure.Acquisitions;

namespace Radar.Infrastructure.Tests.Acquisitions;

/// <summary>
/// SPEC 217 §1 — the append-only acquisitions store and the heal-forward <c>acqscan-v1</c> answer cache.
/// </summary>
public sealed class FileAcquisitionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-acq-tests", Guid.NewGuid().ToString("N"));

    private FileAcquisitionStore Store() =>
        new(
            new FileAcquisitionStoreOptions { RootDirectory = Path.Combine(_root, "acquisitions") },
            NullLogger<FileAcquisitionStore>.Instance);

    private FileAcquisitionScanCache Cache() =>
        new(
            new FileAcquisitionScanCacheOptions { RootDirectory = Path.Combine(_root, "cache") },
            NullLogger<FileAcquisitionScanCache>.Instance);

    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static PendingAcquisitionRecord Record(string accession = "0001193125-26-341302") => new(
        Id: PendingAcquisitionRecord.IdentityFor(AcquisitionAgreementScan.Version, Company, accession),
        CompanyId: Company,
        Accession: accession,
        EvidenceId: Guid.Parse("db2132fb-c4b5-d2e7-6449-4716a6186a83"),
        AnnouncedOnUtc: new DateTimeOffset(2026, 8, 10, 8, 0, 12, TimeSpan.Zero),
        AcquirerName: "Safe Harbor Marinas, LLC",
        ConsiderationPerShare: "53.00",
        ConsiderationCurrency: "$",
        ConsiderationKind: AcquisitionConsiderationKind.Cash,
        ConsiderationQuote: "…converted into the right to receive $53.00 in cash…",
        TargetQuote: "…Merger Sub will merge with and into MarineMax, Inc.…",
        ScanVersion: AcquisitionAgreementScan.Version,
        Verification: AcquisitionVerification.Verbatim);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp directory must never fail a test.
        }
    }

    [Fact]
    public async Task WriteIfNew_ThenRead_RoundTripsEveryField()
    {
        var store = Store();
        var record = Record();

        var write = await store.WriteIfNewAsync(record, default);
        Assert.Equal(DurableWriteOutcome.Written, write.Outcome);

        var read = await store.GetAllAsync(default);
        Assert.Equal(0, read.Unreadable);
        Assert.Equal(record, Assert.Single(read.Records));
    }

    [Fact]
    public async Task SecondWrite_IsAlreadyOnDisk_NeverAnOverwrite()
    {
        // Append-only (AD-8): a re-recognition on a later run is a durable no-op, and the caller is told the
        // record IS on disk rather than being told it wrote it.
        var store = Store();
        await store.WriteIfNewAsync(Record(), default);

        var second = await store.WriteIfNewAsync(
            Record() with { AcquirerName = "Someone Else, Inc." }, default);

        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, second.Outcome);
        Assert.True(second.Written);
        Assert.Equal("Safe Harbor Marinas, LLC", Assert.Single((await store.GetAllAsync(default)).Records).AcquirerName);
    }

    [Fact]
    public async Task ExistsAsync_IsTheCheapPerFilingCheck()
    {
        var store = Store();
        Assert.False(await store.ExistsAsync(Company, "0001193125-26-341302", default));

        await store.WriteIfNewAsync(Record(), default);

        Assert.True(await store.ExistsAsync(Company, "0001193125-26-341302", default));
        Assert.False(await store.ExistsAsync(Company, "0001193125-26-290439", default));
        Assert.False(await store.ExistsAsync(Guid.NewGuid(), "0001193125-26-341302", default));
    }

    [Fact]
    public async Task ACorruptFileAtTheTargetPath_IsFailed_NotAlreadyOnDisk()
    {
        // The spec-216 §4 rule: reporting a fragment as the record is exactly how a closed thesis would
        // silently reopen — or, worse, how an unreadable file would be treated as a complete recognition.
        var store = Store();
        var directory = Path.Combine(_root, "acquisitions", Company.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "0001193125-26-341302.json"), "{ not json");

        var write = await store.WriteIfNewAsync(Record(), default);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);
    }

    [Fact]
    public async Task AnUnreadableFile_IsSkippedAndCOUNTED_NeverSilentlyDropped()
    {
        var store = Store();
        await store.WriteIfNewAsync(Record(), default);

        var directory = Path.Combine(_root, "acquisitions", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{ not json");

        var read = await store.GetAllAsync(default);

        Assert.Single(read.Records);
        Assert.Equal(1, read.Unreadable);
    }

    [Fact]
    public async Task ARecordWithNoAccessionOrAcquirer_DoesNotParseAsARecord()
    {
        // A banner naming nobody is worse than no banner: a semantically empty file is unreadable, counted.
        var store = Store();
        var directory = Path.Combine(_root, "acquisitions", Company.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "x.json"),
            "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"accession\":\"\",\"acquirerName\":\"\"}");

        var read = await store.GetAllAsync(default);

        Assert.Empty(read.Records);
        Assert.Equal(1, read.Unreadable);
    }

    [Fact]
    public async Task MissingRoot_IsAMeasuredEmpty_NotAFailure()
    {
        var read = await Store().GetAllAsync(default);

        Assert.Empty(read.Records);
        Assert.Equal(0, read.Unreadable);
    }

    [Fact]
    public async Task Records_AreReturnedInADeterministicOrder()
    {
        var store = Store();
        await store.WriteIfNewAsync(Record("0001193125-26-341302"), default);
        await store.WriteIfNewAsync(Record("0001193125-26-290439"), default);

        var read = await store.GetAllAsync(default);

        Assert.Equal(
            ["0001193125-26-290439", "0001193125-26-341302"],
            read.Records.Select(r => r.Accession));
    }

    // ---- the acqscan answer cache ----------------------------------------------------------------------

    [Fact]
    public async Task ScanCache_RoundTrips_AndAMissIsNull()
    {
        var cache = Cache();
        Assert.Null(await cache.TryGetAsync("0001193125-26-290439", default));

        var record = new AcquisitionScanCacheRecord(
            "0001193125-26-290439",
            Company,
            AcquisitionScanOutcome.NoMergerAgreement,
            AcquisitionAgreementScan.Version,
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

        Assert.True(await cache.SetAsync(record, default));
        Assert.Equal(record, await cache.TryGetAsync("0001193125-26-290439", default));
    }

    [Fact]
    public async Task ScanCache_ReplacesUnderALaterScanVersion_HealingForward()
    {
        // A version bump retires every answer WITHOUT deleting a byte from the acquisitions store: the cache
        // is derived state, so overwriting it is the heal-forward path, not a rewrite of history.
        var cache = Cache();
        await cache.SetAsync(
            new AcquisitionScanCacheRecord(
                "acc", Company, AcquisitionScanOutcome.CompanyNotTarget, "acqscan-v1", DateTimeOffset.UnixEpoch),
            default);

        await cache.SetAsync(
            new AcquisitionScanCacheRecord(
                "acc", Company, AcquisitionScanOutcome.Recognised, "acqscan-v2", DateTimeOffset.UnixEpoch),
            default);

        var read = await cache.TryGetAsync("acc", default);
        Assert.Equal("acqscan-v2", read!.ScanVersion);
        Assert.Equal(AcquisitionScanOutcome.Recognised, read.Outcome);
    }

    [Fact]
    public async Task ScanCache_AnEntryWithNoScanVersion_IsAMiss_NeverAReplayOfAnUnknownRule()
    {
        var directory = Path.Combine(_root, "cache");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "acc.json"),
            "{\"accession\":\"acc\",\"outcome\":\"Recognised\",\"scanVersion\":\"\"}");

        Assert.Null(await Cache().TryGetAsync("acc", default));
    }

    [Fact]
    public async Task ScanCache_AnUnparseableEntry_IsAMiss_NotAThrow()
    {
        var directory = Path.Combine(_root, "cache");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "acc.json"), "{ not json");

        Assert.Null(await Cache().TryGetAsync("acc", default));
    }

    [Fact]
    public async Task AMissingRootIsAMeasuredEmpty_ButAnUNREADABLERootIsNot()
    {
        // The distinction MINOR 3 closed. A missing root means the store is composed and simply holds
        // nothing yet — consumers may honestly say "nothing is pending". A root that cannot be ENUMERATED
        // means there is no answer, and a consumer that reported an empty read there would print
        // "no company is under a recognised pending acquisition" over a store it could not open.
        var missing = await Store().GetAllAsync(default);
        Assert.True(missing.Readable);
        Assert.Empty(missing.Records);

        // A FILE where the root directory is expected — the reachable sibling of an IO/permissions failure
        // on the root, and a misconfiguration in its own right. Both land on Unavailable.
        var root = Path.Combine(_root, "acquisitions");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        await File.WriteAllTextAsync(root, "not a directory");

        var unreadable = await Store().GetAllAsync(default);

        Assert.False(unreadable.Readable);
        Assert.Empty(unreadable.Records);

        // ...and the projection every consumer reads therefore refuses to assert an absence.
        Assert.False(new PendingAcquisitions(unreadable).RecognitionAvailable);
    }
}
