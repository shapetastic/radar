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
    /// <summary>
    /// Mirrors the reviewed <paramref name="signal"/> and its <paramref name="review"/> to disk.
    /// The review must belong to the signal (<c>review.SignalId == signal.Id</c>), otherwise an
    /// <see cref="ArgumentException"/> is thrown to protect the review→signal provenance trace.
    /// </summary>
    Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct);
}
