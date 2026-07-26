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
/// </summary>
/// <param name="AsOfPoints">How many historical as-of instants were scored at.</param>
/// <param name="Strategies">How many strategies were replayed over those instants.</param>
/// <param name="SnapshotsWritten">Total snapshots written to the replay-scoped store.</param>
public sealed record ReplayResult(int AsOfPoints, int Strategies, int SnapshotsWritten);
