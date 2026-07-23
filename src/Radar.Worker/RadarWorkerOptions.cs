namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>Which evidence collectors to run, additively. Each kind is one of: "rss", "localfile", "sec", "secform4", "sec13dg", "usaspending", "news", "newssearch", "hiringats", "patents", "fccauth".</summary>
    public IReadOnlyList<string> Collectors { get; init; } = ["rss"];

    /// <summary>
    /// SEC EDGAR filing collector configuration (bound from "Radar:Sec"). Only read when the "sec" collector
    /// is enabled; a blank <see cref="SecWorkerOptions.UserAgent"/> fails fast at that point (SEC requires a
    /// compliant User-Agent).
    /// </summary>
    public SecWorkerOptions Sec { get; init; } = new();

    /// <summary>
    /// SEC Form 4 (insider-transaction) collector configuration (bound from "Radar:SecForm4"). Only read when
    /// the "secform4" collector is enabled; a blank <see cref="SecForm4WorkerOptions.UserAgent"/> fails fast at
    /// that point (SEC requires a compliant User-Agent).
    /// </summary>
    public SecForm4WorkerOptions SecForm4 { get; init; } = new();

    /// <summary>
    /// SEC Schedule 13D/13G (institutional/activist beneficial-ownership) collector configuration (bound from
    /// "Radar:Sec13DG"). Only read when the "sec13dg" collector is enabled; a blank
    /// <see cref="Sec13DGWorkerOptions.UserAgent"/> fails fast at that point (SEC requires a compliant
    /// User-Agent).
    /// </summary>
    public Sec13DGWorkerOptions Sec13DG { get; init; } = new();

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
    /// Google News RSS news-attention collector configuration (bound from "Radar:News"). Only read when the
    /// "newssearch" collector is enabled; the defaults let the rss-only configuration keep working with no News
    /// config.
    /// </summary>
    public NewsWorkerOptions News { get; init; } = new();

    /// <summary>
    /// ATS job-board hiring collector configuration (bound from "Radar:Hiring"). Only read when the
    /// "hiringats" collector is enabled (opt-in, OFF by default — it is not in the default Collectors); the
    /// defaults let the rss-only configuration keep working with no Hiring config.
    /// </summary>
    public HiringWorkerOptions Hiring { get; init; } = new();

    /// <summary>
    /// PatentsView granted-patent activity collector configuration (bound from "Radar:Patents"). Only read
    /// when the "patents" collector is enabled (opt-in, OFF by default — it is not in the default Collectors);
    /// the defaults let the rss-only configuration keep working with no Patents config. The API key VALUE is
    /// never here — it is read at runtime from the env var NAMED by <see cref="PatentWorkerOptions.ApiKeyEnvVar"/>.
    /// </summary>
    public PatentWorkerOptions Patents { get; init; } = new();

    /// <summary>
    /// FCC Equipment Authorization (EAS) collector configuration (bound from "Radar:Fcc"; spec 128). Only read
    /// when the "fccauth" collector is enabled (opt-in, OFF by default — it is not in the default Collectors);
    /// the defaults let the rss-only configuration keep working with no Fcc config. The EAS export needs no API
    /// key.
    /// </summary>
    public FccWorkerOptions Fcc { get; init; } = new();

    /// <summary>
    /// AI chat-client seam configuration (bound from "Radar:Ai"). A blank <see cref="AiWorkerOptions.Provider"/>
    /// (the default) means AI is DISABLED — no <c>IChatClient</c> is wired and no provider packages load — so the
    /// default configuration surfaces no AI. Only read when a provider is configured.
    /// </summary>
    public AiWorkerOptions Ai { get; init; } = new();

    /// <summary>
    /// Daily price-history reference acquisition configuration (bound from "Radar:Prices"). DISABLED by default
    /// (<see cref="PricesWorkerOptions.Enabled"/> is <c>false</c>): when disabled, nothing price-related is
    /// registered and the pipeline graph is byte-for-byte unchanged. Price is validation/reference data only —
    /// never evidence, never a signal, never a scoring input (AD-14).
    /// </summary>
    public PricesWorkerOptions Prices { get; init; } = new();

    /// <summary>
    /// Price-efficacy reporting configuration (bound from "Radar:Efficacy"). DISABLED by default
    /// (<see cref="EfficacyWorkerOptions.Enabled"/> is <c>false</c>): when disabled, nothing efficacy-related is
    /// registered and the pipeline graph is byte-for-byte unchanged. The efficacy layer is READ-ONLY over score
    /// history + price and emits a per-company score-vs-price SVG + CSV only — never evidence, never a signal,
    /// never a scoring input (AD-14 read side).
    /// </summary>
    public EfficacyWorkerOptions Efficacy { get; init; } = new();

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

    /// <summary>Root directory for the content-addressed effective-scoring-config file store (spec 91).</summary>
    public string ScoringConfigsDirectory { get; init; } = "data/scoring-configs";

    /// <summary>Root directory for the daily price-history reference store (AD-14). Only used when "Radar:Prices:Enabled" is true.</summary>
    public string PricesDirectory { get; init; } = "data/prices";

    /// <summary>Root directory for the per-company price-efficacy artifacts (AD-14 read side). Only used when "Radar:Efficacy:Enabled" is true.</summary>
    public string EfficacyDirectory { get; init; } = "data/efficacy";

    /// <summary>
    /// Root directory for the per-accession earnings-analysis-result cache (spec 107, AD-14 analogue). Only used
    /// when AI directional filing signals are enabled (a provider is configured); lets the directional source
    /// replay a previously-analyzed filing instead of re-fetching the same www.sec.gov exhibit every run.
    /// </summary>
    public string AnalyzedFilingCacheDirectory { get; init; } = "data/filings-cache";

    /// <summary>
    /// Root directory for the opt-in per-accession AI filing-read debug records (spec 115, diagnostic-only /
    /// AD-14 read-side). Only used when AI directional filing signals are enabled (a provider is configured)
    /// AND "Radar:Ai:Filings:PersistReadDebug" is true; never an evidence/signal/scoring/report input.
    /// </summary>
    public string FilingReadDebugDirectory { get; init; } = "data/ai-debug/filings";

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

    /// <summary>
    /// How many times the earnings-release reader re-issues an Archives request after an HTTP 429 before
    /// giving up (default 2). SEC 429s the burst of exhibit fetches this reader fires; a bounded retry stops a
    /// transient throttle starving the AI directional path (spec 105). Set to 0 to restore single-attempt.
    /// </summary>
    public int MaxRetriesOn429 { get; init; } = 2;

    /// <summary>
    /// Base cool-down before the first earnings-release 429 retry, in seconds; the reader doubles it per retry.
    /// Defaults to 2 (SEC recovers quickly, so a short base — unlike GDELT's 60s — suffices).
    /// </summary>
    public int RetryBackoffSeconds { get; init; } = 2;

    /// <summary>
    /// Minimum milliseconds between the earnings reader's successive www.sec.gov requests, paced via the injected
    /// TimeProvider (spec 107). Keeps the reader well under SEC's ~10 req/s fair-access limit and reduces the
    /// sustained footprint that gets the IP flagged. Defaults to 250; set to 0 to disable pacing.
    /// </summary>
    public int MinRequestIntervalMs { get; init; } = 250;

    /// <summary>
    /// Minimum milliseconds between ANY two SEC (*.sec.gov) requests across the WHOLE process. Every SEC client —
    /// the sec/secform4/sec13dg collectors AND the earnings-release reader — shares one global pacer
    /// (<c>SecRequestPacer</c>), so the AGGREGATE request rate of a run, not each client in isolation, stays under
    /// SEC's ~10 req/s per-IP fair-access ceiling. Without it an unpaced collector burst trips SEC's mitigation and
    /// blocks www.sec.gov, starving the AI earnings path. Defaults to 150 (~6.7 req/s); set to 0 to disable global
    /// pacing. Must not be negative. This is orthogonal to <see cref="MinRequestIntervalMs"/> (the earnings reader's
    /// own per-reader self-pacing) — the global pacer bounds the whole run's SEC traffic.
    /// </summary>
    public int GlobalMinIntervalMs { get; init; } = 150;

    /// <summary>
    /// Per-fetch timeout, in seconds, for each SEC request — measured from AFTER the global pacer grants the
    /// request its turn (the <c>SecRateLimitingHandler</c> owns it; the SEC clients' ambient HttpClient timeout is
    /// disabled). Because the clock starts post-pacing, pacing wait can never consume the fetch budget however deep
    /// the shared pacer queue grows as the watch universe scales up. Defaults to 100 (the historical HttpClient
    /// timeout default); set to 0 to disable the per-fetch timeout (fetch then bounded only by run cancellation).
    /// Must not be negative.
    /// </summary>
    public int GlobalFetchTimeoutSeconds { get; init; } = 100;
}

