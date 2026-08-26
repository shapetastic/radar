using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 191 — the durable signal record gained a TRAILING, NULLABLE <c>metadataJson</c> provenance envelope.
/// The two properties that make it safe: an already-written (or metadata-free) file is byte-unchanged
/// because the property is OMITTED when null, and an absent property hydrates as <c>null</c> — NOT RECORDED,
/// never a fabricated empty bag.
/// </summary>
public sealed class FileSignalStoreMetadataTests : IDisposable
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);

    private const string Envelope =
        """{"metadata":{"newsJudgmentId":"22222222-0000-0000-0000-000000000002","newsJudgmentCohortKey":"cohort","newsObservationId":"11111111-0000-0000-0000-000000000001"},"companyHints":[]}""";

    private readonly string _tempDir;

    public FileSignalStoreMetadataTests()
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

    private FileSignalStore CreateStore() => new(
        new FileSignalStoreOptions { RootDirectory = _tempDir },
        NullLogger<FileSignalStore>.Instance);

    private static SignalReview ReviewFor(Signal signal) => new(
        Id: Guid.NewGuid(),
        SignalId: signal.Id,
        ReviewerName: "DeterministicSignalReviewer",
        Decision: SignalReviewDecision.Approve,
        Summary: "Third-party news coverage.",
        IssuesJson: null,
        ReviewedAtUtc: new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ASignalCarryingMetadata_PersistsIt_AndHydratesItBack()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.MediaAttention)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(Observed)
            .WithMetadataJson(Envelope)
            .Build();

        var store = CreateStore();
        var path = (await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None)).Path;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(Envelope, doc.RootElement.GetProperty("metadataJson").GetString());

        // A FRESH store instance, so the value comes off disk rather than out of the writer's own index.
        var hydrated = await CreateStore().GetByIdAsync(signal.Id, CancellationToken.None);
        Assert.Equal(Envelope, hydrated!.MetadataJson);
    }

    [Fact]
    public async Task ASignalWithoutMetadata_OmitsThePropertyEntirely()
    {
        // The compatibility proof: a metadata-free signal serializes exactly as it did pre-191.
        var signal = new SignalBuilder()
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(Observed)
            .Build();

        var path = (await CreateStore()
            .WriteAsync(signal, ReviewFor(signal), CancellationToken.None)).Path;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.False(
            doc.RootElement.TryGetProperty("metadataJson", out _),
            "A metadata-free signal must not write the property at all.");
    }

    [Fact]
    public async Task ALegacyFileWithNoMetadataProperty_HydratesAsNotRecorded()
    {
        var signalId = Guid.NewGuid();
        var directory = Path.Combine(_tempDir, "2026", "08");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, signalId + ".json"),
            $$"""
            {
              "signalId": "{{signalId}}",
              "evidenceId": "{{Guid.Empty}}",
              "companyId": null,
              "companyMention": "Acme Corp",
              "type": "MediaAttention",
              "direction": "Neutral",
              "strength": 4,
              "novelty": 4,
              "confidence": 0.5,
              "supportingExcerpt": "Acme in the news",
              "reason": "Third-party news coverage (media attention)",
              "reviewStatus": "Approved",
              "observedAt": "2026-08-20T09:30:00+00:00",
              "createdAt": "2026-08-20T09:30:00+00:00",
              "review": {
                "reviewId": "{{Guid.NewGuid()}}",
                "signalId": "{{signalId}}",
                "reviewerName": "DeterministicSignalReviewer",
                "decision": "Approve",
                "summary": "ok",
                "issuesJson": null,
                "reviewedAt": "2026-08-21T08:00:00+00:00"
              }
            }
            """);

        var hydrated = await CreateStore().GetByIdAsync(signalId, CancellationToken.None);

        Assert.NotNull(hydrated);
        Assert.Null(hydrated.MetadataJson);
    }
}
