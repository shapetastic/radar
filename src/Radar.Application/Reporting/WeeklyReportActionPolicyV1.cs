namespace Radar.Application.Reporting;

using System.Globalization;
using Radar.Application.Acquisitions;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Reports;
using Radar.Domain.Signals;

/// <summary>
/// Deterministic first implementation of <see cref="IReportActionPolicy"/>. Maps a company's current
/// score snapshot (and its immediately-prior snapshot, for improving/deteriorating) onto one of the six
/// AD-9 ALLOWED weekly-report labels plus a plain-English, advice-free rationale. Pure: no clock, no
/// randomness, no I/O. May return <see cref="RadarReportAction.Ignore"/> for adequate-evidence,
/// low-opportunity companies, and never emits financial-advice language.
/// <para>
/// <b>Corroboration floor (v2).</b> A company that would otherwise fall to
/// <see cref="RadarReportAction.Ignore"/> is floored to <see cref="RadarReportAction.Watch"/> when it is
/// under-followed (<see cref="FollowingTier.Small"/>/<see cref="FollowingTier.Mid"/>), its trajectory is
/// not below neutral, and at least <see cref="MinCorroboratingSignalTypes"/> DISTINCT
/// <see cref="SignalType"/>s among its contributing signals point
/// <see cref="SignalDirection.Positive"/>. Several independent axes agreeing is exactly the pattern
/// Radar exists to surface, and a mixed quarter can pull opportunity below the Watch line while that
/// corroboration is intact. The floor is deliberately <b>tier-gated</b>: it never fires for
/// <see cref="FollowingTier.Large"/>/<see cref="FollowingTier.Mega"/> names, so the spec-117 notedness
/// posture (already-noticed mega-caps stay low) is preserved. It is a <b>floor only</b> — it can lift
/// <c>Ignore → Watch</c> and nothing else: it never reaches <see cref="RadarReportAction.Investigate"/>
/// and never overrides thin evidence or an improving/deteriorating thesis.
/// </para>
/// <para>
/// <b>The floor names what it counted (v3, spec 210).</b> One real-world announcement can wear two
/// extractors' clothes — a keyword-typed signal from a filing plus a judgment-derived
/// <see cref="SignalType.MediaAttention"/> from the same event's coverage — and satisfy the count without
/// independent corroboration. v3 does NOT change the count, the threshold or any label outcome; it changes
/// the rationale CONTRACT so a reader can see the shape: for every counted type, every distinct
/// (source class, observed date, judgment-derived) support tuple is rendered in a deterministic order, or,
/// past a small cap, the type's distinct-date count and date range. Nothing picks a "first" or "latest"
/// tuple silently — arbitrarily choosing one could manufacture or hide an echo. Missing provenance renders
/// as unknown, never as false. Still pure: no clock, no I/O.
/// </para>
/// <para>
/// <b>The floor prints the report's labels (v4, spec 211).</b> v3 inserted the STORED
/// <see cref="SignalType"/> name into the rationale, bypassing the report's presentation relabels
/// (<c>GuidanceChange</c> reappeared in 15 of 19 live floored lines; <c>InsiderBuying</c> — the forbidden
/// substring — in LBRT's). v4 names each counted type through the shared <see cref="SignalTypeDisplay"/>
/// seam, so the rationale prints <c>EarningsTrajectory</c> / <c>InsiderActivity</c>. Count, threshold,
/// grouping, ordering and every label outcome are unchanged and still run on the stored enum.
/// </para>
/// <para>
/// <b>The lines are inputs (v5, spec 212).</b> The Investigate / Watch Opportunity lines used to be two
/// private constants (60 / 40) tuned for <c>radar-formula-v8</c>'s multi-channel composite; since spec 184
/// the labels follow the LEAD arm, whose scale is not comparable (the v11 Lead never reached 40 across
/// 3,471 accrued snapshots, so every score-era Watch since 2026-08-23 came from the floor below). The policy
/// now reads the labelled arm's <see cref="LabelThresholds"/> from <see cref="ReportActionContext.Thresholds"/>
/// (<c>null</c> ⇒ <see cref="LabelThresholds.Default"/>, byte-identical to v4). Rule order is unchanged; the
/// rationale interpolates the line it actually applied; the corroboration floor floors against the arm's
/// Watch line and never above it. Trajectory / evidence-confidence / thesis-delta constants stay: those
/// components are computed by the shared <c>ScoreSignalMath</c> on the same scale for every formula — only
/// Opportunity changed scale.
/// </para>
/// <para>
/// <b>A pending acquisition closes the thesis (v6, spec 217).</b> A new RULE 0 runs ahead of every other
/// rule: when <see cref="ReportActionContext.PendingAcquisition"/> is present — a deterministically
/// recognised, verbatim-verified agreement to acquire THIS company — the label is
/// <see cref="RadarReportAction.Ignore"/> and the rationale names the acquirer, the stated consideration
/// and the announcement date. No new label is introduced (AD-9: the six are unchanged); the STATE lives in
/// the rationale. Because rule 0 is first, <see cref="RadarReportAction.ThesisImproving"/> and
/// <see cref="RadarReportAction.ThesisDeteriorating"/> cannot fire for a company being bought, which is the
/// structural fix for the 2026-08-10 shape (an all-cash sale of the whole company read as a partnership and
/// reported as <c>Thesis improving</c>). Every company without a pending acquisition is byte-identical to
/// v5. Still pure: no clock, no I/O.
/// </para>
/// </summary>
public sealed class WeeklyReportActionPolicyV1 : IReportActionPolicy
{
    // Evidence-confidence floor: below this, the thesis is "needs more evidence" regardless of score.
    // This is the dividing line between the two low-signal labels: below the floor means the evidence
    // is insufficient to judge (NeedsMoreEvidence); at/above the floor with a sub-Watch opportunity
    // means the evidence is adequate but the opportunity is simply low (Ignore).
    private const int EvidenceConfidenceFloor = 35;
    // Trajectory midpoint (50 = neutral, matches radar-formula-v1).
    private const int NeutralTrajectory = 50;
    // Minimum trajectory change vs the previous snapshot to call a thesis improving/deteriorating.
    private const int ThesisDelta = 5;
    // The Investigate / Watch Opportunity lines are NOT constants here (spec 212): they are the labelled
    // arm's LabelThresholds, read from the context in Decide. LabelThresholds.Default is the single owner
    // of the pre-212 60 / 40.
    // Minimum number of DISTINCT positive-direction signal types among the contributing signals for a
    // sub-Watch, under-followed company to be floored to Watch instead of Ignore. Distinct TYPES (not
    // rows): two independent axes agreeing (e.g. CustomerWin + StrategicPartnership), not the same
    // phrase matched twice. Tunable — a follow-up may raise it after a live re-measure.
    private const int MinCorroboratingSignalTypes = 2;
    // Spec 210: above this many DISTINCT support tuples for one counted type, the rationale renders the
    // type's distinct-date count + date range + tuple count instead of every tuple. A summary, never a
    // silently-chosen subset.
    private const int MaxRenderedSupportTuplesPerType = 3;

