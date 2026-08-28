using Radar.Application.Collectors;

namespace Radar.Application.News;

/// <summary>
/// The per-collection-pass batch manifest (spec 177 §5): the durable answer to "did this run's news
/// observation capture actually complete, for which companies, and how do we know?". It is associated with
/// the resulting <c>PipelineRunRecord</c> by EXPLICIT batch id (the run record carries
/// <c>NewsObservationBatchId</c>) — never by a nearest-time join.
/// <para>
/// <b>Failure is durable, never quiet.</b> An archive write error is a Warning that is RECORDED here
/// (<see cref="ObservationsFailed"/> &gt; 0 ⇒ <see cref="CaptureProven"/> false) and never aborts company
/// scoring — but it makes this company/run <i>unproven capture</i>, which a later semantic reader must
/// treat as "unknown", never as a clean zero. Incomplete per-company coverage (a failed or capped feed) is
/// visible the same way, through the embedded spec-169 coverage rows.
/// </para>
/// </summary>
/// <param name="BatchId">This batch's id — the explicit run association key.</param>
/// <param name="RunAsOfUtc">The producing pass's single as-of instant (AD-7), byte-equal to the run record's <c>CreatedAtUtc</c>.</param>
/// <param name="SchemaVersion">The archive schema version (<see cref="NewsObservationRecord.CurrentSchemaVersion"/>).</param>
/// <param name="FullUniverse">
/// Whether the producing pass collected the WHOLE watch universe. A spec-161 company-filtered collect pass
/// records <c>false</c> — it may capture observations, but it can never establish the whole-universe
/// prospective boundary.
/// </param>
/// <param name="ObservationsAttempted">Surviving candidates handed to the archive.</param>
/// <param name="ObservationsWritten">Candidates that produced a NEW archive file.</param>
/// <param name="ObservationsCrossRunDeduped">Candidates whose identical payload was already archived by an earlier run/partition.</param>
/// <param name="ObservationsFailed">Candidates the archive could not durably persist (disk failure or id/hash conflict).</param>
/// <param name="CaptureProven">True iff every attempted observation is durably accounted for (<see cref="ObservationsFailed"/> == 0).</param>
/// <param name="Collectors">Per-collector capture provenance: the spec-169 coverage rows plus provider outcomes.</param>
/// <param name="AttentionPublisherCoverage">
/// Spec 196 §3: how this pass's ATTEMPTED observation candidates distribute across the curated attention
/// publisher tier map, plus the largest unclassified publishers. A <b>CAPTURE-FLOW DIAGNOSTIC</b> for
/// curating that map — explicitly NOT the <c>AttentionScore</c> input, which consumes tier-weighted DISTINCT
/// publishers per company over the scoring window. Its tier counts sum to
/// <see cref="ObservationsAttempted"/>. Trailing and nullable: <c>null</c> on a pre-196 batch means NOT
/// RECORDED, never a measured zero. It carries its OWN
/// <see cref="AttentionPublisherCoverageSummary.CurrentVersion"/> token, so
/// <see cref="SchemaVersion"/> — which is shared with every individual observation record — is UNCHANGED.
/// </param>
public sealed record NewsObservationBatch(
    Guid BatchId,
    DateTimeOffset RunAsOfUtc,
    string SchemaVersion,
    bool FullUniverse,
    int ObservationsAttempted,
    int ObservationsWritten,
    int ObservationsCrossRunDeduped,
    int ObservationsFailed,
    bool CaptureProven,
    IReadOnlyList<NewsObservationCollectorCapture> Collectors,
    AttentionPublisherCoverageSummary? AttentionPublisherCoverage = null);

/// <summary>
/// One observation-emitting collector's capture provenance inside a batch: its spec-169 per-company/query
/// coverage rows (which carry the <c>MissingFeed</c> / <c>SourceFailure</c> / <c>ResultLimitReached</c>
/// vocabulary) and its per-source provider failures — whose typed detail strings are where the malformed /
/// unreachable / rate-limit outcomes live. Captured from the UNMERGED per-collector result, for the same
/// structural reason spec 169 captures coverage there: the merge discards attribution.
/// <para>
/// <b>Spec 190 naming note:</b> a "result limit reached" here is Radar's own EFFECTIVE/LOCAL retention
/// limit, not a proven provider ceiling — see <see cref="AnyFeedHitProviderCap"/>.
/// </para>
/// </summary>
/// <param name="CollectorName">The collector's stable provenance name.</param>
/// <param name="CompanyCoverage">The collector's spec-169 per-company coverage rows; <c>null</c> = not recorded ⇒ unproven.</param>
/// <param name="ProviderFailures">Per-source failures (feed name, token, typed reason — e.g. "HTTP 429 (rate limited)", "malformed XML").</param>
/// <param name="AnyFeedHitProviderCap">
/// <b>HISTORICAL MISNAMER, retained only so existing artifacts stay readable.</b> The fact it has always
/// carried is that some feed's raw result count reached Radar's own EFFECTIVE/LOCAL retention limit — never
/// a measured provider ceiling. It is non-nullable, so new captures keep MIRRORING the effective-limit fact
/// into it for old readers; <b>that mirror is not evidence about provider behaviour</b>. New code reads
/// <see cref="AnyFeedHitEffectiveResultLimit"/> and treats this member only as a legacy fallback.
/// </param>
/// <param name="AnyFeedHitEffectiveResultLimit">
/// Spec 190, correctly named: true when any of this collector's feeds reached the effective clamped LOCAL
/// retention limit (POSSIBLE truncation). <c>null</c> = not recorded (a pre-190 artifact or a collector that
/// records no per-company coverage) — never <c>false</c>.
/// </param>
/// <param name="AnyFeedConfirmedLocalTruncation">
/// Spec 190: true when at least one feed's already-fetched response held a structurally valid item BEYOND
/// that local limit, so Radar's own discard is CONFIRMED rather than suspected. <c>false</c> means no such
/// item was observed — it does NOT prove the provider had no further results. <c>null</c> = not recorded.
/// </param>
public sealed record NewsObservationCollectorCapture(
    string CollectorName,
    IReadOnlyList<CollectorCompanyCoverage>? CompanyCoverage,
    IReadOnlyList<SourceFailure> ProviderFailures,
    bool AnyFeedHitProviderCap,
    bool? AnyFeedHitEffectiveResultLimit = null,
    bool? AnyFeedConfirmedLocalTruncation = null);
