namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>
/// Deterministic first implementation of <see cref="IReportActionPolicy"/>. Maps a company's current
/// score snapshot (and its immediately-prior snapshot, for improving/deteriorating) onto one of the five
/// ALLOWED weekly-report labels plus a plain-English, advice-free rationale. Pure: no clock, no
/// randomness, no I/O. Never returns <see cref="RadarReportAction.Ignore"/> and never emits
/// financial-advice language.
/// </summary>
public sealed class WeeklyReportActionPolicyV1 : IReportActionPolicy
{
    // Evidence-confidence floor: below this, the thesis is "needs more evidence" regardless of score.
    private const int EvidenceConfidenceFloor = 35;
    // Trajectory midpoint (50 = neutral, matches radar-formula-v1).
    private const int NeutralTrajectory = 50;
    // Minimum trajectory change vs the previous snapshot to call a thesis improving/deteriorating.
    private const int ThesisDelta = 5;
    // Opportunity thresholds for the steady-state labels.
    private const int InvestigateOpportunity = 60;
    private const int WatchOpportunity = 40;

    public string Version => "weekly-report-action-v1";

    public ReportActionResult Decide(ReportActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Current);

        var current = context.Current;
        var previous = context.Previous;

        // Decision precedence (first match wins):
        //   1. Thin evidence overrides everything.
        //   2. Deterioration (surfaced before opportunity, to stay honest).
        //   3. Improvement.
        //   4. Steady-state by opportunity (Investigate / Watch / NeedsMoreEvidence).

        // 1. Thin evidence overrides everything.
        if (current.EvidenceConfidenceScore < EvidenceConfidenceFloor)
        {
            return new ReportActionResult(
                RadarReportAction.NeedsMoreEvidence,
                $"Evidence confidence {current.EvidenceConfidenceScore} is below {EvidenceConfidenceFloor}; needs more evidence.");
        }

        if (previous is not null)
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
        if (current.OpportunityScore >= InvestigateOpportunity)
        {
            return new ReportActionResult(
                RadarReportAction.Investigate,
                $"Opportunity {current.OpportunityScore} (>= {InvestigateOpportunity}); worth investigating.");
        }

        if (current.OpportunityScore >= WatchOpportunity)
        {
            return new ReportActionResult(
                RadarReportAction.Watch,
                $"Opportunity {current.OpportunityScore} (>= {WatchOpportunity}); watch for further signals.");
        }

        return new ReportActionResult(
            RadarReportAction.NeedsMoreEvidence,
            $"Opportunity {current.OpportunityScore} below {WatchOpportunity}; needs more evidence.");
    }
}
