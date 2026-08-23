namespace Radar.Worker;

/// <summary>Host-level configuration for a Radar run (bound from the "Radar" config section).</summary>
public sealed class RadarWorkerOptions
{
    /// <summary>
    /// Which pass this process runs (spec 144), case-insensitively: <c>"full"</c> (the default — collect AND
    /// score in one pass, exactly as before this key existed), <c>"collect"</c> (stages 1–5 only: writes the
    /// durable evidence + signal stores, no scoring, no report), <c>"score"</c> (stage 6 + optionally 7 over
    /// whatever has accrued — no collector is constructed or invoked and no AI read happens), or
    /// <c>"replay"</c> (the read-only historical as-of replay of spec 139).
    /// <para>
    /// Reconciled with the pre-existing <see cref="ReplayWorkerOptions.Enabled"/> switch:
    /// <c>Radar:Replay:Enabled</c> alone still selects a replay (unchanged), while combining it with
    /// <c>"collect"</c>/<c>"score"</c> fails fast naming both keys. An unknown value fails fast listing the
    /// valid ones.
    /// </para>
    /// </summary>
    public string RunMode { get; init; } = "full";

    /// <summary>
    /// Standalone <c>score</c>-pass configuration (bound from "Radar:Score"; spec 144). Only read when
    /// <see cref="RunMode"/> is <c>"score"</c>; the defaults score at the current instant.
    /// </summary>
    public ScoreWorkerOptions Score { get; init; } = new();

    /// <summary>
    /// OPTIONAL ticker filter restricting a <b>collection</b> pass to a subset of the watch universe (spec
    /// 161). Empty (the default) means NO filter — the whole universe, byte-identical to a deployment that
    /// never heard of this key; the off-switch is absence.
    /// <para>
    /// <b>COLLECT-ONLY, by guard.</b> A non-empty list with <see cref="RunMode"/> anything other than
    /// <c>"collect"</c> fails fast at startup: a filtered SCORING run would overwrite the date-keyed weekly
    /// report with a one-company report and mint sparse as-of dates into the strategy-vs-price efficacy join.
    /// Filter the gathering, never the measuring — scoring stays whole-universe on the next full/score run.
    /// </para>
    /// <para>
    /// Tokens are matched against the seed's tickers case-insensitively and whitespace-trimmed, and duplicates
    /// collapse. A blank token, or one matching no seed company, FAILS FAST naming the token — a typo that
    /// silently filtered to nothing would be a run that "worked" and collected nothing.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Companies { get; init; } = [];

    /// <summary>Which evidence collectors to run, additively. Each kind is one of: "rss", "localfile", "sec", "secform4", "sec13dg", "usaspending", "news", "newssearch", "hiringats", "patents", "fda", "trademarks". Not read in <c>"score"</c> <see cref="RunMode"/> — that pass registers no collector at all.</summary>
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
    /// USPTO ODP granted-patent activity collector configuration (bound from "Radar:Patents"). Only read
    /// when the "patents" collector is enabled (opt-in, OFF by default — it is not in the default Collectors);
    /// the defaults let the rss-only configuration keep working with no Patents config. The API key VALUE is
    /// never here — it is read at runtime from the env var NAMED by <see cref="PatentWorkerOptions.ApiKeyEnvVar"/>.
    /// </summary>
    public PatentWorkerOptions Patents { get; init; } = new();

    /// <summary>
    /// openFDA device clearance/approval collector configuration (bound from "Radar:Fda"; spec 129). Only read
    /// when the "fda" collector is enabled (opt-in, OFF by default — it is not in the default Collectors); the
    /// defaults let the rss-only configuration keep working with no Fda config. openFDA needs no API key.
    /// </summary>
    public FdaWorkerOptions Fda { get; init; } = new();

    /// <summary>
    /// USPTO trademark-activity collector configuration (bound from "Radar:Trademarks"; spec 130). Only read
    /// when the "trademarks" collector is enabled (opt-in, OFF by default — it is not in the default Collectors);
    /// the defaults let the rss-only configuration keep working with no Trademarks config. The API key VALUE is
    /// never here — it is read at runtime from the env var NAMED by <see cref="TrademarkWorkerOptions.ApiKeyEnvVar"/>.
    /// </summary>
    public TrademarkWorkerOptions Trademarks { get; init; } = new();

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

