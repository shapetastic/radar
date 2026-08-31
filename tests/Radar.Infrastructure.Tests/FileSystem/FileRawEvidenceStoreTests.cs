using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileRawEvidenceStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileRawEvidenceStoreTests()
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

    private FileRawEvidenceStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileRawEvidenceStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileRawEvidenceStore>.Instance);

    [Fact]
    public async Task WriteIfNewAsync_NewEvidence_WritesFileAtExpectedPathAndRoundTrips()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithSourceName("Rocket Lab Investor News")
            .WithSourceUrl("https://example.com/rklb")
            .WithTitle("Rocket Lab Announces New Multi-Launch Agreement")
            .WithRawText("Rocket Lab signed a multi-launch agreement.")
            .WithContentHash("6AF3E9")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.Zero))
            .WithCollectedAtUtc(new DateTimeOffset(2026, 1, 10, 10, 15, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{"k":"v"},"companyHints":["RKLB"]}""")
            .Build();

        var store = CreateStore();
        var wrote = await store.WriteIfNewAsync(evidence, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, wrote.Outcome);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2026", "01", "6AF3E9.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        await using var stream = File.OpenRead(expectedPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(evidence.Id.ToString(), root.GetProperty("evidenceId").GetString());
        Assert.Equal("press_release", root.GetProperty("sourceType").GetString());
        Assert.Equal("Rocket Lab Investor News", root.GetProperty("sourceName").GetString());
        Assert.Equal("https://example.com/rklb", root.GetProperty("sourceUrl").GetString());
        Assert.Equal("Rocket Lab Announces New Multi-Launch Agreement", root.GetProperty("title").GetString());
        Assert.Equal("Rocket Lab signed a multi-launch agreement.", root.GetProperty("rawText").GetString());
        Assert.Equal("6AF3E9", root.GetProperty("contentHash").GetString());

        var hints = root.GetProperty("companyHints").EnumerateArray().Select(h => h.GetString()!).ToArray();
        Assert.Equal(["RKLB"], hints);
        Assert.Equal("v", root.GetProperty("metadata").GetProperty("k").GetString());

        // normalizedText is intentionally absent.
        Assert.False(root.TryGetProperty("normalizedText", out _));
    }

    [Fact]
    public async Task WriteIfNewAsync_CalledTwice_IsInsertOnlyAndLeavesFileUnchanged()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("HASH123")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var store = CreateStore();

        var first = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, first.Outcome);

        var path = Path.Combine(_tempDir, "press-releases", "2026", "03", "HASH123.json");
        var bytesBefore = await File.ReadAllBytesAsync(path);

        // Spec 206 §3: the repeat is the durable dedupe — AlreadyAvailable, with Written still true (the
        // record IS on disk; it just was not produced by this call).
        var second = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, second.Outcome);
        Assert.True(second.Written);

        var bytesAfter = await File.ReadAllBytesAsync(path);
        Assert.Equal(bytesBefore, bytesAfter);
    }

    /// <summary>
    /// Spec 206 §3: a FRESH store instance over an accrued root reports the re-collected item as
    /// AlreadyAvailable (the hydrated index holds it) — the cross-run dedupe that used to be
    /// AddIfNewAsync's job now lives on the typed write outcome.
    /// </summary>
    [Fact]
    public async Task WriteIfNewAsync_AccruedEvidence_InAFreshProcess_ReportsAlreadyAvailable()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("ACCRUED1")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var firstProcess = CreateStore();
        Assert.Equal(
            DurableWriteOutcome.Written,
            (await firstProcess.WriteIfNewAsync(evidence, CancellationToken.None)).Outcome);

        var secondProcess = CreateStore();
        var result = await secondProcess.WriteIfNewAsync(evidence, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, result.Outcome);
        Assert.True(result.Written);
    }

    /// <summary>
    /// Spec 206 §3 — the insert-race surrogate: another writer creates the immutable final path AFTER this
    /// instance hydrated. The store reads the file back, confirms it is the same valid evidence, and
    /// reports AlreadyAvailable rather than Failed or a false Written.
    /// </summary>
    [Fact]
    public async Task WriteIfNewAsync_FileCreatedByAnotherWriterAfterHydration_ReportsAlreadyAvailable()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("RACED1")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var store = CreateStore();
        // Force hydration over the (empty) root first, so the index cannot already hold the item.
        Assert.Empty(await store.GetAllAsync(CancellationToken.None));

        // The "other writer": a second store instance over the same root creates the final path.
        Assert.Equal(
            DurableWriteOutcome.Written,
            (await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None)).Outcome);

        var result = await store.WriteIfNewAsync(evidence, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, result.Outcome);
        // …and the resolved on-disk record is now readable through this instance's repository side.
        Assert.NotNull(await store.GetByContentHashAsync("RACED1", CancellationToken.None));
    }

    /// <summary>
    /// Spec 206 §3: an existing final path that cannot be resolved as the SAME VALID evidence is Failed,
    /// never AlreadyAvailable — the bytes there are not a trustworthy durable record of this evidence, and
    /// the insert-only rule forbids replacing them.
    /// </summary>
    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("""{"evidenceId":"00000000-0000-0000-0000-000000000001","sourceType":"press_release","sourceName":"S","sourceUrl":null,"title":"T","rawText":"R","publishedAt":"2026-03-05T00:00:00+00:00","collectedAt":"2026-03-05T00:00:00+00:00","contentHash":"DIFFERENT","companyHints":[],"metadata":{}}""")]
    public async Task WriteIfNewAsync_MalformedOrConflictingExistingPath_ReportsFailed(string existingContent)
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("CONFLICT1")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var path = Path.Combine(_tempDir, "press-releases", "2026", "03", "CONFLICT1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, existingContent);

        var result = await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(result.Written);
        // The existing bytes are never overwritten (insert-only, AD-1).
        Assert.Equal(existingContent, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfNewAsync_Cancellation_Propagates()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("CANCELLED")
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateStore().WriteIfNewAsync(evidence, cts.Token));
    }

    [Fact]
    public async Task WriteIfNewAsync_DerivesYearMonthFromPublishedAt_WhenPresent()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("PUB")
            .WithPublishedAtUtc(new DateTimeOffset(2025, 11, 20, 9, 0, 0, TimeSpan.Zero))
            .WithCollectedAtUtc(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2025", "11", "PUB.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");
    }

    [Fact]
    public async Task WriteIfNewAsync_DerivesYearMonthFromCollectedAt_WhenPublishedAtAbsent()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("COL")
            .WithPublishedAtUtc(null)
            .WithCollectedAtUtc(new DateTimeOffset(2024, 7, 9, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2024", "07", "COL.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");
    }

    [Fact]
    public async Task WriteIfNewAsync_IoFailure_ReportsFailedWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("IOFAIL")
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var store = CreateStore(rootAsFile);

        var wrote = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, wrote.Outcome);
        Assert.False(wrote.Written);
        Assert.False(File.Exists(wrote.Path));

        // Spec 206 §3: a Failed item is indexed NOWHERE, so the SAME instance retries — and succeeds once
        // the disk recovers (the blocking file is removed).
        File.Delete(rootAsFile);
        var retried = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, retried.Outcome);
        Assert.True(File.Exists(retried.Path));
    }
}
