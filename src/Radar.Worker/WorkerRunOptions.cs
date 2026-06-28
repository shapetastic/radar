namespace Radar.Worker;

/// <summary>
/// The small, already-resolved schedule shape the <see cref="Worker"/> consumes — keeps the Worker free
/// of raw configuration parsing.
/// </summary>
public sealed class WorkerRunOptions
{
    /// <summary>Run the pipeline once then stop the application (true), or loop on an interval (false).</summary>
    public bool RunOnce { get; init; } = true;

    /// <summary>Interval between runs when <see cref="RunOnce"/> is false.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}