    /// <summary>
    /// Historical as-of replay configuration (bound from "Radar:Replay"; spec 139). DISABLED by default
    /// (<see cref="ReplayWorkerOptions.Enabled"/> is <c>false</c>): when disabled, nothing replay-related is
    /// registered and the pipeline graph is byte-for-byte unchanged. When ENABLED the run becomes a read-only
    /// OFFLINE replay INSTEAD of a pipeline run — it scores stored signals at past instants and writes only to
    /// the replay root; it collects nothing, mutates no live store, and produces no report.
    /// </summary>
    public ReplayWorkerOptions Replay { get; init; } = new();

    /// <summary>
    /// Point-in-time news observation archive + safe article-fetch configuration (bound from
    /// "Radar:NewsResearch"; spec 177). Fail-closed: the section is validated against a strict key
    /// allowlist at startup, capture defaults ON (it is pure observation — no AI, no price, no scoring
    /// input), and the article fetch defaults OFF with an empty allowlist. None of these are scoring
    /// weights and none are hashed into any fingerprint.
    /// </summary>
    public NewsResearchWorkerOptions NewsResearch { get; init; } = new();

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
    /// Root directory for the read-only audit artifacts (spec 172). Only used when
    /// "Radar:Efficacy:DenominatorAudit:Enabled" is true (inside the "Radar:Efficacy:Enabled" gate).
    /// Deliberately a NEW root, separate from <see cref="EfficacyDirectory"/>, so no existing efficacy
    /// artifact can be overwritten; it is created only when the audit actually writes, so the default-off
    /// configuration leaves no directory behind.
    /// </summary>
    public string AuditsDirectory { get; init; } = "data/audits";

    /// <summary>
    /// Root directory for the historical as-of replay output (spec 139). Only used when
    /// "Radar:Replay:Enabled" is true. Deliberately its OWN root, NOT a subdirectory of
    /// <see cref="ScoresDirectory"/>: the forward efficacy series is accrued history and a replay is a
    /// hypothesis, so "replay never writes into the live scores directory" is structural rather than a rule
    /// someone must remember.
    /// </summary>
    public string ReplayDirectory { get; init; } = "data/replays";

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
    /// Path to the committed operating-calls file (spec 184 §2) — the ONLY runtime input to the
    /// operating-call layer. Read only in a multi-strategy composition; its absence is the honest "no call
    /// is declared" state (stated in the rendered report), while an invalid file fails startup naming the
    /// file and the violated rule.
    /// </summary>
    public string OperatingCallsFilePath { get; init; } = "data/strategy-operating-calls.json";

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
/// Standalone <c>score</c>-pass configuration (bound from "Radar:Score"; spec 144). Only read when
/// <see cref="RadarWorkerOptions.RunMode"/> is <c>"score"</c>.
/// </summary>
public sealed class ScoreWorkerOptions
{
    /// <summary>
    /// The as-of instant the score pass scores at, as a UTC date/time (e.g. "2026-07-27T09:00:00Z"). BLANK
    /// (the default) means the current instant, which is the normal case.
    /// <para>
    /// A value in the PAST is rejected at run time: a standalone score pass writes the LIVE series — the
    /// record of what Radar thinks now — and back-dating it would rewrite accrued history with a hypothesis.
    /// Scoring a historical instant is a REPLAY (<c>Radar:Replay:*</c>, spec 139), which writes only under
    /// <c>Radar:ReplayDirectory</c>. Parsed at startup with the same UTC treatment the replay bounds get, so
    /// an unparseable value fails fast rather than silently scoring "now".
    /// </para>
    /// </summary>
    public string AsOfUtc { get; init; } = string.Empty;
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
/// USPTO ODP granted-patent activity collector configuration (bound from "Radar:Patents"; spec 127, repointed
/// to the USPTO Open Data Portal PFW Search API in spec 131). Surfaces the ODP host base URL, the lookback
/// window, the metadata title-sample bound, the API-key env-var NAME, and the request page size through to
/// <c>PatentCollectorOptions</c>. The ODP PFW Search API requires an API key, read at RUNTIME from the env var
/// named by <see cref="ApiKeyEnvVar"/> — the key VALUE is never committed here. The collector is opt-in OFF
/// (not in the default Collectors); the defaults let the rss-only configuration keep working with no Patents
/// config.
/// </summary>
public sealed class PatentWorkerOptions
{
    /// <summary>The USPTO ODP host base URL the reader POSTs the PFW Search request to (the search path is fixed). Defaults to "https://api.uspto.gov".</summary>
    public string BaseUrl { get; init; } = "https://api.uspto.gov";

