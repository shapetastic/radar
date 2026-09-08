using Radar.Domain.Filings;

namespace Radar.Application.Filings;

/// <summary>
/// The reported-metrics half of one filing read (spec 215 §1): every metric that survived deterministic
/// verbatim verification, in the order the model returned it, plus the three drop classes — each a
/// MEASURED count (a 0 is a measured zero; this type exists only when the model response was examined).
/// </summary>
/// <param name="Verified">The metrics that passed value-in-quote, unit-in-quote and quote-in-body verification.</param>
/// <param name="DroppedUnrecognised">Entries naming a metric outside the closed <see cref="ReportedMetric"/> set.</param>
/// <param name="DroppedUnverified">Entries whose value, unit or quote failed the verbatim scan, or that were blank (including a quote past the truncation cap).</param>
/// <param name="DroppedDuplicate">Entries repeating an already-verified (metric, period) pair — one ledger record per pair, so the repeat is counted, never silently collapsed.</param>
/// <param name="PriorPairsDroppedIncomplete">Verified entries whose prior pair arrived HALF-stated (a prior value with no prior period, or the reverse): the entry is kept without its prior pair, and the discarded half is counted here rather than nulled silently.</param>
public sealed record VerifiedReportedMetrics(
    IReadOnlyList<ReportedMetricReading> Verified,
    int DroppedUnrecognised,
    int DroppedUnverified,
    int DroppedDuplicate,
    int PriorPairsDroppedIncomplete);

/// <summary>
/// What one earnings-release read returns (spec 215 §1): the directional <see cref="FilingSentiment"/>
/// exactly as before, plus the metrics the release STATED that survived verification.
/// <para>
/// <see cref="ReportedMetrics"/> is <c>null</c> when NO metric extraction was examined — the analyzer was
/// configured not to request metrics, no model call happened (empty text, misconfigured cap), or the
/// response could not be parsed — and non-null (possibly with an empty verified list) when the model
/// response WAS examined. The distinction matters downstream: null means "not extracted this pass" and
/// writes no ledger file, while an empty verified list is a recorded fact about the release.
/// </para>
/// </summary>
public sealed record FilingRead(FilingSentiment Sentiment, VerifiedReportedMetrics? ReportedMetrics)
{
    /// <summary>The safe default: an Unknown sentiment with no metric extraction examined.</summary>
    public static FilingRead Unknown { get; } = new(FilingSentiment.Unknown, null);

    /// <summary>A read whose metric extraction was not examined (extraction disabled, or no parseable response).</summary>
    public static FilingRead WithoutMetrics(FilingSentiment sentiment) => new(sentiment, null);
}

/// <summary>
/// Reads an earnings-release plain text (from the SEC earnings-release reader, spec 73) and returns a typed,
/// validated <see cref="FilingRead"/> — a directional read of the results AS REPORTED (improving vs
/// deteriorating trajectory), NOT a beat-vs-consensus claim (Radar has no consensus feed), plus (spec 215)
/// the verified metrics the release states. Implementations MUST validate the model output before returning
/// it and MUST degrade to <see cref="FilingRead.Unknown"/> (Direction = Unknown, Confidence = 0, no metrics
/// examined) rather than throw on a malformed/empty/failed AI response; only genuine caller cancellation
/// propagates. Output must never contain advice language.
/// </summary>
public interface IFilingAnalyzer
{
    /// <summary>
    /// Analyzes the supplied earnings-release plain text and returns a validated <see cref="FilingRead"/>.
    /// Null/empty/whitespace text returns <see cref="FilingRead.Unknown"/> without calling the model.
    /// A malformed/empty/failed AI response degrades to <see cref="FilingRead.Unknown"/> and never throws;
    /// only genuine caller cancellation propagates.
    /// </summary>
    Task<FilingRead> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct);
}
