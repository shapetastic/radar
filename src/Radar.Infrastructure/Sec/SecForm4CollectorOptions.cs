namespace Radar.Infrastructure.Sec;

/// <summary>
/// Options for the SEC Form 4 (insider-transaction) collector. <see cref="UserAgent"/> is <b>required</b>:
/// SEC returns HTTP 403 for any request without a compliant declared UA, so a missing/blank value is a
/// fail-fast configuration error (validated at registration). <see cref="MaxFilingsPerCompany"/> caps how
/// many of the most-recent Form 4 filings each company contributes per run — Form 4s are numerous (a single
/// issuer can file several on one day), so the cap keeps the per-run fetch bounded and polite. The
/// materiality tiers that scale the resulting signal's Strength live in the extractor, not here.
/// </summary>
public sealed class SecForm4CollectorOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. <c>"Radar Research example@example.com"</c>). Required — every
    /// request 403s without it. Registration fails fast when this is null/blank.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Maximum most-recent Form 4 filings to fetch/parse per company per run. Defaults to 15.</summary>
    public int MaxFilingsPerCompany { get; init; } = 15;
}
