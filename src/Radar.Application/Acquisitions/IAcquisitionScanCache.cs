namespace Radar.Application.Acquisitions;

/// <summary>
/// One cached <c>acqscan-v1</c> answer for one item-1.01 filing (spec 217 §1), keyed by SEC accession — the
/// negative half of the "cached like the earnings read" rule. Without it the pass would re-fetch all 177
/// NOT-recognised item-1.01 filings on every run forever; the acquisitions store alone caches only the
/// recognitions.
/// <para>
/// <b>Only an AUTHORITATIVE answer is cached.</b> A failed fetch and an
/// <see cref="AcquisitionScanOutcome.EmptyBody"/> read are never written, so a transient SEC block can
/// never permanently suppress a filing (the spec-114 precedent). A recognition is cached too, but the
/// acquisitions store — not this cache — is the record.
/// </para>
/// <para>
/// <see cref="ScanVersion"/> IS the invalidation key: an entry produced by a different scan version is a
/// MISS and the filing is re-scanned, so bumping <see cref="AcquisitionAgreementScan.Version"/> retires
/// every cached answer without deleting a byte (append-only, AD-8).
/// </para>
/// </summary>
public sealed record AcquisitionScanCacheRecord(
    string Accession,
    Guid CompanyId,
    AcquisitionScanOutcome Outcome,
    string ScanVersion,
    DateTimeOffset ScannedAtUtc);

/// <summary>
/// The heal-forward scan cache. Implemented in Infrastructure over the file system; a read failure is a
/// MISS (never a throw), and a write failure is reported so the caller can count it rather than assume it
/// landed.
/// </summary>
public interface IAcquisitionScanCache
{
    /// <summary>The cached answer for <paramref name="accession"/>, or null on a miss/unreadable entry.</summary>
    Task<AcquisitionScanCacheRecord?> TryGetAsync(string accession, CancellationToken ct);

    /// <summary>
    /// Records an authoritative answer. Returns false when nothing reached disk — the caller counts that
    /// as a re-fetch it will pay for again, never as a cached result.
    /// </summary>
    Task<bool> SetAsync(AcquisitionScanCacheRecord record, CancellationToken ct);
}
