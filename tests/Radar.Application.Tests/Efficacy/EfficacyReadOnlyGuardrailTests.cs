using System.Reflection;

using Radar.Application.Scoring;

namespace Radar.Application.Tests.Efficacy;

/// <summary>
/// Structural guardrail for the AD-14 read-only boundary (spec 101, test 13): the efficacy subsystem
/// (<c>Radar.Application/Efficacy/*</c>) must depend on NO evidence/signal/scoring <b>write</b> type. It reads
/// only <c>IScoreSnapshotFileStore</c> / <c>IPriceHistoryStore</c> / <c>ICompanyRepository</c> and writes only
/// <c>IEfficacyArtifactStore</c>. A source scan keeps a future edit from silently letting price/score data flow
/// back into scoring.
/// </summary>
public sealed class EfficacyReadOnlyGuardrailTests
{
    // Write/compute types the efficacy layer must never reference (reading persisted score history via
    // IScoreSnapshotFileStore is allowed; these are the evidence/signal/scoring WRITE + compute seams).
    // NOTE: deliberately NOT including "IRadarPipeline" — the efficacy layer legitimately DOCUMENTS that it runs
    // OUTSIDE IRadarPipeline (that prose is the boundary, not a dependency). These are the actual write/compute
    // seams that must never be referenced.
    private static readonly string[] ForbiddenTypeReferences =
    [
        "EvidenceItem",
        "CollectedEvidence",
        "ISignalExtractor",
        "IScoringEngine",
        "ScoringEngine",
        "ScoringConfigFingerprint",
        "ISignalRepository",
        "IEvidenceRepository",
        "IEvidenceCollector",
        // The durable WRITE seams the original list never named. Their write methods are called WriteAsync,
        // which cannot join ForbiddenMutationCalls below because the efficacy artifact stores legitimately
        // use that name — so banning the TYPES is the only guard that catches them. None of these is
        // referenced by any efficacy source today; this pins that.
        "ISignalFileStore",
        "IRawEvidenceStore",
        "IScoreRepository",
    ];

    // ---------------------------------------------------------------------------------------------------
    // Spec 169: the ONE sanctioned exception, narrowed rather than waived.
    //
    // AD-16's attention-arrival screen has to READ durable signals and evidence — its whole outcome is
    // "distinct third-party publishers with a resolving MediaAttention signal", which is not expressible over
    // score snapshots alone. So Efficacy/Attention may name the two READ seams and the evidence record it
    // reads fields off. It may still NOT name any collection/extraction/scoring COMPUTE type, and the
    // second test below asserts positively that it never calls a repository MUTATION — which is the property
    // the type ban was standing in for. That is a stronger check than the name ban it replaces here, not a
    // weaker one.
    // ---------------------------------------------------------------------------------------------------
    private const string AttentionSubfolder = "Attention";

    private static readonly string[] AttentionReadSeamExemptions =
    [
        "EvidenceItem",
        "ISignalRepository",
        "IEvidenceRepository",
    ];

    // Every repository/store MUTATION the efficacy layer must never call. The attention screen's ONLY
    // sanctioned write is IAttentionArrivalArtifactStore.WriteAsync, which is why the artifact store's own
    // method name is deliberately absent from this list and the store type is not scanned here.
    private static readonly string[] ForbiddenMutationCalls =
    [
        "AddAsync(",
        "AddIfNewAsync(",
        "AddSnapshotAsync(",
        "AddEvidenceLinkAsync(",
        "AddAliasAsync(",
        "AddSourceFeedAsync(",
        "WriteIfNewAsync(",
    ];

