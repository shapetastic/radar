using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Lifecycle;

namespace Radar.Infrastructure.FileSystem;

/// <summary>The efficacy artifact directory the facts are read from (the spec-140/155 artifact root).</summary>
public sealed record FileStrategyEvidenceFactsSourceOptions(string EfficacyDirectory);

/// <summary>
/// Reads the persisted spec-140/183 leaderboard CSV and the spec-155/170 paired-comparison CSV into
/// <see cref="EfficacyEvidenceFacts"/> (spec 184 §1). These are the artifacts that ALREADY exist — the
/// status layer adds no new efficacy computation and no second read of the score/price stores.
/// <para>
/// <b>Degrades, never throws.</b> A missing, unreadable, wrong-schema or structurally surprising artifact
/// logs the specific failure and reports that artifact as unavailable; the calculator then renders
/// "Accruing (evidence unavailable)" — the display degrades, the arm is never hidden, the run never fails.
/// Columns are resolved BY HEADER NAME, not position, so an additive column cannot silently shift a value.
/// </para>
/// </summary>
public sealed class FileStrategyEvidenceFactsSource : IStrategyEvidenceFactsSource
{
    /// <summary>The fixed artifact names, matching <c>FileEfficacyArtifactStore</c>'s stems.</summary>
    public const string LeaderboardFileName = "strategy-leaderboard.csv";
    public const string PairedComparisonFileName = "strategy-paired-comparison.csv";

    /// <summary>The one leaderboard CSV schema this reader understands (spec 183's excess schema).</summary>
    public const string SupportedLeaderboardSchema = "strategy-leaderboard-v2";

    private readonly FileStrategyEvidenceFactsSourceOptions _options;
    private readonly ILogger<FileStrategyEvidenceFactsSource> _logger;

    public FileStrategyEvidenceFactsSource(
        FileStrategyEvidenceFactsSourceOptions options, ILogger<FileStrategyEvidenceFactsSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EfficacyDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<EfficacyEvidenceFacts> ReadAsync(CancellationToken ct)
    {
        var (leaderboardAvailable, rows) = await ReadLeaderboardAsync(ct).ConfigureAwait(false);
        var paired = await ReadPairedAsync(ct).ConfigureAwait(false);

        return new EfficacyEvidenceFacts(
            LeaderboardAvailable: leaderboardAvailable,
            Leaderboard: rows,
            PairedAvailable: paired is not null,
            Paired: paired);
    }

    private async Task<(bool Available, IReadOnlyList<LeaderboardStrategyFact> Rows)> ReadLeaderboardAsync(
        CancellationToken ct)
    {
        var path = Path.Combine(_options.EfficacyDirectory, LeaderboardFileName);
        var lines = await TryReadLinesAsync(path, ct).ConfigureAwait(false);
        if (lines is null || lines.Count == 0)
        {
            return (false, []);
        }

        var header = IndexHeader(lines[0]);
        if (!TryColumn(header, "schemaVersion", out var schemaCol)
            || !TryColumn(header, "status", out var statusCol)
            || !TryColumn(header, "rank", out var rankCol)
            || !TryColumn(header, "strategy", out var strategyCol)
            || !TryColumn(header, "outOfSampleRhoExcessVsUniverseV1", out var rhoCol)
            || !TryColumn(header, "outOfSampleLower95", out var lowerCol)
            || !TryColumn(header, "outOfSampleUpper95", out var upperCol)
            || !TryColumn(header, "outOfSampleObservations", out var obsCol)
            || !TryColumn(header, "dropReason", out var dropCol))
        {
            _logger.LogWarning(
                "Leaderboard artifact at {Path} does not carry the expected {Schema} columns; treating the "
                    + "evidence as unavailable rather than guessing at positions.",
                path,
                SupportedLeaderboardSchema);
            return (false, []);
        }

        var rows = new List<LeaderboardStrategyFact>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var fields = SplitCsvLine(lines[i]);
            if (!string.Equals(Field(fields, schemaCol), SupportedLeaderboardSchema, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Leaderboard artifact at {Path} carries schema '{Schema}' on row {Row}; this reader "
                        + "understands '{Supported}' only — treating the evidence as unavailable.",
                    path,
                    Field(fields, schemaCol),
                    i,
                    SupportedLeaderboardSchema);
                return (false, []);
            }

            var strategy = Field(fields, strategyCol);
            if (strategy.Length == 0)
            {
                continue;
            }

            var status = Field(fields, statusCol);
            if (string.Equals(status, "ranked", StringComparison.Ordinal))
            {
                if (!TryInt(Field(fields, rankCol), out var rank)
                    || !TryDouble(Field(fields, rhoCol), out var rho)
                    || !TryDouble(Field(fields, lowerCol), out var lower)
                    || !TryDouble(Field(fields, upperCol), out var upper)
                    || !TryInt(Field(fields, obsCol), out var observations))
                {
                    // A ranked row whose numbers cannot be read must not render as Ranked without them
                    // (spec 184 §1) — the whole artifact degrades to unavailable instead.
                    _logger.LogWarning(
                        "Leaderboard artifact at {Path} carries a ranked row for '{Strategy}' with "
                            + "unparseable numbers; treating the evidence as unavailable.",
                        path,
                        strategy);
                    return (false, []);
                }

                rows.Add(new LeaderboardStrategyFact(
                    strategy,
                    Ranked: true,
                    new RankedEvidence(rank, rho, lower, upper, observations),
                    DropReason: null));
            }
            else if (string.Equals(status, "dropped", StringComparison.Ordinal))
            {
                var reason = Field(fields, dropCol);
                rows.Add(new LeaderboardStrategyFact(
                    strategy, Ranked: false, Numbers: null, DropReason: reason.Length > 0 ? reason : null));
            }

            // Unknown statuses are skipped: they are a future schema's business, not grounds to fail.
        }

        return (true, rows);
    }

