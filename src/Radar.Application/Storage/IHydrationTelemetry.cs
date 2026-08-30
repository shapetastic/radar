namespace Radar.Application.Storage;

/// <summary>
/// Spec 203 §1: how long a durable store's one-time hydration walk took in THIS process. Exposed by the
/// signal and raw-evidence file stores (each hydrates lazily, once per instance) so the pipeline runners can
/// report a measured stage duration and carry it on the run record.
/// </summary>
public interface IHydrationTelemetry
{
    /// <summary>
    /// The elapsed time of the hydration walk this instance performed, measured with the monotonic
    /// <see cref="TimeProvider.GetTimestamp"/> / <see cref="TimeProvider.GetElapsedTime(long)"/> pair.
    /// <c>null</c> means this instance has NOT hydrated in this process — never <see cref="TimeSpan.Zero"/>,
    /// because "not measured" and "measured as instantaneous" are different facts.
    /// </summary>
    TimeSpan? HydrationElapsed { get; }
}
