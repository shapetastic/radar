namespace Radar.Worker;

/// <summary>
/// Which pass this process runs (spec 144). Selected by <c>Radar:RunMode</c>, reconciled with the spec-139
/// <c>Radar:Replay:Enabled</c> switch by <see cref="RadarRunModes.Resolve"/>.
/// </summary>
public enum RadarRunMode
{
    /// <summary>Collect AND score in one pass, then report — the default and the pre-144 behaviour.</summary>
    Full,

    /// <summary>Stages 1–5 only: collect evidence and store signals. No scoring, no report.</summary>
    Collect,

    /// <summary>Stage 6 (+ optionally 7) over the accrued stores. No collector, no AI read.</summary>
    Score,

    /// <summary>The read-only historical as-of replay (spec 139), which REPLACES the pipeline run.</summary>
    Replay,
}

/// <summary>
/// Parses and reconciles the run-mode configuration. Verb parsing lives in the composition root
/// (<c>Radar.Worker</c>): <c>Radar.Application</c> receives an already-resolved pipeline implementation and
/// never sees <c>IConfiguration</c> or a mode string.
/// </summary>
internal static class RadarRunModes
{
    private const string FullToken = "full";
    private const string CollectToken = "collect";
    private const string ScoreToken = "score";
    private const string ReplayToken = "replay";

    /// <summary>The accepted <c>Radar:RunMode</c> tokens, for messages.</summary>
    public const string ValidTokens = "\"full\", \"collect\", \"score\", \"replay\"";

    /// <summary>
    /// Resolves the effective mode from <c>Radar:RunMode</c> and the pre-existing
    /// <c>Radar:Replay:Enabled</c> boolean (spec 139), which must keep working exactly as it does today
    /// (<c>run-radar.ps1 -Replay</c> sets it and nothing else).
    /// <list type="bullet">
    /// <item><description><c>Radar:RunMode</c> blank/absent ⇒ <see cref="RadarRunMode.Full"/>; an unknown
    /// value FAILS FAST listing the valid tokens (a typo'd verb must not silently run the wrong
    /// pass).</description></item>
    /// <item><description><c>Replay:Enabled</c> true with mode <c>full</c> (or unset) ⇒
    /// <see cref="RadarRunMode.Replay"/> — byte-for-byte today's behaviour.</description></item>
    /// <item><description><c>Replay:Enabled</c> true with mode <c>collect</c>/<c>score</c> ⇒ FAIL FAST
    /// naming BOTH keys. The two describe different runs, and a replay silently winning (or silently losing)
    /// would produce a plausible-looking series answering the other question.</description></item>
    /// <item><description>mode <c>replay</c> with <c>Replay:Enabled</c> false ⇒
    /// <see cref="RadarRunMode.Replay"/>; the missing From/To then fails fast in the replay-plan builder, so
    /// there is one message for "no replay range" rather than two.</description></item>
    /// </list>
    /// </summary>
    public static RadarRunMode Resolve(string? runMode, bool replayEnabled)
    {
        var raw = runMode?.Trim() ?? string.Empty;

        var mode = raw.Length == 0
            ? RadarRunMode.Full
            : raw switch
            {
                _ when raw.Equals(FullToken, StringComparison.OrdinalIgnoreCase) => RadarRunMode.Full,
                _ when raw.Equals(CollectToken, StringComparison.OrdinalIgnoreCase) => RadarRunMode.Collect,
                _ when raw.Equals(ScoreToken, StringComparison.OrdinalIgnoreCase) => RadarRunMode.Score,
                _ when raw.Equals(ReplayToken, StringComparison.OrdinalIgnoreCase) => RadarRunMode.Replay,
                _ => throw new InvalidOperationException(
                    $"Radar:RunMode is '{runMode}', which is not a run mode; valid values are {ValidTokens}. "
                        + "Omit Radar:RunMode to run the combined collect+score pass."),
            };

        if (!replayEnabled)
        {
            return mode;
        }

        if (mode is RadarRunMode.Collect or RadarRunMode.Score)
        {
            throw new InvalidOperationException(
                $"Radar:RunMode is '{Token(mode)}' while Radar:Replay:Enabled is true; those describe two "
                    + "different runs and one would have to silently win. A replay is a read-only hypothesis "
                    + "written to Radar:ReplayDirectory, while collect/score are live passes that write the "
                    + "durable stores and the live score series. Set Radar:Replay:Enabled to false, or clear "
                    + "Radar:RunMode (or set it to \"replay\") to run the replay.");
        }

        // full/unset + Replay:Enabled ⇒ replay, exactly as before spec 144 existed.
        return RadarRunMode.Replay;
    }

    /// <summary>The canonical config token for a mode (used in messages and the startup log line).</summary>
    public static string Token(RadarRunMode mode) => mode switch
    {
        RadarRunMode.Collect => CollectToken,
        RadarRunMode.Score => ScoreToken,
        RadarRunMode.Replay => ReplayToken,
        _ => FullToken,
    };
}
