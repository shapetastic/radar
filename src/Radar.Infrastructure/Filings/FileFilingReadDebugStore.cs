using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// Opt-in on-disk store of AI filing-read diagnostics (spec 115): one JSON file per accession at
/// <c>{RootDirectory}/{sanitizedAccession}.json</c>, so the last read attempt for each filing — including
/// no-signal and empty-body outcomes — is inspectable without re-running the pipeline. This is an AD-14
/// read-side analogue: diagnostic/operational data, consumed by NOTHING in the evidence/signal/scoring/report
/// path and never a fingerprint input. It reuses the shared <see cref="GracefulFileWriter.TryWriteAllTextAsync"/>
/// + <see cref="RadarFileStoreJson.Options"/> scaffolding (the "reuse over copy" rule) so its on-disk shape and
/// graceful-degrade posture cannot diverge from the other file stores; all file I/O and JSON stay confined to
/// Infrastructure (AD-5).
/// <para>
/// Best-effort by contract: a blank/invalid accession or a disk failure degrades to a logged no-op — a
/// diagnostic write must never abort a run. Before writing, the record is defensively re-scrubbed (AD-9): a
/// rationale that carries advice language is DROPPED (even though <see cref="ChatFilingAnalyzer"/> already
/// scrubs — a non-validating <c>IFilingAnalyzer</c> implementation must not be able to persist advice language
/// through this store), and the rationale is re-bounded to the same cap the analyzer enforces.
/// </para>
/// </summary>
public sealed class FileFilingReadDebugStore : IFilingReadDebugSink
{
    private readonly FileFilingReadDebugStoreOptions _options;
    private readonly ILogger<FileFilingReadDebugStore> _logger;

    public FileFilingReadDebugStore(
        FileFilingReadDebugStoreOptions options,
        ILogger<FileFilingReadDebugStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task RecordAsync(FilingReadDebugRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Reuse the shared filename-key sanitizer (FileTickerKey) exactly as FileAnalyzedFilingCache does —
        // despite the ticker-oriented name it is the shared filename-safe key helper (reuse over copy). A
        // blank/invalid accession is a logged skip, never a throw (best-effort contract).
        var sanitized = FileTickerKey.Sanitize(record.Accession);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "AI filing-read debug accession '{Accession}' is blank or contains invalid filename characters; skipping the debug record.",
                record.Accession);
            return;
        }

        var path = Path.Combine(_options.RootDirectory, sanitized + ".json");
        var scrubbed = Scrub(record);
        var json = JsonSerializer.Serialize(scrubbed, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Persisted AI filing-read debug record for accession {Accession} ({Outcome}) to {Path}.",
                scrubbed.Accession,
                scrubbed.Outcome,
                path);
        }
    }

    /// <summary>
    /// Defensive persistence scrub (AD-9): re-bounds the rationale to the analyzer's own cap
    /// (<see cref="ChatFilingAnalyzer.MaxRationaleLength"/>) and DROPS it entirely if it carries advice
    /// language per the shared <see cref="AdviceLanguageGuard"/> — this store must never write advice language
    /// even if a non-validating analyzer produced it.
    /// </summary>
    private FilingReadDebugRecord Scrub(FilingReadDebugRecord record)
    {
        var rationale = record.Rationale;
        if (string.IsNullOrEmpty(rationale))
        {
            return record;
        }

        if (rationale.Length > ChatFilingAnalyzer.MaxRationaleLength)
        {
            rationale = rationale[..ChatFilingAnalyzer.MaxRationaleLength];
        }

        if (AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            _logger.LogWarning(
                "AI filing-read debug rationale for accession {Accession} contained advice language; dropping the rationale before persistence.",
                record.Accession);
            rationale = null;
        }

        return ReferenceEquals(rationale, record.Rationale) ? record : record with { Rationale = rationale };
    }
}