    /// <summary>Recent-activity window length, in days (the query's grant-date floor is now minus this). Defaults to 180.</summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>Maximum patent titles carried in the evidence <c>sampleTitles</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleTitles { get; init; } = 5;

    /// <summary>The NAME of the environment variable holding the USPTO ODP API key (read at runtime; the key value is never committed). Defaults to "PATENTSVIEW_API_KEY" (kept for back-compat; the value is now an ODP key).</summary>
    public string ApiKeyEnvVar { get; init; } = "PATENTSVIEW_API_KEY";

    /// <summary>Maximum patents requested on the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}

/// <summary>
/// openFDA device clearance/approval collector configuration (bound from "Radar:Fda"; spec 129). Surfaces the
/// lookback window, the metadata clearance-sample bound, and the request page size through to
/// <c>FdaCollectorOptions</c>. The openFDA 510(k)/PMA endpoints need no API key. The collector is opt-in OFF
/// (not in the default Collectors); the defaults let the rss-only configuration keep working with no Fda config.
/// </summary>
public sealed class FdaWorkerOptions
{
    /// <summary>Recent-activity window length, in days (the query's decision-date floor is now minus this). Defaults to 365 (device clearances are lower-frequency).</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum clearances carried in the evidence <c>sampleClearances</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleClearances { get; init; } = 5;

    /// <summary>Maximum clearances requested on the single bounded page per endpoint (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}

/// <summary>
/// USPTO trademark-activity collector configuration (bound from "Radar:Trademarks"; spec 130). Surfaces the
/// lookback window, the metadata mark-sample bound, the request page size, and the API-key env-var NAME through
/// to <c>TrademarkCollectorOptions</c>. The reachable USPTO trademark search route requires a free API key, read
/// at RUNTIME from the env var named by <see cref="ApiKeyEnvVar"/> — the key VALUE is never committed here. The
/// collector is opt-in OFF (not in the default Collectors); the defaults let the rss-only configuration keep
/// working with no Trademarks config.
/// </summary>
public sealed class TrademarkWorkerOptions
{
    /// <summary>Recent-activity window length, in days (the query's filing-date floor is now minus this). Defaults to 365 (trademark filings are lower-frequency).</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum marks carried in the evidence <c>sampleMarks</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleMarks { get; init; } = 5;

    /// <summary>Maximum trademark applications requested on the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;

    /// <summary>The NAME of the environment variable holding the USPTO API key (read at runtime; the key value is never committed). Defaults to "USPTO_API_KEY".</summary>
    public string ApiKeyEnvVar { get; init; } = "USPTO_API_KEY";
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
    /// Comparability confidence cap for directional filing signals (spec 160): when the deterministic
    /// comparability scan finds cap-triggering markers in the release body (the release declares its own
    /// comparability breaks — "litigation settlement", "discontinued operations", …), the persisted confidence
    /// of the AI read is <c>min(readConfidence, cap)</c>, applied BEFORE the <see cref="MinConfidence"/> gate.
    /// In [0,1]; 1.0 is the exact off-switch (byte-identical to pre-spec-160 behaviour). A scoring-affecting
    /// magnitude like <see cref="MinConfidence"/>/<see cref="Strength"/>/<see cref="Novelty"/> — deliberately
    /// HERE beside them, never under the diagnostics-only <see cref="Filings"/> block — folded into the scoring
    /// fingerprint by value, so tuning it re-stamps <c>ScoringConfigVersion</c> automatically. Only read when a
    /// provider is configured. Defaults to 0.65 (keeps a capped read above the default 0.6 gate: dampen, don't
    /// veto — while cutting its scoring weight ~28%).
    /// </summary>
    public decimal ComparabilityConfidenceCap { get; init; } = 0.65m;

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

    /// <summary>
    /// Strategy-vs-price comparison configuration (bound from "Radar:Efficacy:Comparison"; spec 140). Only
    /// consulted when <see cref="Enabled"/> — the comparison is part of the efficacy read side.
    /// </summary>
    public StrategyComparisonWorkerOptions Comparison { get; init; } = new();

    /// <summary>
    /// AD-16 attention-arrival screen configuration (bound from "Radar:Efficacy:AttentionArrival"; spec 169).
    /// Only consulted when <see cref="Enabled"/>, mirroring <see cref="Comparison"/>.
    /// </summary>
    public AttentionArrivalWorkerOptions AttentionArrival { get; init; } = new();

