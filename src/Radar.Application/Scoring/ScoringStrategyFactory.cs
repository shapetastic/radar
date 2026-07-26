using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Builds one <see cref="ScoringEngine"/> per configured strategy (spec 137). Everything above scoring —
/// collection, the AI directional read, extraction, resolution, review and signal persistence — is shared
/// and strategy-independent, so this factory only varies what the engine's constructor already varies:
/// the <see cref="ScoringWeights"/> (via a per-strategy <see cref="IScoreFormula"/> from
/// <see cref="IScoreFormulaFactory"/>), the strategy name stamped on each snapshot, and the
/// <see cref="IScoreRepository"/> the engine writes into.
/// <para>
/// The runtimes are built eagerly-once behind a <see cref="Lazy{T}"/> and cached: each engine computes its
/// effective config + fingerprint in its constructor, so a single build per process keeps that cost and the
/// resulting stamps identical to the previous single-engine composition.
/// </para>
/// </summary>
public sealed class ScoringStrategyFactory : IScoringStrategyFactory
{
    private readonly Lazy<IReadOnlyList<ScoringStrategyRuntime>> _runtimes;

    public ScoringStrategyFactory(
        ScoringStrategySet strategies,
        ISignalRepository signalRepository,
        ISignalFileStore signalFileStore,
        IEvidenceRepository evidenceRepository,
        IScoreRepositoryFactory scoreRepositoryFactory,
        ICompanyRepository companyRepository,
        IScoreFormulaFactory formulaFactory,
        IAttentionSourceWeights sourceWeights,
        ISignalSourceDescriptor sourceDescriptor,
        InsiderMaterialityWeights insiderMaterialityWeights,
        MediaAttentionCollapse mediaCollapse,
        ScoringOptions options,
        ILogger<ScoringEngine> engineLogger)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepositoryFactory);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(formulaFactory);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(insiderMaterialityWeights);
        ArgumentNullException.ThrowIfNull(mediaCollapse);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engineLogger);

        _runtimes = new Lazy<IReadOnlyList<ScoringStrategyRuntime>>(
            () => strategies.Strategies
                .Select(definition => new ScoringStrategyRuntime(
                    definition,
                    new ScoringEngine(
                        signalRepository,
                        signalFileStore,
                        evidenceRepository,
                        scoreRepositoryFactory.ForStrategy(definition),
                        companyRepository,
                        formulaFactory.Create(definition.Weights),
                        definition.Weights,
                        sourceWeights,
                        sourceDescriptor,
                        insiderMaterialityWeights,
                        mediaCollapse,
                        options,
                        engineLogger,
                        definition.Name)))
                .ToList(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<ScoringStrategyRuntime> Runtimes => _runtimes.Value;

    // ScoringStrategySet guarantees exactly one primary at construction, so First is total here.
    public ScoringStrategyRuntime Primary => Runtimes.First(r => r.Definition.IsPrimary);
}
