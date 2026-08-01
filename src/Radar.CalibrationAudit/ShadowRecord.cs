using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.CalibrationAudit;

/// <summary>
/// The per-accession shadow read outcome (spec 164). <see cref="Status"/> is recorded SEPARATELY from the
/// result so an infrastructure failure can never be counted as a <c>Neutral</c> (or any other) read:
/// <c>ok</c> carries a direction/confidence, <c>call-failed</c> and <c>parse-failed</c> carry none and are
/// listed instead. Failed records are RE-RUNNABLE — the skip rule skips only <c>ok</c>.
/// </summary>
public static class ShadowStatus
{
    public const string Ok = "ok";
    public const string CallFailed = "call-failed";
    public const string ParseFailed = "parse-failed";
}

/// <summary>The cohort an accession belongs to, read from the SEALED worksheet's outcome column.</summary>
public static class ShadowCohort
{
    /// <summary>Worksheet outcome <c>DirectionalSignalProduced</c> — the stability cohort.</summary>
    public const string Directional = "directional";

    /// <summary>Worksheet outcome <c>NoDirectionalSignal</c> — the recovery cohort.</summary>
    public const string NoSignal = "no-signal";
}

/// <summary>
/// One shadow record, persisted as <c>{outputRoot}/shadow/{accession}.json</c>. Carries the read AND its full
/// provenance: the prompt version + the LF-normalized SHA-256 of the exact instruction bytes sent, the model
/// identity, and the manifest-verified model-input hash the read was performed over. Nullable result fields
/// are <c>null</c> (never a default direction) for a non-<c>ok</c> status.
/// </summary>
public sealed record ShadowRecord
{
    [JsonPropertyName("accession")]
    public required string Accession { get; init; }

    [JsonPropertyName("cohort")]
    public required string Cohort { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("confidence")]
    public decimal? Confidence { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("rawResponse")]
    public string? RawResponse { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("promptVersion")]
    public required string PromptVersion { get; init; }

    [JsonPropertyName("promptSha256")]
    public required string PromptSha256 { get; init; }

    [JsonPropertyName("modelIdentity")]
    public required string ModelIdentity { get; init; }

    [JsonPropertyName("modelInputSha256")]
    public required string ModelInputSha256 { get; init; }

    [JsonPropertyName("readAtUtc")]
    public required string ReadAtUtc { get; init; }

    [JsonIgnore]
    public bool IsOk => string.Equals(Status, ShadowStatus.Ok, StringComparison.Ordinal);
}

/// <summary>
/// The shadow record store: ONE directory, <c>{outputRoot}/shadow/</c>, and nothing else is ever written by
/// the shadow pass. Records are keyed by accession, so a re-run overwrites in place (idempotent).
/// </summary>
public static class ShadowRecordStore
{
    /// <summary>The one directory the shadow pass writes.</summary>
    public const string DirectoryName = "shadow";

    public const string SummaryFileName = "shadow-summary.csv";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string[] SummaryHeader =
    [
        "accession", "cohort", "status", "direction", "confidence", "promptVersion", "promptSha256",
        "modelIdentity", "modelInputSha256", "readAtUtc", "error", "rationale",
    ];

    public static string RootFor(string outputRoot) => Path.Combine(outputRoot, DirectoryName);

    public static string PathFor(string outputRoot, string accession) =>
        Path.Combine(RootFor(outputRoot), accession + ".json");

    public static string SummaryPath(string outputRoot) => Path.Combine(RootFor(outputRoot), SummaryFileName);

    /// <summary>Reads an existing record, or null when absent/unreadable (an unreadable record is re-read).</summary>
    public static ShadowRecord? TryRead(string outputRoot, string accession)
    {
        var path = PathFor(outputRoot, accession);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ShadowRecord>(File.ReadAllBytes(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string outputRoot, ShadowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(RootFor(outputRoot));
        File.WriteAllBytes(PathFor(outputRoot, record.Accession), JsonSerializer.SerializeToUtf8Bytes(record, Json));
    }

    /// <summary>Reads every record under the shadow directory, ordered by SHA-256(accession) hex ascending.</summary>
    public static IReadOnlyList<ShadowRecord> ReadAll(string outputRoot)
    {
        var root = RootFor(outputRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var records = new List<ShadowRecord>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            var accession = Path.GetFileNameWithoutExtension(file);
            var record = TryRead(outputRoot, accession);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        records.Sort(static (a, b) => string.CompareOrdinal(
            AccessionHash.HexOf(a.Accession), AccessionHash.HexOf(b.Accession)));
        return records;
    }

    /// <summary>Writes <c>shadow-summary.csv</c> in the study's one ordering (SHA-256(accession) ascending).</summary>
    public static void WriteSummary(string outputRoot, IEnumerable<ShadowRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var ordered = records
            .OrderBy(static r => AccessionHash.HexOf(r.Accession), StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", SummaryHeader));
        foreach (var r in ordered)
        {
            sb.AppendLine(Csv.Line(
                r.Accession,
                r.Cohort,
                r.Status,
                r.Direction ?? string.Empty,
                r.Confidence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                r.PromptVersion,
                r.PromptSha256,
                r.ModelIdentity,
                r.ModelInputSha256,
                r.ReadAtUtc,
                r.Error ?? string.Empty,
                r.Rationale ?? string.Empty));
        }

        Directory.CreateDirectory(RootFor(outputRoot));
        File.WriteAllText(SummaryPath(outputRoot), sb.ToString());
    }
}
