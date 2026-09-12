using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// SPEC 216 §2 — the on-disk reported-metrics OUTBOX, laid out under the ledger root so the payload and
/// the records it produces live together:
/// <list type="bullet">
/// <item><c>{root}/outbox/pending/{accession}.{policy}.json</c> — enqueued, company RESOLVED, ledger write
/// not yet acknowledged;</item>
/// <item><c>{root}/outbox/unresolved/{accession}.{policy}.json</c> — enqueued with no resolved company; the
/// collection pass re-runs it through company resolution on every replay and re-enqueues it under the
/// company once resolution succeeds;</item>
/// <item><c>{root}/outbox/acknowledged/{resolved|unresolved}.{accession}.{policy}.json</c> — kept forever
/// (append-only, never deleted) so the ledger's provenance chain stays walkable. The area prefix keeps the
/// unresolved copy's acknowledgement from colliding with the resolved copy's.</item>
/// </list>
/// Every write goes through the shared <see cref="AtomicFileWriter"/> (temp file + rename), so an envelope
/// is never observable half-written; every method degrades gracefully — a disk failure is a typed/false
/// outcome and never a throw. All file I/O stays in Infrastructure (AD-5).
/// </summary>
public sealed class FileReportedMetricOutbox : IReportedMetricOutbox
{
    private const string PendingArea = "pending";
    private const string UnresolvedArea = "unresolved";
    private const string AcknowledgedArea = "acknowledged";

    private readonly FileReportedMetricStoreOptions _options;
    private readonly ILogger<FileReportedMetricOutbox> _logger;

    public FileReportedMetricOutbox(
        FileReportedMetricStoreOptions options, ILogger<FileReportedMetricOutbox> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    // The ONE name for the outbox subtree (reuse over copy): the ledger inventory (spec 223 §2) excludes
    // exactly this directory, so the two cannot drift.
    private string OutboxRoot => Path.Combine(_options.RootDirectory, FileReportedMetricStore.OutboxSubdirectoryName);

    public async Task<DurableWriteResult> EnqueueAsync(
        ReportedMetricOutboxEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var fileName = EnvelopeFileName(envelope.Accession, envelope.Policy);
        if (fileName is null)
        {
            _logger.LogWarning(
                "Reported-metrics outbox envelope for accession '{Accession}' / policy '{Policy}' cannot name "
                    + "a file; the envelope was NOT persisted and the analyzed-filing cache is left unstamped "
                    + "so the filing re-analyzes.",
                envelope.Accession,
                envelope.Policy);
            return DurableWriteResult.NotPersisted(OutboxRoot);
        }

        var area = envelope.CompanyId is null ? UnresolvedArea : PendingArea;
        var path = Path.Combine(OutboxRoot, area, fileName);

        // An envelope already accounted for IN THIS AREA — pending/unresolved or its own acknowledged
        // counterpart — is durable either way, which is exactly what the caller's "may I stamp the cache
        // now?" question asks. The check is area-SCOPED on purpose: when an unresolved envelope finally
        // resolves, the pass re-enqueues it under the company, and a whole-outbox check would refuse that
        // write because the unresolved copy it is replacing still exists.
        if (ExistingEnvelopePath(envelope.Accession, envelope.Policy, area) is { } existing)
        {
            return DurableWriteResult.AlreadyOnDisk(existing);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(envelope, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(
                ex, "Failed to serialize the reported-metrics outbox envelope for {Path}.", path);
            return DurableWriteResult.NotPersisted(path);
        }

        var written = await AtomicFileWriter.WriteNewAsync(path, json, _logger, ct).ConfigureAwait(false);
        return written switch
        {
            AtomicWriteOutcome.Committed => DurableWriteResult.Succeeded(path),
            AtomicWriteOutcome.AlreadyExists => DurableWriteResult.AlreadyOnDisk(path),
            _ => DurableWriteResult.NotPersisted(path),
        };
    }

    public async Task<IReadOnlyList<ReportedMetricOutboxEnvelope>> EnumeratePendingAsync(CancellationToken ct)
    {
        var envelopes = new List<ReportedMetricOutboxEnvelope>();
        var unreadable = 0;
        var alreadyAcknowledged = 0;

        foreach (var area in new[] { UnresolvedArea, PendingArea })
        {
            var directory = Path.Combine(OutboxRoot, area);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            List<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex, "Failed to enumerate the reported-metrics outbox under '{Directory}'.", directory);
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var envelope = await TryReadAsync(file, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    unreadable++;
                    continue;
                }

                // A file that says it is ACKNOWLEDGED but still sits in a pending area is a half-completed
                // move (the acknowledged copy was written and the source could not be deleted). It is NOT
                // replayed — the ledger already has it — and it is COUNTED rather than skipped in silence.
                if (envelope.State == ReportedMetricOutboxState.Pending)
                {
                    envelopes.Add(envelope);
                }
                else
                {
                    alreadyAcknowledged++;
                }
            }
        }

        if (unreadable > 0 || alreadyAcknowledged > 0)
        {
            // ONE aggregated Warning per pass (the spec-145 precedent), never one line per file.
            _logger.LogWarning(
                "Reported-metrics outbox: {Unreadable} envelope file(s) could not be read this pass (each "
                    + "named above) and were NOT replayed — they stay on disk for a later run; "
                    + "{AlreadyAcknowledged} file(s) in a pending area were already ACKNOWLEDGED (a "
                    + "half-completed move) and were correctly not replayed.",
                unreadable,
                alreadyAcknowledged);
        }

        // Deterministic order (AD-3): company id, then accession. An unresolved envelope has no company, so
        // it sorts first under the empty key — it is also the work most in need of another resolution try.
        return
        [
            .. envelopes
                .OrderBy(e => e.CompanyId?.ToString("D") ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.Accession, StringComparer.Ordinal)
                .ThenBy(e => e.Policy, StringComparer.Ordinal),
        ];
    }