/// <summary>
/// SEC Form 4 (insider-transaction) collector configuration (bound from "Radar:SecForm4"). Surfaces the
/// required, compliant User-Agent and the per-company cap through to <c>SecForm4CollectorOptions</c>. Defaults
/// so the rss-only configuration works without any SecForm4 config.
/// </summary>
public sealed class SecForm4WorkerOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. "Radar Research example@example.com"). Required when the "secform4"
    /// collector is enabled — every SEC request 403s without it. Defaults to empty so the default rss-only
    /// configuration stays working without any SecForm4 config.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>
    /// Maximum most-recent Form 4 filings to fetch/parse per company per run. Defaults to 15 (Form 4s are
    /// numerous, so the cap keeps the per-run fetch bounded).
    /// </summary>
    public int MaxFilingsPerCompany { get; init; } = 15;
}

/// <summary>
/// SEC Schedule 13D/13G (beneficial-ownership) collector configuration (bound from "Radar:Sec13DG"). Surfaces
/// the required, compliant User-Agent and the per-company cap through to <c>Sec13DGCollectorOptions</c>.
/// Defaults so the rss-only configuration works without any Sec13DG config.
/// </summary>
public sealed class Sec13DGWorkerOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. "Radar Research example@example.com"). Required when the "sec13dg"
    /// collector is enabled — every SEC request 403s without it. Defaults to empty so the default rss-only
    /// configuration stays working without any Sec13DG config.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>
    /// Maximum most-recent 13D/13G filings to fetch/classify per company per run. Defaults to 20 (13D/13G are
    /// far less frequent than Form 4, but the cap keeps the per-run fetch bounded).
    /// </summary>
    public int MaxFilingsPerCompany { get; init; } = 20;
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
/// Google News RSS news-attention collector configuration (bound from "Radar:News"). Surfaces the per-company
/// cap, the English-only toggle, and the inter-request pacing delay through to <c>NewsCollectorOptions</c>.
/// Unlike GDELT, Google News RSS is NOT per-IP throttled, so only a small polite pace is needed. Defaults so
/// the rss-only configuration works without any News config.
/// </summary>
public sealed class NewsWorkerOptions
{
    /// <summary>Maximum surviving (relevance-filtered, deduped) articles to collect per company per run. Defaults to 25.</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict coverage to English/US. Defaults to true.</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>Pause between successive per-company requests, in seconds. Defaults to 1 (Google News RSS is not per-IP throttled).</summary>
    public int InterRequestDelaySeconds { get; init; } = 1;
}

