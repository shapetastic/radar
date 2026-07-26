using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// The durable <see cref="IEvidenceRepository"/> side of spec 142: <see cref="FileRawEvidenceStore"/> IS
/// the repository, so a FRESH instance over the same directory must reconstruct every field the scoring
/// formula reads — <see cref="EvidenceQuality"/> above all, since it is a v8 input and losing it would
/// silently score history differently from how it was scored live.
/// </summary>
public sealed class FileRawEvidenceStoreHydrationTests : IDisposable
{
    private readonly string _tempDir;

    public FileRawEvidenceStoreHydrationTests()
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
            // Best-effort cleanup.
        }
    }

    private FileRawEvidenceStore CreateStore() =>
        new(
            new FileRawEvidenceStoreOptions { RootDirectory = _tempDir },
            NullLogger<FileRawEvidenceStore>.Instance);

    // -------------------------------------------------------------------------------------------------
    // Full-fidelity round trip through a FRESH instance (the "new process" the spec's invariant needs).
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EvidenceQuality.Unknown)]
    [InlineData(EvidenceQuality.Low)]
    [InlineData(EvidenceQuality.Medium)]
    [InlineData(EvidenceQuality.High)]
    [InlineData(EvidenceQuality.PrimarySource)]
    public async Task Hydration_RoundTripsEveryField_IncludingQuality(EvidenceQuality quality)
    {
        var original = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.Filing)
            .WithSourceName("Acme — SEC filings")
            .WithSourceUrl("https://example.com/acme/filing")
            .WithTitle("8-K filed 2026-03-04")
            .WithSummary(null)
            .WithRawText("Acme filed an 8-K.")
            .WithContentHash("hash-quality-" + quality)
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero))
            .WithCollectedAtUtc(new DateTimeOffset(2026, 3, 5, 6, 0, 0, TimeSpan.Zero))
            .WithQuality(quality)
            .WithMetadataJson(EvidenceMetadata.Compose(
                new Dictionary<string, string> { ["quality"] = "High", ["form"] = "8-K" }, ["ACME"]))
            .Build();

        Assert.True(await CreateStore().WriteIfNewAsync(original, CancellationToken.None));

        // FRESH instance == a fresh process: nothing carries over but the bytes on disk.
        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByIdAsync(original.Id, CancellationToken.None);

        Assert.NotNull(read);
        // The EXPLICIT quality field wins, even when metadata.quality disagrees — proving the top-level
        // field is authoritative for new writes rather than the recovery path masking a loss.
        Assert.Equal(original, read);
    }

    [Fact]
    public async Task Hydration_PreservesNonNullSummary()
    {
        // Production always writes Summary: null, so persisting it changes nothing live — but without it
        // the round-trip assertion above would be green by accident rather than by fidelity.
        var original = new EvidenceBuilder()
            .WithContentHash("hash-summary")
            .WithSummary("A one-line human summary.")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();

        Assert.True(await CreateStore().WriteIfNewAsync(original, CancellationToken.None));

        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByIdAsync(original.Id, CancellationToken.None);

        Assert.Equal("A one-line human summary.", read!.Summary);
    }

    [Fact]
    public async Task WriteIfNewAsync_NullSummary_OmitsThePropertyEntirely()
    {
        // The on-disk shape of a REAL file must stay unchanged: production Summary is always null.
        var original = new EvidenceBuilder()
            .WithContentHash("hash-nosummary")
            .WithSummary(null)
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithPublishedAtUtc(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();

        Assert.True(await CreateStore().WriteIfNewAsync(original, CancellationToken.None));

        var path = Path.Combine(_tempDir, "press-releases", "2026", "04", "hash-nosummary.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.False(doc.RootElement.TryGetProperty("summary", out _));
        // …while quality IS written, because it is a formula input that must not be inferred.
        Assert.Equal("High", doc.RootElement.GetProperty("quality").GetString());
    }

    [Fact]
    public async Task Hydration_ReconstructsMetadataJsonByteIdentically()
    {
        // The persisted file stores companyHints and metadata as SEPARATE nodes; the in-memory
        // MetadataJson is the envelope the mapper authored. Both go through EvidenceMetadata.Compose, so
        // the reconstruction is byte-identical by construction — this pins that.
        var envelope = EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                ["quality"] = "Medium",
                ["secFeedUrl"] = "https://data.sec.gov/submissions/CIK0000098677.json",
                ["form"] = "SC 13G/A",
            },
            ["TR", "Tootsie Roll"]);

        var original = new EvidenceBuilder()
            .WithContentHash("hash-envelope")
            .WithMetadataJson(envelope)
            .WithPublishedAtUtc(new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero))
            .Build();

        Assert.True(await CreateStore().WriteIfNewAsync(original, CancellationToken.None));

        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByIdAsync(original.Id, CancellationToken.None);

        Assert.Equal(envelope, read!.MetadataJson);
    }

    // -------------------------------------------------------------------------------------------------
    // Legacy `quality` handling — explicit, documented, and never flattering.
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PrimarySource", EvidenceQuality.PrimarySource)]
    [InlineData("High", EvidenceQuality.High)]
    [InlineData("medium", EvidenceQuality.Medium)] // case-insensitive, exactly as the mapper parses it
    [InlineData("Low", EvidenceQuality.Low)]
    public async Task LegacyFile_WithoutQualityField_RecoversItFromPersistedMetadataQuality(
        string declared, EvidenceQuality expected)
    {
        // A file written before `quality` became a first-class field. The collector's declared quality has
        // been persisted inside the metadata bag all along, so this is a RECOVERY of the value the item
        // actually carried when it was scored live — not a fabricated default.
        await WriteLegacyFileAsync("legacy-recover", $$"""
            {
              "evidenceId": "11111111-1111-1111-1111-111111111111",
              "sourceType": "filing",
              "sourceName": "Acme — SEC filings",
              "sourceUrl": null,
              "title": "8-K",
              "rawText": "Acme filed an 8-K.",
              "publishedAt": "2026-02-07T22:24:53+00:00",
              "collectedAt": "2026-07-06T21:35:31.3566397+00:00",
              "contentHash": "legacy-recover",
              "companyHints": [ "ACME" ],
              "metadata": { "quality": "{{declared}}", "form": "8-K" }
            }
            """);

        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByContentHashAsync("legacy-recover", CancellationToken.None);

        Assert.Equal(expected, read!.Quality);
    }

    [Fact]
    public async Task LegacyFile_WithNeitherQualityFieldNorMetadataQuality_IsUnknown()
    {
        // Unknown is exactly what CollectedEvidenceMapper itself produces for evidence that declared no
        // quality, and ScoringWeights.QualityUnknown (0.40) sits BELOW Medium (0.60) / High (0.85) /
        // PrimarySource (1.00) — so this cannot flatter a score. It is never mapped up to Medium.
        await WriteLegacyFileAsync("legacy-none", """
            {
              "evidenceId": "22222222-2222-2222-2222-222222222222",
              "sourceType": "news_article",
              "sourceName": "Example Wire",
              "title": "Acme in the news",
              "rawText": "Acme did a thing.",
              "collectedAt": "2026-07-06T21:35:31+00:00",
              "contentHash": "legacy-none",
              "companyHints": [],
              "metadata": {}
            }
            """);

        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByContentHashAsync("legacy-none", CancellationToken.None);

        Assert.Equal(EvidenceQuality.Unknown, read!.Quality);
    }

    [Fact]
    public async Task ExplicitQualityField_WinsOverMetadataQuality()
    {
        await WriteLegacyFileAsync("explicit-wins", """
            {
              "evidenceId": "33333333-3333-3333-3333-333333333333",
              "sourceType": "filing",
              "sourceName": "Acme — SEC filings",
              "title": "8-K",
              "rawText": "Acme filed an 8-K.",
              "collectedAt": "2026-07-06T21:35:31+00:00",
              "contentHash": "explicit-wins",
              "companyHints": [],
              "metadata": { "quality": "Low" },
              "quality": "PrimarySource"
            }
            """);

        IEvidenceRepository hydrated = CreateStore();
        var read = await hydrated.GetByContentHashAsync("explicit-wins", CancellationToken.None);

        Assert.Equal(EvidenceQuality.PrimarySource, read!.Quality);
    }

    // -------------------------------------------------------------------------------------------------
    // sourceType round-trip.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EverySourceType_RoundTripsThroughTheSnakeCasedFileField()
    {
        var store = CreateStore();
        var written = new List<EvidenceItem>();

        foreach (var sourceType in Enum.GetValues<EvidenceSourceType>())
        {
            var item = new EvidenceBuilder()
                .WithSourceType(sourceType)
                .WithContentHash("hash-" + sourceType)
                .WithPublishedAtUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
                .Build();

            Assert.True(await store.WriteIfNewAsync(item, CancellationToken.None));
            written.Add(item);
        }

        IEvidenceRepository hydrated = CreateStore();
        foreach (var item in written)
        {
            var read = await hydrated.GetByIdAsync(item.Id, CancellationToken.None);
            Assert.Equal(item.SourceType, read!.SourceType);
        }
    }

    [Fact]
    public async Task UnknownSourceType_DegradesTheFile_NotTheSourceType()
    {
        // SourceType feeds attention breadth/diversity in the v8 formula, so an unparseable value must
        // never silently become the wrong type — the FILE is skipped instead.
        await WriteLegacyFileAsync("bad-source-type", """
            {
              "evidenceId": "44444444-4444-4444-4444-444444444444",
              "sourceType": "carrier_pigeon",
              "sourceName": "Example",
              "title": "T",
              "rawText": "R",
              "collectedAt": "2026-07-06T21:35:31+00:00",
              "contentHash": "bad-source-type",
              "companyHints": [],
              "metadata": {}
            }
            """);

        IEvidenceRepository hydrated = CreateStore();
        Assert.Null(await hydrated.GetByContentHashAsync("bad-source-type", CancellationToken.None));
        Assert.Empty(await hydrated.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedFile_IsSkipped_AndTheRestStillHydrate()
    {
        var good = new EvidenceBuilder()
            .WithContentHash("good")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();
        Assert.True(await CreateStore().WriteIfNewAsync(good, CancellationToken.None));

        await WriteLegacyFileAsync("broken", "{ this is not json");

        IEvidenceRepository hydrated = CreateStore();
        var all = await hydrated.GetAllAsync(CancellationToken.None);
        Assert.Equal([good.Id], all.Select(e => e.Id).ToArray());
    }

    // -------------------------------------------------------------------------------------------------
    // Idempotent re-collection: "new" means new to the ACCRUED store, not merely to this process.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddIfNewAsync_FreshInstance_RejectsEvidenceStoredByAPreviousRun()
    {
        var first = new EvidenceBuilder()
            .WithContentHash("shared-hash")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();

        var run1 = CreateStore();
        Assert.True(await ((IEvidenceRepository)run1).AddIfNewAsync(first, CancellationToken.None));
        Assert.True(await run1.WriteIfNewAsync(first, CancellationToken.None));

        // Run 2 re-collects the SAME content; the mapper mints a fresh evidence Guid every run, so only
        // the ContentHash identifies it as already-seen.
        var second = new EvidenceBuilder()
            .WithContentHash("shared-hash")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();

        IEvidenceRepository run2 = CreateStore();
        Assert.False(await run2.AddIfNewAsync(second, CancellationToken.None));

        // Nothing was rewritten or duplicated on disk (AD-1/AD-8), and the ORIGINAL item survives.
        Assert.Single(Directory.EnumerateFiles(_tempDir, "*.json", SearchOption.AllDirectories));
        Assert.Equal(first.Id, (await run2.GetByContentHashAsync("shared-hash", CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task WriteThenRead_SameInstance_IsImmediatelyVisibleWithoutADiskReread()
    {
        var store = CreateStore();
        var item = new EvidenceBuilder()
            .WithContentHash("write-then-read")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            .Build();

        Assert.True(await store.WriteIfNewAsync(item, CancellationToken.None));

        IEvidenceRepository repo = store;
        Assert.Equal(item, await repo.GetByIdAsync(item.Id, CancellationToken.None));
        Assert.Equal(item, await repo.GetByContentHashAsync("write-then-read", CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateContentHash_ResolvesToTheOrdinalFirstPath_OnEveryHydration()
    {
        // Two files, same contentHash, different evidence ids — the store holds ~9x content-equivalent
        // duplication today (the mapper mints a fresh Guid per run). Hydration TryAdds, so the FIRST file
        // read wins; Directory.EnumerateFiles has no defined order, so without an ordinal sort the winning
        // item — and therefore the scored evidence set — could differ between runs and between OSes.
        // Written ordinal-LAST first, so creation order and ordinal order disagree: the test then fails
        // on a creation-ordered filesystem if the sort is removed outright, not merely if it is reversed.
        await WriteLegacyFileAsync("dupe", DuplicateJson("22222222-2222-2222-2222-222222222222"), "zzz");
        await WriteLegacyFileAsync("dupe", DuplicateJson("11111111-1111-1111-1111-111111111111"), "aaa");

        var expected = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // Repeated FRESH instances must agree — a single assertion could pass on enumeration luck.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            IEvidenceRepository hydrated = CreateStore();

            var winner = await hydrated.GetByContentHashAsync("dupe-hash", CancellationToken.None);
            Assert.Equal(expected, winner!.Id);

            // The loser is dropped entirely, not indexed under its own id.
            Assert.Null(await hydrated.GetByIdAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222"), CancellationToken.None));
            Assert.Single(await hydrated.GetAllAsync(CancellationToken.None));
        }
    }

    private static string DuplicateJson(string evidenceId) =>
        $$"""
        {
          "evidenceId": "{{evidenceId}}",
          "sourceType": "press_release",
          "sourceName": "Example",
          "title": "T",
          "rawText": "R",
          "collectedAt": "2026-07-06T21:35:31+00:00",
          "contentHash": "dupe-hash",
          "companyHints": [],
          "metadata": {}
        }
        """;

    [Fact]
    public async Task GetAllAsync_OverAnEmptyRoot_ReturnsEmpty_AndDoesNotThrow()
    {
        IEvidenceRepository repo = new FileRawEvidenceStore(
            new FileRawEvidenceStoreOptions { RootDirectory = Path.Combine(_tempDir, "does-not-exist") },
            NullLogger<FileRawEvidenceStore>.Instance);

        Assert.Empty(await repo.GetAllAsync(CancellationToken.None));
    }

    private async Task WriteLegacyFileAsync(string name, string json, string folder = "legacy")
    {
        var dir = Path.Combine(_tempDir, folder);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, name + ".json"), json);
    }
}
