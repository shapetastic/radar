using System.Collections.Frozen;

using Radar.Application.Acquisitions;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 217 §2 — the pure, read/assembly-time supersede of the keyword extractor's
/// <see cref="SignalType.StrategicPartnership"/> read of an item-1.01 8-K that <c>acqscan-v1</c> recognised
/// as a pending acquisition OF THE COMPANY.
///
/// <para>
/// <b>The gap this closes, stated once so the class is legible without the spec.</b> On 2026-08-10
/// MarineMax agreed to be bought for $53.00 a share in cash. Radar collected the 8-K (items 1.01/7.01/9.01)
/// and the extractor's "material definitive agreement" rule minted a POSITIVE
/// <c>StrategicPartnership</c> at strength 4; trajectory rose 56 → 62 and the report labelled the company
/// <b>Thesis improving</b>. A $1.5B all-cash sale of the whole company had been read as a partnership. The
/// keyword rule is NOT removed — it is a scoring input, and changing the rule table is
/// <c>RuleSetVersion</c> territory — so, exactly as spec 194 §1.3 does for the judgment-derived news
/// signal, the correction happens at ASSEMBLY: the signal over that ONE recognised evidence item is
/// REPLACED by a Neutral <see cref="SignalType.CorporateAction"/> signal at strength 0, carrying a reason
/// that names the acquisition.
/// </para>
/// <para>
/// <b>The rule.</b> For every signal whose <c>EvidenceId</c> is in the recognised set and whose type is
/// <see cref="SignalType.StrategicPartnership"/>, emit a replacement with the SAME id, evidence, company,
/// observed/created instants and confidence, but
/// <see cref="SignalType.CorporateAction"/>/<see cref="SignalDirection.Neutral"/>, Strength 0, Novelty 0
/// and a rewritten <c>Reason</c>. The id is preserved deliberately: the persisted
/// <c>ScoreEvidenceLink</c> still points at the signal on disk, so provenance walks
/// report → snapshot → signal → evidence unbroken, and the replacement is visible as a CHANGE rather than
/// as a disappearance. Nothing is removed and nothing is added, so the signal COUNT is unchanged — this is
/// a rewrite, not a collapse.
/// </para>
/// <para>
/// <b>It touches nothing else.</b> A signal of another type over the same evidence (the 8-K's own
/// <c>GuidanceChange</c>, an insider filing, an article) is untouched: the extractor's partnership read is
/// the one thing a recognition contradicts. An evidence id with no recognition is untouched by
/// construction — the recognised set is the whole rule.
/// </para>
/// <para>
/// Deterministic (AD-3): no clock, config, state, IO or randomness; survivors keep the input's relative
/// ordering, and the fast path returns the INPUT INSTANCE when nothing can apply (which is every company
/// that is not under a recognised acquisition — almost all of them).
/// </para>
/// </summary>
public static class CorporateActionSupersede
{
    /// <summary>
    /// The versioned identity of THIS supersede rule. Hashed into <c>ScoringConfigVersion</c> through
    /// <c>SignalSourceDescriptor</c>'s <c>acq=</c> field together with
    /// <see cref="AcquisitionAgreementScan.Version"/> (spec 217 §2), so changing WHICH signal replaces which
    /// can never hide inside an unchanged fingerprint. Bump it when the match or the replacement changes —
    /// not when a comment does.
    /// </summary>
    public const string Version = "acq-supersede-v1";

    /// <summary>Applies the supersede to the current-window signal+evidence pairs the engine scores.</summary>
    public static CorporateActionSupersedeResult<ScoringSignal> Apply(
        IReadOnlyList<ScoringSignal> signals,
        PendingAcquisitions acquisitions,
        Guid companyId) =>
        ApplyCore(
            signals,
            acquisitions,
            companyId,
            static s => s.Signal,
            static (s, replacement) => s with { Signal = replacement });