    public async Task<ReportedMetricOutboxEnvelope?> MarkAttemptAsync(
        ReportedMetricOutboxEnvelope envelope, DateTimeOffset attemptedAtUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var updated = envelope with
        {
            Attempts = envelope.Attempts + 1,
            LastAttemptAtUtc = attemptedAtUtc,
        };

        var path = PathFor(updated, updated.CompanyId is null ? UnresolvedArea : PendingArea);
        if (path is null)
        {
            return null;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(updated, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to serialize the outbox attempt update for {Path}.", path);
            return null;
        }

        // Replace, not insert: the envelope is already durable and this only advances its attempt count.
        // Still atomic, so a crash mid-update cannot leave an unreadable envelope that would then be
        // skipped forever.
        var written = await AtomicFileWriter.ReplaceAsync(path, json, _logger, ct).ConfigureAwait(false);
        return written == AtomicWriteOutcome.Committed ? updated : null;
    }

    public async Task<bool> AcknowledgeAsync(ReportedMetricOutboxEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var area = envelope.CompanyId is null ? UnresolvedArea : PendingArea;
        var source = PathFor(envelope, area);
        var destination = AcknowledgedPathFor(envelope.Accession, envelope.Policy, area);
        if (source is null || destination is null)
        {
            return false;
        }

        // The acknowledged copy carries the acknowledged STATE, so a half-completed move (destination
        // written, source not yet deleted) still reads correctly: EnumeratePendingAsync replays PENDING
        // envelopes only.
        var acknowledged = envelope with { State = ReportedMetricOutboxState.Acknowledged };
        string json;
        try
        {
            json = JsonSerializer.Serialize(acknowledged, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to serialize the acknowledged outbox envelope for {Path}.", destination);
            return false;
        }

        var written = await AtomicFileWriter
            .ReplaceAsync(destination, json, _logger, ct).ConfigureAwait(false);
        if (written != AtomicWriteOutcome.Committed)
        {
            _logger.LogWarning(
                "Failed to acknowledge the reported-metrics outbox envelope for accession {Accession} "
                    + "(policy {Policy}); it stays PENDING and the next pass replays it (the ledger write is "
                    + "insert-if-new, so a replay of an already-written record is a durable no-op).",
                envelope.Accession,
                envelope.Policy);
            return false;
        }

        try
        {
            if (File.Exists(source))
            {
                File.Delete(source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The acknowledged copy IS durable, so the work is done; the leftover pending file is named
            // rather than swallowed, and because the acknowledged copy exists the enqueue guard treats the
            // accession as accounted for.
            _logger.LogWarning(
                ex,
                "Acknowledged the reported-metrics outbox envelope for accession {Accession} but could not "
                    + "remove the pending copy at '{Path}'.",
                envelope.Accession,
                source);
        }

        return true;
    }

    public Task<bool> ExistsAsync(string policy, string accession, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(accession);
        ct.ThrowIfCancellationRequested();

        var accountedFor = ExistingEnvelopePath(accession, policy, PendingArea) is not null
            || ExistingEnvelopePath(accession, policy, UnresolvedArea) is not null;
        return Task.FromResult(accountedFor);
    }

    /// <summary>The envelope file name for one (accession, policy) pair; null when it cannot name a file.</summary>
    private static string? EnvelopeFileName(string accession, string policy)
    {
        var sanitizedAccession = FileTickerKey.Sanitize(accession);
        var sanitizedPolicy = FileTickerKey.Sanitize(policy);
        return sanitizedAccession is null || sanitizedPolicy is null
            ? null
            : $"{sanitizedAccession}.{sanitizedPolicy}.json";
    }

    private string? PathFor(ReportedMetricOutboxEnvelope envelope, string area)
    {
        var fileName = EnvelopeFileName(envelope.Accession, envelope.Policy);
        return fileName is null ? null : Path.Combine(OutboxRoot, area, fileName);
    }

    /// <summary>
    /// Where an acknowledged envelope from <paramref name="area"/> is kept. The area prefix keeps an
    /// unresolved copy's acknowledgement from colliding with the resolved copy's for the same accession.
    /// </summary>
    private string? AcknowledgedPathFor(string accession, string policy, string area)
    {
        var fileName = EnvelopeFileName(accession, policy);
        return fileName is null
            ? null
            : Path.Combine(
                OutboxRoot,
                AcknowledgedArea,
                $"{(area == UnresolvedArea ? UnresolvedArea : "resolved")}.{fileName}");
    }

    /// <summary>
    /// The path of an envelope already accounted for under (accession, policy) IN <paramref name="area"/> —
    /// either still sitting there or already acknowledged out of it — or null when none exists.
    /// </summary>
    private string? ExistingEnvelopePath(string accession, string policy, string area)
    {
        var fileName = EnvelopeFileName(accession, policy);
        if (fileName is null)
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(OutboxRoot, area, fileName),
            AcknowledgedPathFor(accession, policy, area)!,
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<ReportedMetricOutboxEnvelope?> TryReadAsync(string file, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ReportedMetricOutboxEnvelope>(
                text, RadarFileStoreJson.Options);
            if (parsed is not null)
            {
                return parsed;
            }

            _logger.LogWarning("Reported-metrics outbox envelope '{File}' deserialized to null.", file);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to read the reported-metrics outbox envelope '{File}'.", file);
            return null;
        }
    }
}
