namespace Radar.Infrastructure.Sec;

/// <summary>
/// Options for the SEC Schedule 13D/13G (beneficial-ownership) collector. <see cref="UserAgent"/> is
/// <b>required</b>: SEC returns HTTP 403 for any request without a compliant declared UA, so a missing/blank
/// value is a fail-fast configuration error (validated at registration). <see cref="MaxFilingsPerCompany"/>
/// caps how many of the most-recent 13D/13G filings each company contributes per run — 13D/13G are far less
/// frequent than Form 4, but the cap keeps the per-run fetch bounded. Direction/strength come from the form
/// type alone (via spec 99's extractor rules); there are no materiality tiers here (v1 does not parse % of class).
/// </summary>
public sealed class Sec13DGCollectorOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. <c>"Radar Research example@example.com"</c>). Required — every
    /// request 403s without it. Registration fails fast when this is null/blank.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Maximum most-recent 13D/13G filings to fetch/classify per company per run. Defaults to 20.</summary>
    public int MaxFilingsPerCompany { get; init; } = 20;
}
