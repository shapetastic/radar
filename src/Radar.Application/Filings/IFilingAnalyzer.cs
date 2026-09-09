using Radar.Domain.Filings;

namespace Radar.Application.Filings;

/// <summary>
/// The reported-metrics half of one filing read (spec 215 §1, widened by spec 216 §3): every metric that
/// survived deterministic verification, in the order the model returned it, plus EVERY drop class — each a
/// MEASURED count (a 0 is a measured zero; this type exists only when the model response was examined).
/// Each drop class is its own axis on purpose: "the model invented a period", "the model attached the
/// wrong metric label to a real figure" and "the value was a fragment of a bigger number" are different
/// facts about the provider, and collapsing them would hide the pressure the live bound in spec 216 §6
/// exists to measure.
/// </summary>
/// <param name="Verified">The metrics that passed every rule under <see cref="ReportedMetricsPolicy.Version"/>.</param>
/// <param name="DroppedUnrecognised">Entries naming a metric outside the closed <see cref="ReportedMetric"/> set.</param>
/// <param name="DroppedUnverified">Entries that were blank, or whose quote is not verbatim inside the truncated body the model was shown (including a quote past the truncation cap).</param>
/// <param name="DroppedDuplicate">Entries repeating an already-verified (metric, period) pair — one ledger record per pair, so the repeat is counted, never silently collapsed.</param>
/// <param name="PriorPairsDroppedIncomplete">Verified entries whose prior pair arrived HALF-stated (a prior value with no prior period, or the reverse): the entry is kept without its prior pair, and the discarded half is counted here rather than nulled silently.</param>
/// <param name="DroppedMetricNotInQuote">SPEC 216 §3: the quote does not NAME the labelled metric under the closed <see cref="ReportedMetricSynonyms"/> table (a cash figure labelled Backlog).</param>
/// <param name="DroppedPeriodNotInQuote">SPEC 216 §3: the stated period does not appear verbatim in the quote (an invented period).</param>
/// <param name="DroppedFragment">SPEC 216 §3: the value, unit or prior value matched only as a FRAGMENT of a bigger token ("384" inside "384.0" or "3,384").</param>
/// <param name="DroppedNotAssociated">SPEC 216 §3: the metric synonym, the value+unit and the period are all present in the quote but not together in ONE bounded fragment with the value nearest the metric ("cash was $100m and backlog was $2.5bn").</param>
public sealed record VerifiedReportedMetrics(
    IReadOnlyList<ReportedMetricReading> Verified,
    int DroppedUnrecognised,
    int DroppedUnverified,
    int DroppedDuplicate,
    int PriorPairsDroppedIncomplete,
    int DroppedMetricNotInQuote = 0,
    int DroppedPeriodNotInQuote = 0,
    int DroppedFragment = 0,
    int DroppedNotAssociated = 0)
{
    /// <summary>
    /// Every entry the model returned that this verification examined: the verified ones plus every drop
    /// class. The live &gt; 50% bound in spec 216 §6 is measured against THIS denominator, so it has one
    /// definition rather than one per caller. (<see cref="PriorPairsDroppedIncomplete"/> is deliberately
    /// absent: it counts a discarded HALF of a KEPT entry, not a dropped entry.)
    /// </summary>
    public int Examined =>
        Verified.Count
        + DroppedUnrecognised
        + DroppedUnverified
        + DroppedDuplicate
        + DroppedMetricNotInQuote
        + DroppedPeriodNotInQuote
        + DroppedFragment
        + DroppedNotAssociated;

    /// <summary>Every entry that was examined and NOT kept.</summary>
    public int DroppedTotal => Examined - Verified.Count;
}

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
