namespace Radar.Application.Scoring;

/// <summary>
/// The default <see cref="IScoreFormulaFactory"/>: produces the shipped <see cref="RadarScoreFormulaV8"/>
/// bound to the supplied per-strategy <see cref="ScoringWeights"/> and the shared, strategy-independent
/// <see cref="IAttentionSourceWeights"/> tier map. Selecting the formula CLASS stays a code decision (AD-6);
/// only the magnitudes vary per strategy (AD-10 as amended).
/// </summary>
public sealed class RadarScoreFormulaV8Factory : IScoreFormulaFactory
{
    private readonly IAttentionSourceWeights _sourceWeights;

    public RadarScoreFormulaV8Factory(IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(sourceWeights);
        _sourceWeights = sourceWeights;
    }

    public IScoreFormula Create(ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return new RadarScoreFormulaV8(weights, _sourceWeights);
    }
}
