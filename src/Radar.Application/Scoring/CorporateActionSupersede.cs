using System.Collections.Frozen;

using Radar.Application.Acquisitions;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// SPEC 217 §2, widened by SPEC 226 — the pure, read/assembly-time supersede of the keyword extractor's read
/// of an item-1.01 8-K that <c>acqscan-v1</c> recognised as a pending acquisition OF THE COMPANY.
///
/// <para>
/// <b>The gap this closes, stated once so the class is legible without the spec.</b> On 2026-08-10
/// MarineMax agreed to be bought for $53.00 a share in cash. Radar collected the 8-K (items 1.01/7.01/9.01)
/// and the extractor's "material definitive agreement" rule minted a POSITIVE
/// <c>StrategicPartnership</c> at strength 4; trajectory rose 56 → 62 and the report labelled the company
/// <b>Thesis improving</b>. A $1.5B all-cash sale of the whole company had been read as a partnership.
/// Exactly as spec 194 §1.3 does for the judgment-derived news signal, the correction happens at ASSEMBLY:
/// the extractor's signal over that ONE recognised evidence item is REPLACED by a Neutral
/// <see cref="SignalType.CorporateAction"/> signal at strength 0, carrying a reason that names the
/// acquisition.
/// </para>
/// <para>
/// ⚠ <b>Amended in place by spec 226.</b> Under <c>acq-supersede-v1</c> this doc said "The keyword rule is NOT
/// removed". Spec 226 (<c>radar-keyword-rules-v9</c>) DID change that rule: every Item 1.01 / 2.01 heading now
/// mints a Neutral <see cref="SignalType.CorporateAction"/> at strength 4 for every company, because a heading
/// is an event type and never a direction. The supersede is still needed — it is what names the ACQUISITION
/// on the recognised filing and takes its activity to zero — but its MATCH changed, hence
/// <c>acq-supersede-v2</c>: accrued pre-226 signals on disk stay <see cref="SignalType.StrategicPartnership"/>
/// (AD-8, never backfilled) while newly extracted ones are already <see cref="SignalType.CorporateAction"/>, so
/// the rule accepts EITHER.
/// </para>
/// <para>
/// <b>The rule (v2).</b> Over the ONE recognised <c>EvidenceId</c>, every signal whose type is
/// <see cref="SignalType.StrategicPartnership"/> (an accrued v8 heading read, or any partnership-phrase read) or <see cref="SignalType.CorporateAction"/>
/// (a v9 read) is REWRITABLE. ONE rewritable signal (the survivor, chosen as below) is rewritten with the SAME id,
/// evidence, company, observed/created instants and confidence, but
/// <see cref="SignalType.CorporateAction"/>/<see cref="SignalDirection.Neutral"/>, Strength 0, Novelty 0 and a
/// rewritten <c>Reason</c>. The id is preserved deliberately: the persisted <c>ScoreEvidenceLink</c> still
/// points at the signal on disk, so provenance walks report → snapshot → signal → evidence unbroken, and the
/// replacement is visible as a CHANGE rather than as a disappearance.
/// </para>
/// <para>
/// <b>Exactly ONE CorporateAction per recognised filing, whichever rule-set minted it.</b> Evidence is extracted
/// exactly once, when it is first durably written (<c>CollectionPass</c> extracts only
/// <c>DurableWriteOutcome.Written</c> evidence, and evidence identity is content-derived, spec 145), and
/// first-match-per-type emits at most one signal of each TYPE — so a v8 and a v9 read of the same evidence do
/// not coexist on the normal path. But a SECOND rewritable signal over one filing IS a normal-path outcome under
/// v9: the item-heading rules mint <see cref="SignalType.CorporateAction"/> while the "partnership" /
/// "partners with" / "teams up" rules mint <see cref="SignalType.StrategicPartnership"/>, a different type, so
/// ONE extraction of text carrying both a partnership phrase and a heading yields a Positive partnership AND a
/// Neutral corporate action over the same evidence (a lost-and-recollected raw file is a further, abnormal
/// source). Measured on the live store 2026-09-14: 0 such evidence items in the scoring window and 0 all time
/// among resolvable accrued evidence (spec 226 §4 harness). Rather than score two reads of one takeover filing,
/// every further rewritable signal is REMOVED and COUNTED
/// (<see cref="CorporateActionSupersedeResult{T}.DuplicatesCollapsed"/>), and the survivor's reason names how
/// many it absorbed. The survivor is chosen independently of input order — the earliest
/// <c>ObservedAtUtc</c>, then the lowest signal id — and keeps its own position. Nothing else is ever removed.
/// </para>
/// <para>
/// <b>It touches nothing else.</b> A signal of another type over the same evidence (the 8-K's own
/// <c>GuidanceChange</c>, an insider filing, an article) is untouched. An evidence id with no recognition is
/// untouched by construction — the recognised set is the whole rule — so an ordinary v9 item-heading
/// <c>CorporateAction</c> keeps its strength 4 everywhere but the recognised filing.
/// </para>
/// <para>
/// <b>A recognition that rewrote nothing is COUNTED, never silent</b>
/// (<see cref="CorporateActionSupersedeResult{T}.Outcome"/>): the recognised filing may simply have no signal
/// in this window (it aged out, or the strategy's type filter excluded it) or — the case worth reading — it
/// may have signals in the window, none of them rewritable.
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
    /// not when a comment does. v2 (spec 226): the match widened from StrategicPartnership alone to
    /// StrategicPartnership OR CorporateAction, with the one-per-filing duplicate guard.
    /// </summary>
    public const string Version = "acq-supersede-v2";

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
    /// It must run there too: the previous window builds no contributions (AD-6), but a strength-4 signal over
    /// a recognised takeover counting as prior activity would still move velocity for an event the rewrite
    /// takes to zero in the current window.
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

    /// <summary>True for the signal types the v2 rule may rewrite over the recognised filing.</summary>
    public static bool IsRewritable(SignalType type) =>
        type is SignalType.StrategicPartnership or SignalType.CorporateAction;

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
            return CorporateActionSupersedeResult<T>.Untouched(items, CorporateActionSupersedeOutcome.NoRecognition);
        }

        // Pass 1: which positions carry the recognised filing, and which of those are rewritable.
        var filingSignals = 0;
        List<int>? rewritable = null;
        for (var i = 0; i < items.Count; i++)
        {
            var signal = signalOf(items[i]);
            if (signal.EvidenceId != record.EvidenceId)
            {
                continue;
            }

            filingSignals++;
            if (IsRewritable(signal.Type))
            {
                (rewritable ??= []).Add(i);
            }
        }

        if (rewritable is null)
        {
            return CorporateActionSupersedeResult<T>.Untouched(
                items,
                filingSignals == 0
                    ? CorporateActionSupersedeOutcome.RecognisedFilingHasNoSignalInWindow
                    : CorporateActionSupersedeOutcome.RecognisedFilingHasNoRewritableSignal);
        }

        // Pass 2: rewrite the survivor (earliest ObservedAtUtc, then lowest Id), remove (and count) every other rewritable signal.
        var fromPartnership = 0;
        var fromCorporateAction = 0;
        foreach (var index in rewritable)
        {
            if (signalOf(items[index]).Type == SignalType.StrategicPartnership)
            {
                fromPartnership++;
            }
            else
            {
                fromCorporateAction++;
            }
        }

        var duplicates = rewritable.Count - 1;

        // The survivor is a total order over the rewritable signals — earliest ObservedAtUtc, then lowest Id —
        // never "first in input order", so the same set yields the same survivor however a caller ordered it.
        var survivorIndex = rewritable[0];
        foreach (var index in rewritable)
        {
            var candidate = signalOf(items[index]);
            var incumbent = signalOf(items[survivorIndex]);
            var byObserved = candidate.ObservedAtUtc.CompareTo(incumbent.ObservedAtUtc);
            if (byObserved < 0 || (byObserved == 0 && candidate.Id.CompareTo(incumbent.Id) < 0))
            {
                survivorIndex = index;
            }
        }

        var survivor = signalOf(items[survivorIndex]);
        var reason = DescribeSupersede(record, duplicates);

        var result = new List<T>(items.Count - duplicates);
        for (var i = 0; i < items.Count; i++)
        {
            if (i == survivorIndex)
            {
                result.Add(withSignal(
                    items[i],
                    survivor with
                    {
                        Type = SignalType.CorporateAction,
                        Direction = SignalDirection.Neutral,
                        Strength = 0,
                        Novelty = 0,
                        Reason = reason,
                    }));
                continue;
            }

            if (duplicates > 0 && rewritable.Contains(i))
            {
                continue;
            }

            result.Add(items[i]);
        }

        return new CorporateActionSupersedeResult<T>(
            result,
            new Dictionary<Guid, string> { [survivor.Id] = reason })
        {
            Outcome = CorporateActionSupersedeOutcome.Rewrote,
            SupersededFromStrategicPartnership = fromPartnership,
            SupersededFromCorporateAction = fromCorporateAction,
            DuplicatesCollapsed = duplicates,
        };
    }

    /// <summary>
    /// The ONE wording of the replacement reason — used verbatim as the signal's <c>Reason</c> and appended
    /// to the persisted <c>ScoreEvidenceLink</c>'s contribution reason, so the two can never disagree about
    /// what replaced what. Advice-free by construction: it states the filed fact and nothing else.
    /// </summary>
    public static string DescribeSupersede(PendingAcquisitionRecord record) => DescribeSupersede(record, 0);

    /// <summary>
    /// The replacement reason when <paramref name="duplicatesCollapsed"/> further rewritable signals over the
    /// same filing were absorbed into the survivor (the duplicate guard); identical to
    /// <see cref="DescribeSupersede(PendingAcquisitionRecord)"/> when none were.
    /// </summary>
    public static string DescribeSupersede(PendingAcquisitionRecord record, int duplicatesCollapsed)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicatesCollapsed);

        var reason = $"Superseded by {Version}: this filing is an agreement to acquire the company "
            + $"({record.AcquirerName} at {record.DescribeConsideration()}), recognised by "
            + $"{record.ScanVersion} — a corporate action, not a partnership or a direction.";
        return duplicatesCollapsed == 0
            ? reason
            : reason + $" {duplicatesCollapsed} further keyword read(s) of this filing were collapsed into this one.";
    }
}

