using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Signals;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record. Writes each
/// <see cref="Signal"/> (with its embedded <see cref="SignalReview"/>) to
/// <c>{RootDirectory}/{yyyy}/{MM}/{signalId}.json</c>, preserving provenance (evidence id, resolved
/// company id, and the embedded review whose <c>signalId</c> traces back to the signal). All file I/O
/// is confined to Infrastructure; the Application sees only <see cref="ISignalFileStore"/>. Disk
/// failures degrade gracefully (warn + return the attempted path) and never crash the run; the
/// in-memory repository copy still exists.
/// </summary>
/// <remarks>
/// <b>Overwrite-allowed (upsert-by-Id, last-write-wins).</b> This deliberately DIFFERS from the
/// insert-only <see cref="FileRawEvidenceStore"/>: AD-1 immutability governs <i>evidence only</i>.
/// Signals are upsert-by-Id, so an existing file for the same signal id is overwritten rather than
/// skipped. This is intentional — do not re-flag it as an AD-1 violation.
/// </remarks>
public sealed class FileSignalStore : ISignalFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Persist enums as their plain string names (e.g. "CustomerWin", "Approved", "Positive")
        // so the on-disk shape is human-readable and lossless, never integer ordinals.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileSignalStoreOptions _options;
    private readonly ILogger<FileSignalStore> _logger;

    public FileSignalStore(
        FileSignalStoreOptions options,
        ILogger<FileSignalStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(review);

        var observedUtc = signal.ObservedAtUtc.ToUniversalTime();
        var path = Path.Combine(
            _options.RootDirectory,
            observedUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            observedUtc.ToString("MM", CultureInfo.InvariantCulture),
            signal.Id + ".json");

        var json = Serialize(signal, review);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Overwrite-allowed (upsert-by-Id, last-write-wins): unlike the insert-only evidence store,
            // signals are NOT immutable (AD-1 covers evidence only), so we do not guard on File.Exists.
            await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);

            _logger.LogInformation("Wrote signal {SignalId} to {Path}.", signal.Id, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must not crash the run; the in-memory repository copy still exists.
            _logger.LogWarning(
                ex,
                "Failed to write signal {SignalId} to {Path}; skipping.",
                signal.Id,
                path);
            return path;
        }
    }

    private static string Serialize(Signal signal, SignalReview review)
    {
        var file = new SignalFile(
            SignalId: signal.Id,
            EvidenceId: signal.EvidenceId,
            CompanyId: signal.CompanyId,
            CompanyMention: signal.CompanyMention,
            Type: signal.Type,
            Direction: signal.Direction,
            Strength: signal.Strength,
            Novelty: signal.Novelty,
            Confidence: signal.Confidence,
            SupportingExcerpt: signal.SupportingExcerpt,
            Reason: signal.Reason,
            ReviewStatus: signal.ReviewStatus,
            ObservedAt: signal.ObservedAtUtc,
            CreatedAt: signal.CreatedAtUtc,
            Review: new SignalReviewFile(
                ReviewId: review.Id,
                SignalId: review.SignalId,
                ReviewerName: review.ReviewerName,
                Decision: review.Decision,
                Summary: review.Summary,
                IssuesJson: review.IssuesJson,
                ReviewedAt: review.ReviewedAtUtc));

        return JsonSerializer.Serialize(file, SerializerOptions);
    }

    /// <summary>
    /// The persisted signal shape. Property names render camelCase via the serializer options
    /// (<c>signalId</c>, <c>evidenceId</c>, …); enums render as their string names. Carries the
    /// provenance fields (<c>evidenceId</c>, nullable <c>companyId</c>) and the embedded review.
    /// </summary>
    private sealed record SignalFile(
        Guid SignalId,
        Guid EvidenceId,
        Guid? CompanyId,
        string CompanyMention,
        SignalType Type,
        SignalDirection Direction,
        int Strength,
        int Novelty,
        decimal Confidence,
        string SupportingExcerpt,
        string Reason,
        SignalReviewStatus ReviewStatus,
        DateTimeOffset ObservedAt,
        DateTimeOffset CreatedAt,
        SignalReviewFile Review);

    /// <summary>
    /// The embedded review shape. Its <c>signalId</c> traces back to the parent signal (provenance).
    /// </summary>
    private sealed record SignalReviewFile(
        Guid ReviewId,
        Guid SignalId,
        string ReviewerName,
        SignalReviewDecision Decision,
        string Summary,
        string? IssuesJson,
        DateTimeOffset ReviewedAt);
}
