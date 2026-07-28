using Radar.Application.Storage;

namespace Radar.Application.Scoring;

/// <summary>
/// The validated, composition-time-resolved set of <see cref="ScoringStrategyDefinition"/>s a run scores
/// with (spec 137). Exactly one member is the <see cref="Primary"/>: its snapshots keep the legacy storage
/// location and its in-memory score repository is the one the weekly report renders from; every other
/// strategy is storage- and repository-scoped to itself so it can never overwrite or leak into the primary
/// series.
/// <para>
/// Construction is the single validation point for strategy identity, so a misconfigured
/// <c>Radar:Strategies</c> fails fast at startup rather than surfacing later as a confusing empty or
/// mislabelled score series. Infrastructure owns the profile resolution and the
/// <c>Radar:PrimaryStrategy</c> selection; this type owns the invariants of the resulting set.
/// </para>
/// </summary>
public sealed class ScoringStrategySet
{
    /// <summary>The strategy name synthesised when <c>Radar:Strategies</c> is absent or empty.</summary>
    public const string DefaultStrategyName = "default";

    public ScoringStrategySet(IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        if (strategies.Count == 0)
        {
            throw new InvalidOperationException(
                "Radar:Strategies resolved to an empty strategy set; at least one strategy must be configured "
                    + "(clear Radar:Strategies entirely to run the single synthesised \"default\" strategy).");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);

            if (string.IsNullOrWhiteSpace(strategy.Name))
            {
                throw new InvalidOperationException(
                    "Radar:Strategies contains a strategy with a blank Name; every strategy needs a Name (it is "
                        + "stamped on every snapshot and names the non-primary storage directory).");
            }

            // The shared "usable as one storage directory segment" rule (see StorageSegmentName): a strategy
            // name segments the non-primary snapshot storage, so a separator or relative segment would escape
            // the scores root. The replay run label (spec 139) is checked against the very same rule.
            if (!StorageSegmentName.IsUsable(strategy.Name))
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies contains an unusable strategy Name '{strategy.Name}'; a strategy name is "
                        + $"used verbatim as a storage directory segment, so {StorageSegmentName.Rule}.");
            }

            // Spec 138: SignalTypes defaults to SignalTypeFilter.All, so this can only be null if a caller
            // explicitly nulled it (e.g. `definition with { SignalTypes = null! }`). Fail fast here rather
            // than letting the engine silently substitute "all types" for a strategy that meant to declare a
            // narrow set — that would produce a series stamped as broad while the operator believed it narrow.
            if (strategy.SignalTypes is null)
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies strategy '{strategy.Name}' has a null SignalTypes filter; a strategy's "
                        + "consumed signal-type set is a fingerprint input, so it is never inferred (use "
                        + "SignalTypeFilter.All to consume every signal type).");
            }

            // Spec 146: the same "never inferred" rule as SignalTypes. Formula defaults to v8, so a null
            // here means a caller explicitly nulled it; an unknown name means a typo that would otherwise
            // surface as an unresolvable formula deep in the strategy factory.
            if (!ScoreFormulaVersions.IsKnown(strategy.Formula))
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies strategy '{strategy.Name}' declares Formula '{strategy.Formula}', which "
                        + $"is not a known scoring formula (known formulas: {ScoreFormulaVersions.KnownList}). "
                        + "Omit Formula to use the default "
                        + $"{ScoreFormulaVersions.V8}.");
            }

            if (strategy.Channels is null)
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies strategy '{strategy.Name}' has a null Channels set; a strategy's channel "
                        + "budget is a fingerprint input, so it is never inferred (use ScoringChannelSet.Empty "
                        + "to declare none).");
            }

            // A channel budget only means something to a CHANNEL-COMPOSITION formula, and such a formula means
            // nothing without one. Both directions fail fast, because either way the operator's stated intent
            // would be silently discarded: a v8 strategy would ignore the budget it declared, and a channel
            // formula without one would score every company 0.
            //
            // Spec 153 generalised these two rules from a hard-coded radar-formula-v9 onto the SET of channel
            // formulas — ScoreFormulaVersions.ConsumesChannels — which the formula factory's dispatch also
            // reads, so "which formulas take channels" has exactly one definition and the validator and the
            // factory cannot drift apart.
            if (ScoreFormulaVersions.ConsumesChannels(strategy.Formula) && strategy.Channels.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies strategy '{strategy.Name}' declares Formula '{strategy.Formula}' but "
                        + "no Channels; a channel-composition formula with no channels would score every company "
                        + "0. Declare at least one channel, or use "
                        + $"{ScoreFormulaVersions.V8}.");
            }

            if (!ScoreFormulaVersions.ConsumesChannels(strategy.Formula) && !strategy.Channels.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies strategy '{strategy.Name}' declares Channels but Formula "
                        + $"'{strategy.Formula}', which does not consume them — the declared budget would be "
                        + $"silently ignored. Set Formula to one of the channel-composition formulas "
                        + $"({ScoreFormulaVersions.ChannelFormulaList}), or remove Channels.");
            }

            if (!seen.Add(strategy.Name))
            {
                throw new InvalidOperationException(
                    $"Radar:Strategies contains duplicate strategy Name '{strategy.Name}' (names are compared "
                        + "case-insensitively); each strategy must be uniquely named so its snapshot series is "
                        + "unambiguous.");
            }
        }

        var primaries = strategies.Where(s => s.IsPrimary).ToList();
        if (primaries.Count != 1)
        {
            throw new InvalidOperationException(
                $"Radar:Strategies must resolve to exactly one primary strategy (Radar:PrimaryStrategy), but "
                    + $"{primaries.Count} were marked primary. The primary strategy owns the legacy storage "
                    + "location and is the one the weekly report renders.");
        }

        Strategies = [.. strategies];
        Primary = primaries[0];
    }

    /// <summary>The strategies in configured order (deterministic, AD-3). Never empty.</summary>
    public IReadOnlyList<ScoringStrategyDefinition> Strategies { get; }

    /// <summary>The single primary strategy: legacy storage location + the reported series.</summary>
    public ScoringStrategyDefinition Primary { get; }

    /// <summary>
    /// The single-strategy set used when <c>Radar:Strategies</c> is absent or empty — the byte-identical
    /// default. Named <see cref="DefaultStrategyName"/>, primary, and carrying the weights already resolved
    /// from <c>Radar:Scoring:Profile</c>.
    /// </summary>
    public static ScoringStrategySet SingleDefault(
        ScoringWeights weights, string scoringProfile = DefaultStrategyName)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return new ScoringStrategySet(
        [
            new ScoringStrategyDefinition(
                Name: DefaultStrategyName,
                ScoringProfile: scoringProfile,
                Weights: weights,
                IsPrimary: true),
        ]);
    }
}
