using System.Globalization;

using Radar.Application.Collectors;
using Radar.Domain.Signals;

namespace Radar.Application.Filings;

/// <summary>
/// The ONE definition of the provenance keys a NON-DIRECTIONAL AI earnings-read signal carries (spec 204),
/// parallel to <c>NewsDirectionalSignalMetadata</c>. Keys are declared here and nowhere else, and the
/// envelope is composed through the SHARED <see cref="EvidenceMetadata.Compose"/> / read back through
/// <see cref="EvidenceMetadata.TryRead"/> — the repo's single metadata-envelope definition — never a second
/// hand-rolled JSON composer or parser.
/// <para>
/// <b>Why this envelope exists.</b> Before spec 204, a confident "materially two-sided quarter" (Mixed), an
/// "I could not read this" (Unknown) and a directional read that failed the confidence gate all collapsed
/// onto one cached no-signal token and emitted NOTHING — the company kept the spec-57 keyword Neutral, the
/// exact signal an UNREAD filing gets, and the model's direction/confidence/rationale survived nowhere in
/// the signal layer. The read is now persisted as its own <c>GuidanceChange</c> signal (Mixed scores 0
/// exactly like Neutral, so the score does not move — asserted, not argued) and this envelope is what makes
/// that signal distinguishable from the keyword copy it stands beside: the supersede
/// (<c>GuidanceChangeSupersede</c>) prefers a signal carrying <see cref="OutcomeKey"/> over one that does
/// not, so provenance is chosen by "the model actually read this filing", never by GUID order.
/// </para>
/// <para>
/// <b>The magnitudes are the KEYWORD FALLBACK's, deliberately.</b> <see cref="Strength"/> /
/// <see cref="Novelty"/> / <see cref="Confidence"/> mirror the <c>KeywordSignalExtractor</c>
/// "results of operations" rule (Neutral, 3/4/0.4) — NOT the directional read's
/// <c>DirectionalFilingSignalOptions.Strength</c>/<c>Novelty</c> — because the point is provenance without a
/// score move: with identical magnitudes and a direction that scores as 0 exactly like Neutral, every
/// v8/v9/v10/v11 component is byte-identical whether the keyword copy or the read survives. The keyword
/// rule's values are private to the extractor, so the equality is pinned BY TEST through the extractor's
/// public surface (<c>FilingReadSignalMetadataTests</c>) rather than referenced — a drift fails the build's
/// test gate rather than silently splitting the two.
/// </para>
/// <para>
/// <b>The model's real confidence rides in metadata, never in the signal's <c>Confidence</c>.</b> The
/// standing CLAUDE.md rule — "if AI confidence is low, persist the evidence but do not create
/// high-confidence signals" — cuts both ways here: a high-confidence Mixed must not become a high-strength
/// or high-confidence anything, and a below-gate read must not smuggle its sub-gate confidence into
/// scoring. <see cref="ConfidenceKey"/> carries the EFFECTIVE (comparability-CAPPED, spec 160) confidence —
/// the value the <c>MinConfidence</c> gate actually compared and the value the signal's Reason prefix
/// displays, so the envelope can never disagree with the text beside it. The model's RAW pre-cap confidence
/// stays where it always lived: the spec-115 debug record.
/// </para>
/// <para>
/// The envelope's <c>companyHints</c> array is written EMPTY: a signal carries no collector company hints.
/// That is the price of having exactly one envelope definition instead of two (the
/// <c>NewsDirectionalSignalMetadata</c> precedent, verbatim).
/// </para>
/// </summary>
public static class FilingReadSignalMetadata
{
    /// <summary>
    /// Why the analyzed filing produced no DIRECTIONAL signal — one of <see cref="OutcomeMixed"/>,
    /// <see cref="OutcomeUnknown"/>, <see cref="OutcomeBelowConfidence"/>. PRESENCE of this key is the one
    /// question <c>GuidanceChangeSupersede</c> asks (via <see cref="CarriesReadOutcome(string?)"/>): it marks
    /// the signal as an actual AI read of the filing, which beats the deterministic keyword copy.
    /// </summary>
    public const string OutcomeKey = "filingReadOutcome";

    /// <summary>The model's OWN direction token (<c>Mixed</c>/<c>Unknown</c>/<c>Improving</c>/<c>Deteriorating</c>), never the signal's mapped direction.</summary>
    public const string DirectionKey = "filingReadDirection";

