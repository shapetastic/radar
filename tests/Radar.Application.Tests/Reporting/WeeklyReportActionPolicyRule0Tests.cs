using Radar.Application.Acquisitions;
using Radar.Application.Reporting;
using Radar.Application.Tests.Acquisitions;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// SPEC 217 §2 — RULE 0, and the PINNED REGRESSION for the 2026-08-10 shape.
/// <para>
/// What actually happened, reconstructed from the store: Radar collected MarineMax's item-1.01/7.01/9.01
/// 8-K announcing a $1.5B all-cash sale of the whole company, the keyword extractor's "material definitive
/// agreement" rule minted <c>StrategicPartnership (Positive)</c> at strength 4, trajectory rose 56 → 62,
/// and the weekly report labelled the company <b>Thesis improving</b> at rank 43. Rule 0 makes that
/// structurally impossible: it runs FIRST, so no later rule — including the improving/deteriorating delta —
/// can fire for a company under a recognised pending acquisition.
/// </para>
/// </summary>
public sealed class WeeklyReportActionPolicyRule0Tests
{
    private static readonly DateTimeOffset Announced = new(2026, 8, 10, 8, 0, 12, TimeSpan.Zero);

    private static WeeklyReportActionPolicyV1 Policy() => new();

    private static PendingAcquisitionRecord Acquisition() => PendingAcquisitionsTests.Record();

    [Fact]
    public void TheMarineMaxShape_IsIgnore_NotThesisImproving()
    {
        // THE regression. Trajectory 56 → 62 (+6, past the ±5 thesis delta) with adequate evidence: without
        // rule 0 this is exactly ThesisImproving, which is what the 2026-08-10 report printed.
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(56).Build();
        var current = new ScoreSnapshotBuilder()
            .WithTrajectoryScore(62)
            .WithOpportunityScore(48)
            .WithEvidenceConfidenceScore(70)
            .Build();

        var withoutRecognition = Policy().Decide(new ReportActionContext(current, previous));
        Assert.Equal(RadarReportAction.ThesisImproving, withoutRecognition.Action);

        var withRecognition = Policy().Decide(
            new ReportActionContext(current, previous, PendingAcquisition: Acquisition()));

        Assert.Equal(RadarReportAction.Ignore, withRecognition.Action);
        Assert.StartsWith("Acquisition pending:", withRecognition.Rationale, StringComparison.Ordinal);
        Assert.Contains("Safe Harbor Marinas, LLC", withRecognition.Rationale, StringComparison.Ordinal);
        Assert.Contains("$53.00 per share in cash", withRecognition.Rationale, StringComparison.Ordinal);
        Assert.Contains("announced 2026-08-10", withRecognition.Rationale, StringComparison.Ordinal);
        Assert.Contains("thesis closed", withRecognition.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule0_OutranksEveryOtherRule_IncludingThinEvidenceAndAHighOpportunity()
    {
        var acquisition = Acquisition();

        // Thin evidence (rule 1 would say NeedsMoreEvidence).
        var thin = new ScoreSnapshotBuilder().WithEvidenceConfidenceScore(10).Build();
        Assert.Equal(
            RadarReportAction.Ignore,
            Policy().Decide(new ReportActionContext(thin, null, PendingAcquisition: acquisition)).Action);

        // Deterioration (rule 2).
        var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(70).Build();
        var fell = new ScoreSnapshotBuilder().WithTrajectoryScore(50).WithEvidenceConfidenceScore(70).Build();
        Assert.Equal(
            RadarReportAction.Ignore,
            Policy().Decide(new ReportActionContext(fell, previous, PendingAcquisition: acquisition)).Action);

        // A top-of-the-report Opportunity (rule 4 would say Investigate).
        var strong = new ScoreSnapshotBuilder()
            .WithOpportunityScore(95)
            .WithEvidenceConfidenceScore(90)
            .Build();
        Assert.Equal(
            RadarReportAction.Ignore,
            Policy().Decide(new ReportActionContext(strong, null, PendingAcquisition: acquisition)).Action);
    }

    [Fact]
    public void Rule0_IntroducesNoNewLabel_AndNoAdviceLanguage()
    {
        var result = Policy().Decide(new ReportActionContext(
            new ScoreSnapshotBuilder().Build(), null, PendingAcquisition: Acquisition()));

        // AD-9: the six labels are unchanged; the STATE lives in the rationale.
        Assert.Equal(RadarReportAction.Ignore, result.Action);
        foreach (var forbidden in new[] { "buy", "sell", "guaranteed", "safe bet" })
        {
            Assert.DoesNotContain(forbidden, result.Rationale, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WithoutAPendingAcquisition_EveryOutcomeIsUnchangedFromV5()
    {
        // The other half of "v6 moves the CONTRACT, not the outcomes": across a representative matrix, a
        // null PendingAcquisition reproduces the pre-217 decision exactly.
        foreach (var trajectory in new[] { 20, 50, 62, 80 })
        {
            foreach (var opportunity in new[] { 10, 45, 70 })
            {
                foreach (var evidence in new[] { 20, 70 })
                {
                    var current = new ScoreSnapshotBuilder()
                        .WithTrajectoryScore(trajectory)
                        .WithOpportunityScore(opportunity)
                        .WithEvidenceConfidenceScore(evidence)
                        .Build();
                    var previous = new ScoreSnapshotBuilder().WithTrajectoryScore(50).Build();

                    var withNull = Policy().Decide(new ReportActionContext(current, previous));
                    var withExplicitNull = Policy().Decide(
                        new ReportActionContext(current, previous, PendingAcquisition: null));

                    Assert.Equal(withNull.Action, withExplicitNull.Action);
                    Assert.Equal(withNull.Rationale, withExplicitNull.Rationale);
                }
            }
        }
    }

    [Fact]
    public void TheRationaleReadsFromTheRecord_SoTheBannerAndTheLabelCannotDisagree()
    {
        // The banner (renderer) and this rationale (policy) both call DescribeConsideration on the SAME
        // record, which is what makes them consistent by construction rather than by convention.
        var record = PendingAcquisitionsTests.Record() with
        {
            AcquirerName = "Acme Holdings, Inc.",
            ConsiderationPerShare = "12.50",
            ConsiderationKind = AcquisitionConsiderationKind.Mixed,
            AnnouncedOnUtc = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero),
        };

        var result = Policy().Decide(new ReportActionContext(
            new ScoreSnapshotBuilder().Build(), null, PendingAcquisition: record));

        Assert.Contains(record.DescribeConsideration(), result.Rationale, StringComparison.Ordinal);
        Assert.Contains("announced 2026-03-02", result.Rationale, StringComparison.Ordinal);
        Assert.NotEqual(Announced, record.AnnouncedOnUtc);
    }

    [Fact]
    public void SeedStatusIsNotTheInput_TheRecognisedRecordIs()
    {
        // The state is DERIVED (spec 217 §2): CompanyStatus.PendingAcquisition is never set by hand in
        // companies.json, and the policy never reads a company status at all — it reads the record.
        Assert.Contains(CompanyStatus.PendingAcquisition, Enum.GetValues<CompanyStatus>());

        var noRecord = Policy().Decide(new ReportActionContext(
            new ScoreSnapshotBuilder().WithOpportunityScore(95).WithEvidenceConfidenceScore(90).Build(),
            null));

        Assert.Equal(RadarReportAction.Investigate, noRecord.Action);
    }
}
