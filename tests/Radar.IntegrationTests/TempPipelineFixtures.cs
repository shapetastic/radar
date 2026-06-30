using System.Text;
using System.Text.Json;

namespace Radar.IntegrationTests;

/// <summary>
/// Per-test temp-directory harness for the integration tests. Creates a unique directory with an
/// <c>evidence/</c> subfolder, writes <c>companies.json</c> (the company watch-universe seed) and
/// <c>evidence/*.json</c> files in the exact shapes the real <c>LocalFile*</c> sources read, and
/// best-effort deletes the whole tree on dispose. Only the file inputs and the clock are faked; the
/// rest of the pipeline graph is the real production code.
/// </summary>
internal sealed class TempPipelineFixtures : IDisposable
{
    public TempPipelineFixtures()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "radar-it-" + Guid.NewGuid());
        EvidenceDir = Path.Combine(RootDir, "evidence");
        SeedFilePath = Path.Combine(RootDir, "companies.json");
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(EvidenceDir);
    }

    /// <summary>Root temp directory for this fixture instance.</summary>
    public string RootDir { get; }

    /// <summary>The <c>evidence/</c> subfolder passed to <c>AddLocalFileCollector</c>.</summary>
    public string EvidenceDir { get; }

    /// <summary>The <c>companies.json</c> path passed to <c>AddLocalFileCompanySeed</c>.</summary>
    public string SeedFilePath { get; }

    /// <summary>
    /// The raw-evidence root passed to <c>AddFileRawEvidenceStore</c>. Not pre-created — the store
    /// creates directories on demand; <see cref="RootDir"/>'s recursive delete on dispose cleans it up.
    /// </summary>
    public string RawEvidenceDir => Path.Combine(RootDir, "evidence-raw");

    /// <summary>
    /// The signals root passed to <c>AddFileSignalStore</c>. Not pre-created — the store creates
    /// directories on demand; <see cref="RootDir"/>'s recursive delete on dispose cleans it up.
    /// </summary>
    public string SignalsDir => Path.Combine(RootDir, "signals");

    /// <summary>
    /// A single seed company definition. <see cref="Name"/> is matched against evidence
    /// <c>sourceName</c> during resolution, so it must be a plain multi-word name with no trailing
    /// company-suffix token (inc/corp/ltd/...) that the resolver would strip.
    /// </summary>
    public readonly record struct SeedCompany(Guid Id, string Name, string Ticker, string[] Aliases);

    /// <summary>Writes <c>companies.json</c> from the supplied seed companies.</summary>
    public void WriteCompanies(IEnumerable<SeedCompany> companies)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"companies\": [\n");

        var list = companies.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var aliases = string.Join(", ", c.Aliases.Select(a => $"\"{Escape(a)}\""));
            sb.Append("    {\n");
            sb.Append($"      \"id\": \"{c.Id}\",\n");
            sb.Append($"      \"name\": \"{Escape(c.Name)}\",\n");
            sb.Append($"      \"ticker\": \"{Escape(c.Ticker)}\",\n");
            sb.Append($"      \"aliases\": [{aliases}]\n");
            sb.Append(i == list.Count - 1 ? "    }\n" : "    },\n");
        }

        sb.Append("  ]\n}\n");
        File.WriteAllText(SeedFilePath, sb.ToString());
    }

    /// <summary>
    /// Writes an <c>evidence/&lt;name&gt;.json</c> file. <paramref name="publishedAtUtc"/> null omits
    /// the field entirely (so the collector's <c>ObservedAtUtc</c> falls back to <c>CollectedAtUtc</c>),
    /// and <paramref name="quality"/> null omits the quality field (defaulting to Unknown).
    /// </summary>
    public void WriteEvidence(
        string fileName,
        string sourceName,
        string title,
        string rawText,
        string? publishedAtUtc,
        string? quality = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"sourceName\": \"{Escape(sourceName)}\",\n");
        sb.Append($"  \"title\": \"{Escape(title)}\",\n");
        sb.Append($"  \"rawText\": \"{Escape(rawText)}\"");

        if (publishedAtUtc is not null)
        {
            sb.Append($",\n  \"publishedAtUtc\": \"{publishedAtUtc}\"");
        }

        if (quality is not null)
        {
            sb.Append($",\n  \"quality\": \"{Escape(quality)}\"");
        }

        sb.Append("\n}\n");
        File.WriteAllText(Path.Combine(EvidenceDir, fileName), sb.ToString());
    }

    // Defer to the JSON serializer for escaping so control characters (newlines, tabs, etc.) in
    // inputs like rawText produce valid JSON. JsonSerializer.Serialize returns a quoted string;
    // strip the surrounding quotes since call sites add their own.
    private static string Escape(string value)
    {
        var json = JsonSerializer.Serialize(value);
        return json[1..^1];
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }
}