    // v5 (spec 212): the Investigate / Watch lines are INPUTS (ReportActionContext.Thresholds) rather than
    // two constants — the mapping CONTRACT changed, so the version moves even though every result under
    // LabelThresholds.Default is byte-identical to v4. v4 (spec 211) made the Watch-floor rationale print
    // each counted type's PRESENTATION label via the shared SignalTypeDisplay seam. Nothing hashes this
    // token into ScoringConfigVersion (spec 211 verified; spec 212 re-verified).
    // v6 (spec 217): RULE 0 — a company under a recognised pending acquisition is labelled Ignore with the
    // acquisition as its rationale, ahead of every other rule including thin evidence. The mapping CONTRACT
    // changed (a new input decides a label), so the version moves; every company WITHOUT a pending
    // acquisition is byte-identical to v5. Nothing hashes this token into ScoringConfigVersion (spec 211
    // verified; 212 and 217 re-verified) — the acqscan/supersede RULE is hashed through the signal-source
    // descriptor's acq= field, but this label policy is report-layer only.
    public string Version => "weekly-report-action-v6";

    public ReportActionResult Decide(ReportActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Current);

        var current = context.Current;
        var previous = context.Previous;

        // 0. A PENDING ACQUISITION CLOSES THE THESIS (spec 217 §2). It runs FIRST — ahead of the
        // evidence-confidence floor, ahead of the improving/deteriorating delta, ahead of opportunity —
        // because from the announcement the price sits at the bid and tracks the deal, not the business, so
        // every other rule would be describing a number that no longer measures what it claims to. This is
        // structurally what stops the 2026-08-10 MarineMax shape recurring: a $1.5B all-cash sale of the
        // whole company was read as a StrategicPartnership, trajectory rose 56 → 62, and rule 3 labelled it
        // "Thesis improving". With rule 0 first, the improving/deteriorating rules CANNOT fire for a
        // company being acquired, whatever the signals say.
        //
        // NO NEW LABEL IS INTRODUCED (AD-9 / the philosophy file): the label is `Ignore`, one of the six,
        // and the STATE is carried by the rationale. The wording is advice-free — it states the filed fact
        // and says the thesis is closed; it does not say what to do about the shares.
        if (context.PendingAcquisition is { } acquisition)
        {
            return new ReportActionResult(
                RadarReportAction.Ignore,
                $"Acquisition pending: {acquisition.AcquirerName} at "
                    + $"{acquisition.DescribeConsideration()} announced "
                    + $"{RenderDate(DateOnly.FromDateTime(acquisition.AnnouncedOnUtc.UtcDateTime))}; "
                    + "thesis closed — trajectory and opportunity are not the driver of this price.");
        }

