using System.Collections.Frozen;

using Radar.Application.News;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 194 §1.4 — the pure, versioned, read-side admission transform that fails ALREADY-PERSISTED spec-191
/// directional news signals CLOSED to Neutral.
///
/// <para>
/// <b>What went wrong, stated once so this class is legible without the spec.</b> Spec 191 gave news a
/// direction in the signal layer, but it took that direction at EXTRACTION time from the company's latest
/// admitted stage-2 judgment — a judgment that, by the live stage order, had necessarily been produced from
/// EARLIER articles and had never read the article being extracted. One judged call was therefore inherited
/// by every later headline the company collected, multiplying a single verdict into N units of directional
/// mass, N being the company's news volume: the news-volume size proxy spec 191 existed to remove,
/// reintroduced wearing a provenance envelope that made it read as grounded. Spec 194 §1.1 deleted that
/// producer, so nothing mints an inherited direction any more.
/// </para>
/// <para>
/// <b>Why a read-side transform is needed at all.</b> The 24 signals that producer already wrote are on
/// disk. The signal stores are append-only (AD-8/AD-1) and this repo does not backfill or rewrite history,
/// and <c>AddIfNewAsync</c> rejects already-seen evidence, so those signals can never be re-extracted as
/// Neutral either. 16 of them sit inside the live 60-day window and would otherwise keep asserting a
/// direction they never earned, on every future run, indefinitely. The only honest lever left is the one
/// this class is: at SCORING-ASSEMBLY time, admit them with the exact pre-191 Neutral media-attention
/// direction/strength. The persisted signal, its review and its file stay byte-identical — nothing here
/// writes anything.
/// </para>
/// <para>
/// <b>The match is on the exact legacy metadata SHAPE, never on <c>Direction != Neutral</c>.</b> Testing
/// direction alone would silently suppress any future directional media family — including the spec-194
/// §1.2 judgment-DERIVED signal this correction exists to make room for. So a signal qualifies only when it
/// carries the spec-191 provenance keys declared on <see cref="NewsDirectionalSignalMetadata"/> and carries
/// NO <see cref="NewsDirectionalSignalMetadata.JudgmentSignalVersionKey"/> token at all — the current
/// <c>news-judgment-signal-v2</c> identity and the retired-but-still-valid v1 one are BOTH judgment-derived
/// and both pass through untouched (spec 197 §1.3). A <c>MediaAttention</c> signal with no metadata, or with
/// metadata that is not this shape, passes through as the very same instance.
/// </para>
/// <para>
/// <b>A malformed v1 envelope also fails closed, and is counted on its OWN axis.</b> A directional
/// <c>MediaAttention</c> signal whose envelope cannot be read, which claims an UNSUPPORTED materializer
/// version, or which claims a supported one while missing the provenance that version promises, is asserting a
/// direction whose grounding Radar cannot verify. Suppressing it follows the same rule as the legacy case —
/// a score must never use a direction it cannot trace — but it is a DIFFERENT fact (a broken writer, versus
/// a known-defective retired one), so the two are never pooled into one number.
/// </para>
/// <para>
/// Deterministic (AD-3): no clock, no config, no IO, no randomness, order preserved, and the result depends
/// only on the input list. <see cref="Version"/> IS a fingerprint input: spec 194 §2 folds it into
/// <c>SignalSourceDescriptor.CanonicalDescriptor()</c> through <see cref="NewsJudgmentScoringIdentity"/>, and
/// it is rendered in BOTH the judgment-enabled and judgment-disabled forms — because this rule runs
/// unconditionally in <c>ScoringEngine</c>, so changing which accrued directions are suppressed changes what a
/// judgment-DISABLED run scores too. Bumping it therefore re-stamps every strategy and trips
/// <c>StrategyIdentityGuard</c>, which is exactly the intended cost of changing a suppression rule.
/// </para>
/// </summary>
public static class LegacyNewsInheritanceNeutralization
{
    /// <summary>
    /// The versioned identity of THIS admission rule. Public because spec 194 §2 folds it into the scoring
    /// identity (via <see cref="NewsJudgmentScoringIdentity"/>), so that changing which accrued directions
    /// are suppressed can never hide inside an unchanged <c>ScoringConfigVersion</c>. Bump it when the MATCH
    /// or the substituted direction/strength changes — not when a comment does.
    /// </summary>
    public const string Version = "legacy-news-inheritance-v1";

    /// <summary>
    /// Applies the transform to the current-window signal+evidence pairs the engine scores. Returns the
    /// INPUT INSTANCE (and an empty map) when nothing matched, so the healthy path — which, once the accrued
    /// signals age out of every window, is every path — allocates nothing.
    /// </summary>
    public static LegacyNewsInheritanceResult<ScoringSignal> Apply(IReadOnlyList<ScoringSignal> signals) =>
        ApplyCore(signals, static s => s.Signal, static (s, neutralized) => s with { Signal = neutralized });

