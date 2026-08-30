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
    public async Task ComparabilityPolicyAndMarkers_RoundTrip_AndLegacyFileReadsAsNull()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // Spec 160: the comparability policy + both marker groups round-trip on the record...
            var record = new AnalyzedFilingRecord(
                Accession,
                AnalyzedFilingOutcome.NoDirectionalSignal,
                null,
                null,
                AnalyzedFilingRecord.CurrentCacheVersion,
                ComparabilityPolicy: "cmpscan-v1;cap=0.65",
                ComparabilityMarkers: new ComparabilityMarkers(
                    ["litigation settlement"], ["continuing operations"]));

            await cache.PutAsync(record, CancellationToken.None);
            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal("cmpscan-v1;cap=0.65", read!.ComparabilityPolicy);
            Assert.NotNull(read.ComparabilityMarkers);
            Assert.Equal(["litigation settlement"], read.ComparabilityMarkers!.CapTriggering);
            Assert.Equal(["continuing operations"], read.ComparabilityMarkers.DiagnosticOnly);

            // ...and a pre-160 file (current cacheVersion, no comparability properties) deserializes to NULL
            // policy/markers — "not scanned", which the source treats as a HIT (heal forward). NOT a
            // CurrentCacheVersion bump: the null-policy hit rule IS the migration story.
            var legacyJson = $$"""
                {
                  "accession": "{{Accession}}",
                  "outcome": "NoDirectionalSignal",
                  "signal": null,
                  "observedAtUtc": null,
                  "cacheVersion": {{AnalyzedFilingRecord.CurrentCacheVersion}}
                }
                """;
            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            await File.WriteAllTextAsync(path, legacyJson, CancellationToken.None);

            var legacy = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.NotNull(legacy); // still a structurally valid HIT at the cache layer...
            Assert.Null(legacy!.ComparabilityPolicy);   // ...recorded honestly as "not scanned",
            Assert.Null(legacy.ComparabilityMarkers);   // never a false claim of a clean scan.
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

    // ---- spec 204: the 2 → 3 bump is OUTCOME-SCOPED, and the four cause fields round-trip -----------------

    [Fact]
    public async Task TryGet_V2ProducedSignalRecord_IsAHit()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // A v2 DirectionalSignalProduced record is treated as CURRENT (spec 204): its signal is intact
            // and carried whole on the record — re-reading it would spend hosted calls and a www.sec.gov
            // fetch to reproduce a known answer.
            var v2Produced = new AnalyzedFilingRecord(
                Accession,
                AnalyzedFilingOutcome.DirectionalSignalProduced,
                new ExtractedSignal(
                    CompanyMention: "Mercury — SEC",
                    SignalType: "GuidanceChange",
                    Direction: "Positive",
                    Strength: 8,
                    Novelty: 6,
                    Confidence: 0.9m,
                    SupportingExcerpt: "8-K — Report",
                    Reason: "Revenue rose 40%."),
                new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero),
                CacheVersion: 2);
            await WriteRecordForAsync(dir, Accession, v2Produced);

            var read = await cache.TryGetAsync(Accession, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, read!.Outcome);
            Assert.Equal(2, read.CacheVersion); // returned as stored — the hit does not re-stamp the file.
            Assert.NotNull(read.Signal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_V2NoSignalRecord_IsAMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // A v2 NoDirectionalSignal record carries no cause/direction/confidence/rationale, so it cannot
            // replay the spec-204 read signal — it must be re-analyzed (a stale-version MISS, bounded like
            // any miss by the MaxFilingsPerRun cap and the 429 breaker).
            var v2NoSignal = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, CacheVersion: 2);
            await WriteRecordForAsync(dir, Accession, v2NoSignal);

            Assert.Null(await cache.TryGetAsync(Accession, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]  // legacy-JSON sentinel
    [InlineData(1)]  // pre-spec-116 prompt
    [InlineData(4)]  // a future version this build does not understand
    public async Task TryGet_OtherStaleVersions_AreMisses_EvenForProducedSignalRecords(int staleVersion)
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // The outcome-scoped acceptance is for version 2 ONLY: every other mismatched version stays a
            // miss for BOTH outcomes, exactly as before spec 204.
            var produced = new AnalyzedFilingRecord(
                Accession,
                AnalyzedFilingOutcome.DirectionalSignalProduced,
                new ExtractedSignal("Mercury — SEC", "GuidanceChange", "Positive", 8, 6, 0.9m, "8-K — Report", "r"),
                new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero),
                CacheVersion: staleVersion);
            await WriteRecordForAsync(dir, Accession, produced);

            Assert.Null(await cache.TryGetAsync(Accession, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task V2Fields_RoundTripIntoV3_AndTheFourCauseFieldsHydrateNull()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            // The cache is path-keyed per accession, never per version, so the re-analysis's v3 write
            // REPLACES the v2 file in place. That is acceptable ONLY because a v2 no-signal record carries
            // nothing a v3 record does not — asserted here: every pre-204 field of a v2-shaped record
            // round-trips through the CURRENT type, and the four new fields hydrate as null (NOT RECORDED,
            // never a fabricated cause).
            var v2Shaped = new AnalyzedFilingRecord(
                Accession,
                AnalyzedFilingOutcome.NoDirectionalSignal,
                null,
                null,
                CacheVersion: 2,
                ComparabilityPolicy: "cmpscan-v1;cap=0.65",
                ComparabilityMarkers: new ComparabilityMarkers(["litigation settlement"], ["continuing operations"]));
            var json = JsonSerializer.Serialize(v2Shaped, RadarFileStoreJson.Options);
            // The v2 writer never emitted the four spec-204 properties; strip nothing — they serialize as
            // null and a REAL v2 file simply lacks them, so also deserialize a hand-written v2 document.
            var realV2Json = $$"""
                {
                  "accession": "{{Accession}}",
                  "outcome": "NoDirectionalSignal",
                  "signal": null,
                  "observedAtUtc": null,
                  "cacheVersion": 2,
                  "comparabilityPolicy": "cmpscan-v1;cap=0.65",
                  "comparabilityMarkers": { "capTriggering": ["litigation settlement"], "diagnosticOnly": ["continuing operations"] }
                }
                """;

            foreach (var document in new[] { json, realV2Json })
            {
                var read = JsonSerializer.Deserialize<AnalyzedFilingRecord>(document, RadarFileStoreJson.Options);
                Assert.NotNull(read);
                Assert.Equal(Accession, read!.Accession);
                Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, read.Outcome);
                Assert.Null(read.Signal);
                Assert.Null(read.ObservedAtUtc);
                Assert.Equal(2, read.CacheVersion);
                Assert.Equal("cmpscan-v1;cap=0.65", read.ComparabilityPolicy);
                Assert.Equal(["litigation settlement"], read.ComparabilityMarkers!.CapTriggering);
                Assert.Equal(["continuing operations"], read.ComparabilityMarkers.DiagnosticOnly);
                Assert.Null(read.NoSignalCause);
                Assert.Null(read.ReadDirection);
                Assert.Null(read.ReadConfidence);
                Assert.Null(read.Rationale);
            }

            // And the v3 re-write over the same path is a plain PutAsync — the record with the cause fields
            // round-trips whole.
            var v3 = v2Shaped with
            {
                CacheVersion = AnalyzedFilingRecord.CurrentCacheVersion,
                NoSignalCause = FilingNoSignalCause.Mixed,
                ReadDirection = "Mixed",
                ReadConfidence = 0.85m,
                Rationale = "Revenue up, margins down.",
            };
            await cache.PutAsync(v3, CancellationToken.None);
            var reread = await cache.TryGetAsync(Accession, CancellationToken.None);
            Assert.NotNull(reread);
            Assert.Equal(FilingNoSignalCause.Mixed, reread!.NoSignalCause);
            Assert.Equal("Mixed", reread.ReadDirection);
            Assert.Equal(0.85m, reread.ReadConfidence);
            Assert.Equal("Revenue up, margins down.", reread.Rationale);
            Assert.Equal("cmpscan-v1;cap=0.65", reread.ComparabilityPolicy);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoSignalCause_IsPersistedAsAToken_AndAnIntegerOnDiskDegradesToAMiss()
    {
        var dir = NewTempDir();
        try
        {
            var cache = CreateCache(dir);
            var record = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null,
                AnalyzedFilingRecord.CurrentCacheVersion,
                NoSignalCause: FilingNoSignalCause.BelowConfidence,
                ReadDirection: "Improving",
                ReadConfidence: 0.5m,
                Rationale: "Weakly improving.");
            await cache.PutAsync(record, CancellationToken.None);

            // Token-based on disk (RadarFileStoreJson: JsonStringEnumConverter, allowIntegerValues false) —
            // the ordinal can never leak into a file...
            var path = Path.Combine(dir, Accession.ToLowerInvariant() + ".json");
            var onDisk = await File.ReadAllTextAsync(path, CancellationToken.None);
            Assert.Contains("\"noSignalCause\": \"BelowConfidence\"", onDisk, StringComparison.Ordinal);

            // ...and a file carrying an integer instead of a token is REJECTED on read (JsonException →
            // cache miss), never silently mapped onto whichever member holds that ordinal today.
            await File.WriteAllTextAsync(
                path,
                onDisk.Replace("\"noSignalCause\": \"BelowConfidence\"", "\"noSignalCause\": 2", StringComparison.Ordinal),
                CancellationToken.None);
            Assert.Null(await cache.TryGetAsync(Accession, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryGet_DifferentModelSegment_IsMiss_SameSegment_IsHit()
    {
        var dir = NewTempDir();
        try
        {
            // Cache identity is scoped to the analyzing model/provider (spec 118) via the ModelSegment: a record
            // written under "model-a" must be a MISS when read under "model-b" over the SAME root (a model switch
            // re-analyzes rather than replaying another model's read), and a HIT when read back under "model-a".
            var record = new AnalyzedFilingRecord(
                Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, AnalyzedFilingRecord.CurrentCacheVersion);

            var cacheA = CreateCache(dir, "model-a");
            await cacheA.PutAsync(record, CancellationToken.None);

            var cacheB = CreateCache(dir, "model-b");
            Assert.Null(await cacheB.TryGetAsync(Accession, CancellationToken.None));

            var cacheASame = CreateCache(dir, "model-a");
            var hit = await cacheASame.TryGetAsync(Accession, CancellationToken.None);
            Assert.NotNull(hit);
            Assert.Equal(Accession, hit!.Accession);
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

    private static FileAnalyzedFilingCache CreateCache(string dir, string modelSegment) =>
        new(
            new FileAnalyzedFilingCacheOptions { RootDirectory = dir, ModelSegment = modelSegment },
            NullLogger<FileAnalyzedFilingCache>.Instance);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-filings-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
