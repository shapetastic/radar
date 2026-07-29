using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Radar.CalibrationAudit;

/// <summary>What the persisted raw filing evidence recorded for an accession (CIK/ticker recovery, spec 162).</summary>
public sealed record RawFilingAttribution(string Cik, string? Ticker, string? SourceName, string SourceFile);

/// <summary>
/// Accession → (CIK, ticker) recovery from the persisted raw evidence under
/// <c>{dataRoot}/evidence/raw/filing/**</c> (SINGULAR <c>filing</c> — the on-disk snake_case source-type
/// folder). The analyzed-filing cache records carry no CIK (directional records carry only a company
/// mention; no-signal records carry nothing but the accession), but every filing's raw evidence persisted
/// the EDGAR index URL (<c>…/edgar/data/{cik}/{acc}/{accession}-index.htm</c>) and the accession in its
/// metadata, so the attribution is recoverable deterministically. Files are scanned in ordinal path order
/// and the FIRST file carrying an accession wins (the same deterministic first-wins rule the spec-145
/// hydration collapse uses); a malformed file is logged and skipped, never thrown. Accessions with no
/// recoverable attribution are the caller's to LIST — this index never silently drops them.
/// </summary>
public sealed partial class RawFilingEvidenceIndex
{
    private readonly Dictionary<string, RawFilingAttribution> _byAccession;

    private RawFilingEvidenceIndex(Dictionary<string, RawFilingAttribution> byAccession)
        => _byAccession = byAccession;

    public int Count => _byAccession.Count;

    public bool TryResolve(string accession, out RawFilingAttribution attribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accession);
        return _byAccession.TryGetValue(accession, out attribution!);
    }

    public static RawFilingEvidenceIndex Load(string rawFilingRoot, ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawFilingRoot);
        ArgumentNullException.ThrowIfNull(logger);

        var byAccession = new Dictionary<string, RawFilingAttribution>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rawFilingRoot))
        {
            logger.LogWarning(
                "Raw filing evidence directory '{Root}' does not exist; no CIK can be recovered.", rawFilingRoot);
            return new RawFilingEvidenceIndex(byAccession);
        }

        var files = Directory.EnumerateFiles(rawFilingRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                var sourceUrl = TryGetString(root, "sourceUrl") ?? string.Empty;

                // The accession: the persisted metadata's accessionNumber when present, else the dashed
                // accession embedded in the index SourceUrl.
                string? accession = null;
                if (root.TryGetProperty("metadata", out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object)
                {
                    accession = TryGetString(metadata, "accessionNumber");
                }

                if (string.IsNullOrWhiteSpace(accession))
                {
                    var accessionMatch = AccessionRegex().Match(sourceUrl);
                    accession = accessionMatch.Success ? accessionMatch.Groups[1].Value : null;
                }

                if (string.IsNullOrWhiteSpace(accession))
                {
                    continue; // Not an accession-bearing filing record (nothing to index).
                }

                // The CIK: from the EDGAR archive path in the persisted SourceUrl.
                var cikMatch = CikRegex().Match(sourceUrl);
                if (!cikMatch.Success)
                {
                    continue; // No CIK recoverable from this file; a later file may still carry it.
                }

                string? ticker = null;
                if (root.TryGetProperty("companyHints", out var hints)
                    && hints.ValueKind == JsonValueKind.Array
                    && hints.GetArrayLength() > 0
                    && hints[0].ValueKind == JsonValueKind.String)
                {
                    ticker = hints[0].GetString();
                }

                // First file (ordinal path order) wins — deterministic across runs.
                if (!byAccession.ContainsKey(accession))
                {
                    byAccession.Add(accession, new RawFilingAttribution(
                        cikMatch.Groups[1].Value,
                        ticker,
                        TryGetString(root, "sourceName"),
                        file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // One malformed/unreadable raw file must not break the audit — log and skip (AD-8 posture).
                logger.LogWarning(ex, "Skipping unreadable raw filing evidence file '{File}'.", file);
            }
        }

        return new RawFilingEvidenceIndex(byAccession);
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"/edgar/data/(\d+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CikRegex();

    [GeneratedRegex(@"(\d{10}-\d{2}-\d{6})")]
    private static partial Regex AccessionRegex();
}