    /// <summary>
    /// Applies the transform to a plain signal list — the activity-only previous window used for velocity.
    /// A legacy inherited direction must not be allowed to misdirect the velocity comparison either; the
    /// previous window carries no contributions or evidence links by design (AD-6), so the suppression there
    /// is reported through this result rather than through a contribution reason.
    /// </summary>
    public static LegacyNewsInheritanceResult<Signal> Apply(IReadOnlyList<Signal> signals) =>
        ApplyCore(signals, static s => s, static (_, neutralized) => neutralized);

    /// <summary>
    /// The short, advice-free note appended to a neutralized signal's contribution reason, so the persisted
    /// <c>ScoreEvidenceLink</c> states that the score used a DIFFERENT direction from the one on the
    /// persisted record. A score that silently disagrees with its own provenance is not a score.
    /// </summary>
    public static string ProvenanceNoteFor(LegacyNewsInheritanceKind kind) => kind switch
    {
        LegacyNewsInheritanceKind.AccruedLegacyInheritance =>
            "scored Neutral: accrued spec-191 news direction was inherited from a judgment that never read "
                + "this article (" + Version + ")",
        LegacyNewsInheritanceKind.MalformedJudgmentSignalEnvelope =>
            "scored Neutral: judgment-derived news provenance could not be verified (" + Version + ")",
        _ => "scored Neutral: news direction could not be grounded (" + Version + ")",
    };

    private static LegacyNewsInheritanceResult<T> ApplyCore<T>(
        IReadOnlyList<T> items, Func<T, Signal> signalOf, Func<T, Signal, T> withSignal)
    {
        ArgumentNullException.ThrowIfNull(items);

        // ONE pass. The rewritten list and the count map are allocated lazily, on the FIRST match, by
        // copying the prefix already walked — so a set carrying no legacy news signal (the overwhelming
        // majority, and eventually all of them) costs a classification per signal and nothing else, and
        // hands the caller back the very instance it passed in.
        List<T>? rewritten = null;
        Dictionary<Guid, LegacyNewsInheritanceKind>? kinds = null;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var signal = signalOf(item);
            var kind = Classify(signal);

            if (kind is null)
            {
                rewritten?.Add(item);
                continue;
            }

            if (rewritten is null)
            {
                rewritten = new List<T>(items.Count);
                for (var j = 0; j < i; j++)
                {
                    rewritten.Add(items[j]);
                }

                kinds = [];
            }

            rewritten.Add(withSignal(item, Neutralize(signal)));

            // Keyed by the neutralized signal's OWN id: unlike a superseded signal it survives, so the
            // natural — and only truthful — attribution is to itself. A duplicate id within one input set
            // records one classification; the map counts distinct signals, never occurrences.
            kinds![signal.Id] = kind.Value;
        }

        return rewritten is null
            ? LegacyNewsInheritanceResult<T>.Untouched(items)
            : new LegacyNewsInheritanceResult<T>(rewritten, kinds!);
    }

    /// <summary>
    /// The exact substitution: the pre-191 Neutral media-attention direction and strength. Everything else —
    /// id, evidence id, company, novelty, confidence, excerpt, reason, review status, both instants and the
    /// metadata envelope itself — is carried through untouched, so the signal remains walkable back to the
    /// record on disk and the suppression is legible rather than a quiet erasure.
    /// </summary>
    private static Signal Neutralize(Signal signal) => signal with
    {
        Direction = SignalDirection.Neutral,

        // NewsTrajectorySignalRules.BaseStrength IS the Neutral MediaAttention strength the extractor has
        // always emitted (it is the floor spec 191's directional read built on, and the value §1.1 restored
        // the news branch to). Sourced from there rather than re-typed as a literal 4 so the two cannot
        // drift: if the ordinary news strength ever moves, the value this transform substitutes moves with
        // it. That class is internal to Radar.Application, which is this assembly.
        //
        // This const read does NOT violate the spec-177/179 Scoring→News guards: they are TYPE-GRAPH guards
        // (base types, interfaces, fields, signatures, generic arguments) and are structurally blind to a
        // const or method-body reference. See NewsJudgmentScoringIdentity's remarks, which state the same
        // boundary from the other side.
        Strength = NewsTrajectorySignalRules.BaseStrength,
    };

    /// <summary>
    /// Classifies one signal, or returns <c>null</c> when it is none of this transform's business.
    /// <para>
    /// SPEC 194 §1.3/§1.5: the envelope question is answered by the SHARED
    /// <see cref="NewsDirectionalSignalMetadata.ClassifyProvenance"/>, which the judgment-signal supersede and
    /// <c>media-collapse-v2</c> also consult. This method contributes only the gates that are specific to a
    /// SUPPRESSION: a signal that is not a news attention event, or that is already Neutral, has no direction
    /// to suppress — note that the Neutral test is a SHORT-CIRCUIT, not the match, since the match is the
    /// metadata shape. One predicate, three call sites: a signal must never be valid enough to win the
    /// supersede while being malformed enough to be neutralized here.
    /// </para>
    /// </summary>
    private static LegacyNewsInheritanceKind? Classify(Signal signal)
    {
        if (signal.Type != SignalType.MediaAttention || signal.Direction == SignalDirection.Neutral)
        {
            return null;
        }

        return NewsDirectionalSignalMetadata.ClassifyProvenance(signal.MetadataJson) switch
        {
            // A structurally valid spec-194 §1.2 judgment-DERIVED signal: untouched, because its direction WAS
            // grounded in the evidence the judgment actually cited — the whole point of the correction.
            NewsJudgmentSignalProvenance.JudgmentDerived => null,
            NewsJudgmentSignalProvenance.MalformedJudgmentEnvelope =>
                LegacyNewsInheritanceKind.MalformedJudgmentSignalEnvelope,
            NewsJudgmentSignalProvenance.LegacyInheritance =>
                LegacyNewsInheritanceKind.AccruedLegacyInheritance,
            // No envelope, or an envelope of an unrelated family: left completely alone, which is exactly what
            // "do not match on Direction alone" means in practice.
            _ => null,
        };
    }
}