    [Fact]
    public void EfficacySources_ReferenceNoEvidenceSignalOrScoringWriteType()
    {
        var efficacyDir = LocateEfficacySourceDirectory();
        // AllDirectories: the guardrail must keep covering efficacy code if it later grows subfolders.
        var files = Directory.GetFiles(efficacyDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var isAttentionScreen = IsAttentionScreenSource(file);

            foreach (var forbidden in ForbiddenTypeReferences)
            {
                if (isAttentionScreen && AttentionReadSeamExemptions.Contains(forbidden, StringComparer.Ordinal))
                {
                    continue;
                }

                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} references forbidden type '{forbidden}' — the efficacy layer must "
                        + "stay read-only over score history + price (AD-14 read side).");
            }
        }
    }

    /// <summary>
    /// The positive half of the spec-169 exemption: the attention screen may NAME the signal/evidence read
    /// seams, but it must never CALL a mutation on them. This is what the type ban was standing in for, and
    /// asserting it directly is stronger — a future edit that adds a write is caught by the call, not by
    /// whether it happened to introduce a new type name.
    /// </summary>
    [Fact]
    public void AttentionScreenSources_CallNoRepositoryMutation()
    {
        var efficacyDir = LocateEfficacySourceDirectory();
        var files = Directory
            .GetFiles(efficacyDir, "*.cs", SearchOption.AllDirectories)
            .Where(IsAttentionScreenSource)
            .ToList();

        // The exemption must not be able to pass vacuously: if the folder is ever emptied or renamed, this
        // fails rather than silently certifying nothing.
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var mutation in ForbiddenMutationCalls)
            {
                Assert.False(
                    text.Contains(mutation, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} calls '{mutation}' — the AD-16 attention screen is READ-ONLY "
                        + "over signals, evidence, scores and reviews; its only sanctioned write is the "
                        + "attention-arrival artifact store.");
            }
        }
    }

    private static bool IsAttentionScreenSource(string file) =>
        Path.GetDirectoryName(file) is { } directory
        && string.Equals(
            Path.GetFileName(directory), AttentionSubfolder, StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------------------
    // AD-14, asserted on the TYPE GRAPH rather than on source text (spec 140).
    //
    // "Price is validation-only, never a scoring input" has to survive a refactor that renames a type or
    // reaches a price value through three hops. A reflection walk over the transitive closure of the scoring
    // namespace — base types, interfaces, fields (including private), properties, method/ctor signatures and
    // every generic argument — proves no price or efficacy type is reachable from it AT ALL.
    // ---------------------------------------------------------------------------------------------------

    private const string ScoringNamespace = "Radar.Application.Scoring";
    private const string PricesNamespace = "Radar.Application.Prices";
    private const string EfficacyNamespace = "Radar.Application.Efficacy";
    private const string ComparisonNamespace = "Radar.Application.Efficacy.Comparison";

    [Fact]
    public void ScoringTypeGraph_CanNeverReachAPriceOrEfficacyType()
    {
        var assembly = typeof(ScoringInput).Assembly;

        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith(ScoringNamespace, StringComparison.Ordinal))
            .ToList();

        // The walk must actually have something to walk, and ScoringInput — the engine's input record, the
        // one type a price value would have to travel through — must be in it.
        Assert.NotEmpty(roots);
        Assert.Contains(typeof(ScoringInput), roots);

        var reachable = TransitiveClosure(roots);

        var leaks = reachable
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(EfficacyNamespace, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "AD-14: no price/efficacy type may be reachable from the scoring type graph, but these are: "
                + string.Join(", ", leaks));
    }

    [Fact]
    public void ComparisonModule_DoesReadPrice_SoTheScoringGuardrailIsNotVacuous()
    {
        // The positive control. If the comparison module stopped referencing price, the assertion above would
        // still pass while proving nothing — this pins that price really is read, just on the other side of
        // the boundary.
        var comparisonTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == ComparisonNamespace)
            .ToList();

        Assert.NotEmpty(comparisonTypes);

        var reachesPrice = TransitiveClosure(comparisonTypes)
            .Any(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal));

        Assert.True(reachesPrice, "The comparison module is supposed to read price — downstream of scoring.");
    }

    [Fact]
    public void ComparisonModule_TouchesOnlyScoringOUTPUTTypes()
    {
        // What the comparison is allowed to know about the scoring namespace: the READ seam over persisted
        // snapshots, and the composition-time description of a strategy. Nothing that computes, mutates, or
        // fingerprints a score.
        string[] permitted =
        [
            nameof(IScoreSnapshotFileStore),
            nameof(ScoringStrategyDefinition),
            nameof(ScoringStrategySet),
        ];

        var comparisonTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == ComparisonNamespace)
            .ToList();

        Assert.NotEmpty(comparisonTypes);

        var scoringReferences = comparisonTypes
            .SelectMany(ReferencedTypes)
            .Where(t => t.Namespace == ScoringNamespace)
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unexpected = scoringReferences.Except(permitted, StringComparer.Ordinal).ToList();
        Assert.True(
            unexpected.Count == 0,
            "The comparison module may depend on scoring OUTPUT only, but it references: "
                + string.Join(", ", unexpected));
    }

    private const string StatisticsNamespace = "Radar.Application.Efficacy.Statistics";

    [Fact]
    public void StatisticsModule_IsOutcomeAgnostic_ReachesNeitherPriceNorTheComparisonHarness()
    {
        // Spec 155: the interval/sign-test/purge helpers exist so the AD-16 attention evaluator can reuse
        // them WITHOUT importing the price harness. That promise is structural: the statistics namespace
        // must reach no price type and no comparison type (the dependency points the other way).
        var statisticsTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == StatisticsNamespace)
            .ToList();

        Assert.NotEmpty(statisticsTypes);

        var leaks = TransitiveClosure(statisticsTypes)
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(ComparisonNamespace, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The efficacy statistics helpers must stay outcome-agnostic (no price, no comparison types), "
                + "but these are reachable: " + string.Join(", ", leaks));
    }

    private const string AttentionNamespace = "Radar.Application.Efficacy.Attention";
    private const string ClaimsNamespace = "Radar.Application.Efficacy.Claims";

    // ---------------------------------------------------------------------------------------------------
    // Spec 170: the AD-15 gate is composite, and the wiring must not be circular. The neutral
    // Efficacy.Claims namespace exists precisely so the comparison can consume AD-16's outcome WITHOUT
    // referencing an Attention type: Attention → Claims and Comparison → Claims are permitted;
    // Comparison → Attention is forbidden — asserted on the TYPE GRAPH, with positive controls so the
    // guard cannot pass vacuously.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ComparisonModule_NeverReachesAnAttentionType()
    {
        var comparisonTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == ComparisonNamespace)
            .ToList();

        Assert.NotEmpty(comparisonTypes);

        var leaks = TransitiveClosure(comparisonTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(AttentionNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "The comparison module must consume AD-16's outcome through the neutral Efficacy.Claims "
                + "prerequisite, never an Attention type — but these are reachable: "
                + string.Join(", ", leaks));
    }

    [Fact]
    public void ClaimsModule_IsNeutral_ReachesNeitherAttentionNorComparisonNorPrice()
    {
        var claimsTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == ClaimsNamespace)
            .ToList();

        Assert.NotEmpty(claimsTypes);

        var leaks = TransitiveClosure(claimsTypes)
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith(AttentionNamespace, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(ComparisonNamespace, StringComparison.Ordinal)
                    || t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            "Efficacy.Claims is the namespace BOTH sides may depend on, so it must depend on neither — "
                + "but these are reachable: " + string.Join(", ", leaks));
    }

    [Fact]
    public void AttentionAndComparison_BothReachClaims_SoTheNeutralityGuardIsNotVacuous()
    {
        // The positive controls: the mapper (Attention → Claims) and the gate consumption
        // (Comparison → Claims) really exist. If either stopped referencing Claims, the guards above would
        // still pass while proving nothing about the boundary.
        var assembly = typeof(ScoringInput).Assembly;

        bool ReachesClaims(string ns) => TransitiveClosure(
                assembly.GetTypes().Where(t => t.Namespace == ns).ToList())
            .Any(t => t.Namespace == ClaimsNamespace);

        Assert.True(ReachesClaims(AttentionNamespace), "Attention is supposed to map onto Claims (spec 170).");
        Assert.True(ReachesClaims(ComparisonNamespace), "Comparison is supposed to consume Claims (spec 170).");
    }

    [Fact]
    public void AttentionScreenModule_TouchesOnlyScoringOUTPUTTypes()
    {
        // The spec-169 mirror of the test above, asserted on the TYPE GRAPH rather than on prose: the AD-16
        // screen reads persisted snapshots and the composition-time description of a strategy, and knows
        // nothing that computes, mutates or fingerprints a score.
        string[] permitted =
        [
            nameof(IScoreSnapshotFileStore),
            nameof(ScoringStrategyDefinition),
            nameof(ScoringStrategySet),
        ];

        var attentionTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == AttentionNamespace)
            .ToList();

        Assert.NotEmpty(attentionTypes);

        var scoringReferences = attentionTypes
            .SelectMany(ReferencedTypes)
            .Where(t => t.Namespace == ScoringNamespace)
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unexpected = scoringReferences.Except(permitted, StringComparer.Ordinal).ToList();
        Assert.True(
            unexpected.Count == 0,
            "The attention-arrival screen may depend on scoring OUTPUT only, but it references: "
                + string.Join(", ", unexpected));
    }

    [Fact]
    public void AttentionScreenModule_NeverReachesAPriceType()
    {
        // AD-16's outcome is ATTENTION ARRIVING LATER, not price. Price is validation-only (AD-14) and has
        // no place in this screen at all — not as an input, not as a reported diagnostic.
        var attentionTypes = typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == AttentionNamespace)
            .ToList();

        Assert.NotEmpty(attentionTypes);

        var priceLeaks = TransitiveClosure(attentionTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            priceLeaks.Count == 0,
            "The attention-arrival screen must never reach a price type, but these are reachable: "
                + string.Join(", ", priceLeaks));
    }

    /// <summary>
    /// Every Radar type reachable from <paramref name="roots"/> through declared members, transitively.
    /// Deliberately includes private fields and compiler-generated members: a leak that hides in a closure is
    /// still a leak.
    /// </summary>
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

    /// <summary>Peels arrays, by-refs, pointers and generic arguments so a wrapped leak is still visible.</summary>
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

    private static string LocateEfficacySourceDirectory()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var efficacyDir = Path.Combine(dir!.FullName, "src", "Radar.Application", "Efficacy");
        Assert.True(Directory.Exists(efficacyDir), $"Expected efficacy source directory at {efficacyDir}.");
        return efficacyDir;
    }
}
