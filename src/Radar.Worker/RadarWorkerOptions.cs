namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>Which evidence collectors to run, additively. Each kind is one of: "rss", "localfile", "sec", "usaspending".</summary>
    public IReadOnlyList<string> Collectors { get; init; } = ["rss"];

    /// <summary>
    /// SEC EDGAR filing collector configuration (bound from "Radar:Sec"). Only read when the "sec" collector
    /// is enabled; a blank <see cref="SecWorkerOptions.UserAgent"/> fails fast at that point (SEC requires a
    /// compliant User-Agent).
    /// </summary>
    public SecWorkerOptions Sec { get; init; } = new();

    /// <summary>
    /// USASpending.gov government-contract collector configuration (bound from "Radar:UsaSpending"). Only read
    /// when the "usaspending" collector is enabled; the defaults let the rss-only configuration keep working
    /// with no USASpending config.
    /// </summary>
    public UsaSpendingWorkerOptions UsaSpending { get; init; } = new();

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

    /// <summary>Root directory for the pipeline run-history file store.</summary>
    public string RunsDirectory { get; init; } = "data/runs";

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

    /// <summary>Recent runs shown in the report footer (maps to WeeklyReportOptions.RecentRunsInReport).</summary>
    public int RecentRunsInReport { get; init; } = 5;

    /// <summary>Whether the run ends by building the weekly report (maps to PipelineOptions.GenerateReport).</summary>
    public bool GenerateReport { get; init; } = true;

    /// <summary>Run once then exit (true, MVP default), or loop on an interval (false).</summary>
    public bool RunOnce { get; init; } = true;

    /// <summary>Interval between runs in minutes when RunOnce is false.</summary>
    public int IntervalMinutes { get; init; } = 60;
}

/// <summary>
/// SEC EDGAR filing collector configuration (bound from "Radar:Sec"). Surfaces the required, compliant
/// User-Agent and the form filter / per-company cap through to <c>SecCollectorOptions</c>.
/// </summary>
public sealed class SecWorkerOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. "Radar Research example@example.com"). Required when the "sec"
    /// collector is enabled — every SEC request 403s without it. Defaults to empty so the default
    /// rss-only configuration stays working without any SEC config.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Filing forms to collect (case-insensitive). Defaults to 8-K, 10-Q, 10-K.</summary>
    public IReadOnlyList<string> Forms { get; init; } = ["8-K", "10-Q", "10-K"];

    /// <summary>Maximum most-recent matching filings to collect per company per run.</summary>
    public int MaxFilingsPerCompany { get; init; } = 25;
}

/// <summary>
/// USASpending.gov government-contract collector configuration (bound from "Radar:UsaSpending"). Surfaces the
/// mutually-exclusive award-type group, the recent-activity window, and the per-company cap through to
/// <c>UsaSpendingCollectorOptions</c>. Defaults so the rss-only configuration works without any USASpending config.
/// </summary>
public sealed class UsaSpendingWorkerOptions
{
    /// <summary>Mutually-exclusive award-type group to query. Defaults to the contracts group A/B/C/D (mixing groups is an API 400).</summary>
    public IReadOnlyList<string> AwardTypeCodes { get; init; } = ["A", "B", "C", "D"];

    /// <summary>Recent-activity window length, in days. Defaults to 365.</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum highest-value matching awards to collect per company per run. Defaults to 25.</summary>
    public int MaxAwardsPerCompany { get; init; } = 25;
}