        // Spec 212: the labelled arm's lines; an absent pair is the pre-212 default, never a silent zero.
        var lines = context.Thresholds ?? LabelThresholds.Default;

        // Decision precedence (first match wins):
        //   0. A recognised PENDING ACQUISITION closes the thesis (spec 217) — handled above, before this
        //      block, so nothing below can fire for a company being bought.
        //   1. Thin evidence overrides everything else.
        //   2. Deterioration (surfaced before opportunity, to stay honest).
        //   3. Improvement.
        //   4. Steady-state by opportunity (Investigate / Watch / Ignore).
        // Because rule 1 already handled thin evidence, anything that reaches the steady-state branch
        // has adequate evidence, so a sub-Watch opportunity is Ignore (low signal), not NeedsMoreEvidence.

        // 1. Thin evidence overrides everything.
        if (current.EvidenceConfidenceScore < EvidenceConfidenceFloor)
        {
            return new ReportActionResult(
                RadarReportAction.NeedsMoreEvidence,
                $"Evidence confidence {current.EvidenceConfidenceScore} is below {EvidenceConfidenceFloor}; needs more evidence.");
        }

        // Comparability gate: only diff against the prior snapshot when it was produced by the SAME
        // scoring generation (context.PreviousComparable). An incomparable previous (e.g. scoring logic
        // changed between runs) falls through to the steady-state branch below and never yields
        // ThesisImproving/ThesisDeteriorating — a scoring-logic delta must not be told as a company story.
        if (previous is not null && context.PreviousComparable)
        {
            var delta = current.TrajectoryScore - previous.TrajectoryScore;

            // 2. Deterioration (before opportunity).
            if (delta <= -ThesisDelta)
            {
                return new ReportActionResult(
                    RadarReportAction.ThesisDeteriorating,
                    $"Trajectory fell {previous.TrajectoryScore}→{current.TrajectoryScore} ({delta}) versus the prior snapshot.");
            }

            // 3. Improvement.
            if (delta >= ThesisDelta && current.TrajectoryScore >= NeutralTrajectory)
            {
                return new ReportActionResult(
                    RadarReportAction.ThesisImproving,
                    $"Trajectory rose {previous.TrajectoryScore}→{current.TrajectoryScore} (+{delta}) versus the prior snapshot.");
            }
        }

        // 4. Steady-state by opportunity.
        if (current.OpportunityScore >= lines.Investigate)
        {
            return new ReportActionResult(
                RadarReportAction.Investigate,
                $"Opportunity {current.OpportunityScore} (>= {lines.Investigate}); worth investigating.");
        }

        if (current.OpportunityScore >= lines.Watch)
        {
            return new ReportActionResult(
                RadarReportAction.Watch,
                $"Opportunity {current.OpportunityScore} (>= {lines.Watch}); watch for further signals.");
        }

        // 4b. Corroboration floor (v2): opportunity is below the ARM's Watch line (spec 212), but an under-followed name
        // whose trajectory is not below neutral and whose contributing signals agree across several
        // independent axes is a research lead, not noise. Floor it to Watch — never higher, and never
        // for already-noticed (Large/Mega) names.
        if (context.FollowingTier is FollowingTier.Small or FollowingTier.Mid
            && current.TrajectoryScore >= NeutralTrajectory)
        {
            // Grouped by type in enum order (spec 210): the COUNT is the number of groups — byte-identical
            // to v2's Distinct().Count() — and the groups are what the rationale names below. Grouping,
            // ordering and the count run on the STORED enum; only the printed name goes through the shared
            // SignalTypeDisplay seam (spec 211, v4).
            var positiveByType = context.ContributingSignals
                .Where(s => s.Direction == SignalDirection.Positive)
                .GroupBy(s => s.Type)
                .OrderBy(g => g.Key)
                .ToList();
            var positiveTypeCount = positiveByType.Count;

            if (positiveTypeCount >= MinCorroboratingSignalTypes)
            {
                var named = string.Join(
                    " + ",
                    positiveByType.Select(g => $"{SignalTypeDisplay.Label(g.Key)} ({DescribeSupport(g)})"));

                return new ReportActionResult(
                    RadarReportAction.Watch,
                    $"Opportunity {current.OpportunityScore} below {lines.Watch} but {positiveTypeCount} corroborating positive signal types across an under-followed name; floored to Watch (not Ignore): {named}.");
            }
        }

