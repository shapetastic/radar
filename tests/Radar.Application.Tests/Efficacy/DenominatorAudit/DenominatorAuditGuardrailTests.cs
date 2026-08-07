using System.Reflection;

using Radar.Application.Scoring;

namespace Radar.Application.Tests.Efficacy.DenominatorAudit;

/// <summary>
/// The spec-172 boundary, asserted on the TYPE GRAPH (mirroring the spec-140/169 guards in
/// <c>EfficacyReadOnlyGuardrailTests</c>, which its source-text scan already covers for this subfolder): the
/// denominator audit measures score MECHANICS, not efficacy — price has no place in it at all (AD-14 is
/// about price never feeding a score; here price is not even an outcome) — and it may know only scoring
/// OUTPUT types, never anything that computes, mutates or fingerprints a score.
/// </summary>
public sealed class DenominatorAuditGuardrailTests
{
    private const string DenominatorAuditNamespace = "Radar.Application.Efficacy.DenominatorAudit";
    private const string ScoringNamespace = "Radar.Application.Scoring";
    private const string PricesNamespace = "Radar.Application.Prices";

    private static List<Type> AuditTypes() =>
        typeof(ScoringInput).Assembly.GetTypes()
            .Where(t => t.Namespace == DenominatorAuditNamespace)
            .ToList();

    [Fact]
    public void DenominatorAuditModule_NeverReachesAPriceType()
    {
        var auditTypes = AuditTypes();
        Assert.NotEmpty(auditTypes);

        var priceLeaks = TransitiveClosure(auditTypes)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(PricesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            priceLeaks.Count == 0,
            "The denominator audit measures score mechanics, not efficacy — price must never be an input "
                + "or an output of it, but these are reachable: " + string.Join(", ", priceLeaks));
    }

    [Fact]
    public void DenominatorAuditModule_TouchesOnlyScoringOUTPUTTypes()
    {
        // The same allow-list rule the comparison and attention modules are pinned to: the read seam over
        // persisted snapshots and the composition-time description of a strategy. Nothing that computes,
        // mutates, or fingerprints a score.
        string[] permitted =
        [
            nameof(IScoreSnapshotFileStore),
            nameof(ScoringStrategyDefinition),
            nameof(ScoringStrategySet),
        ];

        var auditTypes = AuditTypes();
        Assert.NotEmpty(auditTypes);

        var scoringReferences = auditTypes
            .SelectMany(ReferencedTypes)
            .Where(t => t.Namespace == ScoringNamespace)
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unexpected = scoringReferences.Except(permitted, StringComparer.Ordinal).ToList();
        Assert.True(
            unexpected.Count == 0,
            "The denominator audit may depend on scoring OUTPUT only, but it references: "
                + string.Join(", ", unexpected));
    }

    // ------------------------------------------------------------------------------------------------
    // The same reflection walk EfficacyReadOnlyGuardrailTests uses (private there, so restated here with
    // identical semantics): base types, interfaces, fields (including private), properties, method/ctor
    // signatures and every generic argument.
    // ------------------------------------------------------------------------------------------------

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
