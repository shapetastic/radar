using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Coverage for the spec-115 opt-in AI filing-read debug store: a record persists as one JSON file per
/// accession; the rationale is defensively re-scrubbed of advice language (AD-9) and re-bounded before
/// persistence; and every failure path (blank/invalid accession, unwritable root) degrades to a logged no-op —
/// a diagnostic write must never abort a run.
/// </summary>
public sealed class FileFilingReadDebugStoreTests
{
    private const string Accession = "0001049521-26-000011";

    private static readonly DateTimeOffset AsOf = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private static FilingReadDebugRecord Record(
        string accession = Accession,
        string? rationale = "Revenue rose 40%; guidance raised.",
        FilingReadOutcome outcome = FilingReadOutcome.DirectionalSignalProduced) =>
        new(
            accession,
            EvidenceId: Guid.NewGuid(),
            InputLength: 1234,
            InputHead: "Revenue rose 40% and the company raised full-year guidance.",
            Direction: "Improving",
            Confidence: 0.9m,
            Rationale: rationale,
            Outcome: outcome,
            AsOfUtc: AsOf);

    [Fact]
    public async Task Record_WritesOneJsonFilePerAccession_RoundTrippable()
    {
        var dir = NewTempDir();
        try
        {
            var store = CreateStore(dir);
            var record = Record();

            await store.RecordAsync(record, CancellationToken.None);

            // Path mirrors FileAnalyzedFilingCache: {root}/{sanitized accession}.json (sanitized == lowercased).
            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            Assert.True(File.Exists(path));

            var read = JsonSerializer.Deserialize<FilingReadDebugRecord>(
                await File.ReadAllTextAsync(path, CancellationToken.None), RadarFileStoreJson.Options);
            Assert.Equal(record, read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_EmptyBodySkipped_NullVerdictFields_RoundTrip()
    {
        var dir = NewTempDir();
        try
        {
            var store = CreateStore(dir);
            var record = Record(rationale: null, outcome: FilingReadOutcome.EmptyBodySkipped) with
            {
                Direction = null,
                Confidence = null,
                InputLength = 0,
                InputHead = string.Empty,
            };

            await store.RecordAsync(record, CancellationToken.None);

            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            var read = JsonSerializer.Deserialize<FilingReadDebugRecord>(
                await File.ReadAllTextAsync(path, CancellationToken.None), RadarFileStoreJson.Options);
            Assert.Equal(record, read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_AdviceLanguageRationale_IsDroppedBeforePersistence()
    {
        // AD-9 defence in depth: ChatFilingAnalyzer already scrubs, but a non-validating IFilingAnalyzer must
        // not be able to persist advice language THROUGH this store — the file on disk carries none of it.
        var dir = NewTempDir();
        try
        {
            var store = CreateStore(dir);
            await store.RecordAsync(
                Record(rationale: "Strong quarter, you should buy this stock."), CancellationToken.None);

            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            var text = await File.ReadAllTextAsync(path, CancellationToken.None);
            Assert.DoesNotContain("buy", text, StringComparison.OrdinalIgnoreCase);

            var read = JsonSerializer.Deserialize<FilingReadDebugRecord>(text, RadarFileStoreJson.Options);
            Assert.Null(read!.Rationale); // dropped, not paraphrased.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_OverlongRationale_IsBoundedToAnalyzerCap()
    {
        var dir = NewTempDir();
        try
        {
            var store = CreateStore(dir);
            await store.RecordAsync(Record(rationale: new string('r', 600)), CancellationToken.None);

            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            var read = JsonSerializer.Deserialize<FilingReadDebugRecord>(
                await File.ReadAllTextAsync(path, CancellationToken.None), RadarFileStoreJson.Options);
            Assert.Equal(500, read!.Rationale!.Length); // same cap as ChatFilingAnalyzer.MaxRationaleLength.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("0001049521/26/000011")] // path separators are invalid filename chars on every platform.
    public async Task Record_BlankOrInvalidAccession_WritesNothing_NeverThrows(string accession)
    {
        var dir = NewTempDir();
        try
        {
            var store = CreateStore(dir);
            await store.RecordAsync(Record(accession: accession), CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_UnwritableRootDirectory_DegradesToLoggedNoOp()
    {
        // Point RootDirectory at an existing FILE: directory creation fails, GracefulFileWriter degrades, and
        // RecordAsync must not throw — a diagnostic write failure never aborts a run.
        var blockingFile = Path.Combine(Path.GetTempPath(), "radar-ai-debug-blocking-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blockingFile, "not a directory", CancellationToken.None);
        try
        {
            var store = CreateStore(blockingFile);
            await store.RecordAsync(Record(), CancellationToken.None); // must not throw.
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    private static FileFilingReadDebugStore CreateStore(string dir) =>
        new(
            new FileFilingReadDebugStoreOptions { RootDirectory = dir },
            NullLogger<FileFilingReadDebugStore>.Instance);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-ai-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
