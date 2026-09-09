using Radar.Application.Identity;
using Radar.Application.Storage;

namespace Radar.Application.Filings;

/// <summary>
/// Where one <see cref="ReportedMetricOutboxEnvelope"/> currently sits. The zero value is
/// <see cref="Pending"/> deliberately (the spec-182 convention inverted for this case): a record whose
/// state cannot be read must be RETRIED, never assumed done.
/// </summary>
public enum ReportedMetricOutboxState
{
    /// <summary>The ledger write for this envelope has not yet succeeded. The default for a missing field.</summary>
    Pending = 0,

    /// <summary>The ledger write succeeded (or the record was already durable) and the envelope is kept as provenance.</summary>
    Acknowledged = 1,
}

/// <summary>
/// SPEC 216 §2 — the COMPLETE, ready-to-write ledger payload for ONE analyzed filing, persisted at the one
/// point in the pipeline where every routing field is known: after the collection pass has resolved the
/// company, and BEFORE the analyzed-filing cache is stamped "extraction done".
/// <para>
/// <b>Why it is its own durable store and not a cache flag.</b> Marking the cache unacknowledged and
/// re-analyzing would re-fetch from SEC and re-run the model, which can return a DIFFERENT direction; and
/// <see cref="ReportedMetricExtraction"/> carries the metrics, accession, form and reader identity but NOT
/// the resolved <see cref="CompanyId"/>, <see cref="EvidenceId"/> or <see cref="FilingDateUtc"/> a ledger
/// record needs, while <see cref="IAnalyzedFilingCache"/> cannot enumerate pending entries at all. Those
/// values existed only transiently in the collection pass, so a failed ledger write lost them permanently
/// — counted, but lost. Nothing in this envelope needs re-derivation: replay is a
/// <see cref="IReportedMetricStore.WriteIfNewAsync"/> call, with no fetch and no model call.
/// </para>
/// </summary>
/// <param name="OutboxId">Content-derived from policy + accession (<see cref="IdentityFor"/>): the same accession re-enqueued under the same policy is the same envelope.</param>
/// <param name="Policy">The <see cref="ReportedMetricsPolicy.Version"/> the metrics were verified under, and the policy the ledger write is made under.</param>
/// <param name="CompanyId">The RESOLVED company, or <c>null</c> when resolution did not succeed at enqueue time — a ROUTABLE state (see <see cref="IReportedMetricOutbox"/>), never a loss.</param>
/// <param name="CompanyMention">The mention text the signal resolved from, kept so an unresolved envelope can be re-run through company resolution on a later pass.</param>
/// <param name="CompanyHints">The collector-supplied company hints for that resolution attempt (empty, never null).</param>
/// <param name="EvidenceId">The earnings-8-K evidence the read was made for (provenance on every ledger record).</param>
/// <param name="Accession">The dashed SEC accession the release was read from.</param>
/// <param name="Form">The filing form the evidence declares.</param>
/// <param name="FilingDateUtc">The evidence's <c>PublishedAtUtc ?? CollectedAtUtc</c> — when the company stated the figures.</param>
/// <param name="ReaderIdentity">The provider:model identity that read the release.</param>
/// <param name="Metrics">The verified readings, in order, exactly as the analyzer returned them.</param>
/// <param name="DroppedUnrecognised">The verifier's drop counts, carried so the accounting survives a restart.</param>
/// <param name="DroppedUnverified">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="DroppedDuplicate">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="PriorPairsDroppedIncomplete">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="DroppedMetricNotInQuote">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="DroppedPeriodNotInQuote">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="DroppedFragment">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="DroppedNotAssociated">See <see cref="VerifiedReportedMetrics"/>.</param>
/// <param name="State">Pending or acknowledged. A missing value on disk reads as <see cref="ReportedMetricOutboxState.Pending"/> — retry, never assume done.</param>
/// <param name="Attempts">How many ledger writes have been attempted for this envelope. PERSISTED, so the three-attempt warning survives restarts; a missing value reads as 0.</param>
/// <param name="CreatedAtUtc">When the envelope was first enqueued.</param>
/// <param name="LastAttemptAtUtc">When the last ledger write was attempted; <c>null</c> = never attempted.</param>
public sealed record ReportedMetricOutboxEnvelope(
    Guid OutboxId,
    string Policy,
    Guid? CompanyId,
    string CompanyMention,
    IReadOnlyList<string> CompanyHints,
    Guid EvidenceId,
    string Accession,
    string Form,
    DateTimeOffset FilingDateUtc,
    string ReaderIdentity,
    IReadOnlyList<ReportedMetricReading> Metrics,
    int DroppedUnrecognised,
    int DroppedUnverified,
    int DroppedDuplicate,
    int PriorPairsDroppedIncomplete,
    int DroppedMetricNotInQuote,
    int DroppedPeriodNotInQuote,
    int DroppedFragment,
    int DroppedNotAssociated,
    ReportedMetricOutboxState State,
    int Attempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc)
{
    /// <summary>
    /// The content-derived envelope identity: one envelope per (policy, accession). It folds the policy for
    /// the same reason the ledger path does — a re-analysis under a later policy is a NEW payload beside
    /// the old one, never a collision with it.
    /// </summary>
    public static Guid IdentityFor(string policy, string accession) =>
        DeterministicGuid.FromCanonicalString($"radar:reported-metric-outbox:{policy}:{accession}");

    /// <summary>
    /// The ledger records this envelope writes, built ONCE here so the enqueue path and every later replay
    /// produce a byte-identical payload (there is no second construction site to drift from).
    /// Returns <c>null</c> when the company is not resolved — an unresolved envelope has nothing to file
    /// under and must go back through resolution first.
    /// </summary>
    public IReadOnlyList<ReportedMetricRecord>? ToRecords() => CompanyId is not { } companyId
        ? null
        :
        [
            .. Metrics.Select(m => new ReportedMetricRecord(
                Id: ReportedMetricRecord.IdentityFor(Accession, m.Metric, m.Period, Policy),
                CompanyId: companyId,
                Accession: Accession,
                EvidenceId: EvidenceId,
                FilingDateUtc: FilingDateUtc,
                Form: Form,
                Metric: m.Metric,
                Value: m.Value,
                Unit: m.Unit,
                Period: m.Period,
                PriorValue: m.PriorValue,
                PriorPeriod: m.PriorPeriod,
                Quote: m.Quote,
                ReaderIdentity: ReaderIdentity,
                Verification: ReportedMetricVerification.Verbatim,
                Policy: Policy)),
        ];
}

