using System.Reflection;

namespace Radar.Application.Tests;

/// <summary>
/// The shared reflection walker behind the type-graph architecture guards (spec 140's AD-14 guard, spec
/// 184's lifecycle boundary): every Radar type reachable from a set of roots through declared members,
/// transitively — base types, interfaces, fields (including private and compiler-generated), properties,
/// method/ctor signatures and every generic argument. Extracted from
/// <c>EfficacyReadOnlyGuardrailTests</c> so a second guard does not carry a second, drifting copy.
/// </summary>
internal static class TypeGraphClosure
{
    /// <summary>
    /// Every Radar type reachable from <paramref name="roots"/> through declared members, transitively.
    /// Deliberately includes private fields and compiler-generated members: a leak that hides in a closure
    /// is still a leak.
    /// </summary>
    public static HashSet<Type> TransitiveClosure(IEnumerable<Type> roots)
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

    public static IEnumerable<Type> ReferencedTypes(Type type)
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
}
