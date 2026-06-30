using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileSignalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Observed = new(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FileSignalStoreTests()
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

    private FileSignalStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileSignalStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileSignalStore>.Instance);

    private static SignalReview ReviewFor(
        Signal signal,
        SignalReviewDecision decision = SignalReviewDecision.Approve,
        string summary = "Material customer win, well evidenced.") =>
        new(
            Id: Guid.NewGuid(),
            SignalId: signal.Id,
            ReviewerName: "DeterministicSignalReviewer",
            Decision: decision,
            Summary: summary,
            IssuesJson: null,
            ReviewedAtUtc: new DateTimeOffset(2026, 2, 7, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task WriteAsync_NewSignal_WritesFileAtExpectedPathAndRoundTrips()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithCompanyMention("Northwind Robotics")
            .WithSupportingExcerpt("major new customer win")
            .WithObservedAtUtc(Observed)
            .Build();
        var review = ReviewFor(signal);

        var store = CreateStore();
        var path = await store.WriteAsync(signal, review, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "2026", "02", signal.Id + ".json");
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        await using var stream = File.OpenRead(expectedPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(signal.Id.ToString(), root.GetProperty("signalId").GetString());
        Assert.Equal(signal.EvidenceId.ToString(), root.GetProperty("evidenceId").GetString());
        Assert.Equal(signal.CompanyId!.Value.ToString(), root.GetProperty("companyId").GetString());
        Assert.Equal("Northwind Robotics", root.GetProperty("companyMention").GetString());
        Assert.Equal("major new customer win", root.GetProperty("supportingExcerpt").GetString());
        Assert.Equal(signal.Reason, root.GetProperty("reason").GetString());

        // Embedded review traces back to the signal (provenance) and carries the decision/summary.
        var reviewElement = root.GetProperty("review");
        Assert.Equal(review.Id.ToString(), reviewElement.GetProperty("reviewId").GetString());
        Assert.Equal(signal.Id.ToString(), reviewElement.GetProperty("signalId").GetString());
        Assert.Equal("DeterministicSignalReviewer", reviewElement.GetProperty("reviewerName").GetString());
        Assert.Equal("Approve", reviewElement.GetProperty("decision").GetString());
        Assert.Equal(review.Summary, reviewElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task WriteAsync_CalledTwiceForSameSignalId_IsOverwriteAllowedLastWriteWins()
    {
        var id = Guid.NewGuid();
        var first = new SignalBuilder()
            .WithId(id)
            .WithReviewStatus(SignalReviewStatus.Pending)
            .WithObservedAtUtc(Observed)
            .Build();
        var second = first with { ReviewStatus = SignalReviewStatus.Approved };

        var store = CreateStore();
        await store.WriteAsync(first, ReviewFor(first, SignalReviewDecision.EscalateToHuman), CancellationToken.None);
        await store.WriteAsync(second, ReviewFor(second, SignalReviewDecision.Approve), CancellationToken.None);

        // Exactly one file for the signal id — proves it is NOT insert-only.
        var dir = Path.Combine(_tempDir, "2026", "02");
        var files = Directory.GetFiles(dir);
        Assert.Single(files);

        await using var stream = File.OpenRead(Path.Combine(dir, id + ".json"));
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        // Contents reflect the second write (last-write-wins).
        Assert.Equal("Approved", root.GetProperty("reviewStatus").GetString());
        Assert.Equal("Approve", root.GetProperty("review").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WriteAsync_PersistsEnumsAsReadableStringNames()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(Observed)
            .Build();
        var review = ReviewFor(signal, SignalReviewDecision.Approve);

        var path = await CreateStore().WriteAsync(signal, review, CancellationToken.None);

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("type").ValueKind);
        Assert.Equal("CustomerWin", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("direction").ValueKind);
        Assert.Equal("Positive", root.GetProperty("direction").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("reviewStatus").ValueKind);
        Assert.Equal("Approved", root.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("review").GetProperty("decision").ValueKind);
        Assert.Equal("Approve", root.GetProperty("review").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var signal = new SignalBuilder().WithObservedAtUtc(Observed).Build();
        var review = ReviewFor(signal);

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(signal, review, CancellationToken.None);

        // The attempted path is returned (no throw); the in-memory copy still exists in production.
        var expectedPath = Path.Combine(rootAsFile, "2026", "02", signal.Id + ".json");
        Assert.Equal(expectedPath, path);
    }
}
