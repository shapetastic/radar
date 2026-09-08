namespace Radar.Infrastructure.Filings;

/// <summary>
/// Configuration for <see cref="ChatFilingAnalyzer"/>. <see cref="MaxInputLength"/> caps the number of
/// earnings-release characters sent to the model (token/latency control); the analyzer truncates to this
/// leading-substring length before building the prompt. Registered as a singleton by
/// <c>AddRadarFilingAnalyzer</c>.
/// </summary>
public sealed class FilingAnalyzerOptions
{
    /// <summary>Maximum earnings-release characters sent to the model. Default 12000.</summary>
    public int MaxInputLength { get; init; } = 12000;

    /// <summary>
    /// Whether the read also asks the model for the metrics the release STATES (spec 215 §1) and verifies
    /// them verbatim for the reported-metrics ledger. Default true. When false the reported-metrics
    /// paragraph is omitted from the system instruction and nothing is examined or returned — every read
    /// returns <see cref="Radar.Application.Filings.FilingRead.ReportedMetrics"/> = <c>null</c> ("not
    /// extracted"), so no ledger file is written and no cache record claims the reported-metrics policy.
    /// The structured-output schema derived from the response DTO still advertises the list; only the
    /// instruction changes.
    /// </summary>
    public bool ExtractReportedMetrics { get; init; } = true;
}
