using System.Text.Json;

namespace Radar.TestSupport;

/// <summary>
/// THE ONE C# mirror of <c>scripts/run-radar.ps1</c>'s profile composition, shared by every test that has
/// to bind a committed run profile the way a live run binds it (spec 187 §6).
/// <para>
/// It exists because the script — not a configuration provider — is what actually assembles a live run's
/// arguments: it flattens each profile's JSON into flat <c>Radar:A:B:C</c> keys (arrays become
/// <c>…:0</c>, <c>…:1</c>, …), SKIPS every <c>_comment*</c> annotation key at any depth, and merges an
/// overlay into the base ONE FLAT KEY AT A TIME (<c>$merged[$k] = $overlay[$k]</c>, overlay wins).
/// </para>
/// <para>
/// It lives here rather than in one test project because it had TWO consumers the day it acquired a second
/// one, and a second copy is exactly the drift this mirror exists to catch: on 2026-08-23 the scheduled
/// baseline crashed at startup because the script skipped only the exact key <c>_comment</c> while the
/// profile had grown a <c>_comment2</c> — and the whole suite was green, because the mirror had the same
/// bug. A copy would only ever get one of the next two fixes.
/// </para>
/// <para>
/// It deliberately returns FLAT STRING PAIRS rather than an <c>IConfiguration</c>: the flattening is the
/// part that mirrors the script, and keeping this type free of a configuration dependency lets every test
/// project — Worker, Infrastructure — build whatever configuration/host it needs on top of the same keys.
/// </para>
/// </summary>
public static class RunProfileMirror
{
    /// <summary>
    /// The annotation-key prefix the script skips (<c>-like '_comment*'</c>). A JSON object cannot repeat
    /// a property name, so a section with several notes uses <c>_comment2</c>, <c>_comment3</c>, …; ALL of
    /// them are annotations and NONE may ever reach the Worker's strict config-key allowlists.
    /// </summary>
    public const string CommentKeyPrefix = "_comment";

    /// <summary>
    /// True when a JSON property is a profile ANNOTATION rather than configuration. Mirrors PowerShell's
    /// <c>-like '_comment*'</c>, which is case-INSENSITIVE — so the mirror is case-insensitive too, rather
    /// than being stricter than the implementation it claims to reproduce.
    /// </summary>
    public static bool IsCommentKey(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return propertyName.StartsWith(CommentKeyPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying
    /// <c>scripts/run-profiles/</c>) so no test depends on its working directory.
    /// </summary>
    public static string ProfilesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "run-profiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate scripts/run-profiles/ from " + AppContext.BaseDirectory);
    }

    /// <summary>Resolves one committed profile file by NAME (without the <c>.json</c> suffix).</summary>
    public static string ProfilePath(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        return Path.Combine(ProfilesDirectory(), profileName + ".json");
    }

    /// <summary>
    /// Mirrors <c>Add-Flattened</c>: leaf values become flat <c>A:B:C</c> keys, arrays become indexed keys,
    /// and <c>_comment*</c> keys are skipped at EVERY depth (root included).
    /// </summary>
    public static void Flatten(JsonElement node, string prefix, Dictionary<string, string?> acc)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(acc);

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (IsCommentKey(property.Name))
                    {
                        continue;
                    }

                    Flatten(
                        property.Value,
                        prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}",
                        acc);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index}", acc);
                    index++;
                }

                break;

            case JsonValueKind.True:
                acc[prefix] = "true";
                break;

            case JsonValueKind.False:
                acc[prefix] = "false";
                break;

            case JsonValueKind.Null:
                acc[prefix] = string.Empty;
                break;

            default:
                // Numbers render as their raw (invariant) JSON text, matching the script's invariant
                // ToString; strings render as their value.
                acc[prefix] = node.ToString();
                break;
        }
    }

    /// <summary>Flattens a JSON DOCUMENT (a fixture or a profile's contents) into flat config keys.</summary>
    public static Dictionary<string, string?> FlattenJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        var flattened = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(doc.RootElement, string.Empty, flattened);
        return flattened;
    }

    /// <summary>Flattens one committed profile file, resolved by NAME (without the <c>.json</c> suffix).</summary>
    public static Dictionary<string, string?> FlattenProfile(string profileName) =>
        FlattenJson(File.ReadAllText(ProfilePath(profileName)));

    /// <summary>
    /// Composes <c>default.json</c> plus an optional overlay, the overlay winning ONE FLAT KEY AT A TIME —
    /// exactly the script's <c>$merged[$k] = $overlay[$k]</c> merge, which is a shallow per-key
    /// last-source-wins precedence and NOT a structural JSON merge (an overlay array entry therefore
    /// overrides only the indexed keys it declares).
    /// </summary>
    public static Dictionary<string, string?> Compose(string? overlayProfileName = null)
    {
        var merged = FlattenProfile("default");
        if (overlayProfileName is not null)
        {
            foreach (var (key, value) in FlattenProfile(overlayProfileName))
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Every committed overlay profile (every <c>*.json</c> except <c>default.json</c>), in a fixed ordinal
    /// order so a theory over them is deterministic (AD-3).
    /// </summary>
    public static IReadOnlyList<string> OverlayProfileNames() =>
        Directory.GetFiles(ProfilesDirectory(), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
