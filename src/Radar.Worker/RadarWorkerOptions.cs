namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>Which evidence collector to run ("rss" | "localfile").</summary>
    public string CollectorKind { get; init; } = "rss";

    /// <summary>Directory of local evidence JSON files (Stage 1 source).</summary>
    public string EvidenceSourceDirectory { get; init; } = "data/evidence";

    /// <summary>Root directory for the insert-only raw-evidence file store.</summary>
    public string EvidenceRawDirectory { get; init; } = "data/evidence/raw";

    /// <summary>Root directory for the signal file store.</summary>
    public string SignalsDirectory { get; init; } = "data/signals";

    /// <summary>Root directory for the score snapshot file store.</summary>
    public string ScoresDirectory { get; init; } = "data/scores";

    /// <summary>Root directory for the weekly markdown report writer.</summary>
    public string ReportDirectory { get; init; } = "data/reports";

    /// <summary>Path to the company watch-universe seed JSON file.</summary>
    public string CompanySeedFilePath { get; init; } = "data/companies.json";

    /// <summary>
    /// Recent-signal scoring window length, in days (maps to ScoringOptions.Window).
    /// Defaults to 60: small-cap issuers publish material news roughly monthly, so a 30-day
    /// window systematically misses real recent fundamentals. The scoring formula
    /// (radar-formula-v1) already recency-weights signals within the window (older signals
    /// contribute less), so a wider window adds recall without over-weighting stale news.
    /// </summary>
    public int ScoringWindowDays { get; init; } = 60;

    /// <summary>Report period length, in days (maps to WeeklyReportOptions.Period).</summary>
    public int ReportPeriodDays { get; init; } = 7;

    /// <summary>Max companies in the report (maps to WeeklyReportOptions.MaxItems).</summary>
    public int ReportMaxItems { get; init; } = 25;

    /// <summary>Whether the run ends by building the weekly report (maps to PipelineOptions.GenerateReport).</summary>
    public bool GenerateReport { get; init; } = true;

    /// <summary>Run once then exit (true, MVP default), or loop on an interval (false).</summary>
    public bool RunOnce { get; init; } = true;

    /// <summary>Interval between runs in minutes when RunOnce is false.</summary>
    public int IntervalMinutes { get; init; } = 60;
}
