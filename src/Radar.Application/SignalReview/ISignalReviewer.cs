using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.SignalReview;

/// <summary>
/// Stage 5 reviewer: inspects a <see cref="Signal"/> together with its source
/// <see cref="EvidenceItem"/> and produces a <see cref="SignalReviewOutcome"/>. The caller is
/// responsible for passing the evidence whose <c>Id == signal.EvidenceId</c>; an implementation may
/// record an issue if they do not match rather than throwing.
/// </summary>
public interface ISignalReviewer
{
    Task<SignalReviewOutcome> ReviewAsync(Signal signal, EvidenceItem evidence, CancellationToken ct);
}
