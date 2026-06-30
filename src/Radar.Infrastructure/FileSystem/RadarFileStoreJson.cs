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
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
