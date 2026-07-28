using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// The default <see cref="IScoreFormulaFactory"/>: selects the shipped <c>radar-formula-vN</c> class a
/// strategy declared (<see cref="ScoringStrategyDefinition.Formula"/>) and binds it to that strategy's
/// <see cref="ScoringWeights"/>, its channel budget, and the shared, strategy-independent
/// <see cref="IAttentionSourceWeights"/> tier map.
/// <para>
/// Selecting the formula CLASS remains a code decision (AD-6) — the set of shippable formulas is the closed
/// <see cref="ScoreFormulaVersions.All"/> list, so configuration can only pick between structures the
/// maintainer wrote, never define one. Only the magnitudes and the channel budget vary per strategy (AD-10
/// as amended). A strategy that names no formula gets <see cref="RadarScoreFormulaV8"/> over its weights —
/// byte-identical to the pre-spec-146 factory.
/// </para>
/// <para>
/// Renamed from <c>RadarScoreFormulaV8Factory</c> in spec 146: a factory that dispatches over several
/// formulas should not be named after one of them.
/// </para>
/// </summary>
public sealed class RadarScoreFormulaFactory : IScoreFormulaFactory
{
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ICollectorAttributionResolver _attributionResolver;

    /// <param name="attributionResolver">
    /// How <c>radar-formula-v9</c> establishes the collector behind each signal's evidence (spec 151).
    /// Strategy-independent — it is a property of the DATA, not of a strategy's hypothesis — so it is
    /// resolved once here and handed to every v9 formula this factory builds. Optional and defaulting to the
    /// recorded-only resolver, i.e. pre-151 behaviour.
    /// </param>
    public RadarScoreFormulaFactory(
        IAttentionSourceWeights sourceWeights, ICollectorAttributionResolver? attributionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(sourceWeights);
        _sourceWeights = sourceWeights;
        _attributionResolver = attributionResolver ?? RecordedOnlyCollectorAttributionResolver.Instance;
    }

    /// <inheritdoc />
    public IScoreFormula Create(ScoringStrategyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Weights);

        // ScoringStrategySet already rejects an unknown formula at construction, so this is the second line
        // of defence for a definition composed in code rather than bound from config. It throws rather than
        // defaulting: silently substituting v8 for a typo'd name would produce a series stamped and scored
        // with a structure the operator never asked for.
        var formula = ScoreFormulaVersions.Canonicalize(definition.Formula)
            ?? throw new InvalidOperationException(
                $"Strategy '{definition.Name}' declares Formula '{definition.Formula}', which is not a known "
                    + $"scoring formula (known formulas: {ScoreFormulaVersions.KnownList}). Omit Formula to use "
                    + $"the default {ScoreFormulaVersions.V8}.");

        return formula switch
        {
            ScoreFormulaVersions.V9 =>
                new RadarScoreFormulaV9(
                    definition.Weights, _sourceWeights, definition.Channels, _attributionResolver),
            _ => new RadarScoreFormulaV8(definition.Weights, _sourceWeights),
        };
    }
}
