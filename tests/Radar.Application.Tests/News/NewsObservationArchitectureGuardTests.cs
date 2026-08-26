using System.Reflection;

using Radar.Application.News;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.News;

/// <summary>
/// Structural guardrail for spec 177's "acquisition only" boundary (the
/// <see cref="Radar.Application.Tests.Efficacy.EfficacyReadOnlyGuardrailTests"/> precedent, applied on the
/// TYPE GRAPH): no type in <c>Radar.Application.Scoring</c> or the evidence/signal pipeline machinery may
/// reference the news-observation archive or the content reader. The archive is observational — it must be
/// structurally impossible for a score, an extraction or a review to read it, or the point-in-time record
/// would quietly become a scoring input and AD-14-style honesty would erode.
/// <para>
/// <b>SPEC 191 did NOT weaken this guard, and the distinction matters.</b> The news read now genuinely
/// reaches the signal layer — but through exactly ONE seam,
/// <c>Radar.Application.SignalExtraction.INewsDirectionalReadSource</c>, whose request/response types carry
/// Domain and BCL types ONLY (the established
/// <c>Radar.Application.Filings.IDirectionalFilingSignalSource</c> pattern for an AI-derived scoring input).
/// The concrete implementation lives on the FAR side of that seam, in <c>Radar.Application.News</c> beside
/// <c>NewsObservationMigration</c>, so the archive still cannot be reached from a scoring/extraction type
/// graph. <see cref="Radar.Application.Tests.SignalExtraction.NewsDirectionalReadBoundaryTests"/> asserts
/// the seam's shape from the other direction, WITH a positive control proving the implementation really does
/// reach the archive and the judgment store — a boundary that holds because nobody looked is worthless.
/// </para>
/// </summary>
public sealed class NewsObservationArchitectureGuardTests
{
    // The namespaces whose types must never reach Radar.Application.News. Deliberately NOT including
    // Radar.Application.Pipeline / Radar.Application.Collectors: the collection ORCHESTRATION is the one
    // sanctioned writer (spec 177 §3 — "the collection orchestration writes it"), and the sidecar rides
    // CollectionResult by design. The ban is on the compute/consume side.
    private static readonly string[] GuardedNamespaces =
    [
        "Radar.Application.Scoring",
        "Radar.Application.SignalExtraction",
        "Radar.Application.SignalReview",
        "Radar.Application.EntityResolution",
        "Radar.Application.Evidence",
        "Radar.Application.Signals",
    ];

    private const string ForbiddenNamespace = "Radar.Application.News";

    [Fact]
    public void NoScoringOrEvidencePipelineType_ReferencesTheNewsObservationSubsystem()
    {
        var assembly = typeof(ScoringEngine).Assembly;
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is null
                || !GuardedNamespaces.Any(ns => type.Namespace.Equals(ns, StringComparison.Ordinal)
                    || type.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var referenced in ReferencedTypes(type))
            {
                if (referenced.Namespace is not null
                    && referenced.Namespace.Equals(ForbiddenNamespace, StringComparison.Ordinal))
                {
                    offenders.Add($"{type.FullName} -> {referenced.FullName}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Scoring/evidence-pipeline types must not reference the news-observation subsystem "
                + "(spec 177 acquisition-only boundary):\n" + string.Join('\n', offenders.Distinct()));
    }

    [Fact]
    public void PositiveControl_TheCollectionOrchestration_DoesReferenceTheArchive()
    {
        // The guard above cannot pass vacuously: the SANCTIONED writer demonstrably reaches the archive
        // through this exact walk, so if the walk went blind the control fails first.
        var referenced = ReferencedTypes(typeof(CollectionPass)).ToList();

        Assert.Contains(referenced, t => t == typeof(INewsObservationArchive));
    }

    /// <summary>
    /// Every type <paramref name="type"/> references structurally: base type, interfaces, ALL fields
    /// (private included — a hidden cached reference must not slip through), property/method/constructor
    /// signatures, and every generic argument, recursively unwrapped.
    /// </summary>
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        var seeds = new List<Type>();
        if (type.BaseType is not null)
        {
            seeds.Add(type.BaseType);
        }

        seeds.AddRange(type.GetInterfaces());
        seeds.AddRange(type.GetFields(all).Select(f => f.FieldType));
        seeds.AddRange(type.GetProperties(all).Select(p => p.PropertyType));
        foreach (var method in type.GetMethods(all))
        {
            seeds.Add(method.ReturnType);
            seeds.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (var ctor in type.GetConstructors(all))
        {
            seeds.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        }

        foreach (var seed in seeds)
        {
            foreach (var unwrapped in Unwrap(seed))
            {
                yield return unwrapped;
            }
        }
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
