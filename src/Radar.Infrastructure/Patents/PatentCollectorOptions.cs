namespace Radar.Infrastructure.Patents;

/// <summary>
/// Options for the PatentsView granted-patent activity collector (spec 127). The PatentsView Search API
/// requires a free API key, supplied at runtime via the environment variable NAMED by
/// <see cref="ApiKeyEnvVar"/> — the key VALUE is never committed to config, logged, or surfaced (same
/// posture as the SEC User-Agent / DEEPINFRA key). A blank/absent key degrades every patents feed to a
/// <c>MissingApiKey</c> failure (no HTTP call), never throws, and — because the collector is opt-in OFF —
/// never affects the baseline. <see cref="LookbackDays"/> sets the grant-date floor of the recent-activity
/// window; <see cref="MaxSampleTitles"/> caps the provenance/debug sample of patent titles carried in
/// evidence metadata (never in Title/RawText); <see cref="MaxPageSize"/> caps the single bounded page the
/// reader requests (a count-based v1 needs no pagination). Registration fails fast when any value would let
/// the collector run yet silently collect nothing.
/// </summary>
public sealed class PatentCollectorOptions
{
    /// <summary>Recent-activity window length, in days (the query's grant-date floor is now minus this). Defaults to 180.</summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>Maximum patent titles carried in the evidence <c>sampleTitles</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleTitles { get; init; } = 5;

    /// <summary>
    /// The NAME of the environment variable holding the PatentsView API key (read at runtime; the key value is
    /// never committed to config). Defaults to <c>PATENTSVIEW_API_KEY</c>. A blank/absent key degrades every
    /// patents feed to a <c>MissingApiKey</c> failure (no HTTP call).
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = "PATENTSVIEW_API_KEY";

    /// <summary>Maximum patents requested on the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}
