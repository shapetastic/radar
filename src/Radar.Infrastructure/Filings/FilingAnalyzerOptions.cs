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
}
