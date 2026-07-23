namespace Radar.Infrastructure.Fda;

/// <summary>
/// Options for the openFDA device clearance/approval activity collector (spec 129). The openFDA 510(k)/PMA
/// device endpoints require NO API key for the low request volume this collector uses (an optional key only
/// raises rate limits), so — unlike the patents collector — there is no key plumbing at all.
/// <see cref="LookbackDays"/> sets the decision-date floor of the recent-activity window (device clearances
/// are lower-frequency than patents/press, so the default window is longer);
/// <see cref="MaxSampleClearances"/> caps the provenance/debug sample of submission numbers + device names
/// carried in evidence metadata (never in Title/RawText); <see cref="MaxPageSize"/> caps the single bounded
/// page the reader requests per endpoint (a count-based v1 needs no pagination). Registration fails fast when
/// any value would let the collector run yet silently collect nothing.
/// </summary>
public sealed class FdaCollectorOptions
{
    /// <summary>Recent-activity window length, in days (the query's decision-date floor is now minus this). Defaults to 365 (device clearances are lower-frequency, so a longer window avoids mostly-empty snapshots).</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum clearances carried in the evidence <c>sampleClearances</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleClearances { get; init; } = 5;

    /// <summary>Maximum clearances requested on the single bounded page per endpoint (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}