/// <summary>
/// SPEC 216 §2 — the durable outbox that stands between "the model told us these figures" and "the ledger
/// holds them". Implemented in Infrastructure under <c>{ledger root}/outbox/</c>.
/// <list type="bullet">
/// <item><b>Ordering.</b> analysis (transient extraction) → the collection pass resolves the company →
/// <see cref="EnqueueAsync"/> (DURABLE) → only on a durable enqueue is the analyzed-filing record stamped
/// <c>reportedMetricsPolicy</c> → ledger write → <see cref="AcknowledgeAsync"/>. If the process dies before
/// the envelope is durable, the cache is unstamped and the filing re-analyzes next run exactly as any
/// uncached read does (nothing was persisted to lose). If it dies after, the envelope replays.</item>
/// <item><b>Replay.</b> <see cref="EnumeratePendingAsync"/> runs BEFORE the fresh reads of each pass, so a
/// stuck envelope is retried before new work is added. Replay is a store write and nothing else — no
/// fetch, no model call.</item>
/// <item><b>Unresolved company is routable, not lost.</b> An envelope with a null
/// <see cref="ReportedMetricOutboxEnvelope.CompanyId"/> is re-run through company resolution on every
/// replay (resolution may succeed later — a universe addition, a hint fix) and re-enqueued under the
/// company once it does.</item>
/// <item><b>Acknowledged envelopes are KEPT</b> (moved, never deleted) so the ledger's provenance chain
/// stays walkable.</item>
/// </list>
/// An AD-14 analogue: operational routing data, never evidence, never a signal source, never a scoring
/// input. Every method degrades gracefully — a disk failure is a typed/false outcome, never a throw; only
/// caller cancellation propagates.
/// </summary>
public interface IReportedMetricOutbox
{
    /// <summary>
    /// Persists <paramref name="envelope"/> as PENDING. An envelope already on disk for the same
    /// (policy, accession) is <see cref="DurableWriteOutcome.AlreadyAvailable"/> — the payload is durable
    /// either way, which is exactly what the caller's "may I stamp the cache now?" question asks.
    /// </summary>
    Task<DurableWriteResult> EnqueueAsync(ReportedMetricOutboxEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Every PENDING envelope, in deterministic order (company id, then accession — AD-3; unresolved
    /// envelopes sort first under the empty company key). An unreadable file is named and skipped.
    /// </summary>
    Task<IReadOnlyList<ReportedMetricOutboxEnvelope>> EnumeratePendingAsync(CancellationToken ct);

    /// <summary>
    /// Records ONE failed ledger attempt: increments the PERSISTED
    /// <see cref="ReportedMetricOutboxEnvelope.Attempts"/> and stamps
    /// <see cref="ReportedMetricOutboxEnvelope.LastAttemptAtUtc"/>. Returns the updated envelope, or
    /// <c>null</c> when the update could not be persisted (counted by the caller, never silent).
    /// </summary>
    Task<ReportedMetricOutboxEnvelope?> MarkAttemptAsync(
        ReportedMetricOutboxEnvelope envelope, DateTimeOffset attemptedAtUtc, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="envelope"/> acknowledged and MOVES it to the append-only acknowledged area.
    /// Returns false when the move could not be completed (the envelope stays pending and replays).
    /// </summary>
    Task<bool> AcknowledgeAsync(ReportedMetricOutboxEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Whether ANY envelope — pending or acknowledged — exists for (<paramref name="policy"/>,
    /// <paramref name="accession"/>). This is what makes a cache record carrying a policy stamp with NO
    /// envelope behind it (the 215-era shape) VISIBLE rather than silently treated as "extraction done,
    /// ledger empty".
    /// </summary>
    Task<bool> ExistsAsync(string policy, string accession, CancellationToken ct);
}
