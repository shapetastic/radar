using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 218 §1: the corpus ENUMERATION seam over the accrued read records. Every file that does not become a
/// record is COUNTED under its own named reason — a silent skip here would make every downstream denominator
/// a lie.
/// </summary>
public sealed class AnalyzedFilingReadCorpusTests
{
    private const string Segment = "openai-model-abc";

    [Fact]
    public async Task ReadAll_HydratesEveryRecordUnderTheCurrentSegment_InDeterministicAccessionOrder()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, Segment);
            await cache.PutAsync(Directional("0001049521-26-000030"), CancellationToken.None);
            await cache.PutAsync(Directional("0001049521-26-000011"), CancellationToken.None);
            await cache.PutAsync(NoSignal("0001049521-26-000020"), CancellationToken.None);

            var corpus = await cache.ReadAllAsync(CancellationToken.None);

            Assert.Equal(3, corpus.FilesScanned);
            Assert.Equal(3, corpus.RecordsHydrated);
            Assert.Equal(Segment, corpus.ModelSegment);
            Assert.True(corpus.CorpusDirectoryExists);
            Assert.False(corpus.EnumerationFailed);
            Assert.Equal(0, corpus.FilesExcluded);
            Assert.Equal(
                ["0001049521-26-000011", "0001049521-26-000020", "0001049521-26-000030"],
                corpus.Entries.Select(e => e.Record.Accession));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_CountsFilesAtTheRootThatTheCurrentSegmentDoesNotReach()
    {
        var dir = NewTempDir();
        try
        {
            // Two legacy files at the ROOT, written before the model segment existed.
            await CreateCache(dir, modelSegment: string.Empty)
                .PutAsync(Directional("0001049521-26-000001"), CancellationToken.None);
            await CreateCache(dir, modelSegment: string.Empty)
                .PutAsync(Directional("0001049521-26-000002"), CancellationToken.None);

            var segmented = CreateCache(dir, Segment);
            await segmented.PutAsync(Directional("0001049521-26-000003"), CancellationToken.None);

            var corpus = await segmented.ReadAllAsync(CancellationToken.None);

            Assert.Equal(1, corpus.RecordsHydrated);
            // The legacy reads are real accrued reads: they must not vanish just because this segment
            // cannot see them.
            Assert.Equal(2, corpus.OutsideCurrentModelSegmentFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_WithNoSegmentConfigured_ReportsAStructuralZeroOutsideTheSegment()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, modelSegment: string.Empty);
            await cache.PutAsync(Directional("0001049521-26-000001"), CancellationToken.None);

            var corpus = await cache.ReadAllAsync(CancellationToken.None);

            Assert.Null(corpus.ModelSegment);
            Assert.Equal(1, corpus.RecordsHydrated);
            // No file CAN be outside a segment that does not exist — a structural zero, not "not counted".
            Assert.Equal(0, corpus.OutsideCurrentModelSegmentFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_CountsUnparseableFiles_AndNeverThrows()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, Segment);
            await cache.PutAsync(Directional("0001049521-26-000001"), CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(dir, Segment, "0001049521-26-000002.json"), "{ not json");

            var corpus = await cache.ReadAllAsync(CancellationToken.None);

            Assert.Equal(2, corpus.FilesScanned);
            Assert.Equal(1, corpus.RecordsHydrated);
            Assert.Equal(1, corpus.UnreadableOrUnparseableFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_CountsFilenameAccessionMismatchAndOutcomeSignalMismatch_Separately()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, Segment);
            var segmentDir = Path.Combine(dir, Segment);

            // Filed under one key, carrying another accession: written correctly by the cache, then MOVED,
            // which is exactly the shape a hand-edited or half-migrated cache directory takes.
            await cache.PutAsync(Directional("0001049521-26-999999"), CancellationToken.None);
            File.Move(
                Path.Combine(segmentDir, "0001049521-26-999999.json"),
                Path.Combine(segmentDir, "0001049521-26-000001.json"));

            // A "no directional signal" record that nonetheless carries a signal.
            await cache.PutAsync(Directional("0001049521-26-000002"), CancellationToken.None);
            var mismatchPath = Path.Combine(segmentDir, "0001049521-26-000002.json");
            await File.WriteAllTextAsync(
                mismatchPath,
                (await File.ReadAllTextAsync(mismatchPath)).Replace(
                    "\"DirectionalSignalProduced\"", "\"NoDirectionalSignal\"", StringComparison.Ordinal));

            var corpus = await cache.ReadAllAsync(CancellationToken.None);

            Assert.Equal(0, corpus.RecordsHydrated);
            Assert.Equal(1, corpus.FileNameAccessionMismatchFiles);
            Assert.Equal(1, corpus.OutcomeSignalMismatchFiles);
            Assert.Equal(0, corpus.UnreadableOrUnparseableFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_DoesNotApplyTheStaleVersionRule_SoALegacyRecordStillCounts()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, Segment);
            await cache.PutAsync(Directional("0001049521-26-000001"), CancellationToken.None);

            // Rewrite the stamped version to the pre-spec-204 one, through the SAME on-disk shape.
            var path = Path.Combine(dir, Segment, "0001049521-26-000001.json");
            var stale = JsonSerializer.Deserialize<AnalyzedFilingRecord>(
                await File.ReadAllTextAsync(path), RadarFileStoreJson.Options)! with { CacheVersion = 2 };
            await File.WriteAllTextAsync(
                path, JsonSerializer.Serialize(stale, RadarFileStoreJson.Options));

            var corpus = await cache.ReadAllAsync(CancellationToken.None);

            // The enumeration answers "what has the reader produced", not "may the pipeline replay this":
            // the version rides out on the record and the CALLER splits by it.
            var entry = Assert.Single(corpus.Entries);
            Assert.Equal(2, entry.Record.CacheVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAll_OnAMissingDirectory_ReportsAMeasuredAbsence_NotAFailure()
    {
        var dir = NewTempDir();
        try
        {
            var corpus = await CreateCache(dir, Segment).ReadAllAsync(CancellationToken.None);

            Assert.False(corpus.CorpusDirectoryExists);
            Assert.False(corpus.EnumerationFailed);
            Assert.Empty(corpus.Entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheCorpusSeam_AndThePerAccessionCache_AreTheSameObject()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir, Segment);
            Assert.IsAssignableFrom<IAnalyzedFilingCache>(cache);
            Assert.IsAssignableFrom<IAnalyzedFilingReadCorpus>(cache);

            await cache.PutAsync(Directional("0001049521-26-000001"), CancellationToken.None);
            Assert.NotNull(await cache.TryGetAsync("0001049521-26-000001", CancellationToken.None));
            Assert.Single((await cache.ReadAllAsync(CancellationToken.None)).Entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static AnalyzedFilingRecord Directional(string accession) => new(
        accession,
        AnalyzedFilingOutcome.DirectionalSignalProduced,
        new ExtractedSignal(
            CompanyMention: "Test — SEC",
            SignalType: "GuidanceChange",
            Direction: "Positive",
            Strength: 8,
            Novelty: 6,
            Confidence: 0.9m,
            SupportingExcerpt: "8-K — Report",
            Reason: "Revenue rose 40%."),
        new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
        AnalyzedFilingRecord.CurrentCacheVersion);

    private static AnalyzedFilingRecord NoSignal(string accession) => new(
        accession,
        AnalyzedFilingOutcome.NoDirectionalSignal,
        Signal: null,
        ObservedAtUtc: null,
        CacheVersion: AnalyzedFilingRecord.CurrentCacheVersion,
        NoSignalCause: FilingNoSignalCause.Mixed);

    private static FileAnalyzedFilingCache CreateCache(string root, string modelSegment) =>
        new(
            new FileAnalyzedFilingCacheOptions { RootDirectory = root, ModelSegment = modelSegment },
            NullLogger<FileAnalyzedFilingCache>.Instance);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