    /// <summary>
    /// Applies the supersede to a plain signal list — the activity-only previous window used for velocity.
    /// It must run there too: the previous window builds no contributions (AD-6), but a strength-4 positive
    /// partnership counting as prior activity would still move velocity for an event that never was one.
    /// </summary>
    public static CorporateActionSupersedeResult<Signal> Apply(
        IReadOnlyList<Signal> signals,
        PendingAcquisitions acquisitions,
        Guid companyId) =>
        ApplyCore(
            signals,
            acquisitions,
            companyId,
            static s => s,
            static (_, replacement) => replacement);

    private static CorporateActionSupersedeResult<T> ApplyCore<T>(
        IReadOnlyList<T> items,
        PendingAcquisitions acquisitions,
        Guid companyId,
        Func<T, Signal> signalOf,
        Func<T, Signal, T> withSignal)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(acquisitions);

        // The fast path: this company has no recognised acquisition, so no evidence of its can be in the
        // recognised set. Byte-identical to not running the transform at all.
        var record = acquisitions.For(companyId);
        if (record is null)
        {
            return CorporateActionSupersedeResult<T>.Untouched(items);
        }

        List<T>? result = null;
        Dictionary<Guid, string>? superseded = null;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var signal = signalOf(item);
            if (signal.Type != SignalType.StrategicPartnership
                || signal.EvidenceId != record.EvidenceId)
            {
                result?.Add(item);
                continue;
            }

            if (result is null)
            {
                result = new List<T>(items.Count);
                for (var j = 0; j < i; j++)
                {
                    result.Add(items[j]);
                }
            }

            var reason = DescribeSupersede(record);
            result.Add(withSignal(
                item,
                signal with
                {
                    Type = SignalType.CorporateAction,
                    Direction = SignalDirection.Neutral,
                    Strength = 0,
                    Novelty = 0,
                    Reason = reason,
                }));

            (superseded ??= [])[signal.Id] = reason;
        }

        return result is null
            ? CorporateActionSupersedeResult<T>.Untouched(items)
            : new CorporateActionSupersedeResult<T>(
                result,
                superseded ?? CorporateActionSupersedeResult<T>.NoCounts);
    }

    /// <summary>
    /// The ONE wording of the replacement reason — used verbatim as the signal's <c>Reason</c> and appended
    /// to the persisted <c>ScoreEvidenceLink</c>'s contribution reason, so the two can never disagree about
    /// what replaced what. Advice-free by construction: it states the filed fact and nothing else.
    /// </summary>
    public static string DescribeSupersede(PendingAcquisitionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return $"Superseded by {Version}: this filing is an agreement to acquire the company "
            + $"({record.AcquirerName} at {record.DescribeConsideration()}), recognised by "
            + $"{record.ScanVersion} — a corporate action, not a partnership.";
    }
}

/// <summary>
/// The result of a <see cref="CorporateActionSupersede"/> application: the (possibly rewritten) signals and,
/// per REWRITTEN <c>Signal.Id</c>, the reason that replaced it.
/// <para>
/// The shape mirrors <see cref="NewsJudgmentSupersedeResult{T}"/> and
/// <see cref="GuidanceChangeSupersedeResult{T}"/> rather than inventing a fourth one, so the assembly steps
/// that sit in a row inside <c>ScoringEngine.ScoreCompanyAsync</c> read the same way — but it carries the
/// REASON rather than a count, because this transform rewrites one signal in place instead of removing N.
/// </para>
/// </summary>
public sealed record CorporateActionSupersedeResult<T>(
    IReadOnlyList<T> Signals,
    IReadOnlyDictionary<Guid, string> SupersededReasons)
{
    /// <summary>The shared empty map, frozen for the reason <see cref="NewsJudgmentSupersedeResult{T}"/> freezes its own.</summary>
    internal static readonly IReadOnlyDictionary<Guid, string> NoCounts = FrozenDictionary<Guid, string>.Empty;

    /// <summary>The fast path: nothing could apply, so the INPUT INSTANCE is handed back unchanged.</summary>
    internal static CorporateActionSupersedeResult<T> Untouched(IReadOnlyList<T> signals) =>
        new(signals, NoCounts);

    /// <summary>How many signals this transform rewrote — the per-company count the scoring log reports.</summary>
    public int TotalSuperseded => SupersededReasons.Count;
}
