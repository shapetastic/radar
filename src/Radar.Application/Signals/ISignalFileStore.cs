namespace Radar.Application.Signals;

using Radar.Domain.Signals;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record. Writes one JSON file per signal under
/// the signals root, capturing provenance (evidence id, resolved company id) and the embedded review.
/// A signal is upsert-by-Id (AD-1): an existing file for the same signal id is overwritten
/// (last-write-wins). Returns the written path.
/// </summary>
public interface ISignalFileStore
{
    Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct);
}
