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
/// formula structure or extractor rule structure — i.e. the cases where a series would otherwise silently
/// continue under one name while measuring something else.
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
                await store.RecordStrategyFingerprintAsync(name, computed, ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Recorded first identity for scoring strategy {StrategyName}: {Fingerprint}.",
                    name, computed);
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
