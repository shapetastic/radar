namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>Which evidence collectors to run, additively. Each kind is one of: "rss", "localfile", "sec", "usaspending", "news".</summary>
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

    /// <summary>
    /// GDELT DOC 2.0 news collector configuration (bound from "Radar:Gdelt"). Only read when the "news"
    /// collector is enabled; the defaults let the rss-only configuration keep working with no Gdelt config.
    /// </summary>
    public GdeltWorkerOptions Gdelt { get; init; } = new();

    /// <summary>
    /// AI chat-client seam configuration (bound from "Radar:Ai"). A blank <see cref="AiWorkerOptions.Provider"/>
    /// (the default) means AI is DISABLED — no <c>IChatClient</c> is wired and no provider packages load — so the
    /// default configuration surfaces no AI. Only read when a provider is configured.
    /// </summary>
    public AiWorkerOptions Ai { get; init; } = new();

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

/// <summary>
/// GDELT DOC 2.0 news collector configuration (bound from "Radar:Gdelt"). Surfaces the recent-coverage
/// window, the per-company cap, the English-only toggle, the inter-request pacing delay (GDELT throttles
/// hard), and the 429 retry bound through to <c>GdeltCollectorOptions</c>. Defaults so the rss-only
/// configuration works without any Gdelt config.
/// </summary>
public sealed class GdeltWorkerOptions
{
    /// <summary>Recent-coverage window as a GDELT timespan token. Defaults to "2w".</summary>
    public string Timespan { get; init; } = "2w";

    /// <summary>Maximum surviving (relevance-filtered, deduped) articles to collect per company per run. Defaults to 25.</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict the query to English-language coverage. Defaults to true.</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>Pause between successive per-company requests, in seconds. Defaults to 6 (GDELT allows 1 request / 5s per IP).</summary>
    public int InterRequestDelaySeconds { get; init; } = 6;

    /// <summary>How many times the reader re-issues a request after an HTTP 429 before giving up. Defaults to 2.</summary>
    public int MaxRetriesOn429 { get; init; } = 2;

    /// <summary>Base cool-down before the first 429 retry, in seconds; the reader doubles it per retry. Defaults to 60 (→ 60s/120s).</summary>
    public int RetryBackoffSeconds { get; init; } = 60;
}

/// <summary>
/// AI chat-client seam configuration (bound from "Radar:Ai"). Surfaces the provider selection and model id plus the
/// nested <see cref="AiAnthropicWorkerOptions"/> / <see cref="AiOllamaWorkerOptions"/> config blocks through to
/// <c>AiClientOptions</c>. A blank <see cref="Provider"/> (the default) means AI is DISABLED — nothing is wired and
/// no provider packages load — so the default rss-only configuration keeps working with no AI config.
/// </summary>
public sealed class AiWorkerOptions
{
    /// <summary>The AI provider: "anthropic" (hosted Claude) or "ollama" (local, keyless). Blank by default = AI DISABLED.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>The model id (e.g. "claude-opus-4-8" for anthropic or an Ollama tag like "llama3.1"). Required when a provider is set.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Anthropic (hosted) provider config. Only read when Provider is "anthropic".</summary>
    public AiAnthropicWorkerOptions Anthropic { get; init; } = new();

    /// <summary>Ollama (local, keyless) provider config. Only read when Provider is "ollama".</summary>
    public AiOllamaWorkerOptions Ollama { get; init; } = new();

    /// <summary>
    /// Maximum earnings-release characters sent to the filing analyzer (token/latency control). The analyzer
    /// truncates the release to this leading-substring length before calling the model. Only read when a
    /// provider is configured. Defaults to 12000.
    /// </summary>
    public int MaxInputLength { get; init; } = 12000;

    /// <summary>
    /// Confidence gate for directional filing signals: an AI read below this yields no directional
    /// GuidanceChange signal (the deterministic Neutral from spec 57 stands). In [0,1]. Only read when a
    /// provider is configured. Defaults to 0.6.
    /// </summary>
    public decimal MinConfidence { get; init; } = 0.6m;

    /// <summary>
    /// Cost cap on the directional filing enrichment: the source reads/analyzes at most this many
    /// earnings-8-K filings per run. Must be positive. Only read when a provider is configured. Defaults to 5.
    /// </summary>
    public int MaxFilingsPerRun { get; init; } = 5;
}

/// <summary>Anthropic (hosted) provider config (bound from "Radar:Ai:Anthropic").</summary>
public sealed class AiAnthropicWorkerOptions
{
    /// <summary>The Anthropic API key. Required when Provider is "anthropic". Defaults to empty.</summary>
    public string ApiKey { get; init; } = string.Empty;
}

/// <summary>Ollama (local, keyless) provider config (bound from "Radar:Ai:Ollama").</summary>
public sealed class AiOllamaWorkerOptions
{
    /// <summary>The Ollama base URL. Only used when Provider is "ollama". Defaults to http://localhost:11434.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434";
}