/// <summary>
/// ATS job-board hiring collector configuration (bound from "Radar:Hiring"). Surfaces the metadata title-sample
/// bound through to <c>HiringCollectorOptions</c>. The Greenhouse/Lever endpoints need no User-Agent or key
/// (keyless access verified). Defaults so the rss-only configuration works without any Hiring config.
/// </summary>
public sealed class HiringWorkerOptions
{
    /// <summary>Maximum job titles carried in the evidence <c>sampleTitles</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleTitles { get; init; } = 5;
}

/// <summary>
/// PatentsView granted-patent activity collector configuration (bound from "Radar:Patents"; spec 127). Surfaces
/// the lookback window, the metadata title-sample bound, the API-key env-var NAME, and the request page size
/// through to <c>PatentCollectorOptions</c>. The PatentsView Search API requires a free API key, read at
/// RUNTIME from the env var named by <see cref="ApiKeyEnvVar"/> — the key VALUE is never committed here. The
/// collector is opt-in OFF (not in the default Collectors); the defaults let the rss-only configuration keep
/// working with no Patents config.
/// </summary>
public sealed class PatentWorkerOptions
{
    /// <summary>Recent-activity window length, in days (the query's grant-date floor is now minus this). Defaults to 180.</summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>Maximum patent titles carried in the evidence <c>sampleTitles</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleTitles { get; init; } = 5;

