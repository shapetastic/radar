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
/// <para>
/// Spec 146 adds two per-strategy variations and ONE validation. The variations: the formula CLASS
/// (<see cref="ScoringStrategyDefinition.Formula"/>, resolved through the same
/// <see cref="IScoreFormulaFactory"/>) and the channel budget
/// (<see cref="ScoringStrategyDefinition.Channels"/>, which travels into the engine so it is folded into that
/// strategy's fingerprint). The validation: a <c>radar-formula-v9</c> collector channel may only name
/// collectors that are actually REGISTERED, and this is the first place in the graph where that set is known
/// (<see cref="ISignalSourceDescriptor.EnabledCollectors"/>). It runs while the runtimes are built, which
/// <c>StrategyIdentityGuard</c> forces as the very first statement of <c>RadarPipelineRunner.RunAsync</c> —
/// so a typo'd collector name costs no collection, instead of silently scoring that channel 0 forever.
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
            () =>
            {
                // Spec 146: validate every declared collector channel against the collectors that are
                // genuinely registered, BEFORE any engine is built, so the failure names the configuration
                // rather than surfacing as a permanently-dark channel.
                ValidateChannelCollectors(strategies, sourceDescriptor.EnabledCollectors());

                return strategies.Strategies
                    .Select(definition => new ScoringStrategyRuntime(
                        definition,
                        new ScoringEngine(
                            signalRepository,
                            signalFileStore,
                            evidenceRepository,
                            scoreRepositoryFactory.ForStrategy(definition),
                            companyRepository,
                            // Spec 146: the formula CLASS is now the strategy's choice too, resolved from the
                            // whole definition (version + magnitudes + channel budget). An omitted Formula
                            // resolves to radar-formula-v8 over the strategy's weights — exactly what this
                            // call produced before the slice.
                            formulaFactory.Create(definition),
                            definition.Weights,
                            sourceWeights,
                            sourceDescriptor,
                            insiderMaterialityWeights,
                            mediaCollapse,
                            options,
                            engineLogger,
                            definition.Name,
                            // Spec 138: the declared signal-type set travels with the strategy into its engine,
                            // which both applies it at the read→score seam and folds it into that engine's
                            // fingerprint. Default (all types) is a no-op on both counts.
                            definition.SignalTypes,
                            // Spec 146: likewise the channel budget — the engine folds it into that strategy's
                            // fingerprint, so the composition the formula performs and the hashed identity of
                            // that composition cannot drift. Default (no channels) is a no-op.
                            definition.Channels)))
                    .ToList();
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Fails fast when a collector channel names a collector that is not registered for this run. Matching is
    /// EXACT (ordinal) against <c>IEvidenceCollector.CollectorName</c>: a case-insensitive match would have to
    /// pick a spelling to select with, and a near-miss that quietly selects nothing is precisely the silent
    /// failure a declared budget exists to prevent — so a case typo is a startup error too, with both the
    /// offending name and every known name in the message.
    /// </summary>
    private static void ValidateChannelCollectors(
        ScoringStrategySet strategies, IReadOnlyList<string> enabledCollectors)
    {
        var known = new HashSet<string>(enabledCollectors, StringComparer.Ordinal);

        foreach (var definition in strategies.Strategies)
        {
            foreach (var channel in definition.Channels.Channels)
            {
                foreach (var collector in channel.Collectors)
                {
                    if (known.Contains(collector))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Strategy '{definition.Name}' channel '{channel.Name}' names collector "
                            + $"'{collector}', which is not a registered evidence collector (registered "
                            + $"collectors: {(known.Count == 0 ? "(none)" : string.Join(", ", enabledCollectors))}). "
                            + "A channel over a collector that does not exist could only ever score 0, silently "
                            + "costing the strategy this channel's whole share; collector names are matched "
                            + "exactly, so check the spelling and casing.");
                }
            }
        }
    }

    public IReadOnlyList<ScoringStrategyRuntime> Runtimes => _runtimes.Value;

    // ScoringStrategySet guarantees exactly one primary at construction, so First is total here.
    public ScoringStrategyRuntime Primary => Runtimes.First(r => r.Definition.IsPrimary);
}
