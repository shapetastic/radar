namespace Radar.Application.Reporting;

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
/// </summary>
public sealed record ReportActionContext(
    CompanyScoreSnapshot Current,
    CompanyScoreSnapshot? Previous,
    bool PreviousComparable = true,
    IReadOnlyList<ReportSignalRef>? ContributingSignals = null,
    FollowingTier FollowingTier = FollowingTier.Small)
{
    /// <summary>
    /// The signals behind <see cref="Current"/>; never null (an absent set reads as "no corroboration").
    /// </summary>
    public IReadOnlyList<ReportSignalRef> ContributingSignals { get; init; } = ContributingSignals ?? [];
}
