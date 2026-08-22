using System.Reflection;

using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Evaluation;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §10 — the AD-14 boundary, asserted on the TYPE GRAPH (the
/// <see cref="Radar.Application.Tests.Efficacy.EfficacyReadOnlyGuardrailTests"/> pattern): scoring, the
/// evidence/signal pipeline and the score formulas can never reach a NewsRisk type; the LIVE news-risk
/// generator can never reach a price type (only the §9 evaluator may); and the guards carry positive
/// controls so they cannot pass vacuously.
/// </summary>
public sealed class NewsRiskArchitectureGuardTests
{
    private const string NewsRiskNamespace = "Radar.Application.NewsRisk";
    private const string EvaluationNamespace = "Radar.Application.NewsRisk.Evaluation";
    private const string PricesNamespace = "Radar.Application.Prices";

    // Everything that computes/consumes on the evidence → signal → score path, plus the pipeline
    // orchestration itself: none of it may know news-risk exists (the shadow is invoked by the WORKER,
    // after the pipeline returns).
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
    public void ScoringAndPipelineTypeGraphs_CanNeverReachANewsRiskType()
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
                && t.Namespace.StartsWith(NewsRiskNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "No scoring/evidence-pipeline/score-formula type may reach the news-risk subsystem "
                + "(spec 179 §10), but these are reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void LiveNewsRiskGenerator_CanNeverReachAPriceType()
    {
        // The LIVE side: every NewsRisk type OUTSIDE the Evaluation namespace. Price belongs only to the
        // read-only evaluator (AD-14).
        var liveTypes = typeof(NewsRiskShadowGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == NewsRiskNamespace)
            .ToList();

        Assert.NotEmpty(liveTypes);
        Assert.Contains(typeof(NewsRiskShadowGenerator), liveTypes);

        var leaks = TransitiveClosure(liveTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The live news-risk shadow generator must never reach a price type (AD-14), but these are "
                + "reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void LiveNewsRiskGenerator_HoldsNoScoreStoreSeam()
    {
        // The handed-in section instances are the ONE candidate source: no score repository, snapshot file
        // store or repository factory may be reachable, so a "reopen and re-rank" regression fails here.
        string[] forbidden =
        [
            "IScoreRepository",
            "IScoreRepositoryFactory",
            "IScoreSnapshotFileStore",
            "IScoreSnapshotFileStoreFactory",
        ];

        var liveTypes = typeof(NewsRiskShadowGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == NewsRiskNamespace)
            .ToList();

        var reachableNames = TransitiveClosure(liveTypes).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var leaks = forbidden.Where(reachableNames.Contains).ToList();

        Assert.True(
            leaks.Count == 0,
            "The shadow generator must consume the handed-in report sections only — no score-store seam — "
                + "but it reaches: " + string.Join(", ", leaks));
    }

    [Fact]
    public void Evaluator_DoesReachPrice_SoTheLiveGuardIsNotVacuous()
    {
        // The positive control: the evaluator legitimately joins frozen assessments to price, so if the
        // walk went blind the control fails before the guard silently passes.
        var evaluationTypes = typeof(NewsRiskEvaluationGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == EvaluationNamespace)
            .ToList();

        Assert.NotEmpty(evaluationTypes);

        var reachesPrice = TransitiveClosure(evaluationTypes)
            .Any(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal));

        Assert.True(reachesPrice, "The §9 evaluator is supposed to read price — downstream, read-only.");
    }

    [Fact]
    public void Evaluator_NeverInvokesSelectionFetchingOrAi()
    {
        // §9: the evaluator reads frozen assessments + declarations + price ONLY. Reaching the analyzer,
        // the content reader, the candidate selector or the live generator would mean it can re-derive or
        // re-fetch what is supposed to be frozen.
        string[] forbidden =
        [
            nameof(INewsRiskAnalyzer),
            nameof(NewsRiskCandidateSelector),
            nameof(NewsRiskShadowGenerator),
            "INewsArticleContentReader",
            "IChatClient",
        ];

        var evaluationTypes = typeof(NewsRiskEvaluationGenerator).Assembly.GetTypes()
            .Where(t => t.Namespace == EvaluationNamespace)
            .ToList();

        var reachableNames = TransitiveClosure(evaluationTypes)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var leaks = forbidden.Where(reachableNames.Contains).ToList();

        Assert.True(
            leaks.Count == 0,
            "The evaluator must never select, fetch or invoke AI, but it reaches: "
                + string.Join(", ", leaks));
    }

    /// <summary>Transitive closure over declared members — private fields included (a leak in a closure is still a leak).</summary>
    private static HashSet<Type> TransitiveClosure(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var referenced in ReferencedTypes(queue.Dequeue()))
            {
                if (IsRadarType(referenced) && seen.Add(referenced))
                {
                    queue.Enqueue(referenced);
                }
            }
        }

        return seen;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var candidates = new List<Type?>();

        if (type.BaseType is not null)
        {
            candidates.Add(type.BaseType);
        }

        candidates.AddRange(type.GetInterfaces());
        candidates.AddRange(type.GetFields(All).Select(f => f.FieldType));
        candidates.AddRange(type.GetProperties(All).Select(p => p.PropertyType));

        foreach (var method in type.GetMethods(All))
        {
            candidates.Add(method.ReturnType);
            candidates.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (var ctor in type.GetConstructors(All))
        {
            candidates.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        }

        candidates.AddRange(type.GetNestedTypes(All));

        return candidates.Where(c => c is not null).SelectMany(c => Unwrap(c!)).Distinct();
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var current = type;
        while (current.HasElementType)
        {
            current = current.GetElementType()!;
        }

        yield return current;

        if (current.IsGenericType)
        {
            foreach (var argument in current.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }
        }
    }

    private static bool IsRadarType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Radar.", StringComparison.Ordinal) == true;
}