    /// <summary>
    /// The EFFECTIVE read confidence, invariant <c>G29</c>: the comparability-capped (spec 160) value when
    /// the cap moved the number, else the model's raw value — i.e. exactly what the <c>MinConfidence</c>
    /// gate saw and what the Reason prefix renders. The raw pre-cap value lives on the spec-115 debug
    /// record; recording the capped one HERE keeps the envelope, the Reason text and the cache record's
    /// <c>ReadConfidence</c> one value with no way to disagree.
    /// </summary>
    public const string ConfidenceKey = "filingReadConfidence";

    /// <summary>The reading model identity (<c>provider:model</c>, the spec-119 comparability label) the read was produced by.</summary>
    public const string ModelKey = "filingReadModel";

    /// <summary>A confident Mixed read: the release is materially two-sided. The signal's direction is <c>Mixed</c> (scores 0, like Neutral).</summary>
    public const string OutcomeMixed = "mixed";

    /// <summary>An Unknown read (any confidence): the model could not establish a direction. The signal's direction is <c>Neutral</c>.</summary>
    public const string OutcomeUnknown = "unknown";

    /// <summary>A read whose (capped) confidence fell below <c>MinConfidence</c>. The signal's direction is <c>Neutral</c>.</summary>
    public const string OutcomeBelowConfidence = "below-confidence";

    /// <summary>
    /// The emitted signal's Strength — the keyword fallback's value (see the type remarks: pinned equal to
    /// the extractor's "results of operations" rule by test, so a drift cannot be silent).
    /// </summary>
    public const int Strength = 3;

    /// <summary>The emitted signal's Novelty — the keyword fallback's value (pinned by test, as above).</summary>
    public const int Novelty = 4;

    /// <summary>The emitted signal's (scoring) Confidence — the keyword fallback's value (pinned by test, as above).</summary>
    public const decimal Confidence = 0.4m;

    /// <summary>
    /// The <see cref="OutcomeKey"/> token for a persisted no-signal cause. <see cref="FilingNoSignalCause.EmptyBody"/>
    /// deliberately THROWS: an empty-body skip made no model call, is never cached (spec 114) and never
    /// produces a signal, so composing an envelope for it would fabricate a read that did not happen.
    /// </summary>
    public static string OutcomeTokenFor(FilingNoSignalCause cause) => cause switch
    {
        FilingNoSignalCause.Mixed => OutcomeMixed,
        FilingNoSignalCause.Unknown => OutcomeUnknown,
        FilingNoSignalCause.BelowConfidence => OutcomeBelowConfidence,
        _ => throw new ArgumentOutOfRangeException(
            nameof(cause), cause, "Only Mixed/Unknown/BelowConfidence reads produce a read signal."),
    };

    /// <summary>
    /// Composes the provenance envelope for one non-directional AI earnings-read signal, through the SHARED
    /// <see cref="EvidenceMetadata.Compose"/>. <paramref name="effectiveConfidence"/> is the CAPPED value
    /// when the spec-160 comparability cap applied (see <see cref="ConfidenceKey"/>); it is rendered with
    /// invariant <c>G29</c> — the decimal round-trip format the scoring descriptor already uses — so the
    /// persisted text is deterministic and culture-independent (AD-3).
    /// </summary>
    public static string Compose(
        FilingNoSignalCause cause,
        string readDirection,
        decimal effectiveConfidence,
        string modelIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readDirection);
        ArgumentNullException.ThrowIfNull(modelIdentity);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OutcomeKey] = OutcomeTokenFor(cause),
            [DirectionKey] = readDirection,
            [ConfidenceKey] = effectiveConfidence.ToString("G29", CultureInfo.InvariantCulture),
            [ModelKey] = modelIdentity,
        };

        return EvidenceMetadata.Compose(metadata, []);
    }

    /// <summary>
    /// True when <paramref name="metadataJson"/> is a readable envelope carrying a non-blank
    /// <see cref="OutcomeKey"/> — i.e. the signal IS a persisted AI earnings read. This is the ONE
    /// definition of that predicate; <c>GuidanceChangeSupersede</c> routes through it rather than holding a
    /// second copy of the key/parse rules. Defensive like every metadata read in this repo: <c>null</c>,
    /// blank or unreadable JSON, an unrelated bag, or a blank outcome value are all simply <c>false</c> —
    /// never a throw — because an absent claim records nothing and must never fail an assembly.
    /// </summary>
    public static bool CarriesReadOutcome(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        if (!EvidenceMetadata.TryRead(metadataJson, out var metadata, out _))
        {
            return false;
        }

        return metadata.TryGetValue(OutcomeKey, out var outcome) && !string.IsNullOrWhiteSpace(outcome);
    }

    /// <summary>Convenience overload over the signal's own envelope (see <see cref="CarriesReadOutcome(string?)"/>).</summary>
    public static bool IsFilingReadSignal(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return CarriesReadOutcome(signal.MetadataJson);
    }
}
