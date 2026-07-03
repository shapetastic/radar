using System.Globalization;
using System.Text.Json;

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

        // Provenance guard: the embedded review must belong to this signal. Persisting a mismatched
        // pair would write an internally inconsistent file and silently break the review→signal trace.
        if (review.SignalId != signal.Id)
        {
            throw new ArgumentException(
                $"Review {review.Id} targets signal {review.SignalId}, not signal {signal.Id}; refusing to persist a mismatched pair.",
                nameof(review));
        }

        var observedUtc = signal.ObservedAtUtc.ToUniversalTime();
        var path = Path.Combine(
            _options.RootDirectory,
            observedUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            observedUtc.ToString("MM", CultureInfo.InvariantCulture),
            signal.Id + ".json");

        var json = Serialize(signal, review);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote signal {SignalId} to {Path}.", signal.Id, path);
        }

        return path;
    }

    public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct)
    {
        // WriteAsync stores each signal date-partitioned at {RootDirectory}/{yyyy}/{MM}/{signalId}.json
        // (by ObservedAtUtc), NOT grouped by company — so there is no per-company directory to open.
        // Enumerate the whole tree recursively and filter by the persisted CompanyId instead.
        if (!Directory.Exists(_options.RootDirectory))
        {
            return Array.Empty<Signal>();
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(_options.RootDirectory, "*.json", SearchOption.AllDirectories)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to enumerate signal files in '{RootDirectory}'; returning no previous-window signals.",
                _options.RootDirectory);
            return Array.Empty<Signal>();
        }

        var matches = new List<Signal>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<SignalFile>(text, RadarFileStoreJson.Options);
                if (parsed is null)
                {
                    // A JSON literal `null` deserializes to a null record — treat it as a malformed
                    // entry so operators can spot corrupted signal files.
                    _logger.LogWarning("Signal file '{File}' contained a null signal; skipping.", file);
                    continue;
                }

                // Approved-only + in the (startExclusive, endInclusive] window for this company. The shared
                // boundary (AD-6): a signal exactly at endInclusiveUtc (== the current window's start)
                // belongs to THIS previous window and is never double-counted against the current window.
                if (parsed.CompanyId != companyId
                    || parsed.ReviewStatus != SignalReviewStatus.Approved
                    || parsed.ObservedAt <= startExclusiveUtc
                    || parsed.ObservedAt > endInclusiveUtc)
                {
                    continue;
                }

                // Reconstruct the full Signal from the persisted fields. Evidence / ScoreEvidenceLinks are
                // intentionally NOT rehydrated: this is the activity-only previous window for velocity
                // (Strength magnitude), NOT dropped provenance — AD-6 says it carries none by design.
                matches.Add(new Signal(
                    Id: parsed.SignalId,
                    EvidenceId: parsed.EvidenceId,
                    CompanyId: parsed.CompanyId,
                    CompanyMention: parsed.CompanyMention,
                    Type: parsed.Type,
                    Direction: parsed.Direction,
                    Strength: parsed.Strength,
                    Novelty: parsed.Novelty,
                    Confidence: parsed.Confidence,
                    SupportingExcerpt: parsed.SupportingExcerpt,
                    Reason: parsed.Reason,
                    ReviewStatus: parsed.ReviewStatus,
                    ObservedAtUtc: parsed.ObservedAt,
                    CreatedAtUtc: parsed.CreatedAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // One unreadable/malformed signal file must not break the whole read.
                _logger.LogWarning(ex, "Failed to read signal file '{File}'; skipping.", file);
            }
        }

        // Deterministic order (AD-3): ObservedAtUtc then Id.
        return matches
            .OrderBy(s => s.ObservedAtUtc)
            .ThenBy(s => s.Id)
            .ToList();
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

        return JsonSerializer.Serialize(file, RadarFileStoreJson.Options);
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
