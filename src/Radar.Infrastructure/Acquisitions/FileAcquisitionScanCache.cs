using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Acquisitions;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Acquisitions;

/// <summary>Where the acqscan answer cache keeps its files.</summary>
public sealed class FileAcquisitionScanCacheOptions
{
    public string RootDirectory { get; init; } = Path.Combine("data", "acquisitions-cache");
}

/// <summary>
/// SPEC 217 §1 — the on-disk <c>acqscan-v1</c> answer cache: one JSON file per accession at
/// <c>{RootDirectory}/{sanitizedAccession}.json</c>.
/// <para>
/// <b>It is the NEGATIVE half of "cached like the earnings read".</b> The acquisitions store caches the
/// recognitions; without this, the 177 item-1.01 filings that are NOT acquisitions would be re-fetched from
/// www.sec.gov on every run, forever. Only AUTHORITATIVE answers are written (the caller never offers an
/// empty-body read), so a transient block can never make a NOT-recognised answer permanent.
/// </para>
/// <para>
/// <b>Heal-forward, never mass-invalidated</b> (AD-8/AD-1): the record carries the
/// <see cref="AcquisitionScanCacheRecord.ScanVersion"/> it was produced under and the CALLER treats a
/// different version as a miss, so bumping <c>acqscan-v1</c> retires every answer without deleting a byte.
/// </para>
/// <para>
/// A read failure is a MISS (never a throw) and a write failure returns <c>false</c> so the caller can
/// count the re-fetch it will pay for; only caller cancellation propagates. Reuses the shared
/// <see cref="RadarFileStoreJson.Options"/>, <see cref="FileTickerKey"/> and
/// <see cref="AtomicFileWriter"/> — no private JSON, key or write helper (AD-5 + reuse over copy).
/// </para>
/// </summary>
public sealed class FileAcquisitionScanCache : IAcquisitionScanCache
{
    private readonly FileAcquisitionScanCacheOptions _options;
    private readonly ILogger<FileAcquisitionScanCache> _logger;

    public FileAcquisitionScanCache(
        FileAcquisitionScanCacheOptions options, ILogger<FileAcquisitionScanCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<AcquisitionScanCacheRecord?> TryGetAsync(string accession, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accession);

        var path = PathFor(accession);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer
                .DeserializeAsync<AcquisitionScanCacheRecord>(stream, RadarFileStoreJson.Options, ct)
                .ConfigureAwait(false);

            // An entry with no scan version cannot be matched against the current one, so it is a MISS —
            // never a hit that would replay an unknown rule's answer.
            return record is null || string.IsNullOrWhiteSpace(record.ScanVersion) ? null : record;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(
                ex, "Could not read the acqscan cache entry {Path}; treating it as a miss.", path);
            return null;
        }
    }

    public async Task<bool> SetAsync(AcquisitionScanCacheRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(record.Accession);
        if (path is null)
        {
            _logger.LogWarning(
                "Acqscan cache accession '{Accession}' is blank or contains invalid filename characters; the "
                    + "answer cannot be cached and the filing will be re-fetched.",
                record.Accession);
            return false;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Failed to serialize the acqscan cache entry for {Path}.", path);
            return false;
        }

        // Replace rather than insert-if-new: a re-scan under the SAME version yields the same answer, and a
        // re-scan under a NEW version must overwrite the retired one (heal forward) rather than be refused.
        var outcome = await AtomicFileWriter.ReplaceAsync(path, json, _logger, ct).ConfigureAwait(false);
        return outcome == AtomicWriteOutcome.Committed;
    }

    private string? PathFor(string accession)
    {
        var key = FileTickerKey.Sanitize(accession);
        return key is null ? null : Path.Combine(_options.RootDirectory, key + ".json");
    }
}