    /// <summary>
    /// Score-move vs evidence-denominator audit configuration (bound from
    /// "Radar:Efficacy:DenominatorAudit"; spec 172). Only consulted when <see cref="Enabled"/> — and, unlike
    /// <see cref="Comparison"/> / <see cref="AttentionArrival"/>, DEFAULT OFF even inside the efficacy gate.
    /// </summary>
    public DenominatorAuditWorkerOptions DenominatorAudit { get; init; } = new();
}

/// <summary>
/// Score-move vs evidence-denominator audit configuration (bound from "Radar:Efficacy:DenominatorAudit";
/// spec 172). DISABLED by default even <b>within</b> the already-opt-in <c>Radar:Efficacy</c> gate —
/// deliberately unlike the comparison and the attention screen, because this is a one-shot diagnostic, not a
/// nightly artifact, and the nightly baseline run is unattended. When disabled nothing audit-related is
/// registered, no file is written and no directory is created.
/// <para>
/// The audit is READ-ONLY over persisted score history (snapshots + their stored evidence links): it measures
/// whether score MOVES concentrate where the directional evidence base is thin, changes no score, reads no
/// price (AD-14), and writes only <c>data/audits/score-move-denominator.{csv,md}</c> — a NEW directory, so no
/// existing efficacy artifact can be overwritten. A replay run skips it entirely (a replay replaces the
/// pipeline run and never reaches the efficacy step).
/// </para>
/// </summary>
public sealed class DenominatorAuditWorkerOptions
{
    /// <summary>Whether to build and write the score-move denominator audit when efficacy reporting is enabled. DISABLED by default.</summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// AD-16 attention-arrival screen configuration (bound from "Radar:Efficacy:AttentionArrival"; spec 169).
/// ENABLED by default <b>within</b> the already-opt-in <c>Radar:Efficacy</c> gate, mirroring
/// <see cref="StrategyComparisonWorkerOptions"/>: with too little history it writes an honest <c>Pending</c>
/// artifact rather than an error, and it never touches an existing artifact.
/// <para>
/// Note what is deliberately NOT here: the horizon, the minimum company/date counts, the failure threshold and
/// the first eligible date. Those are AD-16 PRECOMMITMENTS and live as code constants in
/// <c>AttentionArrivalScreen</c> — a declared threshold an operator can tune between runs is not declared at
/// all.
/// </para>
/// </summary>
public sealed class AttentionArrivalWorkerOptions
{
    /// <summary>Whether to evaluate and write the attention-arrival screen when efficacy reporting is enabled. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Directory holding the committed cohort declarations AD-16's 2026-07-31 amendment reads (the evaluator
    /// reads the file, never git history). Defaults to <c>docs/cohorts</c>. A missing directory suppresses the
    /// primary status rather than silently including every company.
    /// </summary>
    public string CohortsDirectory { get; init; } = "docs/cohorts";
}

/// <summary>
/// Strategy-vs-price comparison configuration (bound from "Radar:Efficacy:Comparison"; spec 140). ENABLED by
/// default <b>within</b> the already-opt-in <c>Radar:Efficacy</c> gate, because with too little history it is a
/// no-op that writes an honest "nothing could be ranked" leaderboard rather than an error, and it never touches
/// an existing artifact.
/// <para>
/// It ranks strategies by how closely their scores tracked SUBSEQUENT price movement, with a chronological
/// hold-out: the ranking is computed in-sample and the headline number is out-of-sample. Price is
/// validation-only and read strictly downstream of scoring (AD-14); nothing here feeds a score.
/// </para>
/// </summary>
public sealed class StrategyComparisonWorkerOptions
{
    /// <summary>Whether to emit the strategy leaderboard when efficacy reporting is enabled. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The forward horizon <c>h</c> in calendar days: a score at D is judged only against price over
    /// <c>(D, D+h]</c>. Defaults to 21 (≈ one trading month). Must be at least 1.
    /// </summary>
    public int ForwardHorizonDays { get; init; } = 21;

    /// <summary>
    /// The share of DISTINCT as-of dates — taken from the chronologically latest end — held out of ranking and
    /// used for the headline number. Defaults to 0.30. Must be strictly between 0 and 1.
    /// </summary>
    public double HoldOutFraction { get; init; } = 0.30;

