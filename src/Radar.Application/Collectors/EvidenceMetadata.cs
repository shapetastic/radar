using System.Text.Json;

namespace Radar.Application.Collectors;

/// <summary>
/// The single reader <b>and writer</b> for the evidence-metadata envelope
/// (<c>{ "metadata": { ... }, "companyHints": [ ... ] }</c>). Every consumer of
/// <c>EvidenceItem.MetadataJson</c> reads through <see cref="TryRead"/> and every producer writes through
/// <see cref="Compose"/>, so the envelope's shape has exactly one definition instead of each call site
/// hand-rolling <see cref="JsonDocument"/> traversal or a serializer call. Reading is defensive at every
/// hop: null/blank/malformed JSON, a missing/mistyped root, or a mistyped
/// <c>metadata</c>/<c>companyHints</c> node all degrade to an empty result — this reader never throws on
/// bad input (skip-don't-throw, mirroring the mapper's tolerance).
/// </summary>
public static class EvidenceMetadata
{
    // Default options, deliberately: the envelope is an internal wire format whose exact bytes are part of
    // EvidenceItem.MetadataJson's identity. Both the collection-time author (CollectedEvidenceMapper) and
    // the durable hydration path (FileRawEvidenceStore, spec 142) serialize through THIS instance, so a
    // hydrated item's MetadataJson is byte-identical to the one collection produced by construction.
    private static readonly JsonSerializerOptions ComposeOptions = new();

    /// <summary>
    /// Writes the envelope: <c>{ "metadata": { ... }, "companyHints": [ ... ] }</c>. The parameter types
    /// mirror <see cref="CollectedEvidence.Metadata"/> / <see cref="CollectedEvidence.CompanyHints"/>
    /// exactly, so the serialized shape cannot drift from what the collection path produces.
    /// </summary>
    public static string Compose(
        IReadOnlyDictionary<string, string> metadata, IReadOnlyList<string> hints)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(hints);

        return JsonSerializer.Serialize(new { metadata, companyHints = hints }, ComposeOptions);
    }

    /// <summary>
    /// Projects a <c>metadata</c> OBJECT element to its <b>string-valued</b> properties (ordinal keys, in
    /// document order). This is the same projection <see cref="TryRead"/> applies to the envelope's
    /// <c>metadata</c> node, exposed so a caller that already holds the bare object — the durable
    /// raw-evidence store, whose on-disk shape stores <c>metadata</c> and <c>companyHints</c> as SEPARATE
    /// nodes — reuses the rule rather than copying it. A non-object element projects to empty.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadMetadataObject(JsonElement metadataObject)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        ProjectStringProperties(metadataObject, dict);
        return dict;
    }

    private static void ProjectStringProperties(JsonElement element, Dictionary<string, string> into)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                into[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
    }

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

            // metadata projection: string-valued properties only, ordinal keys — the SAME rule
            // ReadMetadataObject exposes for callers holding the bare object.
            if (root.TryGetProperty("metadata", out var metadataElement))
            {
                ProjectStringProperties(metadataElement, dict);
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
