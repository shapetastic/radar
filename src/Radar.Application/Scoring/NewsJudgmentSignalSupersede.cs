using System.Collections.Frozen;

using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 194 §1.3 — the pure, read/assembly-time supersede of the ordinary news attention event by the
/// judgment-DERIVED directional signal over the SAME article evidence.
///
/// <para>
/// <b>The gap this closes, stated once so the class is legible without the spec.</b> §1.2 materializes one
/// grounded <see cref="SignalType.MediaAttention"/> signal per validated judgment, anchored to the evidence
/// that judgment actually cited. But that article was already collected and already extracted, so its
/// ordinary Neutral <c>MediaAttention</c> signal is on disk too. Without this step the cited article would
/// contribute TWO attention signals over ONE evidence id: the activity, the signal count and the media
/// channel's saturation would all rise simply because Radar formed a judgment — which would reintroduce, in
/// miniature, exactly the volume inflation the whole correction exists to remove. The rule is therefore that
/// the grounded signal REPLACES the ordinary one rather than joining it: one attention event in, one
/// attention event out.
/// </para>
/// <para>
/// <b>The rule.</b> Among <see cref="SignalType.MediaAttention"/> signals sharing one <c>EvidenceId</c>, a
/// structurally valid <c>news-judgment-signal-v1</c> signal (see
/// <see cref="NewsDirectionalSignalMetadata.IsJudgmentDerived"/> — the ONE definition of that predicate,
/// shared with §1.4's neutralization and §1.5's <c>media-collapse-v2</c>) supersedes every other
/// <c>MediaAttention</c> signal over that evidence: the ordinary Neutral article signal, and any accrued
/// spec-191 v7 directional article signal. If more than one materialized signal shares the anchor, the
/// LATEST <c>CreatedAtUtc</c> wins, then the lowest <c>Id</c> — the newest grounded read of the same article
/// is the current one, which is the opposite of the guidance supersede's earliest-observed tie-break and
/// deliberately so: <c>ObservedAtUtc</c> on a materialized signal is the ARTICLE's instant (identical across
/// re-materializations), so only the creation instant distinguishes them.
/// </para>
/// <para>
/// <b>Without a valid materialized signal, this transform removes NOTHING.</b> An evidence id carrying only
/// ordinary and/or legacy media signals is completely untouched — de-noising a company's ordinary news
/// volume is the same-event <see cref="MediaAttentionCollapse"/>'s job, not this one's, and quietly widening
/// a supersede into a second collapse would drop attention events no judgment ever replaced.
/// </para>
/// <para>
/// Deterministic (AD-3): no clock, config, state, IO or randomness. Winner selection uses a strict
/// comparison, so the survivor never depends on input order, and survivors keep the input's relative
/// ordering.
/// </para>
/// </summary>
public static class NewsJudgmentSignalSupersede
{
    /// <summary>
    /// The versioned identity of THIS supersede rule. Declared public and hashed into nothing yet, exactly as
    /// <see cref="LegacyNewsInheritanceNeutralization.Version"/> is: spec 194 §2 folds both into
    /// <c>SignalSourceDescriptor.CanonicalDescriptor()</c> in its own pass, so that changing WHICH signal
    /// replaces which can never hide inside an unchanged <c>ScoringConfigVersion</c>. Bump it when the match
    /// or the winner rule changes — not when a comment does.
    /// </summary>
    public const string Version = "news-judgment-supersede-v1";

    /// <summary>
    /// Applies the supersede to the current-window signal+evidence pairs the engine scores. Returns the INPUT
    /// INSTANCE (and an empty count map) when no supersede can apply — i.e. on every company that has no
    /// materialized judgment signal in the window, which is most of them.
    /// </summary>
    public static NewsJudgmentSupersedeResult<ScoringSignal> Apply(IReadOnlyList<ScoringSignal> signals) =>
        ApplyCore(signals, static s => s.Signal);

    /// <summary>
    /// Applies the supersede to a plain signal list — the activity-only previous window used for velocity.
    /// It must run there too, and for the same reason: if the cited article's ordinary signal and its
    /// grounded companion both counted as previous-window activity, velocity would read the company as
    /// accelerating purely because a judgment was formed in the earlier window. The previous window builds no
    /// contributions or evidence links by design (AD-6), so its removals are reported through this result and
    /// the engine's aggregated log line rather than through a contribution reason.
    /// </summary>
    public static NewsJudgmentSupersedeResult<Signal> Apply(IReadOnlyList<Signal> signals) =>
        ApplyCore(signals, static s => s);

    private static NewsJudgmentSupersedeResult<T> ApplyCore<T>(
        IReadOnlyList<T> items, Func<T, Signal> signalOf)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Pass 1: the winner per EvidenceId, over the materialized signals ALONE. Building the map from only
        // the valid v1 signals is what makes "without a valid materialized signal, nothing is removed" true
        // by construction rather than by a later guard: an evidence id with no materialized signal simply has
        // no entry, so pass 2 cannot remove anything for it.
        //
        // The classification parses JSON, so it is guarded by the cheap type test first and, above all, by
        // the fact that MetadataJson is null on essentially every signal in the store (the classifier returns
        // None immediately for a blank envelope).
        Dictionary<Guid, Signal>? winners = null;
        foreach (var item in items)
        {
            var signal = signalOf(item);
            if (signal.Type != SignalType.MediaAttention
                || !NewsDirectionalSignalMetadata.IsJudgmentDerived(signal))
            {
                continue;
            }

            winners ??= [];
            if (!winners.TryGetValue(signal.EvidenceId, out var incumbent) || Beats(signal, incumbent))
            {
                winners[signal.EvidenceId] = signal;
            }
        }