    /// <summary>
    /// The minimum usable observations a strategy needs in EACH window to be ranked; below it the strategy is
    /// dropped and named in the output. Defaults to 20. Must be at least 4 (the Fisher-z interval's floor).
    /// </summary>
    public int MinimumObservations { get; init; } = 20;

    /// <summary>
    /// How many CALENDAR days short of <c>D+h</c> the last bar in the forward window may fall and still count as
    /// covering the horizon; an observation that falls further short is a PARTIAL window, excluded from the
    /// correlation instead of being reported as a full-horizon return (spec 152). Defaults to 4. Must be at
    /// least 0 and strictly less than <see cref="ForwardHorizonDays"/>.
    /// <para>
    /// The default is measured over <c>data/prices/</c> as of 2026-07-27 (43 tickers, 11,153 bars): the maximum
    /// gap between consecutive bars is 4 calendar days and the maximum shortfall over the 15,334
    /// genuinely-complete 21-day windows is 3 days, so 4 = that maximum plus one day of headroom for an
    /// unscheduled closure and discards 0% of those complete windows (a tolerance of 1 would discard 16.3%).
    /// </para>
    /// </summary>
    public int ExitToleranceDays { get; init; } = 4;

    /// <summary>
    /// Optional: compare a spec-139 REPLAY run's per-strategy series (the label under
    /// <c>Radar:ReplayDirectory</c>) instead of the live forward series. Blank (the default) reads the live
    /// forward series. A replay run replaces the pipeline run and never renders efficacy, so using this means
    /// running the replay first and pointing a later pass at its label.
    /// </summary>
    public string ReplayLabel { get; init; } = string.Empty;

    /// <summary>
    /// The PREDECLARED primary composite of the spec-155 paired comparison. Blank (the default) means no
    /// primary is predeclared: when <c>baseline-*</c> strategies exist the paired artifact is still written —
    /// pairing the pipeline's primary strategy, honestly labelled exploratory and naming the missing
    /// predeclaration — but the AD-15 gate can never pass, because only an arm named primary BEFORE its
    /// outcomes exist may use it.
    /// </summary>
    public string PairedPrimaryStrategy { get; init; } = string.Empty;

    /// <summary>
    /// The spec-155 claim boundary as a whole UTC calendar day (e.g. "2026-09-29"): only as-of dates at or
    /// after it enter the paired claim interval; earlier dates are development data. Blank (the default)
    /// means NO boundary is precommitted — the paired result is <c>NoPrecommittedEvaluationBoundary</c> and
    /// exploratory.
    /// <para>
    /// <b>IMMUTABLE BY CONVENTION</b> (spec 141's rule, applied to a claim boundary): record it BEFORE its
    /// outcomes exist and never move it afterwards — moving it invalidates the whole claim family, and
    /// deriving it from observed deltas is the unfalsifiability failure AD-16's pre-commitment clause names.
    /// Neither the evaluator nor this config may infer a boundary; absent means no claim.
    /// </para>
    /// </summary>
    public string PairedFirstEligibleAsOfUtc { get; init; } = string.Empty;

    /// <summary>
    /// The minimum joint companies a candidate date needs for its per-date cross-sectional rhos to count in
    /// the paired comparison; a thinner date is dropped and named (<c>too-few-companies</c>). Defaults to 10
    /// — a claim needs a real cross-section, and the mathematical floor of 2 is a validity bound, not a
    /// sensible default. Must be at least 2.
    /// </summary>
    public int PairedMinimumCompaniesPerDate { get; init; } = 10;
}

/// <summary>
/// Point-in-time news observation archive configuration (bound from "Radar:NewsResearch"; spec 177). The
/// archive records what news text/provenance Radar actually observed and when — it is never evidence, never
/// a signal, never a scoring/fingerprint input, and nothing in the evidence → signal → score path reads it.
/// The composition root validates this whole section against a strict key allowlist (specs 149/174
/// precedent), so an unknown/typo'd key fails startup instead of silently doing nothing.
/// </summary>
public sealed class NewsResearchWorkerOptions
{
    /// <summary>
    /// Whether the collection pass archives each surviving Google News RSS article as an immutable
    /// point-in-time observation. Defaults to true — capture is pure observation, independent of AI.
    /// Score-mode passes never register the archive regardless (it is a collection-side concern).
    /// </summary>
    public bool CaptureRss { get; init; } = true;

    /// <summary>Root directory of the observation archive. run-radar.ps1 supplies this beneath its output root.</summary>
    public string ObservationDirectory { get; init; } = "data/news-observations";