/// <summary>
/// Why a signal's persisted direction was not admitted. The two members are never pooled: one is the known
/// residue of a retired producer (spec 191), the other is a live integrity failure in a current one, and
/// conflating them would hide the second inside the first's expected count.
/// <para>
/// There is deliberately no "none"/default member: the map on <see cref="LegacyNewsInheritanceResult{T}"/>
/// holds only explicitly classified signals, so a <c>default</c> value is unreachable rather than
/// meaningful.
/// </para>
/// </summary>
public enum LegacyNewsInheritanceKind
{
    /// <summary>
    /// An accrued spec-191 directional news signal: it carries that producer's judgment/cohort/observation
    /// provenance but no <c>news-judgment-signal-v1</c> token, so its direction came from a company-level
    /// judgment that had never read the matched article.
    /// </summary>
    AccruedLegacyInheritance = 0,

    /// <summary>
    /// A directional news signal whose judgment-derived envelope could not be verified: unreadable JSON, or
    /// a <c>news-judgment-signal-v1</c> claim without the provenance that version promises.
    /// </summary>
    MalformedJudgmentSignalEnvelope = 1,
}

/// <summary>
/// The result of a <see cref="LegacyNewsInheritanceNeutralization.Apply(IReadOnlyList{ScoringSignal})"/>:
/// the ADMITTED signals (identical to the input except that matched signals carry the Neutral pre-191
/// direction/strength) and, per neutralized <c>Signal.Id</c>, why.
/// <para>
/// The shape follows <see cref="GuidanceChangeSupersedeResult{T}"/> — survivors plus a map keyed by a
/// surviving signal's id — rather than inventing a third one, so the accounting steps that now sit in a row
/// inside <c>ScoringEngine.ScoreCompanyAsync</c> read the same way. It is GENERIC for the same reason the
/// supersede is: it runs over BOTH the current window's <c>ScoringSignal</c> pairs and the previous window's
/// plain <see cref="Signal"/> list.
/// </para>
/// </summary>
public sealed record LegacyNewsInheritanceResult<T>(
    IReadOnlyList<T> Signals,
    IReadOnlyDictionary<Guid, LegacyNewsInheritanceKind> NeutralizedKinds)
{
    /// <summary>
    /// The shared empty map, so the untouched fast path allocates no dictionary. Frozen rather than a bare
    /// <see cref="Dictionary{TKey,TValue}"/> precisely BECAUSE it is shared (the
    /// <see cref="GuidanceChangeSupersedeResult{T}"/> precedent): a consumer casting the interface back to
    /// the concrete type could otherwise mutate every past and future fast-path result at once.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Guid, LegacyNewsInheritanceKind> NoNeutralizations =
        FrozenDictionary<Guid, LegacyNewsInheritanceKind>.Empty;

    /// <summary>
    /// The fast path: nothing matched, so the INPUT INSTANCE is handed back unchanged alongside an empty
    /// map. Byte-identical to not running the transform at all.
    /// </summary>
    internal static LegacyNewsInheritanceResult<T> Untouched(IReadOnlyList<T> signals) =>
        new(signals, NoNeutralizations);

    /// <summary>How many accrued spec-191 inherited directions were suppressed.</summary>
    public int LegacyInheritanceCount => CountOf(LegacyNewsInheritanceKind.AccruedLegacyInheritance);

    /// <summary>How many unverifiable judgment-signal envelopes were suppressed — a separate axis, never pooled.</summary>
    public int MalformedEnvelopeCount => CountOf(LegacyNewsInheritanceKind.MalformedJudgmentSignalEnvelope);

    /// <summary>Every suppression, both axes — drives the "did anything happen at all" test.</summary>
    public int TotalNeutralized => NeutralizedKinds.Count;

    private int CountOf(LegacyNewsInheritanceKind kind)
    {
        var total = 0;
        foreach (var value in NeutralizedKinds.Values)
        {
            if (value == kind)
            {
                total++;
            }
        }

        return total;
    }
}
