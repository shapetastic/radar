using System.Text.RegularExpressions;

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
/// SPEC 215 §1, MADE REAL BY SPEC 216 §3 — deterministic verification of the metrics the model says a
/// release states, BEFORE anything is kept. The spec-160 shape (<see cref="EarningsComparabilityScan"/>):
/// the scan is code, not the model's word. Pure and static — no I/O, no clock, no configuration — so the
/// same response over the same body always verifies the same way (AD-3). It lives beside the comparability
/// scan in Infrastructure because both reuse the shared
/// <see cref="FeedTargetRelevance.NormalizeWhitespace"/> collapser (reuse over copy) and both run over the
/// body the Infrastructure analyzer actually read.
/// <para>
/// <b>What v1 verified, and did not.</b> The metric LABEL was trusted (a cash figure labelled Backlog
/// passed), the period only had to be non-blank (an invented period passed), and
/// <c>quote.Contains(value)</c> was a substring test ("384" verified inside "384.0"). Metric and period
/// drive projection matching, so all three reached the judge as facts. Each is now its own rule with its
/// own counter.
/// </para>
/// <para>
/// <b>The rules, in order, per entry.</b>
/// (1) A null entry, or a blank metric, value, period or quote, is <c>DroppedUnverified</c> — there is
/// nothing to verify against.
/// (2) A non-blank metric outside the closed <see cref="ReportedMetric"/> set is <c>DroppedUnrecognised</c>
/// (the digit-rejecting shared token parser, so "3" never becomes a metric).
/// (3) The QUOTE must appear verbatim inside the TRUNCATED body the analyzer sent to the model (ordinal,
/// after collapsing whitespace runs on both sides), else <c>DroppedUnverified</c> — so a release whose
/// numbers live past the <c>MaxInputLength</c> cap shows up as a gap, not a zero.
/// (4) The quote must NAME the labelled metric under the closed <see cref="ReportedMetricSynonyms"/> table
/// (whole phrase, longest-wins, case-insensitive), else <c>DroppedMetricNotInQuote</c>.
/// (5) The PERIOD must appear verbatim in the quote, else <c>DroppedPeriodNotInQuote</c> — the same rule
/// the prior period already obeys.
/// (6) The VALUE, the UNIT (when non-blank) and the PRIOR VALUE / PRIOR PERIOD (each when present) must
/// match as WHOLE TOKENS, bounded so a number can never match inside a bigger number ("384" does not
/// verify inside "384.0" or "3,384"), else <c>DroppedFragment</c>.
/// (7) ASSOCIATION, not co-presence: the metric synonym, the value (with its unit) and the period must all
/// lie inside ONE bounded fragment of the quote, and inside it the value must be the NEAREST
/// verified-shape number to the metric synonym — else <c>DroppedNotAssociated</c>. "cash was $100m and
/// backlog was $2.5bn" therefore cannot verify a Backlog entry whose value is 100.
/// (8) A second entry for an already-verified (metric, period) pair is <c>DroppedDuplicate</c>.
/// (9) A verified entry whose prior pair arrived half-stated keeps its current value, loses the half pair,
/// and is counted as <c>PriorPairsDroppedIncomplete</c>.
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
    /// A VERIFIED-SHAPE number: a digit run, not glued to a preceding word character (so <c>Q2</c>,
    /// <c>FY27</c> and <c>H1</c> are not numbers), that EITHER carries a currency symbol, OR is followed by
    /// a scale word / percent, OR contains a decimal point or a thousands comma. A bare integer with none
    /// of those — a calendar year, a segment count — is deliberately NOT a verified-shape number, because
    /// the association rule below asks which number is NEAREST the metric and a stray "2026" would
    /// otherwise beat the figure being verified.
    /// </summary>
    private static readonly Regex VerifiedShapeNumber = new(
        @"(?<![\w.,])(?:"
            + @"[\$€£]\s?\d[\d,]*(?:\.\d+)?"
            + @"|\d[\d,]*(?:\.\d+)?\s*(?:%|percent|bn|b\b|m\b|k\b|million|billion|thousand|trillion)"
            + @"|\d{1,3}(?:,\d{3})+(?:\.\d+)?"
            + @"|\d+\.\d+"
            + @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The FRAGMENT boundary alternatives, in the SPEC'S ORDER OF PREFERENCE (spec 216 §3). Exactly one
    /// alternative is used per quote — the highest-preference one that actually occurs — because a table
    /// splits on its row separator and a prose sentence splits on its full stop, and applying both at once
    /// would tear a table row ("Revenue␣␣$384.0 million") into a metric with no figure beside it.
    /// <para>
    /// The sentence-ending period is <c>.</c> followed by whitespace-then-uppercase, or by end of text: a
    /// decimal point inside <c>384.0</c>, <c>$0.79</c> or <c>2.5B</c> is NEVER a boundary (splitting on
    /// every <c>.</c> would break the very values being verified).
    /// </para>
    /// </summary>
    private static readonly Regex[] FragmentBoundaries =
    [
        new(@"\r?\n", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\|", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@";", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"[ \t]{2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\.(?=\s+\p{Lu})|\.\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

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
        var metricNotInQuote = 0;
        var periodNotInQuote = 0;
        var fragmentValue = 0;
        var notAssociated = 0;

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

            // Rule 3 — the quote is the release's own words or it is nothing. Checked FIRST of the content
            // rules so a fabricated quote is never graded on its metric or its period.
            if (!body.Contains(quote, StringComparison.Ordinal))
            {
                unverified++;
                continue;
            }

            // Rule 4 — the quote must name the metric the model labelled it with.
            if (!ReportedMetricSynonyms.Names(quote, metric))
            {
                metricNotInQuote++;
                continue;
            }

            // Rule 5 — "as the release words it" is satisfied by requiring the release's own words.
            if (!quote.Contains(period, StringComparison.Ordinal))
            {
                periodNotInQuote++;
                continue;
            }

            // Rule 6 — whole tokens only, everywhere a figure or a unit is claimed.
            var unitOk = unit.Length == 0 || ContainsWholeToken(quote, unit);
            var priorValueOk = priorValue.Length == 0 || ContainsWholeToken(quote, priorValue);
            var priorPeriodOk = priorPeriod.Length == 0 || ContainsWholeToken(quote, priorPeriod);
            if (!ContainsWholeToken(quote, value) || !unitOk || !priorValueOk || !priorPeriodOk)
            {
                fragmentValue++;
                continue;
            }

            // Rule 7 — association inside ONE fragment of the RAW quote (fragment boundaries are destroyed
            // by whitespace collapsing, so the split runs on the text the model actually returned and each
            // fragment is normalised afterwards, exactly as the whole quote was).
            if (!IsAssociated(entry.Quote ?? string.Empty, metric, value, unit, period))
            {
                notAssociated++;
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

        return new VerifiedReportedMetrics(
            verified,
            unrecognised,
            unverified,
            duplicate,
            incompletePriorPairs,
            metricNotInQuote,
            periodNotInQuote,
            fragmentValue,
            notAssociated);
    }

    /// <summary>
    /// Splits <paramref name="rawQuote"/> into bounded fragments using the HIGHEST-PREFERENCE boundary
    /// kind that actually occurs in it (spec 216 §3), then trims and whitespace-collapses each. Exposed to
    /// the test assembly so the boundary rule — the fiddliest part of the policy — is pinned directly
    /// rather than only through its consequences.
    /// </summary>
    internal static IReadOnlyList<string> Fragments(string rawQuote)
    {
        ArgumentNullException.ThrowIfNull(rawQuote);

        foreach (var boundary in FragmentBoundaries)
        {
            if (!boundary.IsMatch(rawQuote))
            {
                continue;
            }

            var parts = boundary
                .Split(rawQuote)
                .Select(FeedTargetRelevance.NormalizeWhitespace)
                .Where(f => f.Length > 0)
                .ToList();
            if (parts.Count > 0)
            {
                return parts;
            }
        }

        var whole = FeedTargetRelevance.NormalizeWhitespace(rawQuote);
        return whole.Length == 0 ? [] : [whole];
    }

    /// <summary>
    /// Whether SOME fragment of the quote carries the metric synonym, the period and the value (with its
    /// unit) together, with the value the NEAREST verified-shape number to that synonym occurrence.
    /// </summary>
    private static bool IsAssociated(
        string rawQuote, ReportedMetric metric, string value, string unit, string period)
    {
        foreach (var fragment in Fragments(rawQuote))
        {
            var synonyms = ReportedMetricSynonyms.Occurrences(fragment, metric);
            if (synonyms.Count == 0
                || !fragment.Contains(period, StringComparison.Ordinal)
                || (unit.Length > 0 && !ContainsWholeToken(fragment, unit)))
            {
                continue;
            }

            var valueSpans = WholeTokenSpans(fragment, value);
            if (valueSpans.Count == 0)
            {
                continue;
            }

            var numbers = VerifiedShapeNumber.Matches(fragment)
                .Select(m => (Start: m.Index, End: m.Index + m.Length))
                .ToList();

            foreach (var (synIndex, synLength) in synonyms)
            {
                var synEnd = synIndex + synLength;
                var nearest = int.MaxValue;
                foreach (var (start, end) in numbers)
                {
                    nearest = Math.Min(nearest, Distance(synIndex, synEnd, start, end));
                }

                foreach (var (start, end) in valueSpans)
                {
                    // A verified-shape number that CONTAINS the matched value counts as the value's own
                    // token ("$2.5 billion" is one number; the model reported the value as "2.5").
                    var valueDistance = numbers
                        .Where(n => n.Start <= start && n.End >= end)
                        .Select(n => Distance(synIndex, synEnd, n.Start, n.End))
                        .DefaultIfEmpty(Distance(synIndex, synEnd, start, end))
                        .Min();
                    if (valueDistance <= nearest)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Character gap between two spans; 0 when they touch or overlap.</summary>
    private static int Distance(int aStart, int aEnd, int bStart, int bEnd) =>
        bStart >= aEnd ? bStart - aEnd
        : aStart >= bEnd ? aStart - bEnd
        : 0;

    private static bool ContainsWholeToken(string text, string token) =>
        WholeTokenSpans(text, token).Count > 0;

    /// <summary>
    /// Every occurrence of <paramref name="token"/> in <paramref name="text"/> that is a COMPLETE token:
    /// a numeric edge may not touch a digit or a digit-bearing separator (so "384" never matches inside
    /// "384.0" or "3,384"), and an alphanumeric edge may not touch a word character (so "m" never matches
    /// inside "million"). A purely symbolic edge — a currency sign, a percent — constrains nothing, which
    /// is what lets the unit "$" verify inside "$2.5".
    /// </summary>
    private static List<(int Start, int End)> WholeTokenSpans(string text, string token)
    {
        var spans = new List<(int Start, int End)>();
        if (token.Length == 0)
        {
            return spans;
        }

        var from = 0;
        while (from <= text.Length - token.Length)
        {
            var index = text.IndexOf(token, from, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var end = index + token.Length;
            if (LeftEdgeOk(text, index, token[0]) && RightEdgeOk(text, end, token[^1]))
            {
                spans.Add((index, end));
            }

            from = index + 1;
        }

        return spans;
    }

    private static bool LeftEdgeOk(string text, int index, char first)
    {
        if (index == 0)
        {
            return true;
        }

        var before = text[index - 1];
        if (char.IsAsciiDigit(first))
        {
            // A digit start may not continue a number: neither a digit nor a separator BETWEEN digits.
            return !char.IsAsciiDigit(before)
                && !(before is '.' or ',' && index >= 2 && char.IsAsciiDigit(text[index - 2]));
        }

        return !char.IsLetterOrDigit(first) || !char.IsLetterOrDigit(before);
    }

    private static bool RightEdgeOk(string text, int end, char last)
    {
        if (end >= text.Length)
        {
            return true;
        }

        var after = text[end];
        if (char.IsAsciiDigit(last))
        {
            return !char.IsAsciiDigit(after)
                && !(after is '.' or ',' && end + 1 < text.Length && char.IsAsciiDigit(text[end + 1]));
        }

        return !char.IsLetterOrDigit(last) || !char.IsLetterOrDigit(after);
    }
}
