using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Acquisitions;
using Radar.Application.Storage;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Acquisitions;

/// <summary>Where the acquisitions store keeps its files.</summary>
public sealed class FileAcquisitionStoreOptions
{
    public string RootDirectory { get; init; } = Path.Combine("data", "acquisitions");
}

/// <summary>
/// SPEC 217 §1 — the on-disk acquisitions store: one JSON file per recognised (company, scan version,
/// accession) at <c>{RootDirectory}/{companyId:D}/{sanitizedScanVersion}/{sanitizedAccession}.json</c>, holding
/// that filing's <see cref="PendingAcquisitionRecord"/>.
/// <para>
/// <b>Version in the path (spec 227 §2).</b> Before spec 227 the path carried no version
/// (<c>{companyId:D}/{accession}.json</c>), so a recognition under a corrected rule would have collided with
/// the file the retired rule wrote. Those LEGACY files are never moved, edited or deleted; <see cref="GetAllAsync"/>
/// still reads them (it enumerates recursively, and each record carries its own <c>scanVersion</c>), and
/// <see cref="ExistsAsync"/> honours one only for the version its content names.
/// </para>
/// <para>
/// <b>Append-only and insert-if-new</b> (AD-8; the spec-216 ledger pattern). The write goes through the
/// shared <see cref="AtomicFileWriter"/>, so the rename IS the commit point and a half-written file can
/// never become the record. An existing file that READS BACK as a complete record is
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/>; an UNPARSEABLE file at the target path is
/// <see cref="DurableWriteOutcome.Failed"/> with reason <c>corrupt-existing</c> — never
/// <c>AlreadyOnDisk</c>, because reporting a fragment as the record is exactly how a closed thesis would
/// silently reopen. A disk failure never throws; only caller cancellation propagates.
/// </para>
/// <para>
/// Read side: <see cref="GetAllAsync"/> enumerates every company folder, deserializes every file, and skips
/// an unreadable one with a Warning while COUNTING it on the result — a caller that read fewer acquisitions
/// than exist can then say so instead of quietly leaving a thesis open.
/// </para>
/// <para>
/// Reuses <see cref="RadarFileStoreJson.Options"/> (the shared on-disk JSON shape), the shared
/// <see cref="FileTickerKey"/> filename-key sanitizer and <see cref="AtomicFileWriter"/> (reuse over copy);
/// all file I/O stays in Infrastructure (AD-5).
/// </para>
/// </summary>
public sealed class FileAcquisitionStore : IAcquisitionStore
{
    /// <summary>The named <see cref="DurableWriteOutcome.Failed"/> reason for an unparseable file at the target path.</summary>
    internal const string CorruptExistingReason = "corrupt-existing";

    private readonly FileAcquisitionStoreOptions _options;
    private readonly ILogger<FileAcquisitionStore> _logger;

    // One Warning per path per process for a corrupt file: the store is read on every run and every report,
    // and a permanently corrupt file would otherwise reprint its Warning forever.
    private readonly HashSet<string> _corruptReported = new(StringComparer.OrdinalIgnoreCase);

