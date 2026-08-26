namespace Radar.Application.Collectors;

/// <summary>
/// THE closed issue-token vocabulary a collector may record against one company's collection coverage
/// (spec 169 / AD-16's 2026-08-03 amendment). Four names, nothing else: the set is closed on purpose, because
/// the attention-arrival evaluator reads these tokens as a coverage PROOF and a free-form string would let a
/// future collector invent a reason nothing downstream understands — which would then read as "complete".
/// <para>
/// Issue sets are always sorted <b>ordinally</b> so a checkpoint's recorded coverage is byte-stable across
/// runs (AD-3), and an <b>empty</b> set means "complete at that checkpoint". Absence of the whole coverage
/// record (a <c>null</c> <see cref="CollectorCompanyCoverage"/> list) is a different fact entirely: it means
/// UNPROVEN, never success.
/// </para>
/// </summary>
public static class CollectionCoverageIssues
{
    /// <summary>
    /// The run's collection-health reconciliation reported that this collector's feed inventory shrank
    /// between the seed and the collectors (spec 98). The collector itself cannot see that report — the
    /// collection pass stamps this token onto every one of that collector's company rows.
    /// </summary>
    public const string CollectionHealthMismatch = "CollectionHealthMismatch";

    /// <summary>The company has no configured feed of this collector's type, so nothing could be collected for it.</summary>
    public const string MissingFeed = "MissingFeed";

    /// <summary>
    /// At least one of the company's feeds returned a raw result count that REACHED the EFFECTIVE LOCAL
    /// retention limit Radar itself requested (never a proven provider-side ceiling). Equality means
    /// POSSIBLY truncated — Radar stopped retaining there, so the window is not provably complete even when
    /// a later client-side relevance filter kept fewer items. Deliberately unchanged and fail-closed by
    /// spec 190: the diagnostic fields on <see cref="CollectorCompanyCoverage"/> may CONFIRM truncation, but
    /// nothing may downgrade this token, because the absence of an observed tail still cannot prove the
    /// provider had no further results.
    /// </summary>
    public const string ResultLimitReached = "ResultLimitReached";

    /// <summary>At least one expected feed for the company could not be read, parsed or validated.</summary>
    public const string SourceFailure = "SourceFailure";

    /// <summary>The complete vocabulary, ordinally sorted — the same order a coverage row's issue set uses.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CollectionHealthMismatch,
        MissingFeed,
        ResultLimitReached,
        SourceFailure,
    ];

    /// <summary>
    /// Ordinally sorts and de-dupes an issue set. The ONE place the sort rule lives, so a row built by a
    /// collector and a row amended by the collection pass cannot end up ordered differently.
    /// </summary>
    public static IReadOnlyList<string> Canonicalize(IEnumerable<string> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return [.. issues.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal)];
    }
}

/// <summary>
/// One collector's recorded collection coverage for ONE company on ONE run (spec 169). It answers the only
/// question the AD-16 attention-arrival evaluator can accept as proof of a complete observation window:
/// "did this collector actually look at this company, did every configured source succeed, and could the
/// result have been truncated?"
/// <para>
/// <b>It must be computed by the collector, while it still knows the feed→company binding and the RAW
/// returned item count.</b> Reconstructing it afterwards from an aggregate <c>ItemsCollected</c> is invalid:
/// the merge discards per-collector attribution, the aggregate carries no company, and the kept count is a
/// post-filter number that cannot reveal a censored result set.
/// </para>
/// <para>
/// Purely observational (AD-14 discipline): it references no evidence, carries no label or score, and is not
/// an evidence/signal/score/fingerprint input.
/// </para>
/// <para>
/// <b>Spec 190 — three honest states, and none of them is a provider fact.</b> The trailing nullable
/// diagnostic fields distinguish <i>possible truncation</i> (<see cref="HitEffectiveResultLimit"/>: the
/// retained prefix filled Radar's own EFFECTIVE/LOCAL retention limit), <i>confirmed local truncation</i>
/// (<see cref="ConfirmedLocalTruncation"/>: a structurally valid item really was observed beyond that limit
/// and discarded by Radar) and <i>below limit</i>. Even a full scan that observes no item beyond the limit
/// cannot prove the provider had no further results, so the fail-closed
/// <see cref="CollectionCoverageIssues.ResultLimitReached"/> semantics are unchanged and AD-16 / news-risk
/// coverage never silently upgrade. Every diagnostic field is <c>null</c> on a row written before spec 190
/// (or by a collector that records none) — <b><c>null</c> means NOT RECORDED, never <c>false</c></b>.
/// </para>
/// </summary>
/// <param name="CompanyId">The company this row describes. One row per company in the collection context.</param>
/// <param name="ExpectedFeedCount">How many of this collector's feeds are configured for the company. Zero is a legitimate, recorded state (<see cref="CollectionCoverageIssues.MissingFeed"/>), not an omission.</param>
/// <param name="SuccessfulFeedCount">How many of those feeds parsed AND read successfully.</param>
/// <param name="HitEffectiveResultLimit">True when any successful feed's RAW reader result count reached the effective clamped LOCAL request limit — i.e. the result set may have been truncated. Not a claim about the provider.</param>
/// <param name="Issues">The ordinally sorted issue set; EMPTY means complete at this checkpoint.</param>
/// <param name="EffectiveResultLimit">The effective clamped LOCAL retention limit this collector requested per feed; <c>null</c> = not recorded.</param>
/// <param name="MaxValidItemsObserved">The MAXIMUM number of structurally valid response items observed across the company's successful feeds (bounded by the reader's absolute parse ceiling); <c>null</c> = not recorded.</param>
/// <param name="ConfirmedLocalTruncation">True when at least one successful feed's response held a valid item BEYOND <paramref name="EffectiveResultLimit"/>, so the discard is confirmed rather than suspected; <c>false</c> means no such item was observed (NOT that the provider had none); <c>null</c> = not recorded.</param>
/// <param name="UnadmittedRelevantTailItemCount">How many additional UNIQUE company-relevant items were observed in the diagnostic tail and deliberately NOT admitted by the current collection contract; <c>null</c> = not recorded. Purely observational — no such item became evidence, an observation candidate or a scoring input.</param>
public sealed record CollectorCompanyCoverage(
    Guid CompanyId,
    int ExpectedFeedCount,
    int SuccessfulFeedCount,
    bool HitEffectiveResultLimit,
    IReadOnlyList<string> Issues,
    int? EffectiveResultLimit = null,
    int? MaxValidItemsObserved = null,
    bool? ConfirmedLocalTruncation = null,
    int? UnadmittedRelevantTailItemCount = null);
