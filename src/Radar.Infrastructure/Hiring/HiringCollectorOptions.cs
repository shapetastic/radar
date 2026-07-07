namespace Radar.Infrastructure.Hiring;

/// <summary>
/// Options for the ATS job-board hiring collector. Unlike SEC EDGAR, the Greenhouse/Lever endpoints need
/// no User-Agent and no key (verified by the 2026-07-06 reachability spike); if a platform later demands
/// one, a polite generic UA can be added on the named clients. <see cref="MaxSampleTitles"/> bounds the
/// provenance/debug title sample written to evidence METADATA only (raw titles never enter Title/RawText —
/// keyword contamination). Registration fails fast on a negative value (nonsensical configuration).
/// </summary>
public sealed class HiringCollectorOptions
{
    /// <summary>Maximum job titles carried in the evidence <c>sampleTitles</c> metadata (provenance/debug only). Defaults to 5.</summary>
    public int MaxSampleTitles { get; init; } = 5;
}
