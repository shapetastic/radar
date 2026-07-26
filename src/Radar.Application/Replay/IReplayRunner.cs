namespace Radar.Application.Replay;

/// <summary>
/// The replay harness (spec 139): scores the configured strategies across a series of historical as-of
/// instants from the already-stored signals, writing the resulting snapshots to a replay-scoped, labelled
/// location. Read-only over signals and evidence; it mutates nothing except its own output.
/// </summary>
public interface IReplayRunner
{
    /// <summary>
    /// Runs the configured <see cref="ReplayPlan"/> to completion and returns what it did. Cancellation
    /// propagates (a partially-written replay is re-runnable and idempotent, so there is nothing to unwind).
    /// </summary>
    Task<ReplayResult> RunAsync(CancellationToken ct);
}