    /// <summary>The safe publisher-content fetch seam (spec 177 §6). Shipped DISABLED with an empty allowlist.</summary>
    public NewsArticleFetchWorkerOptions ArticleFetch { get; init; } = new();

    /// <summary>The explicit one-shot migration (spec 177 §7). Never part of a default run.</summary>
    public NewsObservationMigrationWorkerOptions Migration { get; init; } = new();

    /// <summary>The in-process news-risk shadow read + frozen-assessment evaluator (spec 179). DISABLED by default in code; the live baseline profile enables it.</summary>
    public NewsRiskShadowWorkerOptions Shadow { get; init; } = new();
}

/// <summary>
/// News-risk shadow configuration (bound from "Radar:NewsResearch:Shadow"; spec 179 §11). Every limit is a
/// cost/safety control — recorded on each persisted assessment and hashed into NO scoring fingerprint. The
/// composition root validates the section against a strict key allowlist and fails startup on an invalid
/// limit; registration happens ONLY for an unfiltered full-mode run with at least one resolvable reader
/// (ambient <c>Radar:Ai</c>, or a configured <see cref="Readers"/> entry).
/// </summary>
public sealed class NewsRiskShadowWorkerOptions
{
    /// <summary>Whether the shadow read runs after each unfiltered full pipeline run. DISABLED by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>News-risk output root (live artifacts, durable assessments, evaluator output). run-radar.ps1 supplies this beneath its output root.</summary>
    public string OutputDirectory { get; init; } = "data/news-risk";

    /// <summary>Observation lookback window in days: input articles come from (D − LookbackDays, D]. Default 30; must be positive.</summary>
    public int LookbackDays { get; init; } = 30;

    /// <summary>Cost budget on selected companies per run (traversal order, spec 179 §3). Default 30; must be positive.</summary>
    public int MaxCompaniesPerRun { get; init; } = 30;

    /// <summary>Cap on supplied articles per company. Default 12; must be positive.</summary>
    public int MaxArticlesPerCompany { get; init; } = 12;

    /// <summary>Cap on articles that may carry a fetched publisher body. Default 3; must be non-negative.</summary>
    public int MaxFetchedArticlesPerCompany { get; init; } = 3;

    /// <summary>
    /// Optional analyzer reader list (spec 179 §5). Omitted/empty ⇒ exactly one reader over the ambient
    /// <c>Radar:Ai</c> provider/model — byte-identical single-reader behaviour requiring no new config.
    /// </summary>
    public IReadOnlyList<NewsRiskReaderWorkerOptions> Readers { get; init; } = [];

    /// <summary>
    /// Path of the committed known-development-example declarations (spec 179 §8). Read DIRECTLY by the
    /// evaluator; run-radar.ps1 supplies the absolute repo path (the relative default would resolve against
    /// the Worker's working directory, mirroring the AttentionArrival cohorts precedent).
    /// </summary>
    public string DevelopmentExamplesPath { get; init; } = "docs/cohorts/news-risk-development.json";
}

/// <summary>
/// One configured news-risk reader (bound from "Radar:NewsResearch:Shadow:Readers:{i}"; spec 179 §5): a
/// unique display/provenance <see cref="Name"/> plus the SAME provider/model/settings shape as
/// <c>Radar:Ai</c> (validated through the same rules). The reader name is provenance display only — cohort
/// identity is provider + exact model id + prompt/schema version, so renaming a reader forks no cohort.
/// </summary>
public sealed class NewsRiskReaderWorkerOptions
{
    /// <summary>The display/provenance label, unique case-insensitively across the reader set. Required.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The AI provider ("anthropic" / "ollama" / "openai"), same vocabulary as Radar:Ai:Provider. Required.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>The model id; for "openai" this is the fallback when <see cref="OpenAi"/>.Model is blank — the same rule as Radar:Ai.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Anthropic provider config. Only read when Provider is "anthropic".</summary>
    public AiAnthropicWorkerOptions Anthropic { get; init; } = new();

    /// <summary>Ollama provider config. Only read when Provider is "ollama".</summary>
    public AiOllamaWorkerOptions Ollama { get; init; } = new();

