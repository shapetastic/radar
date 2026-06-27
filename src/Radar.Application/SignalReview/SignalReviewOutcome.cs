using Radar.Domain.Signals;

namespace Radar.Application.SignalReview;

/// <summary>
/// Result of reviewing a single <see cref="Signal"/>: the (possibly adjusted) reviewed signal
/// produced via a <c>with</c> expression, plus the immutable <see cref="Radar.Domain.Signals.SignalReview"/> audit record.
/// </summary>
public sealed record SignalReviewOutcome(
    Signal ReviewedSignal,
    Radar.Domain.Signals.SignalReview Review);
