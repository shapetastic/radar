using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.FileSystem;
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
                Accession,
                AnalyzedFilingOutcome.DirectionalSignalProduced,
                signal,
                observedAt,
                AnalyzedFilingRecord.CurrentCacheVersion);

            await cache.PutAsync(record, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(Accession, read!.Accession);
            Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, read.Outcome);
            Assert.Equal(observedAt, read.ObservedAtUtc);
            Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, read.CacheVersion);
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
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, AnalyzedFilingRecord.CurrentCacheVersion);

            await cache.PutAsync(record, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, read!.Outcome);
            Assert.Null(read.Signal);
            Assert.Null(read.ObservedAtUtc);
            Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, read.CacheVersion);
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

    [Fact]
    public async Task TryGet_AccessionMismatch_DegradesToMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // A parseable record whose stored accession disagrees with the file it lives in is untrustworthy —
            // returning it as a hit would replay a signal against the wrong filing, so it must degrade to a miss.
            var wrongAccession = new AnalyzedFilingRecord(
                "9999999999-99-999999",
                AnalyzedFilingOutcome.NoDirectionalSignal,
                null,
                null,
                AnalyzedFilingRecord.CurrentCacheVersion);
            await WriteRecordForAsync(dir, Accession, wrongAccession);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.Null(read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_ProducedOutcomeWithNullSignal_DegradesToMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // DirectionalSignalProduced but no signal to replay is a self-contradictory record; treating it as a
            // hit would silently suppress the filing forever, so it must degrade to a miss and be re-fetched.
            var inconsistent = new AnalyzedFilingRecord(
                Accession,
                AnalyzedFilingOutcome.DirectionalSignalProduced,
                null,
                null,
                AnalyzedFilingRecord.CurrentCacheVersion);
            await WriteRecordForAsync(dir, Accession, inconsistent);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.Null(read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_LegacyRecordWithoutCacheVersion_DegradesToMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // A block-era (pre-spec-114) file has no cacheVersion property, so it deserializes to 0 — a mismatch
            // with CurrentCacheVersion. It must be a MISS so the filing is re-analyzed (the poison self-heals
            // with no manual file deletion).
            var legacyJson = """
                {
                  "accession": "0001049521-26-000011",
                  "outcome": "NoDirectionalSignal",
                  "signal": null,
                  "observedAtUtc": null
                }
                """;
            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            await File.WriteAllTextAsync(path, legacyJson, CancellationToken.None);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.Null(read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)] // never a valid stamp (legacy-JSON sentinel).
    [InlineData(1)] // the pre-spec-116 version: entries cached under the old analyzer prompt must re-analyze.
    public async Task TryGet_StaleExplicitCacheVersion_DegradesToMiss(int staleVersion)
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // An otherwise-consistent record stamped with a non-current version must be a miss (re-analyzed),
            // never replayed. Planted directly on disk so PutAsync's re-stamping cannot mask the stale value.
            Assert.NotEqual(AnalyzedFilingRecord.CurrentCacheVersion, staleVersion);
            var stale = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, CacheVersion: staleVersion);
            await WriteRecordForAsync(dir, Accession, stale);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.Null(read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Put_StampsCurrentCacheVersion_EvenWhenRecordCarriesStaleVersion()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // PutAsync stamps CurrentCacheVersion on every write, so a caller cannot accidentally persist an
            // entry that would immediately be treated as a miss.
            var stale = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, CacheVersion: 0);

            await cache.PutAsync(stale, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, read!.CacheVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Serializes <paramref name="record"/> exactly as the cache would and writes it to the file the
    /// cache reads for <paramref name="accessionKey"/> (sanitized == lowercased), so a deliberately inconsistent
    /// record can be planted for the validation tests.</summary>
    private static async Task WriteRecordForAsync(string dir, string accessionKey, AnalyzedFilingRecord record)
    {
        var path = Path.Combine(dir, accessionKey.ToLowerInvariant() + ".json");
        var json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
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
