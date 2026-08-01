namespace Radar.CalibrationAudit;

/// <summary>One SEALED worksheet row, as the shadow pass needs it (accession, cohort, sealed model answer).</summary>
public sealed record StudyWorksheetRow(
    string Accession,
    string Ticker,
    string Outcome,
    string SealedDirection,
    string SealedConfidence)
{
    /// <summary>The shadow cohort token derived from the sealed outcome.</summary>
    public string Cohort => Outcome switch
    {
        "DirectionalSignalProduced" => ShadowCohort.Directional,
        "NoDirectionalSignal" => ShadowCohort.NoSignal,
        _ => "other",
    };
}

/// <summary>
/// Reads the SEALED worksheet CSV the <c>Radar.CalibrationAudit</c> console wrote (<c>worksheet.csv</c>).
/// The shadow pass takes its cohort membership from HERE, never from the analyzed-filing cache: the cache
/// accrues between runs, and — decisively — resolving the cohort from the worksheet means the shadow mode
/// never registers, constructs or opens the production <c>FileAnalyzedFilingCache</c> at all, which is what
/// makes "a shadow read can never land in <c>data/filings-cache/</c>" a structural property rather than a
/// convention.
/// </summary>
public static class StudyWorksheetReader
{
    public const string FileName = "worksheet.csv";

    public static IReadOnlyList<StudyWorksheetRow> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No sealed worksheet at '{path}'. The shadow pass takes its cohort membership from the "
                    + "worksheet the calibration console sealed (never from the live cache) — point "
                    + "--exhibit-root at the study artifact root.",
                path);
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException($"The sealed worksheet at '{path}' is empty.");
        }

        var header = Csv.ParseLine(lines[0]);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            index[header[i]] = i;
        }

        foreach (var required in new[] { "accession", "ticker", "outcome", "direction", "confidence" })
        {
            if (!index.ContainsKey(required))
            {
                throw new InvalidOperationException(
                    $"The sealed worksheet at '{path}' has no '{required}' column; it was not written by the "
                        + "Radar.CalibrationAudit console.");
            }
        }

        var rows = new List<StudyWorksheetRow>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var f = Csv.ParseLine(lines[i]);
            if (f.Count < header.Count)
            {
                throw new InvalidOperationException(
                    $"Malformed worksheet line {i + 1} in '{path}' ({f.Count} fields, expected {header.Count}). "
                        + "The study cohort must be a function of accession; a partially-parsed worksheet is "
                        + "never guessed at.");
            }

            rows.Add(new StudyWorksheetRow(
                f[index["accession"]],
                f[index["ticker"]],
                f[index["outcome"]],
                f[index["direction"]],
                f[index["confidence"]]));
        }

        return rows;
    }

    /// <summary>
    /// Reads the accessions whose legacy/active outcomes CONFLICT from the console's own
    /// <c>legacy-exclusions.csv</c> (the study artifact that records them). Returns an empty list when the
    /// file is absent — the caller reports that as drift rather than silently asserting nothing.
    /// </summary>
    public static IReadOnlyList<string> ReadOutcomeConflicts(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return [];
        }

        var header = Csv.ParseLine(lines[0]);
        var accessionIndex = header.ToList().IndexOf("accession");
        var conflictIndex = header.ToList().IndexOf("outcomeConflict");
        if (accessionIndex < 0 || conflictIndex < 0)
        {
            return [];
        }

        var conflicts = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var f = Csv.ParseLine(lines[i]);
            if (f.Count <= Math.Max(accessionIndex, conflictIndex))
            {
                continue;
            }

            if (string.Equals(f[conflictIndex], "true", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(f[accessionIndex]);
            }
        }

        conflicts.Sort(StringComparer.Ordinal);
        return conflicts;
    }
}
