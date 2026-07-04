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
        // (by ObservedAtUtc), NOT grouped by company. Rather than scan the whole tree on every
        // per-company read (scoring calls this once per company, so a full-tree scan would be
        // O(companies × totalSignalFiles) and degrade as the store grows), open only the year/month
        // directories the requested window can touch and filter those files by the persisted CompanyId.
        // Files are streamed rather than materialised into a list so cancellation stays responsive.
        if (!Directory.Exists(_options.RootDirectory))
        {
            return Array.Empty<Signal>();
        }

        var matches = new List<Signal>();
        foreach (var monthDirectory in EnumerateWindowMonthDirectories(startExclusiveUtc, endInclusiveUtc))
        {
            if (!Directory.Exists(monthDirectory))
            {
                continue;
            }

            try
            {
                // Files live directly under {yyyy}/{MM}/, so a top-directory enumeration suffices.
                foreach (var file in Directory.EnumerateFiles(monthDirectory, "*.json", SearchOption.TopDirectoryOnly))
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

                        // Approved-only + in the (startExclusive, endInclusive] window for this company. The
                        // shared boundary (AD-6): a signal exactly at endInclusiveUtc (== the current window's
                        // start) belongs to THIS previous window and is never double-counted against the
                        // current window.
                        if (parsed.CompanyId != companyId
                            || parsed.ReviewStatus != SignalReviewStatus.Approved
                            || parsed.ObservedAt <= startExclusiveUtc
                            || parsed.ObservedAt > endInclusiveUtc)
                        {
                            continue;
                        }

                        // Reconstruct the full Signal from the persisted fields. Evidence / ScoreEvidenceLinks
                        // are intentionally NOT rehydrated: this is the activity-only previous window for
                        // velocity (Strength magnitude), NOT dropped provenance — AD-6 says it carries none.
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Enumeration of one month directory failed (thrown lazily during iteration); skip that
                // month rather than abandoning the whole read. OperationCanceledException is not caught
                // here, so cancellation still propagates.
                _logger.LogWarning(
                    ex,
                    "Failed to enumerate signal files in '{MonthDirectory}'; skipping that month.",
                    monthDirectory);
            }
        }

        // Collapse cross-run duplicate signals before ordering (spec 85). The same underlying signal is
        // re-minted with a fresh SignalId (and CreatedAt) on every pipeline run — WriteAsync path-keys on
        // signal.Id, so N runs leave N files for ONE signal — inflating this activity-only previous window
        // and making SignalVelocityScore depend on how many times the pipeline has run (an AD-3 violation).
        // The stable identity of a signal is (CompanyId, EvidenceId, Type, Direction):
        //  - EvidenceId + Type + Direction distinguishes the genuinely-DISTINCT signals ONE evidence item
        //    can legitimately produce (e.g. a CustomerWin AND a GuidanceChange, or a Positive vs a Neutral),
        //    so the key never collapses distinct signals into one.
        //  - CompanyId is already fixed per read (filtered above), but is kept in the key so it stays
        //    self-describing and correct even if this read were ever called differently.
        //  - ObservedAt is intentionally EXCLUDED: it is derived from the same evidence and is therefore
        //    constant across a signal's cross-run copies, so it adds nothing; including it would risk NOT
        //    collapsing copies if a future change perturbed ObservedAt derivation. Strength, Confidence,
        //    Novelty, SupportingExcerpt, Reason are likewise evidence/extractor-derived and identical across
        //    copies — excluded too (a legitimately re-scored signal is out of scope for this activity-only
        //    velocity dedup).
        // Deterministic tie-break (AD-3): keep the copy with the LOWEST SignalId — a total, stable order over
        // Guid, independent of filesystem enumeration order. All copies carry identical activity fields
        // (Strength), so the choice cannot change the velocity result; lowest SignalId is simply the simplest
        // reproducible total order. Grouping is order-independent, so the survivor is the same every read.
        var deduped = matches
            .GroupBy(s => (s.CompanyId, s.EvidenceId, s.Type, s.Direction))
            .Select(group => group.OrderBy(s => s.Id).First());

        // Deterministic order (AD-3): ObservedAtUtc then Id.
        return deduped
            .OrderBy(s => s.ObservedAtUtc)
            .ThenBy(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// Yields the <c>{RootDirectory}/{yyyy}/{MM}</c> partition directories that the
    /// <c>(startExclusiveUtc, endInclusiveUtc]</c> window can contain signals for — every month from the
    /// start bound's month through the end bound's month, inclusive. The start bound is exclusive, but a
    /// signal later in that same month is still in-window, so its month is still scanned. Bounding the
    /// scan to the window (typically one or two months) keeps each per-company read from touching files
    /// that cannot possibly match.
    /// </summary>
    private IEnumerable<string> EnumerateWindowMonthDirectories(
        DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc)
    {
        // Partition names come from the persisted ObservedAtUtc, so compare in UTC.
        var startUtc = startExclusiveUtc.ToUniversalTime();
        var endUtc = endInclusiveUtc.ToUniversalTime();
        if (endUtc < startUtc)
        {
            yield break;
        }

        var cursor = new DateTime(startUtc.Year, startUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(endUtc.Year, endUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= last)
        {
            yield return Path.Combine(
                _options.RootDirectory,
                cursor.ToString("yyyy", CultureInfo.InvariantCulture),
                cursor.ToString("MM", CultureInfo.InvariantCulture));
            cursor = cursor.AddMonths(1);
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
