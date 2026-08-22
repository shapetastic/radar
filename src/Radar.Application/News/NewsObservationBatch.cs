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
    IReadOnlyList<NewsObservationCollectorCapture> Collectors);

/// <summary>
/// One observation-emitting collector's capture provenance inside a batch: its spec-169 per-company/query
/// coverage rows (which carry the <c>MissingFeed</c> / <c>SourceFailure</c> / <c>ResultLimitReached</c>
/// vocabulary) and its per-source provider failures — whose typed detail strings are where the provider
/// cap / malformed / unreachable / rate-limit outcomes live. Captured from the UNMERGED per-collector
/// result, for the same structural reason spec 169 captures coverage there: the merge discards attribution.
/// </summary>
/// <param name="CollectorName">The collector's stable provenance name.</param>
/// <param name="CompanyCoverage">The collector's spec-169 per-company coverage rows; <c>null</c> = not recorded ⇒ unproven.</param>
/// <param name="ProviderFailures">Per-source failures (feed name, token, typed reason — e.g. "HTTP 429 (rate limited)", "malformed XML").</param>
/// <param name="AnyFeedHitProviderCap">True when any feed's raw result count reached the effective provider cap (potential truncation).</param>
public sealed record NewsObservationCollectorCapture(
    string CollectorName,
    IReadOnlyList<CollectorCompanyCoverage>? CompanyCoverage,
    IReadOnlyList<SourceFailure> ProviderFailures,
    bool AnyFeedHitProviderCap);
