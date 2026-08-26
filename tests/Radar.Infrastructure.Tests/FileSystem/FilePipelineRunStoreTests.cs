using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Pipeline;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FilePipelineRunStoreTests : IDisposable
{
    private static readonly DateTimeOffset BaseInstant = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FilePipelineRunStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private FilePipelineRunStore CreateStore(string? rootDirectory = null) =>
        new(
            new FilePipelineRunStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FilePipelineRunStore>.Instance);

    private static PipelineRunRecord RecordAt(
        DateTimeOffset createdAtUtc,
        Guid? id = null,
        IReadOnlyList<string>? collectors = null) =>
        new(
            Id: id ?? Guid.NewGuid(),
            CreatedAtUtc: createdAtUtc,
            Collectors: collectors ?? ["sec-edgar", "RssPressReleaseCollector"],
            EvidenceCollected: 12,
            EvidenceNew: 5,
            SignalsExtracted: 7,
            SignalsValid: 6,
            SignalsApproved: 4,
            SignalsNeedingReview: 2,
            CompaniesScored: 9,
            SourcesChecked: 3,
            SourcesFailed: 1,
            ReportId: Guid.NewGuid());

    [Fact]
    public async Task WriteAsync_ThenReadRecent_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var record = RecordAt(BaseInstant, id);

        var store = CreateStore();
        var path = await store.WriteAsync(record, CancellationToken.None);

        // The file is written under {root}/{yyyy}/{MM}/run-...json.
        var expectedDir = Path.Combine(_tempDir, "2026", "02");
        Assert.StartsWith(expectedDir, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);
        var roundTripped = Assert.Single(read);

        Assert.Equal(record.Id, roundTripped.Id);
        Assert.Equal(record.CreatedAtUtc, roundTripped.CreatedAtUtc);
        Assert.Equal(record.Collectors, roundTripped.Collectors);
        Assert.Equal(record.EvidenceCollected, roundTripped.EvidenceCollected);
        Assert.Equal(record.EvidenceNew, roundTripped.EvidenceNew);
        Assert.Equal(record.SignalsExtracted, roundTripped.SignalsExtracted);
        Assert.Equal(record.SignalsValid, roundTripped.SignalsValid);
        Assert.Equal(record.SignalsApproved, roundTripped.SignalsApproved);
        Assert.Equal(record.SignalsNeedingReview, roundTripped.SignalsNeedingReview);
        Assert.Equal(record.CompaniesScored, roundTripped.CompaniesScored);
        Assert.Equal(record.SourcesChecked, roundTripped.SourcesChecked);
        Assert.Equal(record.SourcesFailed, roundTripped.SourcesFailed);
        Assert.Equal(record.ReportId, roundTripped.ReportId);
    }

    [Fact]
    public async Task WriteAsync_ThenReadRecent_RoundTripsCollectionWarnings()
    {
        var warning = new CollectionHealthWarning(
            Code: "feeds-lost-before-collection",
            Severity: CollectionHealthSeverity.Warning,
            FeedType: "sec",
            DeclaredInSeed: 7,
            ReachedCollectors: 0,
            Message: "Seed declares 7 'sec' feed(s) but only 0 reached the collectors.");
        var record = RecordAt(BaseInstant) with { CollectionWarnings = [warning] };

        var store = CreateStore();
        await store.WriteAsync(record, CancellationToken.None);

        var read = await store.ReadRecentAsync(10, CancellationToken.None);
        var roundTripped = Assert.Single(read);

        Assert.NotNull(roundTripped.CollectionWarnings);
        var surfaced = Assert.Single(roundTripped.CollectionWarnings!);
        Assert.Equal(warning, surfaced);
    }

    [Fact]
    public async Task ReadRecentAsync_JsonWithoutTrailingOptionalFields_DeserializesToNull()
    {
        // Back-compat for EVERY trailing optional field added to PipelineRunRecord over time: an on-disk run
        // file written before spec 98 has no collectionWarnings field, one written before spec 137 has no
        // strategies/primaryStrategy fields, and one written before spec 161 has no companyFilter field. All
        // of them must still deserialize, each absent field taking its trailing-optional null default.
        // Asserting them explicitly is the regression guard: a future reordering, a required-modifier slip,
        // or a [JsonRequired] would otherwise break old run files silently.
        //
        // Keep this enumeration COMPLETE — every trailing optional field added to the record belongs here.
        const string legacyJson = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "createdAtUtc": "2026-02-08T12:00:00+00:00",
              "collectors": ["sec-edgar"],
              "evidenceCollected": 12,
              "evidenceNew": 5,
              "signalsExtracted": 7,
              "signalsValid": 6,
              "signalsApproved": 4,
              "signalsNeedingReview": 2,
              "companiesScored": 9,
              "sourcesChecked": 3,
              "sourcesFailed": 1,
              "reportId": null
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "legacy-run.json"), legacyJson);

        var store = CreateStore();
        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var record = Assert.Single(read);
        Assert.Null(record.CollectionWarnings);
        Assert.Null(record.Strategies);
        Assert.Null(record.PrimaryStrategy);
        // Spec 161: null == the run covered the whole watch universe, which is exactly what a pre-161 run
        // did — so an old record reads correctly rather than merely parsing.
        Assert.Null(record.CompanyFilter);
    }

    [Fact]
    public async Task ReadRecentAsync_ReturnsNewestFirst_LimitedToCount()
    {
        var oldest = RecordAt(BaseInstant);
        var middle = RecordAt(BaseInstant.AddMinutes(1));
        var newest = RecordAt(BaseInstant.AddMinutes(2));

        var store = CreateStore();
        await store.WriteAsync(oldest, CancellationToken.None);
        await store.WriteAsync(middle, CancellationToken.None);
        await store.WriteAsync(newest, CancellationToken.None);

        var read = await store.ReadRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, read.Count);
        Assert.Equal(newest.Id, read[0].Id);
        Assert.Equal(middle.Id, read[1].Id);
    }

    [Fact]
    public async Task ReadRecentAsync_MalformedNewestFile_StillReturnsCountValidRecords()
    {
        // The read walks newest-first and stops once it has `count` valid records. A malformed file in
        // the newest position must be skipped without causing the read to under-return older valid runs.
        var oldest = RecordAt(BaseInstant);
        var middle = RecordAt(BaseInstant.AddMinutes(1));
        var newest = RecordAt(BaseInstant.AddMinutes(2));

        var store = CreateStore();
        await store.WriteAsync(oldest, CancellationToken.None);
        await store.WriteAsync(middle, CancellationToken.None);
        var newestPath = await store.WriteAsync(newest, CancellationToken.None);

        // Corrupt the newest run file in place; it must be skipped, and the read must fall through to the
        // next-newest valid records rather than returning fewer than `count`.
        await File.WriteAllTextAsync(newestPath, "{ not valid");

        var read = await store.ReadRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, read.Count);
        Assert.Equal(middle.Id, read[0].Id);
        Assert.Equal(oldest.Id, read[1].Id);
    }

    [Fact]
    public async Task ReadRecentAsync_WithZeroOrNegativeCount_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.WriteAsync(RecordAt(BaseInstant), CancellationToken.None);

        Assert.Empty(await store.ReadRecentAsync(0, CancellationToken.None));
        Assert.Empty(await store.ReadRecentAsync(-1, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRecentAsync_MissingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        var store = CreateStore(missing);

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        Assert.Empty(read);
    }

    [Fact]
    public async Task ReadRecentAsync_SkipsMalformedFile()
    {
        var good = RecordAt(BaseInstant);

        var store = CreateStore();
        await store.WriteAsync(good, CancellationToken.None);

        // Drop a malformed JSON file into the root; it must be skipped, not break the read.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "{ not valid");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var roundTripped = Assert.Single(read);
        Assert.Equal(good.Id, roundTripped.Id);
    }

    [Fact]
    public async Task ReadRecentAsync_SkipsNullRecordFile()
    {
        var good = RecordAt(BaseInstant);

        var store = CreateStore();
        await store.WriteAsync(good, CancellationToken.None);

        // A file whose contents deserialize to null (the JSON literal `null`) is a malformed
        // entry; it must be skipped like other unreadable files, not silently returned as null.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "null.json"), "null");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var roundTripped = Assert.Single(read);
        Assert.Equal(good.Id, roundTripped.Id);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var id = Guid.NewGuid();
        var record = RecordAt(BaseInstant, id);

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(record, CancellationToken.None);

        // The attempted path is returned (no throw); the in-memory result still carries the counts.
        var expectedPath = Path.Combine(
            rootAsFile,
            "2026",
            "02",
            $"run-{BaseInstant.UtcDateTime:yyyyMMddTHHmmssfffZ}-{id}.json");
        Assert.Equal(expectedPath, path);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 169: the time-bounded read and the trailing-optional CollectorRuns field.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadBetweenAsync_ReturnsEveryRecordInTheInclusiveRange_AscendingByCreatedAtThenId()
    {
        var store = CreateStore();

        var early = RecordAt(BaseInstant.AddDays(-2), Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var lowIdAtBoundary = RecordAt(BaseInstant, Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var highIdAtBoundary = RecordAt(BaseInstant, Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var late = RecordAt(BaseInstant.AddDays(2), Guid.Parse("44444444-4444-4444-4444-444444444444"));

        foreach (var record in new[] { late, highIdAtBoundary, lowIdAtBoundary, early })
        {
            await store.WriteAsync(record, CancellationToken.None);
        }

        // BOTH bounds are inclusive: the boundary records are in.
        var read = await store.ReadBetweenAsync(
            BaseInstant, BaseInstant.AddDays(2), CancellationToken.None);

        Assert.Equal(
            [lowIdAtBoundary.Id, highIdAtBoundary.Id, late.Id],
            read.Select(r => r.Id));
    }

    [Fact]
    public async Task ReadBetweenAsync_IsNotTruncated_SoAbsenceIsDistinguishableFromAMissedRead()
    {
        var store = CreateStore();

        // More records than any plausible "recent N" cap, all inside the range: the coverage chain must be
        // able to tell "no run happened here" from "the read stopped before reaching it".
        for (var i = 0; i < 40; i++)
        {
            await store.WriteAsync(RecordAt(BaseInstant.AddHours(i)), CancellationToken.None);
        }

        var read = await store.ReadBetweenAsync(
            BaseInstant, BaseInstant.AddHours(39), CancellationToken.None);

        Assert.Equal(40, read.Count);
    }

    [Fact]
    public async Task ReadBetweenAsync_InvertedRange_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.WriteAsync(RecordAt(BaseInstant), CancellationToken.None);

        Assert.Empty(await store.ReadBetweenAsync(
            BaseInstant.AddDays(1), BaseInstant, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBetweenAsync_MissingDirectory_ReturnsEmpty()
    {
        var store = CreateStore(Path.Combine(_tempDir, "does-not-exist"));

        Assert.Empty(await store.ReadBetweenAsync(
            BaseInstant.AddDays(-1), BaseInstant.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReadBetweenAsync_MalformedFile_IsSkippedNotThrown()
    {
        var store = CreateStore();
        var good = RecordAt(BaseInstant);
        await store.WriteAsync(good, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "broken.json"), "{ not json");

        var read = await store.ReadBetweenAsync(
            BaseInstant.AddDays(-1), BaseInstant.AddDays(1), CancellationToken.None);

        Assert.Equal(good.Id, Assert.Single(read).Id);
    }

    [Fact]
    public async Task CollectorRuns_RoundTripWithTheirPerCompanyCoverage()
    {
        var store = CreateStore();
        var companyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var record = RecordAt(BaseInstant) with
        {
            CollectorRuns =
            [
                new CollectorRunRecord(
                    CollectorName: "newssearch",
                    SourcesChecked: 2,
                    SourcesSucceeded: 1,
                    SourcesFailed: 1,
                    ItemsCollected: 7,
                    Failures: [new SourceFailure("feed", "query=x", "HTTP 500")],
                    CompanyCoverage:
                    [
                        new CollectorCompanyCoverage(
                            companyId, 2, 1, HitEffectiveResultLimit: true,
                            Issues:
                            [
                                CollectionCoverageIssues.ResultLimitReached,
                                CollectionCoverageIssues.SourceFailure,
                            ]),
                    ]),
            ],
        };

        await store.WriteAsync(record, CancellationToken.None);

        var read = Assert.Single(await store.ReadRecentAsync(10, CancellationToken.None));
        var collectorRun = Assert.Single(read.CollectorRuns!);
        Assert.Equal("newssearch", collectorRun.CollectorName);
        Assert.Equal(1, collectorRun.SourcesFailed);
        Assert.Equal("HTTP 500", Assert.Single(collectorRun.Failures).Reason);

        var coverage = Assert.Single(collectorRun.CompanyCoverage!);
        Assert.Equal(companyId, coverage.CompanyId);
        Assert.Equal(2, coverage.ExpectedFeedCount);
        Assert.Equal(1, coverage.SuccessfulFeedCount);
        Assert.True(coverage.HitEffectiveResultLimit);
        Assert.Equal(
            [CollectionCoverageIssues.ResultLimitReached, CollectionCoverageIssues.SourceFailure],
            coverage.Issues);
    }

    [Fact]
    public async Task LegacyRunJsonWithoutCollectorRuns_StillDeserializes_AndReadsAsUnproven()
    {
        // A byte-for-byte plausible pre-spec-169 run record: no collectorRuns property at all. It must still
        // load, and CollectorRuns must be NULL — which the coverage evaluator reads as UNPROVEN, never as a
        // clean checkpoint.
        var directory = Path.Combine(_tempDir, "2026", "02");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "run-legacy.json"),
            """
            {
              "id": "55555555-5555-5555-5555-555555555555",
              "createdAtUtc": "2026-02-08T12:00:00+00:00",
              "collectors": [ "newssearch" ],
              "evidenceCollected": 1,
              "evidenceNew": 1,
              "signalsExtracted": 1,
              "signalsValid": 1,
              "signalsApproved": 1,
              "signalsNeedingReview": 0,
              "companiesScored": 1,
              "sourcesChecked": 1,
              "sourcesFailed": 0,
              "reportId": null
            }
            """);

        var read = Assert.Single(await CreateStore().ReadRecentAsync(10, CancellationToken.None));

        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), read.Id);
        Assert.Null(read.CollectorRuns);
        Assert.Null(read.CompanyFilter);
        Assert.Equal(["newssearch"], read.Collectors);
    }
    /// <summary>
    /// Spec 190: the trailing nullable local-limit diagnostics survive the durable round-trip, so the audit
    /// is readable from the run log rather than only from a log line.
    /// </summary>
    [Fact]
    public async Task CollectorRuns_RoundTripTheSpec190LocalLimitDiagnostics()
    {
        var store = CreateStore();
        var companyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var record = RecordAt(BaseInstant) with
        {
            CollectorRuns =
            [
                new CollectorRunRecord(
                    CollectorName: "newssearch",
                    SourcesChecked: 1,
                    SourcesSucceeded: 1,
                    SourcesFailed: 0,
                    ItemsCollected: 25,
                    Failures: [],
                    CompanyCoverage:
                    [
                        new CollectorCompanyCoverage(
                            companyId, 1, 1, HitEffectiveResultLimit: true,
                            Issues: [CollectionCoverageIssues.ResultLimitReached],
                            EffectiveResultLimit: 25,
                            MaxValidItemsObserved: 31,
                            ConfirmedLocalTruncation: true,
                            UnadmittedRelevantTailItemCount: 4),
                    ]),
            ],
        };

        await store.WriteAsync(record, CancellationToken.None);

        var read = Assert.Single(await store.ReadRecentAsync(10, CancellationToken.None));
        var coverage = Assert.Single(Assert.Single(read.CollectorRuns!).CompanyCoverage!);

        Assert.Equal(25, coverage.EffectiveResultLimit);
        Assert.Equal(31, coverage.MaxValidItemsObserved);
        Assert.True(coverage.ConfirmedLocalTruncation);
        Assert.Equal(4, coverage.UnadmittedRelevantTailItemCount);
        // The fail-closed possible-truncation facts are untouched by the new diagnostics.
        Assert.True(coverage.HitEffectiveResultLimit);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
    }

    /// <summary>
    /// Spec 190: an ACCRUED pre-190 coverage row carries none of the diagnostic properties. It must hydrate
    /// with all four as <c>null</c> — "not recorded" — and never as <c>false</c>/<c>0</c>, which would be a
    /// fabricated claim that Radar observed no response tail.
    /// </summary>
    [Fact]
    public async Task LegacyCoverageRowWithoutSpec190Diagnostics_HydratesAsNotRecorded_NeverFalse()
    {
        var directory = Path.Combine(_tempDir, "2026", "02");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "run-legacy-coverage.json"),
            """
            {
              "id": "66666666-6666-6666-6666-666666666666",
              "createdAtUtc": "2026-02-08T12:00:00+00:00",
              "collectors": [ "newssearch" ],
              "evidenceCollected": 1,
              "evidenceNew": 1,
              "signalsExtracted": 1,
              "signalsValid": 1,
              "signalsApproved": 1,
              "signalsNeedingReview": 0,
              "companiesScored": 1,
              "sourcesChecked": 1,
              "sourcesFailed": 0,
              "reportId": null,
              "collectorRuns": [
                {
                  "collectorName": "newssearch",
                  "sourcesChecked": 1,
                  "sourcesSucceeded": 1,
                  "sourcesFailed": 0,
                  "itemsCollected": 25,
                  "failures": [],
                  "companyCoverage": [
                    {
                      "companyId": "77777777-7777-7777-7777-777777777777",
                      "expectedFeedCount": 1,
                      "successfulFeedCount": 1,
                      "hitEffectiveResultLimit": true,
                      "issues": [ "ResultLimitReached" ]
                    }
                  ]
                }
              ]
            }
            """);

        var read = Assert.Single(await CreateStore().ReadRecentAsync(10, CancellationToken.None));
        var coverage = Assert.Single(Assert.Single(read.CollectorRuns!).CompanyCoverage!);

        Assert.Null(coverage.EffectiveResultLimit);
        Assert.Null(coverage.MaxValidItemsObserved);
        Assert.Null(coverage.ConfirmedLocalTruncation);
        Assert.Null(coverage.UnadmittedRelevantTailItemCount);
        // Everything the legacy row DID record still reads exactly as it did.
        Assert.True(coverage.HitEffectiveResultLimit);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
    }

}
