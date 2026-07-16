using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Round-trip + fail-safe coverage for the spec-107 per-accession earnings-analysis-result cache. A
/// DirectionalSignalProduced record round-trips field-identically; an unknown accession is a miss; a corrupt
/// file degrades to a miss (never throws) so a bad cache file cannot break a run.
/// </summary>
public sealed class FileAnalyzedFilingCacheTests
{
    private const string Accession = "0001049521-26-000011";

    [Fact]
    public async Task Put_ThenTryGet_RoundTripsRecord()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            var signal = new ExtractedSignal(
                CompanyMention: "Mercury — SEC",
                SignalType: "GuidanceChange",
                Direction: "Positive",
                Strength: 6,
                Novelty: 6,
                Confidence: 0.9m,
                SupportingExcerpt: "8-K — Report",
                Reason: "Revenue rose 40%.");
            var observedAt = new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero);
            var record = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.DirectionalSignalProduced, signal, observedAt);

            await cache.PutAsync(record, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(Accession, read!.Accession);
            Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, read.Outcome);
            Assert.Equal(observedAt, read.ObservedAtUtc);
            Assert.NotNull(read.Signal);
            Assert.Equal(signal, read.Signal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoSignal_RoundTripsWithNullSignal()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            var record = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null);

            await cache.PutAsync(record, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, read!.Outcome);
            Assert.Null(read.Signal);
            Assert.Null(read.ObservedAtUtc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_UnknownAccession_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            var read = await cache.TryGetAsync("0000000000-00-000000", CancellationToken.None);
            Assert.Null(read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_CorruptFile_DegradesToMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // Write garbage into the file the cache would read for this accession (sanitized == lowercased).
            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            await File.WriteAllTextAsync(path, "{ not valid json ", CancellationToken.None);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.Null(read); // a bad file is a miss, never a throw.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static FileAnalyzedFilingCache CreateCache(string dir) =>
        new(
            new FileAnalyzedFilingCacheOptions { RootDirectory = dir },
            NullLogger<FileAnalyzedFilingCache>.Instance);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-filings-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
