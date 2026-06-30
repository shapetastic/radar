using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Single source of truth for the JSON serialization shape of Radar's on-disk file-store mirrors
/// (signals, score snapshots, raw evidence). Indented for human readability, camelCase property names,
/// and enums rendered as their string names (never integer ordinals) so the on-disk shape is lossless
/// and stable. Shared so the three JSON stores cannot diverge.
/// </summary>
internal static class RadarFileStoreJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        };

        // Freeze so callers cannot add converters or change policies at runtime; this stays the single
        // source of truth for the on-disk shape.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