        // Evidence is adequate (rule 1 did not fire) but opportunity is below the Watch floor:
        // this is a genuine low signal, not a gap in the evidence — label it Ignore.
        return new ReportActionResult(
            RadarReportAction.Ignore,
            $"Opportunity {current.OpportunityScore} below {lines.Watch} with adequate evidence; low signal.");
    }

    /// <summary>
    /// Spec 210: the support behind one counted type — every DISTINCT (source class, observed date,
    /// judgment-derived) tuple among its positive signals, ordered by date (unknown last), then source
    /// class, then judgment flag; or, above <see cref="MaxRenderedSupportTuplesPerType"/>, the honest
    /// range summary. Two positive signals of the same type from the same (source, date, flag) are ONE
    /// tuple: the rationale is about distinct support, not row count.
    /// </summary>
    private static string DescribeSupport(IEnumerable<ReportSignalRef> positiveSignalsOfOneType)
    {
        var tuples = positiveSignalsOfOneType
            .Select(s => new SupportTuple(
                DescribeSourceClass(s.SourceType),
                s.ObservedAtUtc is { } observed ? DateOnly.FromDateTime(observed.UtcDateTime) : null,
                s.IsJudgmentDerived))
            .Distinct()
            .OrderBy(t => t.Date is null)            // known dates first, unknown last
            .ThenBy(t => t.Date)
            .ThenBy(t => t.SourceClass, StringComparer.Ordinal)
            .ThenBy(t => JudgmentRank(t.IsJudgmentDerived))
            .ToList();

        if (tuples.Count <= MaxRenderedSupportTuplesPerType)
        {
            return string.Join("; ", tuples.Select(RenderTuple));
        }

        // Over the cap: distinct KNOWN dates with their range, unknown-date tuples counted separately
        // (never folded into the range, never dropped), and the total tuple count.
        var knownDates = tuples
            .Where(t => t.Date is not null)
            .Select(t => t.Date!.Value)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        var unknownDateTuples = tuples.Count(t => t.Date is null);

        var dates = knownDates.Count switch
        {
            0 => "0 distinct dates",
            1 => $"1 distinct date {RenderDate(knownDates[0])}",
            _ => $"{knownDates.Count} distinct dates {RenderDate(knownDates[0])}–{RenderDate(knownDates[^1])}",
        };
        var unknown = unknownDateTuples switch
        {
            0 => string.Empty,
            1 => " (+1 date unknown)",
            _ => $" (+{unknownDateTuples} dates unknown)",
        };

        return $"{dates}{unknown}, {tuples.Count} support tuples";
    }

    private static string RenderTuple(SupportTuple tuple)
    {
        var date = tuple.Date is { } d ? RenderDate(d) : "date unknown";
        var judgment = tuple.IsJudgmentDerived switch
        {
            true => ", judgment",
            false => string.Empty,
            null => ", judgment unknown",
        };
        return $"{tuple.SourceClass} {date}{judgment}";
    }

    private static string RenderDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Deterministic order of the judgment flag within a (date, source) tie: false, true, then unknown.
    private static int JudgmentRank(bool? isJudgmentDerived) => isJudgmentDerived switch
    {
        false => 0,
        true => 1,
        null => 2,
    };

    /// <summary>
    /// The ONE deterministic display mapping from the canonical <see cref="EvidenceSourceType"/> to the
    /// source class the rationale prints. <c>null</c> (not recorded) and any enum value this switch does
    /// not know (an appended member, or an undefined integer hydrated from a stale store) both fall back
    /// to "source unknown" explicitly — never to the enum's name and never to an exception. Internal so a
    /// test can prove every DEFINED member maps to a non-fallback string.
    /// </summary>
    internal static string DescribeSourceClass(EvidenceSourceType? sourceType) => sourceType switch
    {
        EvidenceSourceType.Filing => "filing",
        EvidenceSourceType.NewsArticle => "news",
        EvidenceSourceType.PressRelease => "press release",
        EvidenceSourceType.RssFeed => "feed",
        EvidenceSourceType.CompanyBlog => "company blog",
        EvidenceSourceType.EarningsTranscript => "transcript",
        EvidenceSourceType.GovernmentContract => "government contract",
        EvidenceSourceType.JobPosting => "job posting",
        EvidenceSourceType.Patent => "patent",
        EvidenceSourceType.SocialMedia => "social",
        EvidenceSourceType.RegulatoryAnnouncement => "regulatory announcement",
        EvidenceSourceType.InsiderTransaction => "insider filing",
        EvidenceSourceType.ConferenceMention => "conference",
        EvidenceSourceType.RegulatoryApproval => "regulatory approval",
        EvidenceSourceType.Trademark => "trademark",
        EvidenceSourceType.Manual => "manual",
        EvidenceSourceType.LocalFile => "local file",
        _ => "source unknown",
    };

    /// <summary>One distinct unit of support behind a counted type (spec 210).</summary>
    private sealed record SupportTuple(string SourceClass, DateOnly? Date, bool? IsJudgmentDerived);
}