    public FileAcquisitionStore(FileAcquisitionStoreOptions options, ILogger<FileAcquisitionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<DurableWriteResult> WriteIfNewAsync(
        PendingAcquisitionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var companyDirectory = Path.Combine(_options.RootDirectory, record.CompanyId.ToString("D"));

        var fileName = FileTickerKey.Sanitize(record.Accession);
        var versionDirectory = SanitizeVersionDirectory(record.ScanVersion);
        if (fileName is null || versionDirectory is null)
        {
            _logger.LogWarning(
                "Acquisition accession '{Accession}' or scan version '{ScanVersion}' for company {CompanyId} is "
                    + "blank or contains invalid filename characters; the record cannot be written.",
                record.Accession,
                record.ScanVersion,
                record.CompanyId);
            return DurableWriteResult.NotPersisted(companyDirectory);
        }

        // SPEC 227 §2: the scan version is part of the durable path, so a record recognised under a new rule
        // never collides with (and never overwrites) the file an earlier rule wrote for the same filing.
        var path = Path.Combine(companyDirectory, versionDirectory, fileName + ".json");
        if (File.Exists(path))
        {
            return await ExistingFileOutcomeAsync(path, ct).ConfigureAwait(false);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(
                ex, "Failed to serialize the acquisition record for {Path}; nothing was written.", path);
            return DurableWriteResult.NotPersisted(path);
        }

        var outcome = await AtomicFileWriter.WriteNewAsync(path, json, _logger, ct).ConfigureAwait(false);
        return outcome switch
        {
            AtomicWriteOutcome.Committed => DurableWriteResult.Succeeded(path),
            AtomicWriteOutcome.AlreadyExists => await ExistingFileOutcomeAsync(path, ct).ConfigureAwait(false),
            _ => DurableWriteResult.NotPersisted(path),
        };
    }

    public async Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_options.RootDirectory))
        {
            if (File.Exists(_options.RootDirectory))
            {
                // A FILE where the store root belongs is a MISCONFIGURATION, not an empty store: nothing
                // was measured, so no consumer may state an absence from it. (Directory.Exists is false for
                // a file, which is why this case must be separated explicitly rather than falling into the
                // measured-empty branch below.)
                _logger.LogWarning(
                    "The acquisitions store root {Root} is a FILE, not a directory; the recognition state is "
                        + "UNKNOWN for this pass — no acquisition is applied AND no absence is asserted.",
                    _options.RootDirectory);
                return AcquisitionStoreReadResult.Unavailable;
            }

            // A MEASURED empty: the store is composed, the root simply holds nothing yet (no recognition
            // has ever been written). Consumers may honestly state "nothing is pending" from this.
            return AcquisitionStoreReadResult.Empty;
        }

        var records = new List<PendingAcquisitionRecord>();
        var unreadable = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_options.RootDirectory, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // NOT an empty read: the store could not be reached at all, so its emptiness was never
            // MEASURED. Returning Empty here would let the report print "no company is under a recognised
            // pending acquisition" over a store it could not open — a defaulted zero rendering as a
            // measured zero, which is the exact failure this slice exists to prevent.
            _logger.LogWarning(
                ex,
                "Could not enumerate the acquisitions store at {Root}; the recognition state is UNKNOWN for "
                    + "this pass — no acquisition is applied AND no absence is asserted.",
                _options.RootDirectory);
            return AcquisitionStoreReadResult.Unavailable;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var record = await TryReadAsync(file, ct).ConfigureAwait(false);
            if (record is null)
            {
                unreadable++;
                continue;
            }

            records.Add(record);
        }

        // Deterministic order (AD-3): company, then accession ordinal, then scan version ordinal — since spec 227
        // one (company, accession) can hold a record under more than one scan version.
        records.Sort(static (a, b) =>
        {
            var byCompany = a.CompanyId.CompareTo(b.CompanyId);
            if (byCompany != 0)
            {
                return byCompany;
            }

            var byAccession = string.CompareOrdinal(a.Accession, b.Accession);
            return byAccession != 0 ? byAccession : string.CompareOrdinal(a.ScanVersion, b.ScanVersion);
        });

        return new AcquisitionStoreReadResult(records, unreadable);
    }

    public async Task<bool> ExistsAsync(
        Guid companyId, string accession, string scanVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accession);
        ArgumentNullException.ThrowIfNull(scanVersion);

        var fileName = FileTickerKey.Sanitize(accession);
        var versionDirectory = SanitizeVersionDirectory(scanVersion);
        if (fileName is null || versionDirectory is null)
        {
            return false;
        }

        var companyDirectory = Path.Combine(_options.RootDirectory, companyId.ToString("D"));
        if (File.Exists(Path.Combine(companyDirectory, versionDirectory, fileName + ".json")))
        {
            return true;
        }

        // A LEGACY (pre-227, version-less path) file answers only for the scan version its CONTENT carries; for
        // any other version it is "not recognised under THIS rule" and the filing is rescanned. It is read, never
        // moved or edited (AD-8).
        var legacy = Path.Combine(companyDirectory, fileName + ".json");
        if (!File.Exists(legacy))
        {
            return false;
        }

        var record = await TryReadAsync(legacy, ct).ConfigureAwait(false);
        return record is not null
            && string.Equals(record.ScanVersion, scanVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scan version as a single directory segment, or null when it is blank, carries an invalid filename
    /// character, or is a relative-path segment ("." / "..") that would place the file outside its company
    /// folder.
    /// </summary>
    private static string? SanitizeVersionDirectory(string? scanVersion)
    {
        var sanitized = FileTickerKey.Sanitize(scanVersion);
        return sanitized is null or "." or ".." ? null : sanitized;
    }

    /// <summary>
    /// An existing file only counts as the record when it READS BACK as one (the spec-216 §4 rule): an
    /// unparseable file is <see cref="DurableWriteOutcome.Failed"/> with a named reason, never
    /// <c>AlreadyOnDisk</c>.
    /// </summary>
    private async Task<DurableWriteResult> ExistingFileOutcomeAsync(string path, CancellationToken ct)
    {
        if (await TryReadAsync(path, ct).ConfigureAwait(false) is not null)
        {
            return DurableWriteResult.AlreadyOnDisk(path);
        }

        _logger.LogWarning(
            "The acquisitions file at {Path} exists but does not parse as a record ({Reason}); it is NOT "
                + "reported as durable.",
            path,
            CorruptExistingReason);
        return DurableWriteResult.NotPersisted(path);
    }

    private async Task<PendingAcquisitionRecord?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer
                .DeserializeAsync<PendingAcquisitionRecord>(stream, RadarFileStoreJson.Options, ct)
                .ConfigureAwait(false);

            // A structurally-parseable but semantically empty record is not a record: an acquisition with no
            // accession or no acquirer would render a banner naming nobody.
            return record is null
                || string.IsNullOrWhiteSpace(record.Accession)
                || string.IsNullOrWhiteSpace(record.AcquirerName)
                    ? null
                    : record;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_corruptReported.Add(path))
            {
                _logger.LogWarning(
                    ex,
                    "Could not read the acquisitions file {Path}; it is skipped and COUNTED as unreadable, "
                        + "so a recognised acquisition may be missing from this pass.",
                    path);
            }

            return null;
        }
    }
}
