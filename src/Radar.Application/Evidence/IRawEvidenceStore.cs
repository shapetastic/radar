namespace Radar.Application.Evidence;

using Radar.Application.Storage;
using Radar.Domain.Evidence;

/// <summary>
/// Insert-only raw-evidence file store. Writes immutable evidence to local JSON, never overwriting an
/// existing file (provenance, AD-1), and reports a typed outcome (spec 206 §3) — the old <c>bool</c>
/// conflated "healthy dedupe skip" with "disk failure", so a caller could neither count the loss nor trust
/// the dedupe:
/// <list type="bullet">
/// <item><description><see cref="DurableWriteOutcome.Written"/> — this call durably created the immutable
/// raw record; the evidence is NEW to the accrued store.</description></item>
/// <item><description><see cref="DurableWriteOutcome.AlreadyAvailable"/> — the same immutable evidence is
/// already present in the hydrated durable index/store (a dedupe, cross-run or within-run); durable, but not
/// produced by this call.</description></item>
/// <item><description><see cref="DurableWriteOutcome.Failed"/> — the target did not become a trustworthy
/// durable record: a disk failure, or an existing path that cannot be resolved as the same valid evidence.
/// The item is indexed NOWHERE, so a later call in the same process naturally retries.</description></item>
/// </list>
/// <para>
/// Since spec 206 §3 this outcome is the collection pass's ADMISSION decision: no extraction, review or
/// signal persistence may happen for evidence whose raw record is not confirmed durable.
/// </para>
/// </summary>
public interface IRawEvidenceStore
{
    Task<DurableWriteResult> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct);
}
