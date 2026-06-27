namespace Radar.Application.SignalReview;

using Radar.Domain.Signals;

/// <summary>
/// Result of reviewing a single <see cref="Signal"/>: the (possibly adjusted) reviewed signal
/// produced via a <c>with</c> expression, plus the immutable <see cref="SignalReview"/> audit record.
/// </summary>
public sealed record SignalReviewOutcome(
    Signal ReviewedSignal,
    SignalReview Review);
