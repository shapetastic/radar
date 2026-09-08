using Radar.Application.Filings;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The WIRE shape of one reported-metric entry in the filing read's structured response (spec 215 §1) —
/// deliberately all strings (the spec-179 rule), so an out-of-vocabulary metric or a malformed value arrives
/// as data the verifier can COUNT instead of being coerced or thrown away by enum deserialization. Nothing
/// here is persisted as-is: only a verified <see cref="ReportedMetricReading"/> is.
/// </summary>
internal sealed record ReportedMetricWire(
    string? Metric,
    string? Value,
    string? Unit,
    string? Period,
    string? PriorValue,
    string? PriorPeriod,
    string? Quote);

/// <summary>
/// SPEC 215 §1 — deterministic verification of the metrics the model says a release states, BEFORE anything
/// is kept. The spec-160 shape (<see cref="EarningsComparabilityScan"/>): the scan is code, not the model's
/// word. Pure and static — no I/O, no clock, no configuration — so the same response over the same body
/// always verifies the same way (AD-3). It lives beside the comparability scan in Infrastructure because
/// both reuse the shared <see cref="FeedTargetRelevance.NormalizeWhitespace"/> collapser (reuse over copy)
/// and both run over the body the Infrastructure analyzer actually read.
/// <para>
/// <b>The rules, in order, per entry.</b> (1) A null entry, or a blank metric, value, period or quote, is
/// <c>DroppedUnverified</c> — there is nothing to verify against. (2) A non-blank metric outside the closed
/// <see cref="ReportedMetric"/> set is <c>DroppedUnrecognised</c> (the digit-rejecting shared token parser,
/// so "3" never becomes a metric). (3) The VALUE must appear verbatim inside the QUOTE, the PRIOR VALUE
/// and PRIOR PERIOD (each when present) must too, the UNIT token (when non-blank) must too, and the QUOTE must appear verbatim
/// inside the TRUNCATED body the analyzer sent to the model — all ordinal, after collapsing whitespace runs
/// on both sides, so a line break inside a sentence is not a mismatch. Any failure is
/// <c>DroppedUnverified</c>; a quote whose text lies past the <c>MaxInputLength</c> cap therefore cannot
/// verify and is counted, so a release whose numbers live past the cap shows up as a gap, not a zero.
/// (4) A second entry for an already-verified (metric, period) pair is <c>DroppedDuplicate</c> — the ledger
/// keeps one record per pair, and the repeat is counted rather than silently collapsed. (5) A verified
/// entry whose prior pair arrived half-stated keeps its current value, loses the half pair, and is counted
/// as <c>PriorPairsDroppedIncomplete</c>.
/// </para>
/// <para>
/// The persisted strings are TRIMMED and whitespace-collapsed forms of what the model returned — the same
/// normalisation the verification compared, so the record can never disagree with the check that admitted
/// it. No arithmetic, no unit conversion, no number parsing: the figure is kept as the release printed it.
/// </para>
/// </summary>
internal static class ReportedMetricVerifier
{
    /// <summary>
    /// Verifies <paramref name="raw"/> against <paramref name="truncatedBody"/> (the EXACT text the model
    /// was shown — the caller passes the output of <see cref="FilingAnalyzerPrompt.Truncate"/>). A null
    /// list verifies as zero entries with zero drops: the model returned no list, which is a measured
    /// nothing, not an unexamined response (the caller decides whether the response was examined at all).
    /// </summary>
    public static VerifiedReportedMetrics Verify(IReadOnlyList<ReportedMetricWire?>? raw, string truncatedBody)
    {
        ArgumentNullException.ThrowIfNull(truncatedBody);

        var body = FeedTargetRelevance.NormalizeWhitespace(truncatedBody);
        var verified = new List<ReportedMetricReading>();
        var seen = new HashSet<(ReportedMetric Metric, string Period)>();
        var unrecognised = 0;
        var unverified = 0;
        var duplicate = 0;
        var incompletePriorPairs = 0;

        foreach (var entry in raw ?? [])
        {
            if (entry is null)
            {
                unverified++;
                continue;
            }

            var metricToken = entry.Metric?.Trim();
            var value = FeedTargetRelevance.NormalizeWhitespace(entry.Value);
            var period = FeedTargetRelevance.NormalizeWhitespace(entry.Period);
            var quote = FeedTargetRelevance.NormalizeWhitespace(entry.Quote);
            if (string.IsNullOrEmpty(metricToken) || value.Length == 0 || period.Length == 0 || quote.Length == 0)
            {
                unverified++;
                continue;
            }

            if (!NewsTypingTokens.TryParse<ReportedMetric>(metricToken, out var metric))
            {
                unrecognised++;
                continue;
            }

            var unit = FeedTargetRelevance.NormalizeWhitespace(entry.Unit);
            var priorValue = FeedTargetRelevance.NormalizeWhitespace(entry.PriorValue);
            var priorPeriod = FeedTargetRelevance.NormalizeWhitespace(entry.PriorPeriod);

            var valueInQuote = quote.Contains(value, StringComparison.Ordinal);
            var unitInQuote = unit.Length == 0 || quote.Contains(unit, StringComparison.Ordinal);
            var priorValueInQuote = priorValue.Length == 0 || quote.Contains(priorValue, StringComparison.Ordinal);
            // The prompt asks for the prior PERIOD "exactly as printed" too, so a supplied one is held to the
            // same verbatim rule as the prior value: an invented period fails the entry, never persists.
            var priorPeriodInQuote = priorPeriod.Length == 0 || quote.Contains(priorPeriod, StringComparison.Ordinal);
            var quoteInBody = body.Contains(quote, StringComparison.Ordinal);
            if (!valueInQuote || !unitInQuote || !priorValueInQuote || !priorPeriodInQuote || !quoteInBody)
            {
                unverified++;
                continue;
            }

            if (!seen.Add((metric, period)))
            {
                duplicate++;
                continue;
            }

            // A prior VALUE without a prior PERIOD (or the reverse) is half a comparison; the pair is kept
            // only when both halves were stated, so the judge never sees a prior figure it cannot place in
            // time. The entry itself stays (its current value verified above), and the discarded half is
            // COUNTED — never nulled silently.
            var priorPairComplete = priorValue.Length > 0 && priorPeriod.Length > 0;
            if (!priorPairComplete && (priorValue.Length > 0 || priorPeriod.Length > 0))
            {
                incompletePriorPairs++;
            }

            verified.Add(new ReportedMetricReading(
                Metric: metric,
                Value: value,
                Unit: unit,
                Period: period,
                PriorValue: priorPairComplete ? priorValue : null,
                PriorPeriod: priorPairComplete ? priorPeriod : null,
                Quote: quote));
        }

        return new VerifiedReportedMetrics(verified, unrecognised, unverified, duplicate, incompletePriorPairs);
    }
}
