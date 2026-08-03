using Radar.Application.Collectors;
using Radar.Application.Pipeline;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// Why a run record could not serve as a COMPLETE coverage checkpoint for one company. Reported alongside the
/// chain-level reason so an operator can act on the real cause rather than on "incomplete".
/// </summary>
public enum AttentionCheckpointDisqualification
{
    /// <summary>None; the record IS a complete checkpoint for this company.</summary>
    None = 0,

    /// <summary>A company-FILTERED collect pass (spec 161). It observed part of the universe, so it can never prove primary-screen coverage.</summary>
    PartialCollectionRun,

    /// <summary>The record predates the spec-169 coverage contract, so its coverage is UNPROVEN — never assumed clean.</summary>
    LegacyCheckpointWithoutCollectorRuns,

    /// <summary>A standalone score pass: nothing was collected, so there is nothing to prove coverage with.</summary>
    ScoreOnlyRunWithoutCollection,

    /// <summary>The run recorded collector rows but the attention collector is not among them — it did not run.</summary>
    AttentionCollectorDidNotRun,

    /// <summary>The attention collector ran but recorded no per-company coverage at all.</summary>
    CompanyCoverageNotRecorded,

    /// <summary>The attention collector ran but recorded no row for this company: it was not in that pass's universe.</summary>
    CompanyNotInCollectionPass,

    /// <summary>The company has no configured feed of the attention collector's type, so nothing could be collected for it.</summary>
    CompanyFeedMissing,

    /// <summary>At least one of the company's expected feeds failed.</summary>
    CompanyFeedFailed,

    /// <summary>A successful feed's RAW result count reached the effective request limit: potentially truncated, so not provably complete.</summary>
    CompanyFeedCapped,

    /// <summary>The run's collection-health reconciliation reported the attention collector's feed inventory as incomplete.</summary>
    CollectionHealthMismatch,
}

/// <summary>The chain-level verdict for an interval.</summary>
public enum AttentionCoverageReason
{
    /// <summary>The interval is covered end to end by complete checkpoints.</summary>
    Complete = 0,

    /// <summary>No run record at all fell in the relevant span.</summary>
    NoRunRecords,

    /// <summary>No complete checkpoint at or before the interval start, within the maximum gap.</summary>
    NoCheckpointBeforeStart,

    /// <summary>No complete checkpoint at or after the interval end, within the maximum gap.</summary>
    NoCheckpointAfterEnd,

    /// <summary>Two consecutive complete checkpoints spanning the interval are further apart than the maximum gap.</summary>
    CheckpointGapExceeded,
}

/// <summary>
/// The verdict for one company over one interval: complete, or the chain-level reason plus — when one is
/// identifiable — the specific reason the nearest candidate record was rejected.
/// </summary>
public sealed record AttentionCoverageResult(
    bool IsComplete,
    AttentionCoverageReason Reason,
    AttentionCheckpointDisqualification Disqualification)
{
    public static AttentionCoverageResult Complete { get; } =
        new(IsComplete: true, AttentionCoverageReason.Complete, AttentionCheckpointDisqualification.None);
}

/// <summary>
/// Decides whether Radar can PROVE it observed a company's third-party attention across an exact interval
/// <c>(a, b]</c> (spec 169, implementing AD-16 §5 as corrected by its 2026-08-03 amendment).
/// <para>
/// <b>Why proof rather than a recency heuristic.</b> "There was a run yesterday" is not evidence that the news
/// collector succeeded for THIS company, and it certainly is not evidence that a successful query was not
/// truncated at its result limit. Without this, a failed or capped collection window would silently produce a
/// publisher count of zero — indistinguishable from the genuine, central negative case AD-16 §5 requires the
/// sample to keep. That single confusion would invert the meaning of the whole screen.
/// </para>
/// <para>
/// <b>The rule.</b> A chain of COMPLETE checkpoints: one at or before <c>a</c> no more than 36 hours earlier,
/// one at or after <c>b</c> no more than 36 hours later, and no gap greater than 36 hours between consecutive
/// complete checkpoints spanning the interval. 36 hours accommodates ordinary drift in a once-daily job
/// without treating a missed day as covered. It is a collection-CADENCE rule, not a shortened outcome:
/// evidence is still counted through the exact <c>b</c> endpoint, and there is no price-style exit tolerance
/// (spec 152's four-day tolerance exists because markets close; an attention window ending early is simply
/// missing possible events).
/// </para>
/// <para>Pure and deterministic (AD-3): no clock, no I/O, no randomness.</para>
/// </summary>
public sealed class AttentionCoverageEvaluator
{
    private readonly AttentionArrivalOptions _options;

