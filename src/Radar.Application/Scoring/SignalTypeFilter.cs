using System.Collections.ObjectModel;

using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The set of <see cref="SignalType"/>s one scoring strategy consumes (spec 138) — the first thing that lets
/// a strategy express a <b>hypothesis</b> ("insider clusters alone predict better than the blended score")
/// rather than only a different set of weights. It is a pure membership gate applied between the read and the
/// formula: an out-of-set signal is simply not fed to the formula. It is never deleted, its evidence chain is
/// untouched, and nothing above the scoring stage changes (spec 137's one-collection-pass invariant holds).
/// <para>
/// <b>Canonicalisation is load-bearing.</b> "No filter", "an empty list", and "a list naming every member of
/// <see cref="SignalType"/>" are the SAME strategy — they all consume everything — so
/// <see cref="Create(IEnumerable{SignalType}?)"/> collapses all three onto the single <see cref="All"/>
/// instance. <see cref="Describe(string)"/> then returns its input verbatim for <see cref="All"/>, which is
/// what kept the pinned default fingerprints byte-identical when spec 138 shipped (they were AI-OFF
/// <c>radar-scoring-fp-6b2f468041b9</c> / AI-ON <c>radar-scoring-fp-57356123e09b</c> then; they have moved
/// several times since for reasons unrelated to this type — <c>ScoringConfigFingerprintTests</c> holds the
/// current values and the lineage). The property this passthrough guarantees is unchanged: the default
/// strategy hashes exactly the descriptor it hashed before this type existed.
/// </para>
/// </summary>
public sealed class SignalTypeFilter : IEquatable<SignalTypeFilter>
{
    /// <summary>Every declared <see cref="SignalType"/> member, ordered by underlying value.</summary>
    private static readonly SignalType[] DeclaredTypes = [.. Enum.GetValues<SignalType>().OrderBy(t => (int)t)];

    /// <summary>
    /// The exposed view of <see cref="DeclaredTypes"/>. Handing the array itself out behind
    /// <see cref="IReadOnlyList{T}"/> would let a caller cast it back to <c>SignalType[]</c> and mutate the
    /// shared static — which every filter in the process reads — so the public surface is a genuinely
    /// read-only wrapper.
    /// </summary>
    private static readonly ReadOnlyCollection<SignalType> DeclaredTypesView = Array.AsReadOnly(DeclaredTypes);

    /// <summary>
    /// The canonical "consumes everything" filter — the default for every strategy, and the value an
    /// omitted/empty/exhaustive configured set canonicalises to.
    /// </summary>
    public static SignalTypeFilter All { get; } = new(types: null);

    // Null ⇒ all types (the canonical sentinel). Non-null ⇒ a proper subset.
    private readonly HashSet<SignalType>? _types;

    // Exposed via Types, so it is a read-only wrapper rather than the backing array: an IReadOnlyList<T>
    // handed out as a bare array can be cast back to SignalType[] and mutated, and this instance is shared
    // (All is a singleton; every engine holds its filter for the process lifetime).
    private readonly ReadOnlyCollection<SignalType> _ordered;
    private readonly string _segment;

    private SignalTypeFilter(IReadOnlyList<SignalType>? types)
    {
        if (types is null)
        {
            _types = null;
            _ordered = DeclaredTypesView;
            _segment = string.Empty;
            return;
        }

        _types = [.. types];
        _ordered = Array.AsReadOnly<SignalType>([.. types]);

        // Canonical encoding of a proper subset. Two deliberate choices:
        //   * ORDERED BY THE UNDERLYING ENUM VALUE, so the order the types were listed in config is
        //     irrelevant (["A","B"] and ["B","A"] are the same strategy and must hash the same) and so the
        //     encoding is insertion-stable for the way SignalType actually evolves — new members are appended
        //     before the Other sentinel, which never reorders the existing ones.
        //   * The PAYLOAD IS THE MEMBER NAME, not the numeric value, because SignalType is persisted by name
        //     everywhere else in Radar (see the comments on the enum itself). Renaming a member is therefore a
        //     deliberate, VISIBLE re-stamp of every strategy that named it — exactly like a rename of anything
        //     else Radar persists by name — rather than a silent no-op that would leave two differently-named
        //     signal sets sharing one ScoringConfigVersion.
        // DescriptorEscaping is the shared descriptor-escaping primitive (AD-3 injectivity); member names are
        // delimiter-free today, but a value spliced into a descriptor is escaped on principle.
        var csv = string.Join(',', _ordered.Select(t => DescriptorEscaping.Escape(t.ToString())));
        _segment = $"signalTypes={csv};";
    }

    /// <summary>
    /// True when this filter consumes every signal type — the default, and the canonical form of an
    /// omitted, empty, or exhaustive configured set.
    /// </summary>
    public bool IsAll => _types is null;

    /// <summary>
    /// The consumed types, ordered by underlying enum value (for logging and tests). For <see cref="All"/>
    /// this is every declared member, so <c>Create(filter.Types)</c> round-trips to the same filter. A
    /// genuinely read-only view — it cannot be cast back to a mutable array.
    /// </summary>
    public IReadOnlyList<SignalType> Types => _ordered;

    /// <summary>
    /// Canonicalises a declared signal-type set. Null, empty, or a set covering every declared
    /// <see cref="SignalType"/> all return <see cref="All"/> — the byte-identical default. Duplicates are
    /// removed and order is irrelevant.
    /// </summary>
    /// <exception cref="ArgumentException">An undeclared enum value was supplied.</exception>
    public static SignalTypeFilter Create(IEnumerable<SignalType>? types)
    {
        if (types is null)
        {
            return All;
        }

        var distinct = new HashSet<SignalType>();
        foreach (var type in types)
        {
            if (!Enum.IsDefined(type))
            {
                throw new ArgumentException(
                    $"'{(int)type}' is not a declared SignalType; a strategy's signal-type set must name real "
                        + $"signal types ({string.Join(", ", DeclaredTypes)}).",
                    nameof(types));
            }

            distinct.Add(type);
        }

        // Empty ⇒ all (an unstated set states nothing), and a set naming EVERY member is the same strategy as
        // stating nothing — both canonicalise onto All so neither can move the default fingerprint.
        if (distinct.Count == 0 || distinct.Count == DeclaredTypes.Length)
        {
            return All;
        }

        return new SignalTypeFilter([.. DeclaredTypes.Where(distinct.Contains)]);
    }

    /// <summary>True when this strategy consumes <paramref name="type"/>. Always true for <see cref="All"/>.</summary>
    public bool Includes(SignalType type) => _types is null || _types.Contains(type);

    /// <summary>
    /// Folds this filter into the signal-source descriptor that the <c>ScoringConfigVersion</c> fingerprint
    /// hashes, so a strategy's efficacy series is honestly scoped to the signal set it actually consumed.
    /// Returns <paramref name="sourceDescriptor"/> <b>verbatim</b> for <see cref="All"/> — the default must
    /// hash exactly what it hashed before this type existed — and otherwise appends a canonical
    /// <c>signalTypes=…;</c> segment after the existing segments (fixed field ordering, AD-3).
    /// </summary>
    public string Describe(string sourceDescriptor)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        return _types is null ? sourceDescriptor : sourceDescriptor + _segment;
    }

    /// <inheritdoc />
    public bool Equals(SignalTypeFilter? other) =>
        other is not null && string.Equals(_segment, other._segment, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SignalTypeFilter);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_segment);

    /// <inheritdoc />
    public override string ToString() =>
        _types is null ? "all types" : string.Join(",", _ordered);
}