/// <summary>What a <see cref="CorporateActionSupersede"/> application did for one company in one window.</summary>
public enum CorporateActionSupersedeOutcome
{
    /// <summary>The company has no recognised pending acquisition — the fast path; nothing to count.</summary>
    NoRecognition = 0,

    /// <summary>The recognised filing's keyword read was rewritten (and any duplicates collapsed into it).</summary>
    Rewrote,

    /// <summary>
    /// The company HAS a recognition, but no signal in this window cites the recognised filing (it aged out of
    /// the window, predates it, or the strategy's type filter excluded it). Counted, never silent.
    /// </summary>
    RecognisedFilingHasNoSignalInWindow,

    /// <summary>
    /// The company HAS a recognition and the recognised filing HAS signals in this window, but none is a
    /// <see cref="SignalType.StrategicPartnership"/> or <see cref="SignalType.CorporateAction"/>. The case worth
    /// reading: the filing reached scoring with no keyword item-heading read to replace.
    /// </summary>
    RecognisedFilingHasNoRewritableSignal,
}

/// <summary>
/// The result of a <see cref="CorporateActionSupersede"/> application: the (possibly rewritten) signals and,
/// per REWRITTEN <c>Signal.Id</c>, the reason that replaced it.
/// <para>
/// The shape mirrors <see cref="NewsJudgmentSupersedeResult{T}"/> and
/// <see cref="GuidanceChangeSupersedeResult{T}"/> rather than inventing a fourth one, so the assembly steps
/// that sit in a row inside <c>ScoringEngine.ScoreCompanyAsync</c> read the same way — but it carries the
/// REASON rather than a count, because this transform rewrites one signal in place instead of removing N.
/// Spec 226 adds the per-type split, the duplicate count and the <see cref="Outcome"/>.
/// </para>
/// </summary>
public sealed record CorporateActionSupersedeResult<T>(
    IReadOnlyList<T> Signals,
    IReadOnlyDictionary<Guid, string> SupersededReasons)
{
    /// <summary>The shared empty map, frozen for the reason <see cref="NewsJudgmentSupersedeResult{T}"/> freezes its own.</summary>
    internal static readonly IReadOnlyDictionary<Guid, string> NoCounts = FrozenDictionary<Guid, string>.Empty;

    /// <summary>Nothing was rewritten, so the INPUT INSTANCE is handed back unchanged with the reason why.</summary>
    internal static CorporateActionSupersedeResult<T> Untouched(
        IReadOnlyList<T> signals, CorporateActionSupersedeOutcome outcome) =>
        new(signals, NoCounts) { Outcome = outcome };

    /// <summary>How many signals this transform rewrote (0 or 1 — one per recognised filing).</summary>
    public int TotalSuperseded => SupersededReasons.Count;

    /// <summary>What happened; <see cref="CorporateActionSupersedeOutcome.NoRecognition"/> on the fast path.</summary>
    public CorporateActionSupersedeOutcome Outcome { get; init; }

    /// <summary>Rewritable signals over the recognised filing stored as <c>StrategicPartnership</c> (accrued pre-226 reads, or a v9 partnership-phrase read beside a heading).</summary>
    public int SupersededFromStrategicPartnership { get; init; }

    /// <summary>Rewritable signals over the recognised filing that were v9 <c>CorporateAction</c> reads.</summary>
    public int SupersededFromCorporateAction { get; init; }

    /// <summary>Further rewritable signals over the same filing REMOVED so exactly one CorporateAction survives.</summary>
    public int DuplicatesCollapsed { get; init; }

    /// <summary>True when the company has a recognition and this window rewrote nothing (either "nothing" outcome).</summary>
    public bool RecognisedButNothingRewritten =>
        Outcome is CorporateActionSupersedeOutcome.RecognisedFilingHasNoSignalInWindow
            or CorporateActionSupersedeOutcome.RecognisedFilingHasNoRewritableSignal;
}
