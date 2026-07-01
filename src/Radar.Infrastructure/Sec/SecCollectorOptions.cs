namespace Radar.Infrastructure.Sec;

/// <summary>
/// Options for the SEC EDGAR filing collector. <see cref="UserAgent"/> is <b>required</b>: SEC returns
/// HTTP 403 for any request without a compliant declared UA, so a missing/blank value is a fail-fast
/// configuration error (validated at registration). <see cref="Forms"/> filters which filing forms are
/// turned into evidence (default: signal-bearing 8-K/10-Q/10-K); <see cref="MaxFilingsPerCompany"/> caps
/// how many of the most-recent matching filings each company contributes per run.
/// </summary>
public sealed class SecCollectorOptions
{
    /// <summary>
    /// The compliant SEC User-Agent (e.g. <c>"Radar Research example@example.com"</c>). Required — every
    /// request 403s without it. Registration fails fast when this is null/blank.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Filing forms to collect (case-insensitive). Defaults to 8-K, 10-Q, 10-K.</summary>
    public IReadOnlyList<string> Forms { get; init; } = ["8-K", "10-Q", "10-K"];

    /// <summary>Maximum most-recent matching filings to collect per company per run.</summary>
    public int MaxFilingsPerCompany { get; init; } = 25;
}
