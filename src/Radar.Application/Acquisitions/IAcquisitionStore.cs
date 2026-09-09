using Radar.Application.Storage;

namespace Radar.Application.Acquisitions;

/// <summary>
/// The append-only acquisitions store (spec 217 §1), implemented in Infrastructure at
/// <c>{root}/{companyId}/{accession}.json</c> — one file per recognised (company, accession). Insert-if-new
/// (the spec-206 raw-evidence / spec-216 ledger pattern): an existing file is
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/> and is never overwritten; a disk failure is
/// <see cref="DurableWriteOutcome.Failed"/> and never throws; only caller cancellation propagates.
/// <para>
/// The READ side is consumed by scoring (the <c>CorporateAction</c> supersede and the
/// <c>companyStatusAtScoring</c> stamp), the weekly report (the banner, the
/// <c>## Acquisitions pending</c> section and the per-strategy footer) and the efficacy comparison (the
/// <c>CorporateActionInWindow</c> exclusion and the <c>excess-vs-universe-v2</c> peer-mean exclusion). It
/// is never evidence and never a signal source.
/// </para>
/// </summary>
public interface IAcquisitionStore
{
    /// <summary>
    /// Writes <paramref name="record"/> if no file for its (company, accession) exists. Never throws for a
    /// disk failure — the outcome is returned so the caller can COUNT a loss instead of reporting a
    /// success.
    /// </summary>
    Task<DurableWriteResult> WriteIfNewAsync(PendingAcquisitionRecord record, CancellationToken ct);

    /// <summary>
    /// Every recognised acquisition on disk, in a deterministic order (company id, then accession
    /// ordinal). An unreadable file is skipped with a Warning and COUNTED on
    /// <see cref="AcquisitionStoreReadResult.Unreadable"/> — never silently dropped, never a throw.
    /// </summary>
    Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// True when a record for (<paramref name="companyId"/>, <paramref name="accession"/>) is already on
    /// disk — the cheap per-filing check that keeps a re-run from re-fetching a filing it has already
    /// recognised.
    /// </summary>
    Task<bool> ExistsAsync(Guid companyId, string accession, CancellationToken ct);
}

/// <summary>
/// One read of the whole acquisitions store: the records, plus how many files could not be parsed. The
/// unreadable count travels WITH the records (never only in a log) because a caller that silently read
/// fewer acquisitions than exist would leave a closed thesis open with nothing to show for it.
/// </summary>
public sealed record AcquisitionStoreReadResult(
    IReadOnlyList<PendingAcquisitionRecord> Records,
    int Unreadable)
{
    /// <summary>
    /// Whether the store was actually READ. <c>false</c> means the read itself failed — the directory could
    /// not be enumerated at all — so an empty <see cref="Records"/> is NOT a measured zero and no consumer
    /// may state an absence from it.
    /// <para>
    /// This is distinct from <see cref="Unreadable"/>, which counts individual FILES that failed to parse
    /// during a read that otherwise succeeded. The two are different facts: one says "some records may be
    /// missing from this answer", the other says "there is no answer".
    /// </para>
    /// </summary>
    public bool Readable { get; init; } = true;

    /// <summary>
    /// The MEASURED empty read: the store was reached and holds nothing (or no store is registered, in
    /// which case <see cref="PendingAcquisitions.None"/> carries the "we did not look" flag instead).
    /// </summary>
    public static readonly AcquisitionStoreReadResult Empty = new([], 0);

    /// <summary>
    /// The read that FAILED — the store root could not be enumerated. Its emptiness is "not measured", so
    /// <see cref="PendingAcquisitions.RecognitionAvailable"/> is false for it and the weekly report's
    /// <c>## Acquisitions pending</c> section stays SILENT rather than asserting that no company is under a
    /// recognised acquisition. A store that cannot be read is exactly when a closed thesis would otherwise
    /// be rendered as open.
    /// </summary>
    public static readonly AcquisitionStoreReadResult Unavailable = new([], 0) { Readable = false };
}
