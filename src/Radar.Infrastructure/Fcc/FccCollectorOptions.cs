namespace Radar.Infrastructure.Fcc;

/// <summary>
/// Options for the FCC Equipment Authorization (EAS) collector (spec 128). The FCC OET EAS GenericSearch export
/// is a free public database needing NO API key (unlike the PatentsView collector) — so there is no
/// <c>ApiKeyEnvVar</c>. <see cref="LookbackDays"/> sets the grant-date floor of the recent-activity window;
/// <see cref="MaxSampleAuthorizations"/> caps the provenance/debug sample of authorizations carried in evidence
/// metadata (never in Title/RawText); <see cref="MaxPageSize"/> caps the single bounded page the reader parses
/// (a count-based v1 needs no pagination). Registration fails fast when any value would let the collector run
/// yet silently collect nothing.
/// </summary>
public sealed class FccCollectorOptions
{
    /// <summary>Recent-activity window length, in days (the query's grant-date floor is now minus this). Defaults to 180.</summary>
    public int LookbackDays { get; init; } = 180;

    /// <summary>Maximum authorizations carried in the evidence <c>sampleAuthorizations</c> metadata (provenance/debug only — never in Title/RawText). Defaults to 5.</summary>
    public int MaxSampleAuthorizations { get; init; } = 5;

    /// <summary>Maximum authorization rows read from the single bounded page (the count is what matters, not full enumeration). Defaults to 100.</summary>
    public int MaxPageSize { get; init; } = 100;
}