    /// <summary>The NAME of the environment variable holding the PatentsView API key (read at runtime; the key value is never committed). Defaults to "PATENTSVIEW_API_KEY".</summary>
    public string ApiKeyEnvVar { get; init; } = "PATENTSVIEW_API_KEY";

    /// <summary>Maximum patents requested on the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}

/// <summary>
/// FCC Equipment Authorization (EAS) collector configuration (bound from "Radar:Fcc"; spec 128). Surfaces the
/// lookback window, the metadata authorization-sample bound, and the request page size through to
/// <c>FccCollectorOptions</c>. The FCC OET EAS GenericSearch export needs no API key. The collector is opt-in
/// OFF (not in the default Collectors); the defaults let the rss-only configuration keep working with no Fcc
/// config.
/// </summary>
public sealed class FccWorkerOptions
{
    /// <summary>Recent-activity window length, in days (the query's grant-date floor is now minus this). Defaults to 180.</summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>Maximum authorizations carried in the evidence <c>sampleAuthorizations</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleAuthorizations { get; init; } = 5;

    /// <summary>Maximum authorization rows read from the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}

/// <summary>
/// AI chat-client seam configuration (bound from "Radar:Ai"). Surfaces the provider selection and model id plus the
/// nested <see cref="AiAnthropicWorkerOptions"/> / <see cref="AiOllamaWorkerOptions"/> / <see cref="AiOpenAiWorkerOptions"/>
/// config blocks through to <c>AiClientOptions</c>. A blank <see cref="Provider"/> (the default) means AI is DISABLED —
/// nothing is wired and no provider packages load — so the default rss-only configuration keeps working with no AI config.
/// </summary>
public sealed class AiWorkerOptions
{
    /// <summary>The AI provider: "anthropic" (hosted Claude), "ollama" (local, keyless), or "openai" (OpenAI-compatible host, e.g. DeepInfra). Blank by default = AI DISABLED.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>The model id (e.g. "claude-opus-4-8" for anthropic or an Ollama tag like "llama3.1"). Required when a provider is set. For "openai" this is the fallback when <see cref="AiOpenAiWorkerOptions.Model"/> is blank.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Anthropic (hosted) provider config. Only read when Provider is "anthropic".</summary>
    public AiAnthropicWorkerOptions Anthropic { get; init; } = new();

    /// <summary>Ollama (local, keyless) provider config. Only read when Provider is "ollama".</summary>
    public AiOllamaWorkerOptions Ollama { get; init; } = new();

    /// <summary>OpenAI-compatible (e.g. DeepInfra) provider config. Only read when Provider is "openai".</summary>
    public AiOpenAiWorkerOptions OpenAi { get; init; } = new();

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

    /// <summary>
    /// Per-run 429 circuit breaker for the directional filing source (spec 107): after this many CONSECUTIVE
    /// rate-limited (HTTP 429) earnings reads, the source stops attempting the remaining filings this run (the
    /// www.sec.gov host appears blocked). A success or a cache hit resets the count. Only read when a provider is
    /// configured. Defaults to 2; set to 0 to disable the breaker.
    /// </summary>
    public int MaxConsecutiveRateLimited { get; init; } = 2;

    /// <summary>
    /// Strength stamped on each emitted directional <c>GuidanceChange</c> signal. In-range [1,10] (fails fast at
    /// registration otherwise). It is a per-signal magnitude folded into the scoring fingerprint (spec 106), so
    /// tuning it re-stamps <c>ScoringConfigVersion</c> automatically. Only read when a provider is configured.
    /// Defaults to 8 (spec 112): a confident, full-text directional earnings read deliberately EXCEEDS the
    /// keyword extractor maximum of 6 so it can materially move the thesis; applied symmetrically to
    /// Improving→Positive and Deteriorating→Negative reads.
    /// </summary>
    public int Strength { get; init; } = 8;

