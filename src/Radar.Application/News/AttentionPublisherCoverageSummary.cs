namespace Radar.Application.News;

/// <summary>
/// One tier's share of a collection pass's attempted news-observation candidates.
/// </summary>
/// <param name="TierName">
/// The curated tier's name, or the unclassified sentinel (<c>AttentionSourceResolution.UnclassifiedTierName</c>,
/// carried as a plain string so this record stays primitive-only).
/// </param>
/// <param name="Observations">Observation candidates whose publisher resolved to this tier.</param>
public sealed record AttentionPublisherTierCoverage(string TierName, int Observations);

/// <summary>
/// One unclassified publisher and how many of the pass's attempted candidates it produced — the curation
/// worklist, largest first.
/// <para>
/// ⚠ <b>Keyed on the RAW trimmed publisher name (case-insensitively), not on the normalized tier key</b>
/// that <c>Resolve</c> matches with. Two spellings of one unclassified outlet that differ only by
/// punctuation therefore appear as two rows here, so this count is NOT a count of distinct <i>normalized</i>
/// publishers. That is deliberate: the raw name is what a curator needs to look the outlet up and add it to
/// the map, and it affects the worklist only — tiering, and hence every score, keys on the normalized form.
/// </para>
/// </summary>
/// <param name="Publisher">The publisher name as the collector supplied it (blank ⇒ <see cref="Unattributed"/>).</param>
/// <param name="Observations">Observation candidates carrying this publisher name.</param>
public sealed record UnclassifiedPublisherCoverage(string Publisher, int Observations)
{
    /// <summary>
    /// The bucket for candidates whose publisher name is blank. A blank name is not a publisher Radar could
    /// classify, so it is named rather than silently folded into a real outlet's count.
    /// </summary>
    public const string Unattributed = "(unattributed)";
}

/// <summary>
/// <b>A CAPTURE-FLOW DIAGNOSTIC — NOT the input to <c>AttentionScore</c> (spec 196 §3).</b> It answers
/// "how much of what this collection pass actually captured is covered by the curated publisher tier map?",
/// so that the map can be curated against real volume instead of against whichever familiar company someone
/// happened to notice scoring 75.
/// <para>
/// <b>The two populations genuinely differ, and conflating them would be a lie about the score.</b> This
/// counts <i>observation candidates attempted in ONE collection pass</i> — written, cross-run deduped and
/// failed alike — so its tier counts sum to <c>NewsObservationBatch.ObservationsAttempted</c>. Scoring
/// consumes something else entirely: the tier-weighted <b>distinct publishers per company</b> over the
/// scoring window, where a publisher appearing forty times counts once. Never read this, or describe it, as
/// the attention input.
/// </para>
/// <para>
/// <b>It never auto-classifies anything.</b> The tier map stays curated policy (AD-5); this only makes the
/// gap legible, so the next drift is a number someone sees.
/// </para>
/// <para>
/// It rides <c>NewsObservationBatch</c> as a trailing nullable member with its OWN
/// <see cref="CurrentVersion"/> token: <c>NewsObservationBatch.SchemaVersion</c> is stamped with
/// <c>NewsObservationRecord.CurrentSchemaVersion</c> — the same const every individual observation record
/// carries — so bumping it would churn every observation record for a reason that has nothing to do with
/// them. <c>null</c> on a pre-196 batch means NOT RECORDED, never a measured zero.
/// </para>
/// <para>
/// Primitive-only by design: tier names are strings, not scoring types, so <c>Radar.Application.News</c>
/// never takes a dependency on <c>Radar.Application.Scoring</c>.
/// </para>
/// </summary>
/// <param name="Version">This summary's own schema token (<see cref="CurrentVersion"/>).</param>
/// <param name="ObservationsAttempted">
/// The candidates this summary partitions — byte-equal to the batch's own <c>ObservationsAttempted</c>.
/// </param>
/// <param name="Tiers">
/// Every tier's count INCLUDING the unclassified sentinel, ordered by descending count then tier name
/// (ordinal) so the rendering is deterministic (AD-3). Sums to <see cref="ObservationsAttempted"/>.
/// </param>
/// <param name="DistinctUnclassifiedPublishers">
/// How many distinct publisher names went unclassified — the size of the curation tail, of which
/// <see cref="TopUnclassifiedPublishers"/> shows only the head.
/// </param>
/// <param name="TopUnclassifiedPublishers">
/// The largest unclassified publishers by volume (at most <see cref="TopUnclassifiedPublisherLimit"/>),
/// ordered by descending count then publisher name (ordinal).
/// </param>
public sealed record AttentionPublisherCoverageSummary(
    string Version,
    int ObservationsAttempted,
    IReadOnlyList<AttentionPublisherTierCoverage> Tiers,
    int DistinctUnclassifiedPublishers,
    IReadOnlyList<UnclassifiedPublisherCoverage> TopUnclassifiedPublishers)
{
    /// <summary>This record's own schema token, independent of the batch's shared observation-record tag.</summary>
    public const string CurrentVersion = "attention-publisher-coverage-v1";

    /// <summary>How many unclassified publishers the summary names (the rest are counted, not listed).</summary>
    public const int TopUnclassifiedPublisherLimit = 10;
}
