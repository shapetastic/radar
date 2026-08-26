using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.Pipeline;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.SignalExtraction;

/// <summary>
/// SPEC 194 1.1 - ordinary news extraction is Neutral again, and the extractor has NO seam through which a
/// company judgment could reach it.
/// <para>
/// Spec 191 gave the <see cref="EvidenceSourceType.NewsArticle"/> branch a direction taken from an optional
/// <c>INewsDirectionalReadSource</c>. That read ran DURING collection, while the stage-2 judge runs after
/// it, so the only judgment available to a newly collected article was one produced from earlier articles it
/// had never read: Tuesday's headline inherited Monday's verdict, and one call was multiplied by however
/// many headlines followed. The seam is deleted. These tests pin both halves of the correction - the emitted
/// signal is byte-for-byte the pre-191 Neutral media-attention event, and the extractor's type graph cannot
/// reach the news subsystem at all, so the inheritance cannot be reintroduced by accident.
/// </para>
/// </summary>
public sealed class KeywordSignalExtractorNewsNeutralityTests
{
    // The exact pre-191 reason string. Pinned as a literal (not read off the production constant) so a
    // change to what a news signal says is a deliberate, visible edit.
    private const string NeutralReason = "Third-party news coverage (media attention)";

    private static readonly DateTimeOffset CollectedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] NewsSubsystemNamespacePrefixes =
    [
        "Radar.Application.News",        // covers News, NewsRisk and NewsTyping by prefix - deliberately
    ];

    private static EvidenceItem NewsEvidence(string title) =>
        new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithTitle(title)
            .WithSourceName("Example Wire")
            .WithRawText($"{title} - example.wire (2026-08-20T00:00:00Z). Source: https://example.com/a")
            .WithCollectedAtUtc(CollectedAt)
            .Build();

    private static Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence) =>
        new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
            .ExtractAsync(evidence, CancellationToken.None);

    [Fact]
    public async Task ANewsArticle_ExtractsExactlyThePreSpec191NeutralMediaAttentionSignal()
    {
        var evidence = NewsEvidence("Acme reports record quarterly orders");

        var output = await ExtractAsync(evidence);
        var signal = Assert.Single(output.Signals);

        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal(nameof(SignalDirection.Neutral), signal.Direction);
        Assert.Equal(4, signal.Strength);
        Assert.Equal(4, signal.Novelty);
        Assert.Equal(0.5m, signal.Confidence);
        Assert.Equal(NeutralReason, signal.Reason);
        Assert.Equal(evidence.SourceName, signal.CompanyMention);

        // No judgment id, no cohort key, no observation id - nothing. A judgment-derived direction is a
        // SEPARATE signal now, so an extracted article carries no judgment provenance at all.
        Assert.Null(signal.MetadataJson);

        Assert.Equal("1 media-attention signal extracted from news coverage.", output.OverallSummary);
    }

    [Theory]
    [InlineData("Acme raises guidance and beats expectations")]
    [InlineData("Acme cuts guidance after weak quarter")]
    [InlineData("Acme sees deteriorating demand across every segment")]
    public async Task ADirectionalSoundingHeadline_IsStillNeutral(string title)
    {
        // Two independent reasons, both intended: the keyword rules stay suppressed for third-party
        // coverage (spec 70), and there is no judgment to inherit a direction from (spec 194).
        var signal = Assert.Single((await ExtractAsync(NewsEvidence(title))).Signals);

        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal(nameof(SignalDirection.Neutral), signal.Direction);
        Assert.Equal(4, signal.Strength);
        Assert.Null(signal.MetadataJson);
    }

    [Fact]
    public async Task EveryNewsArticleSignalIsIdentical_HoweverManyArticlesArrive()
    {
        // The v7 failure shape, stated as a property: under article inheritance a company second, third and
        // Nth headline each carried the same borrowed verdict, so ONE judgment produced N units of
        // directional mass. With extraction judgment-blind, N articles produce N identical Neutral attention
        // events and no directional mass at all.
        string[] titles = ["Acme wins award", "Acme opens plant", "Acme names CFO"];

        foreach (var title in titles)
        {
            var signal = Assert.Single((await ExtractAsync(NewsEvidence(title))).Signals);
            Assert.Equal(nameof(SignalDirection.Neutral), signal.Direction);
            Assert.Equal(4, signal.Strength);
            Assert.Null(signal.MetadataJson);
        }
    }

    [Fact]
    public void TheExtractorTakesNoNewsSubsystemDependency()
    {
        // The seam was an OPTIONAL trailing constructor parameter, which is exactly how it could be added
        // back without any existing call site changing. Pin the constructor surface.
        var leaks = typeof(KeywordSignalExtractor)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType.Namespace is not null
                && NewsSubsystemNamespacePrefixes.Any(
                    ns => p.ParameterType.Namespace.StartsWith(ns, StringComparison.Ordinal)))
            .Select(p => $"{p.Name}: {p.ParameterType.FullName}")
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "KeywordSignalExtractor must not depend on the news subsystem (spec 194), but it takes: "
                + string.Join(", ", leaks));
    }

    [Fact]
    public void TheExtractorsTypeGraph_ReachesNoNewsObservationJudgmentOrTypingType()
    {
        // The stronger claim: not merely "no direct parameter", but nothing reachable through the whole
        // declared-member closure. This also keeps the two pre-existing reflection guards (spec 177
        // acquisition-only, spec 179) honest from the extraction side.
        var leaks = TransitiveClosure([typeof(KeywordSignalExtractor)])
            .Where(t => t.Namespace is not null
                && NewsSubsystemNamespacePrefixes.Any(
                    ns => t.Namespace.StartsWith(ns, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(leaks.Count == 0, "KeywordSignalExtractor leaks: " + string.Join(", ", leaks));
    }

    [Fact]
    public void CollectionPass_ReachesNoNewsRiskOrNewsTypingType()
    {
        // Still-live boundary, inherited from the spec-191 seam test and now stronger: the pass no longer
        // prepares any news read, so nothing in Radar.Application.Pipeline touches the judgment/typing side.
        // Radar.Application.News itself is deliberately EXCLUDED - the spec-177 guard sanctions the
        // collection orchestration as the observation archive WRITER (CollectionPass holds
        // INewsObservationArchive), so the meaningful claim for the pass is the NewsRisk/NewsTyping one.
        var closure = TransitiveClosure([typeof(CollectionPass)]);

        var leaks = closure
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith("Radar.Application.NewsRisk", StringComparison.Ordinal)
                    || t.Namespace.StartsWith("Radar.Application.NewsTyping", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(leaks.Count == 0, "CollectionPass leaks: " + string.Join(", ", leaks));

        // Positive control: the walk really does traverse the pass (it holds the archive it writes to), so
        // the assertion above cannot pass because the closure came back empty.
        Assert.Contains(typeof(INewsObservationArchive), closure);
    }

    [Fact]
    public void NoDirectionalNewsReadSeamTypeSurvivesInTheApplicationAssembly()
    {
        // "There is no seam left for it to consult", asserted rather than assumed: the spec-191 interface
        // and its request/response record are GONE, not merely unreferenced. A dormant seam is how the
        // inheritance quietly comes back.
        var survivors = typeof(KeywordSignalExtractor).Assembly.GetTypes()
            .Where(t => t.Name is "INewsDirectionalReadSource" or "NewsDirectionalRead"
                or "NewsDirectionalReadSource" or "NewsDirectionalReadOptions")
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            survivors.Count == 0,
            "The spec-191 article-inheritance seam must be deleted, not dormant (spec 194), but these "
                + "types survive: " + string.Join(", ", survivors));
    }

    [Fact]
    public void RuleSetVersionIsV8_ACorrectionNotARollbackToV6()
    {
        // The identity that carries this correction into every ScoringConfigVersion. v6 and v8 emit the same
        // NewsArticle signal but do not mean the same thing: v8 sits downstream of a live judgment layer.
        Assert.Equal("radar-keyword-rules-v8", KeywordSignalExtractor.RuleSetVersion);
    }

    /// <summary>Transitive closure over declared members - private fields included (a leak in a closure is still a leak).</summary>
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
