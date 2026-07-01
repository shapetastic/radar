namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// Options for the USASpending.gov government-contract collector. Unlike SEC EDGAR, the API needs no
/// User-Agent and no key. <see cref="AwardTypeCodes"/> selects one mutually-exclusive award-type group
/// (default the contracts group A/B/C/D — mixing groups is an API 400); <see cref="LookbackDays"/> sets
/// the recent-activity window; <see cref="MaxAwardsPerCompany"/> caps how many of the highest-value
/// matching awards each company contributes per run. Registration fails fast when any value would let the
/// collector run yet silently collect nothing.
/// </summary>
public sealed class UsaSpendingCollectorOptions
{
    /// <summary>
    /// The mutually-exclusive award-type group to query. Defaults to the contracts group
    /// <c>A/B/C/D</c> (BPA Call / Purchase Order / Delivery Order / Definitive Contract). Registration
    /// fails fast when this is null/empty. Do not mix groups — the API returns HTTP 400.
    /// </summary>
    public IReadOnlyList<string> AwardTypeCodes { get; init; } = ["A", "B", "C", "D"];

    /// <summary>Recent-activity window length, in days (the query's <c>start_date</c> is now minus this, clamped no earlier than 2007-10-01).</summary>
    public int LookbackDays { get; init; } = 365;

    /// <summary>Maximum highest-value matching awards to collect per company per run (the API sorts by Award Amount desc).</summary>
    public int MaxAwardsPerCompany { get; init; } = 25;
}