    /// <summary>OpenAI-compatible provider config. Only read when Provider is "openai". The key is resolved from the env var NAMED here, never committed.</summary>
    public AiOpenAiWorkerOptions OpenAi { get; init; } = new();
}

/// <summary>
/// Safe article-fetch configuration (bound from "Radar:NewsResearch:ArticleFetch"; spec 177 §6). When
/// <see cref="Enabled"/> is false (the shipped default) NO reader is registered — the graph carries no
/// publisher-fetch capability at all. Enabling REQUIRES a non-empty <see cref="AllowedDomains"/> allowlist
/// (the operator's explicit retrieval/storage permission) and a contact-bearing <see cref="UserAgent"/>;
/// both fail startup otherwise.
/// </summary>
public sealed class NewsArticleFetchWorkerOptions
{
    /// <summary>Whether the safe content reader is registered at all. DISABLED by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>Exact/suffix domain allowlist (e.g. "example.com" also matches "news.example.com"). Empty by default.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];

    /// <summary>The contact-bearing User-Agent sent on every request (name + reachable email). Required when enabled.</summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Per-attempt deadline, in seconds, covering the whole redirect chain. Default 10; must be positive.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Response-body byte bound. Default 2 MiB; must be positive.</summary>
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>
/// One-shot news observation migration configuration (bound from "Radar:NewsResearch:Migration"; spec 177
/// §7). When <see cref="Enabled"/> the run becomes the migration INSTEAD of a pipeline run (like a replay):
/// accrued raw NewsArticle evidence is copied into honest <c>LegacyHeadlineOnly</c> observations
/// (idempotent — a second run writes nothing new), and with <see cref="RetrospectiveFetch"/> the saved URLs
/// are additionally re-visited through the safe reader as visibly retrospective records that are never
/// backdated. Both are explicit opt-ins, never part of the default run.
/// </summary>
public sealed class NewsObservationMigrationWorkerOptions
{
    /// <summary>Whether this run IS the one-shot migration. DISABLED by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether the migration additionally re-fetches saved landing URLs through the §6 reader (requires
    /// <see cref="NewsArticleFetchWorkerOptions.Enabled"/> — fails startup otherwise). DISABLED by default.
    /// </summary>
    public bool RetrospectiveFetch { get; init; }
}

/// <summary>
/// Historical as-of replay configuration (bound from "Radar:Replay"; spec 139). DISABLED by default: a
/// <see cref="Enabled"/> of <c>false</c> means nothing replay-related is registered, Worker's optional
/// <c>IReplayRunner?</c> stays null, and the graph is byte-for-byte unchanged.
/// <para>
/// When enabled the run is a read-only OFFLINE replay: for each as-of instant in the
/// <see cref="From"/>/<see cref="To"/>/<see cref="Step"/> series, the configured strategies re-score the
/// already-stored signals through the SAME scoring seam the live pipeline uses (spec 136's
/// <c>CreatedAtUtc &lt;= windowEndUtc</c> predicate is what makes that honest), and the resulting snapshots
/// go ONLY to <c>Radar:ReplayDirectory</c>. It collects nothing, mutates no live store, reads no price
/// (AD-14), and produces no report — it replaces the pipeline run rather than adding to it.
/// </para>
/// </summary>
public sealed class ReplayWorkerOptions
{
    /// <summary>Whether this run is a historical as-of replay instead of a pipeline run. DISABLED by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// First as-of instant, as a UTC date/time (e.g. "2026-05-01" or "2026-05-01T00:00:00Z"). REQUIRED when
    /// <see cref="Enabled"/> — there is no sensible default start for "what would this strategy have said?".
    /// </summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Upper bound of the as-of series, as a UTC date/time. REQUIRED when <see cref="Enabled"/>. It is
    /// included only when it lands exactly on a <see cref="Step"/> boundary — a trailing partial step is never
    /// rounded up into a fabricated as-of point.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Spacing between successive as-of instants: a "{n}d" / "{n}h" / "{n}m" / "{n}s" token, or any plain
    /// TimeSpan string. Defaults to "1d" (one snapshot per day, matching the forward daily cadence).
    /// </summary>
    public string Step { get; init; } = "1d";

    /// <summary>
    /// The replay run's label — the directory segment its output lands under, and how one replay is told
    /// apart from another. Optional: when blank, a deterministic label is derived from the series itself
    /// ("{from:yyyyMMdd}-{to:yyyyMMdd}-{step}"), so re-running the same range overwrites the same output
    /// rather than silently accumulating near-duplicate runs.
    /// </summary>
    public string Label { get; init; } = string.Empty;
}
