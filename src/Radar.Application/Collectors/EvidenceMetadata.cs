using System.Text.Json;

namespace Radar.Application.Collectors;

/// <summary>
/// The single reader for the evidence-metadata envelope authored by <see cref="CollectedEvidenceMapper"/>
/// (<c>{ "metadata": { ... }, "companyHints": [ ... ] }</c>). Every consumer of <c>EvidenceItem.MetadataJson</c>
/// reads through this type so the envelope's author (the mapper) and its readers move together, instead of
/// each hand-rolling <see cref="JsonDocument"/> traversal. Defensive at every hop: null/blank/malformed
/// JSON, a missing/mistyped root, or a mistyped <c>metadata</c>/<c>companyHints</c> node all degrade to an
/// empty result — this reader never throws on bad input (skip-don't-throw, mirroring the mapper's tolerance).
/// </summary>
public static class EvidenceMetadata
{
    /// <summary>
    /// Parses the envelope. <paramref name="metadata"/> is the flat <c>metadata</c> object projected to
    /// its <b>string-valued</b> properties (ordinal keys); <paramref name="hints"/> is the
    /// <c>companyHints</c> string array. Returns <c>true</c> when a well-formed envelope object was parsed,
    /// <c>false</c> for null/blank/malformed input (with both out-params set to empty). Callers that only
    /// need one projection may ignore the other. Both projections are materialised into owned collections
    /// before the underlying <see cref="JsonDocument"/> is disposed, so no live <see cref="JsonElement"/>
    /// is handed back. Never throws.
    /// </summary>
    public static bool TryRead(
        string? metadataJson,
        out IReadOnlyDictionary<string, string> metadata,
        out IReadOnlyList<string> hints)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        metadata = dict;
        hints = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // metadata projection: string-valued properties only, ordinal keys.
            if (root.TryGetProperty("metadata", out var metadataElement)
                && metadataElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in metadataElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }

            // hints projection: companyHints array's string elements only.
            if (root.TryGetProperty("companyHints", out var hintsElement)
                && hintsElement.ValueKind == JsonValueKind.Array)
            {
                hints = hintsElement
                    .EnumerateArray()
                    .Where(h => h.ValueKind == JsonValueKind.String)
                    .Select(h => h.GetString()!)
                    .ToArray();
            }

            return true;
        }
        catch (JsonException)
        {
            // Malformed metadata degrades to "no usable metadata" — callers skip rather than crash.
            metadata = dict;
            hints = Array.Empty<string>();
            return false;
        }
    }
}
