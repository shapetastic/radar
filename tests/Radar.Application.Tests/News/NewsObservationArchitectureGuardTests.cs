using System.Reflection;
using System.Text.RegularExpressions;

using Radar.Application.News;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Tests.News;

/// <summary>
/// Structural guardrail for spec 177's "acquisition only" boundary (the
/// <see cref="Radar.Application.Tests.Efficacy.EfficacyReadOnlyGuardrailTests"/> precedent, applied on the
/// TYPE GRAPH): no type in <c>Radar.Application.Scoring</c> or the evidence/signal pipeline machinery may
/// reference the news-observation archive or the content reader. The archive is observational — it must be
/// structurally impossible for a score, an extraction or a review to read it, or the point-in-time record
/// would quietly become a scoring input and AD-14-style honesty would erode.
/// <para>
/// <b>SPEC 194 RESTORED this guard to its strongest form.</b> Spec 191 let the news read reach the signal
/// layer through one seam in <c>Radar.Application.SignalExtraction</c> whose request/response types carried
/// Domain and BCL types ONLY, with the implementation on the FAR side in <c>Radar.Application.News</c>.
/// Spec 194 §1.1 deleted that seam outright: an article was taking its direction from a company judgment
/// produced BEFORE it existed, so the direction now rides its own judgment-derived signal materialized after
/// the judgment. The extraction type graph therefore reaches nothing here at all.
/// <see cref="Radar.Application.Tests.SignalExtraction.KeywordSignalExtractorNewsNeutralityTests"/> asserts
/// that from the extraction side, WITH a positive control so it cannot pass because nobody looked.
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

    // -------------------------------------------------------------------------------------------------
    // SPEC 201 §3: the ban is TOTAL, not type-graph-only. The reflection walk above cannot see a `const`
    // read or a method-body reference (a const is inlined at compile time), and exactly one was live:
    // LegacyNewsInheritanceNeutralization read NewsTrajectorySignalRules.BaseStrength from
    // Radar.Application.News. The constants moved to Radar.Application.SignalExtraction and this SOURCE
    // scan makes the boundary hold at the text level as well. Mutation-proven: re-add
    // `using Radar.Application.News;` to any file under src/Radar.Application/Scoring and this goes red
    // naming file and line.
    // -------------------------------------------------------------------------------------------------

    private static readonly Regex ForbiddenSourceReference = new(
        @"^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?Radar\.Application\.News(Risk)?\s*(\.[\w.]+)?\s*;"
            + @"|Radar\.Application\.News(Risk)?\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoScoringSourceFile_ReferencesNewsOrNewsRisk_AtSourceLevel()
    {
        var scoringRoot = Path.Combine(FindRepositoryRoot(), "src", "Radar.Application", "Scoring");
        Assert.True(Directory.Exists(scoringRoot), $"Expected the Scoring source folder at {scoringRoot}.");

        var offenders = new List<string>();
        var filesScanned = 0;
        foreach (var file in Directory.EnumerateFiles(scoringRoot, "*.cs", SearchOption.AllDirectories))
        {
            filesScanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments may legitimately NAME the banned namespaces (the boundary is documented in
                // place); only code lines are scanned.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ForbiddenSourceReference.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(scoringRoot, file)}:{i + 1}: {trimmed}");
                }
            }
        }

        // Positive control on the scan itself: it must have looked at real files.
        Assert.True(filesScanned > 10, $"Expected to scan the Scoring sources, scanned {filesScanned}.");
        Assert.True(
            offenders.Count == 0,
            "src/Radar.Application/Scoring must carry NO reference of any kind — using directive or "
                + "fully-qualified — to Radar.Application.News or Radar.Application.NewsRisk (spec 201 §3):\n"
                + string.Join('\n', offenders));
    }

    [Fact]
    public void SourceScan_PositiveControl_TheRegexCatchesEveryBannedShape()
    {
        // The scan cannot pass because the pattern went blind: every shape the ban covers is matched.
        Assert.Matches(ForbiddenSourceReference, "using Radar.Application.News;");
        Assert.Matches(ForbiddenSourceReference, "using Radar.Application.NewsRisk;");
        Assert.Matches(ForbiddenSourceReference, "using Radar.Application.NewsRisk.Judgment;");
        Assert.Matches(ForbiddenSourceReference, "using static Radar.Application.News.NewsTrajectorySignalRules;");
        Assert.Matches(ForbiddenSourceReference, "        Strength = Radar.Application.News.NewsTrajectorySignalRules.BaseStrength,");
        Assert.Matches(ForbiddenSourceReference, "var x = global::Radar.Application.NewsRisk.Judgment.NewsJudgmentTrajectory.Unknown;");

        // ...and the namespaces Scoring DOES legitimately reference are not caught.
        Assert.DoesNotMatch(ForbiddenSourceReference, "using Radar.Application.SignalExtraction;");
        Assert.DoesNotMatch(ForbiddenSourceReference, "using Radar.Application.Storage;");
    }

    /// <summary>
    /// Spec 201 §3: relocating the magnitudes out of <c>Radar.Application.News</c> moved NO value — the
    /// mapping's aliases read the SignalExtraction constants — and therefore no fingerprint: the
    /// <c>news=…;</c> identity segment encodes them BY VALUE, so a composition built from the relocated
    /// constants is byte-identical to one built from the spec-194 literals.
    /// </summary>
    [Fact]
    public void RelocatedTrajectoryConstants_KeepTheirValues_AndTheNewsIdentitySegmentIsUnchanged()
    {
        Assert.Equal(4, NewsTrajectorySignalConstants.BaseStrength);
        Assert.Equal(3, NewsTrajectorySignalConstants.MaxFindingContribution);
        Assert.Equal(1, NewsTrajectorySignalConstants.CompleteTypingBonus);
        Assert.Equal(4, NewsTrajectorySignalConstants.Novelty);
        Assert.Equal(0.5m, NewsTrajectorySignalConstants.Confidence);

        Assert.Equal(NewsTrajectorySignalConstants.BaseStrength, NewsTrajectorySignalRules.BaseStrength);
        Assert.Equal(NewsTrajectorySignalConstants.MaxFindingContribution, NewsTrajectorySignalRules.MaxFindingContribution);
        Assert.Equal(NewsTrajectorySignalConstants.CompleteTypingBonus, NewsTrajectorySignalRules.CompleteTypingBonus);
        Assert.Equal(NewsTrajectorySignalConstants.Novelty, NewsTrajectorySignalRules.Novelty);
        Assert.Equal(NewsTrajectorySignalConstants.Confidence, NewsTrajectorySignalRules.Confidence);

        const string cohort = "deepinfra:deepseek|news-judgment-prompt-v3|news-judgment-schema-v3|stage1=x|families=y";
        var viaFactory = NewsJudgmentScoringIdentityFactory.ForPresentationCohort(cohort).Segment;
        var viaSpec194Literals = NewsJudgmentScoringIdentity.ForPresentationCohort(
            cohort,
            NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
            NewsJudgmentScoringIdentityFactory.DirectionMappingTokens,
            4, 3, 1, 4, 0.5m).Segment;

        Assert.Equal(viaSpec194Literals, viaFactory);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Radar.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Radar.sln walking up from {AppContext.BaseDirectory}.");
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
