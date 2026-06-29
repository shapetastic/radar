namespace Radar.Application.Evidence;

using Radar.Domain.Evidence;

/// <summary>
/// Insert-only raw-evidence file store. Writes immutable evidence to local JSON, never overwriting an
/// existing file (provenance, AD-1). Returns true if a new file was written, false if it already
/// existed (skip).
/// </summary>
public interface IRawEvidenceStore
{
    Task<bool> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct);
}