    private async Task<PairedGateFact?> ReadPairedAsync(CancellationToken ct)
    {
        var path = Path.Combine(_options.EfficacyDirectory, PairedComparisonFileName);
        var lines = await TryReadLinesAsync(path, ct).ConfigureAwait(false);
        if (lines is null || lines.Count < 2)
        {
            return null;
        }

        var header = IndexHeader(lines[0]);
        if (!TryColumn(header, "primaryStrategy", out var primaryCol)
            || !TryColumn(header, "primaryPredeclared", out var predeclaredCol)
            || !TryColumn(header, "firstEligibleAsOf", out var boundaryCol)
            || !TryColumn(header, "gateReasons", out var reasonsCol)
            || !TryColumn(header, "qualifiesUnderAd15", out var qualifiesCol))
        {
            _logger.LogWarning(
                "Paired-comparison artifact at {Path} does not carry the expected gate columns; treating "
                    + "the gate evidence as unavailable.",
                path);
            return null;
        }

        // Every data row repeats the same run-level gate context (the per-row variation is per-baseline),
        // so the first data row carries everything the status layer needs.
        var fields = SplitCsvLine(lines[1]);
        var primary = Field(fields, primaryCol);
        if (primary.Length == 0)
        {
            _logger.LogWarning(
                "Paired-comparison artifact at {Path} carries no primary strategy on its first row; "
                    + "treating the gate evidence as unavailable.",
                path);
            return null;
        }

        return new PairedGateFact(
            PrimaryStrategyName: primary,
            PrimaryPredeclared: string.Equals(Field(fields, predeclaredCol), "true", StringComparison.Ordinal),
            BoundaryDeclared: Field(fields, boundaryCol).Length > 0,
            Qualifies: string.Equals(Field(fields, qualifiesCol), "true", StringComparison.Ordinal),
            GateReasons: Field(fields, reasonsCol),
            ArtifactWrittenAtUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private async Task<IReadOnlyList<string>?> TryReadLinesAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Efficacy artifact at {Path} could not be read; the affected evidence status degrades to "
                    + "'Accruing (evidence unavailable)'.",
                path);
            return null;
        }
    }

    private static Dictionary<string, int> IndexHeader(string headerLine)
    {
        var columns = SplitCsvLine(headerLine);
        var index = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            index.TryAdd(columns[i], i);
        }

        return index;
    }

    private static bool TryColumn(Dictionary<string, int> header, string name, out int index) =>
        header.TryGetValue(name, out index);

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Splits one CSV line honouring the shared <c>CsvField.Escape</c> quoting rules (a field containing a
    /// comma/quote/newline is wrapped in double quotes, with embedded quotes doubled).
    /// </summary>
    internal static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
