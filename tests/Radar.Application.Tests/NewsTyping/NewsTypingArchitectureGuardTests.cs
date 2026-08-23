using System.Reflection;

using Radar.Application.NewsTyping;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 — the boundaries, asserted on the TYPE GRAPH (the spec-179
/// <see cref="NewsRisk.NewsRiskArchitectureGuardTests"/> pattern): scoring and the evidence/signal pipeline
/// can never reach a NewsTyping type; the typing generator holds no score-store seam and never reaches a
/// price type; and the STAGE-1 CONTRACT is structural — the wire schema and validated fact types expose no
/// direction/severity/materiality/sentiment/score member.
/// </summary>
public sealed class NewsTypingArchitectureGuardTests
{
    private const string NewsTypingNamespace = "Radar.Application.NewsTyping";
    private const string PricesNamespace = "Radar.Application.Prices";

    private static readonly string[] GuardedNamespaces =
    [
        "Radar.Application.Scoring",
        "Radar.Application.SignalExtraction",
        "Radar.Application.SignalReview",
        "Radar.Application.EntityResolution",
        "Radar.Application.Evidence",
        "Radar.Application.Signals",
        "Radar.Application.Pipeline",
    ];

    [Fact]
    public void ScoringAndPipelineTypeGraphs_CanNeverReachANewsTypingType()
    {
        var assembly = typeof(ScoringInput).Assembly;
        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is not null && GuardedNamespaces.Any(
                ns => t.Namespace.Equals(ns, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(roots);
        Assert.Contains(typeof(ScoringInput), roots);

        var leaks = TransitiveClosure(roots)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(NewsTypingNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "No scoring/evidence-pipeline type may reach the news-typing subsystem (spec 181: read-side "
                + "and shadow), but these are reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void TypingGenerator_CanNeverReachAPriceType()
    {
        var typingTypes = typeof(NewsTypingGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == NewsTypingNamespace)
            .ToList();

        Assert.NotEmpty(typingTypes);
        Assert.Contains(typeof(NewsTypingGenerator), typingTypes);

        var leaks = TransitiveClosure(typingTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The news-typing subsystem must never reach a price type (AD-14), but these are reachable: "
                + string.Join(", ", leaks));
    }

    [Fact]
    public void TypingGenerator_HoldsNoScoreStoreSeam()
    {
        string[] forbidden =
        [
            "IScoreRepository",
            "IScoreRepositoryFactory",
            "IScoreSnapshotFileStore",
            "IScoreSnapshotFileStoreFactory",
        ];

        var typingTypes = typeof(NewsTypingGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == NewsTypingNamespace)
            .ToList();

        var reachableNames = TransitiveClosure(typingTypes).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var leaks = forbidden.Where(reachableNames.Contains).ToList();

        Assert.True(
            leaks.Count == 0,
            "The typing generator reads the observation archive only — no score-store seam — but it "
                + "reaches: " + string.Join(", ", leaks));
    }

    [Fact]
    public void NewsRiskEvaluator_StillReachesPrice_SoThePriceGuardIsNotVacuous()
    {
        // The positive control (spec 179's evaluator legitimately reads price): if the closure walk went
        // blind, this fails before the typing guard silently passes.
        var evaluationTypes = typeof(NewsTypingGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == "Radar.Application.NewsRisk.Evaluation")
            .ToList();

        Assert.NotEmpty(evaluationTypes);
        Assert.True(
            TransitiveClosure(evaluationTypes).Any(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal)),
            "The spec-179 §9 evaluator is supposed to read price — the walk should see it.");
    }

    [Fact]
    public void WireSchemaAndValidatedFacts_ExposeNoDirectionalSeverityOrScoreMember()
    {
        // The stage-1 contract, enforced STRUCTURALLY (spec 181 §2): the extractor withholds the verdict,
        // so no property on the wire/validated/persisted fact shapes may even exist to carry one.
        string[] forbiddenFragments = ["Direction", "Severity", "Materiality", "Sentiment", "Score"];
        Type[] contractTypes =
        [
            typeof(NewsTypingModelResponse),
            typeof(NewsTypingModelFact),
            typeof(NewsTypingValidatedFact),
            typeof(NewsTypingValidationResult),
            typeof(NewsTypingRecord),
        ];

        var violations = contractTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Type: t.Name, Property: p.Name)))
            .Where(p => forbiddenFragments.Any(
                f => p.Property.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Type}.{p.Property}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Stage 1 asks no directional question and records no severity/materiality/score — but these "
                + "members exist: " + string.Join(", ", violations));
    }

    /// <summary>The shared walker (extracted for spec 140/184's guards) — no third drifting copy here.</summary>
    private static HashSet<Type> TransitiveClosure(IEnumerable<Type> roots) =>
        TypeGraphClosure.TransitiveClosure(roots);
}
