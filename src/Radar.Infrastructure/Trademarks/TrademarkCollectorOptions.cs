namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// Options for the USPTO trademark-activity collector (spec 130). The reachable USPTO trademark search route
/// (the Open Data Portal <c>trademark/applications/search</c> endpoint) requires a free API key — the spike
/// confirmed a keyless call returns <c>Missing Authentication Token</c> — supplied at runtime via the
/// environment variable NAMED by <see cref="ApiKeyEnvVar"/>. The key VALUE is never committed to config,
/// logged, or surfaced (same posture as the SEC User-Agent / DEEPINFRA key / spec-127 patent key). A
/// blank/absent key degrades every trademark feed to a <c>MissingApiKey</c> failure (no HTTP call), never
/// throws, and — because the collector is opt-in OFF — never affects the baseline.
/// <see cref="LookbackDays"/> sets the filing-date floor of the recent-activity window (trademark filings are
/// lower-frequency than press, so the default window is long); <see cref="MaxSampleMarks"/> caps the
/// provenance/debug sample of serial numbers + mark texts carried in evidence metadata (never in
/// Title/RawText); <see cref="MaxPageSize"/> caps the single bounded page the reader requests (a count-based
/// v1 needs no pagination). Registration fails fast when any value would let the collector run yet silently
/// collect nothing.
/// </summary>
public sealed class TrademarkCollectorOptions
{
    /// <summary>Recent-activity window length, in days (the query's filing-date floor is now minus this). Defaults to 365 (trademark filings are lower-frequency, so a longer window avoids mostly-empty snapshots).</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum marks carried in the evidence <c>sampleMarks</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleMarks { get; init; } = 5;

    /// <summary>Maximum trademark applications requested on the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;

    /// <summary>
    /// The NAME of the environment variable holding the USPTO API key (read at runtime; the key value is never
    /// committed to config). Defaults to <c>USPTO_API_KEY</c>. A blank/absent key degrades every trademark
    /// feed to a <c>MissingApiKey</c> failure (no HTTP call).
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = "USPTO_API_KEY";
}
