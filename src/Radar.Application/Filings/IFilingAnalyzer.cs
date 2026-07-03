using Radar.Domain.Filings;

namespace Radar.Application.Filings;

/// <summary>
/// Reads an earnings-release plain text (from the SEC earnings-release reader, spec 73) and returns a typed,
/// validated <see cref="FilingSentiment"/> — a directional read of the results AS REPORTED (improving vs
/// deteriorating trajectory), NOT a beat-vs-consensus claim (Radar has no consensus feed). Implementations
/// MUST validate the model output before returning it and MUST degrade to <see cref="FilingSentiment.Unknown"/>
/// (Direction = Unknown, Confidence = 0) rather than throw on a malformed/empty/failed AI response; only
/// genuine caller cancellation propagates. Output must never contain advice language.
/// </summary>
public interface IFilingAnalyzer
{
    /// <summary>
    /// Analyzes the supplied earnings-release plain text and returns a validated <see cref="FilingSentiment"/>.
    /// Null/empty/whitespace text returns <see cref="FilingSentiment.Unknown"/> without calling the model.
    /// A malformed/empty/failed AI response degrades to <see cref="FilingSentiment.Unknown"/> and never throws;
    /// only genuine caller cancellation propagates.
    /// </summary>
    Task<FilingSentiment> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct);
}
