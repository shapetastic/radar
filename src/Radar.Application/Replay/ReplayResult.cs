namespace Radar.Application.Replay;

/// <summary>
/// What a replay run actually did (spec 139), so the operator can reconcile the output on disk against what
/// was asked for. Purely observational — replay's product is the snapshots it wrote, and this is the receipt.
/// <para>
/// <see cref="SnapshotsWritten"/> is <c>AsOfPoints × Strategies × companies</c> for a completed run: replay
/// scores <b>every</b> company at every as-of point (a company with no known signals yields a valid neutral
/// zero-evidence-link snapshot, exactly as forward scoring does), so a series that is discontinuous in the
/// output means something failed, not that a company had nothing to say.
/// </para>
/// <para>
/// <b><see cref="Strategies"/> counts the strategies that actually EXECUTED (spec 206 §1)</b> — a deliberate
/// correction of the pre-206 meaning, which counted every configured strategy even when the config-durability
/// precondition skipped one. A skipped strategy produced no snapshot, so counting it would break the
/// receipt's own arithmetic. The skipped strategies are named on
/// <see cref="StrategiesSkippedForUnpersistedConfig"/>; executed + skipped = configured.
/// </para>
/// </summary>
/// <param name="AsOfPoints">How many historical as-of instants were scored at.</param>
/// <param name="Strategies">How many strategies actually executed (were replayed) over those instants.</param>
/// <param name="SnapshotsWritten">Total snapshots written to the replay-scoped store.</param>
/// <param name="StrategiesSkippedForUnpersistedConfig">
/// The strategies (run order) that wrote NO replay snapshot because their effective scoring-config record
/// could not be made durable (spec 206 §1, the spec-202 §1 forward precondition applied to replay).
/// <c>null</c> means none was skipped — never an empty list pretending to be recorded history. A later
/// replay retries naturally (the store is content-addressed and insert-if-new).
/// </param>
public sealed record ReplayResult(
    int AsOfPoints,
    int Strategies,
    int SnapshotsWritten,
    IReadOnlyList<string>? StrategiesSkippedForUnpersistedConfig = null);