        if (winners is null)
        {
            return NewsJudgmentSupersedeResult<T>.Untouched(items);
        }

        // Pass 2: filter, preserving the input's relative ordering of survivors. The emitted-set guard keeps
        // AT MOST one MediaAttention signal per superseded EvidenceId even if the winner appears twice (exact
        // duplicate copies), mirroring GuidanceChangeSupersede.
        //
        // Every removed signal is charged to the SURVIVOR of its own EvidenceId — the only defensible
        // attribution available, and the same one spec 193 chose for the guidance supersede: there is exactly
        // one signal that took the removed signal's place for that article, and keeping the trace on it is
        // what lets a reader of the persisted ScoreEvidenceLink see that this contribution REPLACED something
        // rather than that something simply vanished.
        var result = new List<T>(items.Count);
        var emittedEvidenceIds = new HashSet<Guid>();
        Dictionary<Guid, int>? supersededBySurvivorId = null;
        foreach (var item in items)
        {
            var signal = signalOf(item);
            if (signal.Type != SignalType.MediaAttention
                || !winners.TryGetValue(signal.EvidenceId, out var winner))
            {
                result.Add(item);
                continue;
            }

            if (signal.Id == winner.Id && emittedEvidenceIds.Add(signal.EvidenceId))
            {
                result.Add(item);
                continue;
            }

            supersededBySurvivorId ??= [];
            supersededBySurvivorId[winner.Id] = supersededBySurvivorId.GetValueOrDefault(winner.Id) + 1;
        }

        return new NewsJudgmentSupersedeResult<T>(
            result,
            supersededBySurvivorId is null
                ? NewsJudgmentSupersedeResult<T>.NoCounts
                : supersededBySurvivorId);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> supersedes <paramref name="incumbent"/> for the same evidence.
    /// Both sides are already known to be valid materialized signals, so the only question is which grounded
    /// read is the current one: latest <c>CreatedAtUtc</c>, then lowest <c>Id</c>. Strict — never true for
    /// the same signal, which is what makes the winner independent of input order (AD-3).
    /// </summary>
    private static bool Beats(Signal candidate, Signal incumbent)
    {
        var byCreated = candidate.CreatedAtUtc.CompareTo(incumbent.CreatedAtUtc);
        return byCreated != 0 ? byCreated > 0 : candidate.Id.CompareTo(incumbent.Id) < 0;
    }
}

/// <summary>
/// The result of a <see cref="NewsJudgmentSignalSupersede.Apply(IReadOnlyList{ScoringSignal})"/>: the
/// surviving signals and, per SURVIVING <c>Signal.Id</c>, how many ordinary/legacy
/// <see cref="SignalType.MediaAttention"/> signals over the same article evidence it superseded (only
/// entries with a positive count are present).
/// <para>
/// The shape deliberately mirrors <see cref="GuidanceChangeSupersedeResult{T}"/> and
/// <see cref="LegacyNewsInheritanceResult{T}"/> — survivors plus a map keyed by a surviving signal's id —
/// rather than inventing a fourth one, so the assembly steps that now sit in a row inside
/// <c>ScoringEngine.ScoreCompanyAsync</c> read the same way. Each step keeps its OWN result type rather than
/// sharing one generic <c>SupersedeResult</c>, because the count on each means a different thing (stale
/// guidance replaced, ordinary attention replaced, direction suppressed) and a single shared type would let
/// a caller mix them up in exactly the place where the numbers are rendered into provenance.
/// </para>
/// </summary>
public sealed record NewsJudgmentSupersedeResult<T>(
    IReadOnlyList<T> Signals,
    IReadOnlyDictionary<Guid, int> SupersededCounts)
{
    /// <summary>
    /// The shared empty map, so the untouched fast path allocates no dictionary. Frozen rather than a bare
    /// <see cref="Dictionary{TKey,TValue}"/> precisely BECAUSE it is shared (the
    /// <see cref="GuidanceChangeSupersedeResult{T}"/> precedent): a consumer casting the interface back to
    /// the concrete type could otherwise mutate every past and future fast-path result at once.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Guid, int> NoCounts = FrozenDictionary<Guid, int>.Empty;

    /// <summary>
    /// The fast path: no materialized judgment signal was present, so no supersede could apply and the INPUT
    /// INSTANCE is handed back unchanged (no list allocation) alongside an empty count map. Byte-identical to
    /// not running the transform at all.
    /// </summary>
    internal static NewsJudgmentSupersedeResult<T> Untouched(IReadOnlyList<T> signals) =>
        new(signals, NoCounts);

    /// <summary>
    /// How many signals the supersede removed in total — the sum over every survivor. Drives the aggregated
    /// per-company scoring log; the per-survivor breakdown drives the contribution reasons.
    /// </summary>
    public int TotalSuperseded
    {
        get
        {
            var total = 0;
            foreach (var count in SupersededCounts.Values)
            {
                total += count;
            }

            return total;
        }
    }
}
