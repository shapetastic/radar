using Radar.Application.Scoring;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// The spec-225 boundary, asserted on the TYPE GRAPH (mirroring the spec-140/169/172 guards): the measurement
/// reads score MECHANICS, not efficacy — price has no place in it — and the ONLY scoring types it may know are
/// the read seam, the strategy description, and the production PRIMITIVES it decomposes through
/// (<see cref="ScoreSignalMath"/> and its input/output records). It never reaches the engine, the fingerprint
/// or a formula class.
/// </summary>
public sealed class EvidenceConfidenceGuardrailTests
{
    private const string ModuleNamespace = "Radar.Application.Efficacy.EvidenceConfidence";
    private const string ScoringNamespace = "Radar.Application.Scoring";
    private const string PricesNamespace = "Radar.Application.Prices";

    private static List<Type> ModuleTypes() =>
        typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == ModuleNamespace)
            .ToList();

    [Fact]
    public void Module_NeverReachesAPriceType()
    {
        var types = ModuleTypes();
        Assert.NotEmpty(types);

        var leaks = TypeGraphClosure.TransitiveClosure(types)
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The EvidenceConfidence measurement reads score mechanics, not efficacy — price must never be an "
                + "input or an output of it, but these are reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void Module_TouchesOnlyTheReadSeam_TheStrategyDescription_AndTheProductionPrimitives()
    {
        string[] permitted =
        [
            nameof(IScoreSnapshotFileStore),
            nameof(ScoringStrategyDefinition),
            nameof(ScoringStrategySet),
            // The spec-225 additions: the measurement decomposes THROUGH the production body, by design.
            nameof(ScoreSignalMath),
            nameof(ScoringSignal),
            nameof(ScoringWeights),
            nameof(ScoreFormulaVersions),
            nameof(EvidenceConfidenceTerms),
        ];

        var types = ModuleTypes();
        Assert.NotEmpty(types);

        var scoringReferences = types
            .SelectMany(TypeGraphClosure.ReferencedTypes)
            .Where(t => t.Namespace == ScoringNamespace)
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unexpected = scoringReferences.Except(permitted, StringComparer.Ordinal).ToList();
        Assert.True(
            unexpected.Count == 0,
            "The EvidenceConfidence measurement may depend on scoring OUTPUT and the shared primitives only, "
                + "but it references: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Module_ReallyDoesReachThePrimitives_SoTheAllowListIsNotVacuous()
    {
        // The positive control: the decomposition record is in the graph. If the reporter stopped going
        // through ScoreSignalMath, the allow-list above would still pass while the spec's central property —
        // "never a second copy of the formula" — had silently been lost.
        var reachable = TypeGraphClosure.TransitiveClosure(ModuleTypes());
        Assert.Contains(typeof(EvidenceConfidenceTerms), reachable);
    }
}
