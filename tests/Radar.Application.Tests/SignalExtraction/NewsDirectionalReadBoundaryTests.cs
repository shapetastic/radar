using System.Reflection;

using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Pipeline;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Tests.SignalExtraction;

/// <summary>
/// Spec 191 — the boundary shape of the ONE seam through which the news read reaches scoring, asserted
/// HONESTLY: the seam itself is Domain/BCL only (so the spec-177 and spec-179 §10 reflection guards stay
/// exactly as strict as before), and — the POSITIVE CONTROL — the concrete implementation demonstrably DOES
/// reach the observation archive and the judgment store. A boundary test that passes because nobody looked
/// is worthless; this one passes because the seam is real and the far side is genuinely on the far side.
/// </summary>
public sealed class NewsDirectionalReadBoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Radar.Application.News",        // covers News, NewsRisk and NewsTyping by prefix — deliberately
    ];

    [Fact]
    public void TheSeamsTypeGraph_ReachesNoNewsObservationJudgmentOrTypingType()
    {
        var leaks = TransitiveClosure([typeof(INewsDirectionalReadSource), typeof(NewsDirectionalRead)])
            .Where(t => t.Namespace is not null
                && ForbiddenNamespacePrefixes.Any(
                    ns => t.Namespace.StartsWith(ns, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "INewsDirectionalReadSource/NewsDirectionalRead must reference Domain and BCL types ONLY "
                + "(spec 191), but these are reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void TheSeamCarriesTheDirectionAsADomainEnum_AndTheTrajectoryAsAToken()
    {
        // The two members that make the boundary possible. Direction is Radar.Domain.Signals.SignalDirection
        // (not the judgment's own trajectory enum), and the trajectory rides as an already-rendered display
        // token — so the extractor never has to name a NewsRisk type.
        var direction = typeof(NewsDirectionalRead).GetProperty(nameof(NewsDirectionalRead.Direction))!;
        var trajectory = typeof(NewsDirectionalRead).GetProperty(nameof(NewsDirectionalRead.TrajectoryToken))!;

        Assert.Equal("Radar.Domain.Signals", direction.PropertyType.Namespace);
        Assert.Equal(typeof(string), trajectory.PropertyType);
    }

    [Fact]
    public void PositiveControl_TheImplementation_DoesReachTheArchiveAndTheJudgmentStore()
    {
        // Without this the guard above could pass vacuously (e.g. if the walk went blind, or if the seam were
        // never implemented at all). It also documents WHERE the far side lives.
        var referenced = TransitiveClosure([typeof(NewsDirectionalReadSource)]);

        Assert.Contains(typeof(INewsObservationArchive), referenced);
        Assert.Contains(typeof(INewsJudgmentStore), referenced);
        Assert.Contains(typeof(NewsJudgmentRecord), referenced);
        Assert.Contains(typeof(NewsObservationEvidenceJoin), referenced);
    }

    [Fact]
    public void TheKeywordExtractorsOwnGraph_StillReachesNoNewsSubsystemType()
    {
        // The extractor holds the SEAM, never the implementation — which is exactly what keeps the two
        // pre-existing reflection guards (spec 177 acquisition-only, spec 179 §10) unweakened.
        var leaks = TransitiveClosure([typeof(KeywordSignalExtractor)])
            .Where(t => t.Namespace is not null
                && ForbiddenNamespacePrefixes.Any(
                    ns => t.Namespace.StartsWith(ns, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(leaks.Count == 0, "KeywordSignalExtractor leaks: " + string.Join(", ", leaks));
    }

    [Fact]
    public void CollectionPass_HoldsTheSeamAndStillReachesNoNewsRiskOrNewsTypingType()
    {
        // CollectionPass now calls PrepareAsync, so it holds INewsDirectionalReadSource — and it lives in
        // Radar.Application.Pipeline, a GUARDED namespace in NewsRiskArchitectureGuardTests whose ban is on
        // the TRANSITIVE closure. That stays legal only because the seam's own closure is Domain/BCL-only,
        // which is the property this file pins. Asserted here explicitly so the two facts cannot drift apart.
        //
        // Radar.Application.News is deliberately EXCLUDED from this particular check: the spec-177 guard
        // sanctions the collection orchestration as the archive's writer (CollectionPass already holds
        // INewsObservationArchive), so the meaningful claim for the pass is the NewsRisk/NewsTyping one.
        var leaks = TransitiveClosure([typeof(CollectionPass)])
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith("Radar.Application.NewsRisk", StringComparison.Ordinal)
                    || t.Namespace.StartsWith("Radar.Application.NewsTyping", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(leaks.Count == 0, "CollectionPass leaks: " + string.Join(", ", leaks));
        Assert.Contains(typeof(INewsDirectionalReadSource), TransitiveClosure([typeof(CollectionPass)]));
    }

    /// <summary>Transitive closure over declared members — private fields included (a leak in a closure is still a leak).</summary>
    private static HashSet<Type> TransitiveClosure(IEnumerable<Type> roots)
    {
        const BindingFlags all =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            foreach (var unwrapped in Unwrap(type))
            {
                if (!seen.Add(unwrapped))
                {
                    continue;
                }

                var next = new List<Type>();
                if (unwrapped.BaseType is not null)
                {
                    next.Add(unwrapped.BaseType);
                }

                next.AddRange(unwrapped.GetInterfaces());
                next.AddRange(unwrapped.GetFields(all).Select(f => f.FieldType));
                next.AddRange(unwrapped.GetProperties(all).Select(p => p.PropertyType));
                foreach (var method in unwrapped.GetMethods(all))
                {
                    next.Add(method.ReturnType);
                    next.AddRange(method.GetParameters().Select(p => p.ParameterType));
                }

                foreach (var ctor in unwrapped.GetConstructors(all))
                {
                    next.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
                }

                foreach (var nested in unwrapped.GetNestedTypes(all))
                {
                    next.Add(nested);
                }

                foreach (var candidate in next)
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        return seen;
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            type = type.GetElementType()!;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return argument;
            }
        }
    }
}
