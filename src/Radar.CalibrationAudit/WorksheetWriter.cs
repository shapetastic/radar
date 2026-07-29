using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Radar.CalibrationAudit;

/// <summary>
/// Writes the SEALED worksheet CSV — the model-answer join source <c>analyze-labels.ps1</c> joins labels
/// against (spec 162). One row per scoped-cohort accession, ordered by SHA-256(accession) hex ascending
/// (the study's one ordering key), carrying everything the cache record holds: outcome, direction,
/// confidence, strength, novelty, signal type, comparability policy/markers, plus the recovered
/// ticker/CIK, the pinned model identity + scope segment, and the cache file the row was read from. The
/// sealed model answer lives HERE, never in the label file — a blinded labeler must not see it.
/// </summary>
public static class WorksheetWriter
{
    public const string FileName = "worksheet.csv";

    private static readonly string[] Header =
    [
        "accession", "accessionSha256", "ticker", "cik", "companyName", "outcome",
        "signalType", "direction", "confidence", "strength", "novelty",
        "supportingExcerpt", "reason",
        "observedAtUtc", "comparabilityPolicy", "comparabilityCapTriggering", "comparabilityDiagnosticOnly",
        "cacheVersion", "modelIdentity", "scopeSegment", "cacheFile",
    ];

    /// <summary>Writes the worksheet and returns (path, SHA-256 hex of the written bytes).</summary>
    public static (string Path, string Sha256) Write(
        string outputRoot,
        IReadOnlyList<CohortRow> rows,
        RawFilingEvidenceIndex evidenceIndex,
        string modelIdentity,
        string scopeSegment)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(evidenceIndex);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header));

        foreach (var row in rows.OrderBy(static r => r.AccessionSha256, StringComparer.Ordinal))
        {
            var attribution = evidenceIndex.TryResolve(row.Accession, out var resolved) ? resolved : null;
            var record = row.Record;
            var signal = record.Signal;

            sb.AppendLine(Csv.Line(
                row.Accession,
                row.AccessionSha256,
                attribution?.Ticker ?? string.Empty,
                attribution?.Cik ?? string.Empty,
                attribution?.SourceName ?? signal?.CompanyMention ?? string.Empty,
                record.Outcome.ToString(),
                signal?.SignalType ?? string.Empty,
                signal?.Direction ?? string.Empty,
                signal?.Confidence.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                signal?.Strength.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                signal?.Novelty.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                signal?.SupportingExcerpt ?? string.Empty,
                signal?.Reason ?? string.Empty,
                record.ObservedAtUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
                record.ComparabilityPolicy ?? string.Empty,
                record.ComparabilityMarkers is null
                    ? string.Empty
                    : string.Join(";", record.ComparabilityMarkers.CapTriggering),
                record.ComparabilityMarkers is null
                    ? string.Empty
                    : string.Join(";", record.ComparabilityMarkers.DiagnosticOnly),
                record.CacheVersion.ToString(CultureInfo.InvariantCulture),
                modelIdentity,
                scopeSegment,
                row.CacheFile));
        }

        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, FileName);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        File.WriteAllBytes(path, bytes);
        return (path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