    /// <summary>
    /// Novelty stamped on each emitted directional <c>GuidanceChange</c> signal. In-range [1,10] (fails fast at
    /// registration otherwise). It is a per-signal magnitude folded into the scoring fingerprint (spec 106), so
    /// tuning it re-stamps <c>ScoringConfigVersion</c> automatically. Only read when a provider is configured.
    /// Defaults to 6.
    /// </summary>
    public int Novelty { get; init; } = 6;

    /// <summary>AI filing-read diagnostics config (bound from "Radar:Ai:Filings"). Only read when a provider is configured.</summary>
    public AiFilingsWorkerOptions Filings { get; init; } = new();
}

/// <summary>
/// AI filing-read diagnostics configuration (bound from "Radar:Ai:Filings"). DISABLED by default:
/// <see cref="PersistReadDebug"/> false means no debug sink is registered, nothing is written, and the pipeline
/// graph is byte-for-byte unchanged. When enabled, every AI filing-read attempt — including no-signal and
/// empty-body outcomes — persists a bounded, advice-scrubbed diagnostic record (spec 115). Diagnostic-only:
/// never an evidence/signal/scoring/report input (AD-14 read-side) and never a fingerprint input.
/// </summary>
public sealed class AiFilingsWorkerOptions
{
    /// <summary>Whether to persist a diagnostic record of every AI filing-read attempt. DISABLED by default.</summary>
    public bool PersistReadDebug { get; init; }
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

/// <summary>
/// OpenAI-compatible (e.g. DeepInfra/Groq/Together) provider config (bound from "Radar:Ai:OpenAi"). Only used
/// when Provider is "openai". The API key is NEVER stored in config — <see cref="ApiKeyEnvVar"/> names the
/// environment variable the key is read from at wiring time (mirrors the SEC-User-Agent secret precedent).
/// </summary>
public sealed class AiOpenAiWorkerOptions
{
    /// <summary>The OpenAI-compatible endpoint base URL (e.g. https://api.deepinfra.com/v1/openai). Required when Provider is "openai"; no default (a blank BaseUrl is a config error).</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The model id at the OpenAI-compatible host (e.g. a DeepSeek/GLM/Qwen tag). Optional override; when blank, the top-level Radar:Ai:Model is used.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The NAME of the environment variable holding the API key (e.g. "DEEPINFRA_API_KEY"). The key VALUE is never committed and never logged — only this name. Required when Provider is "openai".</summary>
    public string ApiKeyEnvVar { get; init; } = string.Empty;
}

/// <summary>
/// Daily price-history reference acquisition configuration (bound from "Radar:Prices"). DISABLED by default: a
/// <see cref="Enabled"/> of <c>false</c> means nothing price-related is registered and the pipeline graph is
/// byte-for-byte unchanged. Price is validation/reference data only — never evidence, never a signal, never a
/// scoring input (AD-14); acquisition runs OUTSIDE the evidence → signal → score pipeline.
/// </summary>
public sealed class PricesWorkerOptions
{
    /// <summary>Whether to acquire daily price history for the watch-universe tickers. DISABLED by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>The daily-bar window as a Yahoo chart range token (1d/5d/1mo/3mo/6mo/1y/2y/5y/10y/ytd/max). Defaults to "1y".</summary>
    public string Range { get; init; } = "1y";

    /// <summary>Pause between successive per-ticker reads, in seconds. Defaults to 1 (a small polite pace). Must not be negative.</summary>
    public int InterRequestDelaySeconds { get; init; } = 1;
}

/// <summary>
/// Price-efficacy reporting configuration (bound from "Radar:Efficacy"). DISABLED by default: a
/// <see cref="Enabled"/> of <c>false</c> means nothing efficacy-related is registered and the pipeline graph is
/// byte-for-byte unchanged. The efficacy layer is READ-ONLY over score history + price (AD-14 read side): it
/// JOINs a company's persisted score snapshots to its daily price series and emits a per-company score-vs-price
/// SVG + CSV under <c>data/efficacy/</c>; it never writes back into evidence → signal → score and runs OUTSIDE
/// <c>IRadarPipeline</c>.
/// </summary>
public sealed class EfficacyWorkerOptions
{
    /// <summary>Whether to render the per-company price-efficacy SVG + CSV artifacts. DISABLED by default.</summary>
    public bool Enabled { get; init; }
}
