using Microsoft.Extensions.Logging;

namespace Radar.Application.Scoring;

/// <summary>
/// The startup tripwire that replaces "the fingerprint must never change" (spec 141). The score series is
/// keyed by <see cref="ScoringStrategyDefinition.Name"/> (see <see cref="ScoreSeriesKey"/>), which is only
/// honest if a strategy is <b>immutable by convention</b>: to change a strategy you add a NEW named strategy
/// (<c>momentum</c> → <c>momentum-v2</c>), never edit one in place. This guard enforces that convention.
/// <para>
/// For each configured strategy it compares the engine's computed <c>ScoringConfigVersion</c> against the
/// fingerprint last recorded for that NAME:
/// <list type="bullet">
/// <item><description>no record ⇒ record it and continue (a brand-new strategy);</description></item>
/// <item><description>equal ⇒ continue (the overwhelmingly common case);</description></item>
/// <item><description>different ⇒ <b>throw</b>, naming the strategy and both fingerprints.</description></item>
/// </list>
/// A collector toggle CANNOT trip it: the enabled-collector set is no longer a fingerprint input (it is
/// recorded on the snapshot as <c>CollectionProvenance</c>), which is exactly what the spec-141 descriptor
/// split buys. What DOES trip it is a real edit to a named strategy's weights, profile, signal types,
/// formula structure <b>expressed as a <c>_formula.Version</c> bump</b> (the AD-6 obligation — the guard sees
/// the version token, never the formula's code) or extractor rule structure — i.e. the cases where a series
/// would otherwise silently continue under one name while measuring something else.
/// </para>
/// <para>
/// ⚠ <b>THE FORMULA-STRUCTURE ARM IS ONLY AS STRONG AS AD-6 COMPLIANCE, and spec 149 shipped the
/// counterexample.</b> Spec 149 added the notedness discount to the COMPOSITION of
/// <see cref="RadarScoreFormulaV9"/> without bumping past <c>radar-formula-v9</c> (v10 was out of scope), and
/// the default <see cref="ScoringWeights"/> did not move either — so a v9 strategy behaves differently before
/// and after that slice while its fingerprint is unchanged and this guard stays silent. Pre- and post-149 v9
/// snapshots under one name are therefore exactly the "silently continuing under one name while measuring
/// something else" case this guard otherwise catches. The recorded remedy is spec 141's
/// immutable-by-convention rule applied by hand: give the retuned strategy a NEW NAME
/// (<c>patents-led</c> → <c>patents-led-v2</c>), which re-keys the series via <see cref="ScoreSeriesKey"/>
/// without needing the stamp to move. See the AD-6 paragraph on <see cref="RadarScoreFormulaV9"/> for the full
/// reasoning and the mitigating facts; keep the two in step.
/// </para>
/// <para>
/// It runs BEFORE any collection work so a misconfiguration costs no network calls, and it is read-mostly:
/// the only write is recording a name's fingerprint for the first time (or after a legitimate rename), which
/// is best-effort like every other file store. A store read failure degrades to "unrecorded" and never trips
/// (AD-8 graceful degrade — a disk hiccup must not fail a run), while
/// <see cref="OperationCanceledException"/> still propagates.
/// </para>
/// </summary>
public static class StrategyIdentityGuard
{
    /// <summary>
    /// Verifies every strategy runtime's fingerprint against the record for its name, recording first
    /// sightings. Throws <see cref="InvalidOperationException"/> naming the first strategy whose fingerprint
    /// moved.
    /// </summary>
    public static async Task VerifyAsync(
        IReadOnlyList<ScoringStrategyRuntime> strategies,
        IScoringConfigStore store,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            var name = strategy.Definition.Name;
            var computed = strategy.Engine.EffectiveConfig.Fingerprint;
            var recorded = await store.ReadStrategyFingerprintAsync(name, ct).ConfigureAwait(false);

            if (recorded is null)
            {
                var recordWrite = await store
                    .RecordStrategyFingerprintAsync(name, computed, ct)
                    .ConfigureAwait(false);
                if (recordWrite.Written)
                {
                    logger.LogInformation(
                        "Recorded first identity for scoring strategy {StrategyName}: {Fingerprint}.",
                        name, computed);
                }
                else
                {
                    // Spec 201 §1: a record that never landed must not be reported as recorded. The run
                    // continues (the guard is read-mostly and best-effort, AD-8), but the operator is told
                    // that the tripwire is UNARMED for this name: the next run will find no record and
                    // silently record whatever it computes then.
                    logger.LogWarning(
                        "Could NOT record the first identity for scoring strategy {StrategyName} "
                            + "({Fingerprint}) at {Path}: the write degraded gracefully. The strategy "
                            + "identity tripwire is not armed for this name until a run succeeds in "
                            + "recording it.",
                        name, computed, recordWrite.Path);
                }

                continue;
            }

            if (string.Equals(recorded, computed, StringComparison.Ordinal))
            {
                logger.LogDebug(
                    "Scoring strategy {StrategyName} identity unchanged ({Fingerprint}).", name, computed);
                continue;
            }

            throw new InvalidOperationException(
                $"Scoring strategy '{name}' was edited in place: its effective scoring config previously "
                    + $"resolved to '{recorded}' but now resolves to '{computed}'. A strategy is IMMUTABLE BY "
                    + "CONVENTION, because its name — not the fingerprint — is the key of its score series "
                    + "(spec 141): changing it in place would silently continue one series while measuring "
                    + $"something else. Add a NEW strategy name instead (e.g. '{name}' -> '{NextName(name)}') "
                    + "and leave this one as it was. Deleting this name's recorded identity file under the "
                    + "scoring-configs 'strategies' folder will silence this error, but it does NOT make the "
                    + "next comparison safe: the scores accrued under the old config and the scores this one "
                    + "produces are not comparable, so the weekly report can compare across the recalibration "
                    + "and report a movement ('Thesis improving' / 'Thesis deteriorating') that is an artefact "
                    + "of the config change rather than of the company. Adding a new strategy name is the "
                    + "correct action.");
        }
    }

    /// <summary>
    /// A suggested successor name for the operator message: <c>momentum</c> → <c>momentum-v2</c>, and
    /// <c>momentum-v2</c> → <c>momentum-v3</c>, so the hint stays useful on the second edit too. Purely
    /// cosmetic (message text); it never becomes a configured name by itself.
    /// </summary>
    private static string NextName(string name)
    {
        var dash = name.LastIndexOf("-v", StringComparison.Ordinal);
        if (dash >= 0
            && int.TryParse(
                name[(dash + 2)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var version)
            && version < int.MaxValue)
        {
            return $"{name[..dash]}-v{version + 1}";
        }

        return $"{name}-v2";
    }
}