    public AttentionCoverageEvaluator(AttentionArrivalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Evaluates coverage for <paramref name="companyId"/> over <c>(<paramref name="exclusiveStartUtc"/>,
    /// <paramref name="inclusiveEndUtc"/>]</c> against <paramref name="orderedRuns"/>, which must already be
    /// ordered by <c>CreatedAtUtc</c> ascending (the <see cref="IPipelineRunStore.ReadBetweenAsync"/>
    /// contract).
    /// </summary>
    public AttentionCoverageResult Evaluate(
        Guid companyId,
        DateTimeOffset exclusiveStartUtc,
        DateTimeOffset inclusiveEndUtc,
        IReadOnlyList<PipelineRunRecord> orderedRuns)
    {
        ArgumentNullException.ThrowIfNull(orderedRuns);

        var gap = AttentionArrivalScreen.MaximumCheckpointGap;
        var spanStart = exclusiveStartUtc - gap;
        var spanEnd = inclusiveEndUtc + gap;

        // Only records inside the relevant span can contribute; anything outside it is irrelevant either way.
        var candidates = orderedRuns
            .Where(r => r.CreatedAtUtc >= spanStart && r.CreatedAtUtc <= spanEnd)
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .Select(r => (Run: r, Disqualification: Classify(companyId, r)))
            .ToList();

        if (candidates.Count == 0)
        {
            return new AttentionCoverageResult(
                IsComplete: false,
                AttentionCoverageReason.NoRunRecords,
                AttentionCheckpointDisqualification.None);
        }

        var complete = candidates
            .Where(c => c.Disqualification == AttentionCheckpointDisqualification.None)
            .Select(c => c.Run)
            .ToList();

        // Opening checkpoint: the LATEST complete record at or before a, no more than `gap` earlier.
        var before = complete
            .Where(r => r.CreatedAtUtc <= exclusiveStartUtc && r.CreatedAtUtc >= exclusiveStartUtc - gap)
            .LastOrDefault();
        if (before is null)
        {
            return new AttentionCoverageResult(
                IsComplete: false,
                AttentionCoverageReason.NoCheckpointBeforeStart,
                // The nearest rejected candidate to the missing opening checkpoint: the LATEST one in the
                // window it should have occupied. Deterministic, and it names the actual cause.
                NearestDisqualification(candidates, exclusiveStartUtc - gap, exclusiveStartUtc, preferLatest: true));
        }

        // Closing checkpoint: the EARLIEST complete record at or after b, no more than `gap` later.
        var after = complete
            .Where(r => r.CreatedAtUtc >= inclusiveEndUtc && r.CreatedAtUtc <= inclusiveEndUtc + gap)
            .FirstOrDefault();
        if (after is null)
        {
            return new AttentionCoverageResult(
                IsComplete: false,
                AttentionCoverageReason.NoCheckpointAfterEnd,
                NearestDisqualification(candidates, inclusiveEndUtc, inclusiveEndUtc + gap, preferLatest: false));
        }

        // No gap greater than `gap` between consecutive complete checkpoints spanning the interval.
        var spanning = complete
            .Where(r => r.CreatedAtUtc >= before.CreatedAtUtc && r.CreatedAtUtc <= after.CreatedAtUtc)
            .ToList();
        for (var i = 1; i < spanning.Count; i++)
        {
            var previous = spanning[i - 1].CreatedAtUtc;
            var current = spanning[i].CreatedAtUtc;
            if (current - previous > gap)
            {
                return new AttentionCoverageResult(
                    IsComplete: false,
                    AttentionCoverageReason.CheckpointGapExceeded,
                    // Whatever was rejected INSIDE the gap, if anything — that is the run that would have
                    // closed it. Earliest, so the answer is the first thing that went wrong.
                    NearestDisqualification(candidates, previous, current, preferLatest: false));
            }
        }

        return AttentionCoverageResult.Complete;
    }

    /// <summary>
    /// Whether one run record is a COMPLETE checkpoint for one company, and if not, why. Every branch fails
    /// CLOSED: an unknown or unrecorded state is never read as coverage.
    /// </summary>
    private AttentionCheckpointDisqualification Classify(Guid companyId, PipelineRunRecord run)
    {
        // (1) The run must be unfiltered. A spec-161 filtered collect pass looked at part of the universe;
        // even for a company it DID cover, treating it as a universe checkpoint would let a partial pass
        // certify a screen that AD-16 pairs across "exactly the same eligible companies".
        if (run.CompanyFilter is not null)
        {
            return AttentionCheckpointDisqualification.PartialCollectionRun;
        }

        if (run.CollectorRuns is null)
        {
            // Null means UNPROVEN, never success (AD-16's 2026-08-03 amendment). Two honest sub-cases: a
            // score-only pass that genuinely collected nothing, and a record written before the contract
            // existed. Neither can prove coverage; both deserve their own name so an operator can tell
            // "we never collected then" from "we collected but did not record it".
            return run.Collectors.Count == 0
                ? AttentionCheckpointDisqualification.ScoreOnlyRunWithoutCollection
                : AttentionCheckpointDisqualification.LegacyCheckpointWithoutCollectorRuns;
        }

        // (2) The attention collector must actually have run.
        var collectorRun = run.CollectorRuns.FirstOrDefault(
            c => string.Equals(c.CollectorName, _options.AttentionCollector, StringComparison.Ordinal));
        if (collectorRun is null)
        {
            return AttentionCheckpointDisqualification.AttentionCollectorDidNotRun;
        }

        if (collectorRun.CompanyCoverage is null)
        {
            return AttentionCheckpointDisqualification.CompanyCoverageNotRecorded;
        }

        var coverage = collectorRun.CompanyCoverage.FirstOrDefault(c => c.CompanyId == companyId);
        if (coverage is null)
        {
            return AttentionCheckpointDisqualification.CompanyNotInCollectionPass;
        }

        // (3)–(5) in AD-16's order, checked against BOTH the issue tokens and the counts. The tokens are
        // authoritative, but a defensive count check means a future collector that forgets a token still
        // fails closed rather than certifying a window it did not observe.
        if (coverage.ExpectedFeedCount == 0
            || coverage.Issues.Contains(CollectionCoverageIssues.MissingFeed, StringComparer.Ordinal))
        {
            return AttentionCheckpointDisqualification.CompanyFeedMissing;
        }

        if (coverage.SuccessfulFeedCount < coverage.ExpectedFeedCount
            || coverage.Issues.Contains(CollectionCoverageIssues.SourceFailure, StringComparer.Ordinal))
        {
            return AttentionCheckpointDisqualification.CompanyFeedFailed;
        }

        if (coverage.HitEffectiveResultLimit
            || coverage.Issues.Contains(CollectionCoverageIssues.ResultLimitReached, StringComparer.Ordinal))
        {
            return AttentionCheckpointDisqualification.CompanyFeedCapped;
        }

        if (coverage.Issues.Contains(CollectionCoverageIssues.CollectionHealthMismatch, StringComparer.Ordinal))
        {
            return AttentionCheckpointDisqualification.CollectionHealthMismatch;
        }

        return AttentionCheckpointDisqualification.None;
    }

    /// <summary>
    /// The disqualification of the nearest REJECTED candidate inside <c>[from, to]</c> — the run that would
    /// have satisfied the failing chain requirement had it been complete. Returns
    /// <see cref="AttentionCheckpointDisqualification.None"/> when nothing ran there at all, which is itself
    /// the answer: the chain reason alone then says "no run", not "a bad run".
    /// </summary>
    private static AttentionCheckpointDisqualification NearestDisqualification(
        IReadOnlyList<(PipelineRunRecord Run, AttentionCheckpointDisqualification Disqualification)> candidates,
        DateTimeOffset from,
        DateTimeOffset to,
        bool preferLatest)
    {
        var inRange = candidates
            .Where(c => c.Disqualification != AttentionCheckpointDisqualification.None)
            .Where(c => c.Run.CreatedAtUtc >= from && c.Run.CreatedAtUtc <= to)
            .ToList();

        if (inRange.Count == 0)
        {
            return AttentionCheckpointDisqualification.None;
        }

        return preferLatest ? inRange[^1].Disqualification : inRange[0].Disqualification;
    }
}
