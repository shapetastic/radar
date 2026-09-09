namespace Radar.Application.Reporting;

using Radar.Application.Acquisitions;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;

/// <summary>
/// Inputs for deciding a company's weekly report label. <paramref name="Current"/> is the snapshot for
/// the reporting period; <paramref name="Previous"/> is the immediately-preceding snapshot for the same
/// company (null if none), used to detect an improving or deteriorating thesis. <paramref name="Previous"/>
/// is only acted on when <paramref name="PreviousComparable"/> is true — i.e. both snapshots were produced
/// by the same scoring generation; otherwise the policy falls back to its no-previous behaviour.
/// <paramref name="ContributingSignals"/> are the signals that actually contributed to
/// <paramref name="Current"/> (resolved from its score-evidence links), so a policy can measure
/// corroboration — how many independent directional axes agree — without re-reading the store.
/// <paramref name="FollowingTier"/> is the company's curated "how noticed already" tier (AD-14; never
/// derived from price), letting a policy treat an under-followed name differently from a mega-cap.
/// Both default to conservative values (no signals / <see cref="FollowingTier.Small"/>).
/// <paramref name="Thresholds"/> (spec 212) are the labelled arm's Investigate / Watch Opportunity lines;
/// <c>null</c> resolves to <see cref="LabelThresholds.Default"/> inside the policy (the pre-212 60 / 40),
/// trailing and defaulted so every existing construction site keeps compiling.
/// <paramref name="PendingAcquisition"/> (spec 217 §2) is the company's recognised, unexpired acquisition
/// agreement as of this snapshot's instant, or <c>null</c> when there is none. It drives RULE 0, which runs
/// ahead of every other rule: a company being bought is labelled <c>Ignore</c> with the acquisition as its
/// rationale, so <c>Thesis improving</c> can never fire for a takeover again (the 2026-08-10 MarineMax
/// shape). Trailing and defaulted, so every existing construction site keeps compiling and keeps today's
/// behaviour.
/// </summary>
public sealed record ReportActionContext(
    CompanyScoreSnapshot Current,
    CompanyScoreSnapshot? Previous,
    bool PreviousComparable = true,
    IReadOnlyList<ReportSignalRef>? ContributingSignals = null,
    FollowingTier FollowingTier = FollowingTier.Small,
    LabelThresholds? Thresholds = null,
    PendingAcquisitionRecord? PendingAcquisition = null)
{
    /// <summary>
    /// The signals behind <see cref="Current"/>; never null (an absent set reads as "no corroboration").
    /// </summary>
    public IReadOnlyList<ReportSignalRef> ContributingSignals { get; init; } = ContributingSignals ?? [];
}
