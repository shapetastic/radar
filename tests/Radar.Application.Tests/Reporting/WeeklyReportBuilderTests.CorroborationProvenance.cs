using Microsoft.Extensions.Logging;
using Radar.Application.Collectors;
using Radar.Application.Reporting;
using Radar.Application.SignalExtraction;
using Radar.Application.Tests.Ai;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Reports;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Reporting;

// Spec 210: the builder stamps each contributing-signal ref with the provenance the action policy's
// Watch-floor rationale names (observed instant, canonical evidence source type, judgment-derived flag),
// from the SINGLE per-snapshot evidence load — and a missing evidence item leaves the source type null
// (rendered "source unknown"), counted once per snapshot, never defaulted.
public sealed partial class WeeklyReportBuilderTests
{
    private const string FloorRationaleMarker = "floored to Watch (not Ignore): ";

    /// <summary>A well-formed judgment-derived envelope, composed by the ONE producer (never hand-rolled).</summary>
    private static string JudgmentEnvelope(Guid citedEvidenceId) =>
        NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
            judgmentId: Guid.NewGuid(),
            judgmentCohortKey: "cohort-2026-02",
            trajectoryToken: "Improving",
            trajectoryFactIds: [Guid.NewGuid()],
            sourceObservationIds: [Guid.NewGuid()],
            citedEvidenceIds: [citedEvidenceId]);

    // A floor candidate: opportunity below the Watch line (40), adequate evidence, neutral-or-better
    // trajectory, under-followed. Two distinct positive types then floor it to Watch.
    private static Task SeedFloorCandidateAsync(
        Harness h, Guid companyId, Guid snapshotId, FollowingTier tier = FollowingTier.Small) =>
        SeedCompanyAsync(
            h, companyId, snapshotId, opportunity: 30, trajectory: 55, evidenceConfidence: 70,
            followingTier: tier);

    [Fact]
    public async Task SignalRefsCarryProvenanceFromTheSingleEvidenceLoadAndTheFloorNamesIt()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedFloorCandidateAsync(h, companyId, snapshotId);

        // The hypothesized same-event echo: a filing-typed positive and a judgment-derived MediaAttention
        // positive observed the SAME day. The count fires on two distinct types; the rationale must show
        // the same-day pair on one line so a reader can see the shape.
        var filingSignalId = await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.GuidanceChange, SignalDirection.Positive,
            "Earnings filing read as improving.",
            sourceType: EvidenceSourceType.Filing, observedAtUtc: InPeriod);
        var newsEvidenceId = Guid.NewGuid();
        var newsSignalId = await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.MediaAttention, SignalDirection.Positive,
            "Judged improving on cited coverage.",
            sourceType: EvidenceSourceType.NewsArticle, observedAtUtc: InPeriod,
            metadataJson: JudgmentEnvelope(newsEvidenceId), evidenceId: newsEvidenceId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // The policy saw refs stamped from the stored signal + the loaded evidence.
        var context = Assert.Single(h.Policy.Contexts);
        var filing = Assert.Single(context.ContributingSignals, s => s.SignalId == filingSignalId);
        Assert.Equal(InPeriod, filing.ObservedAtUtc);
        Assert.Equal(EvidenceSourceType.Filing, filing.SourceType);
        Assert.False(filing.IsJudgmentDerived);
        var news = Assert.Single(context.ContributingSignals, s => s.SignalId == newsSignalId);
        Assert.Equal(InPeriod, news.ObservedAtUtc);
        Assert.Equal(EvidenceSourceType.NewsArticle, news.SourceType);
        Assert.True(news.IsJudgmentDerived);

        // ONE evidence lookup per distinct evidence id (the default seeded link + the two above): the
        // signal refs read the same load as the evidence block and the insider aggregate — no second pass.
        Assert.Equal(3, h.CountingEvidence.GetByIdCallCount);

        // The floored entry's rationale names the same-day pair, and it reaches the rendered "Why" line.
        var item = Assert.Single(result.Items);
        Assert.Equal(RadarReportAction.Watch, item.SuggestedAction);
        const string named = "GuidanceChange (filing 2026-02-05) + MediaAttention (news 2026-02-05, judgment).";
        Assert.EndsWith(FloorRationaleMarker + named, item.Summary, StringComparison.Ordinal);
        Assert.Contains("- Why: " + item.Summary, result.Report.MarkdownContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignalWhoseEvidenceIsNotLoadedKeepsNullSourceTypeAndIsCountedInOneWarning()
    {
        var logger = new CapturingLogger<WeeklyReportBuilder>();
        var h = new Harness(logger: logger);
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedFloorCandidateAsync(h, companyId, snapshotId);

        // Populated: linked evidence stored (press release by default).
        var populatedId = await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.", observedAtUtc: InPeriod);
        // Gap 1: linked, but the store has no such evidence item (loaded as null).
        var notFoundId = await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.", observedAtUtc: InPeriod, storeEvidence: false);
        // Gap 2: the signal cites an evidence id none of the snapshot's links carry (never fetched).
        var notLinkedId = await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Second production order booked.", observedAtUtc: InPeriod, signalEvidenceId: Guid.NewGuid());

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var context = Assert.Single(h.Policy.Contexts);
        var populated = Assert.Single(context.ContributingSignals, s => s.SignalId == populatedId);
        Assert.Equal(EvidenceSourceType.PressRelease, populated.SourceType);

        // Null means NOT RECORDED — never a defaulted enum member. The judgment flag IS evaluated (false),
        // and the observed instant IS recorded: only the evidence-derived member is unknown.
        foreach (var gapId in new[] { notFoundId, notLinkedId })
        {
            var gap = Assert.Single(context.ContributingSignals, s => s.SignalId == gapId);
            Assert.Null(gap.SourceType);
            Assert.False(gap.IsJudgmentDerived);
            Assert.Equal(InPeriod, gap.ObservedAtUtc);
        }

        // Exactly ONE aggregated warning per snapshot, naming both counts — never one line per signal.
        var warning = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("have no loaded evidence behind them", StringComparison.Ordinal));
        Assert.Contains("2 contributing signal(s)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(1 cite an evidence id outside the snapshot's links; 1 cite linked evidence the store did not return)", warning.Message, StringComparison.Ordinal);

        // One lookup per DISTINCT linked evidence id (the default link + the three seeded links = 4), and
        // NO extra fetch for the stray id the third signal cites: the gap is recorded, never chased.
        Assert.Equal(4, h.CountingEvidence.GetByIdCallCount);

        // The floor still fires (two distinct types) and the unknown source is rendered as unknown.
        var item = Assert.Single(result.Items);
        Assert.Equal(RadarReportAction.Watch, item.SuggestedAction);
        Assert.EndsWith(
            FloorRationaleMarker
            + "CustomerWin (press release 2026-02-05; source unknown 2026-02-05) + StrategicPartnership (source unknown 2026-02-05).",
            item.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLinkedEvidenceIsCountedInOneWarningPerSnapshotNeverOnePerItem()
    {
        var logger = new CapturingLogger<WeeklyReportBuilder>();
        var h = new Harness(logger: logger);
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await SeedFloorCandidateAsync(h, companyId, snapshotId);

        // Two DISTINCT linked evidence ids the store does not hold; the per-item log line this replaces
        // would have fired twice here.
        var missingA = Guid.NewGuid();
        var missingB = Guid.NewGuid();
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.", observedAtUtc: InPeriod, evidenceId: missingA, storeEvidence: false);
        await SeedSignalLinkAsync(
            h, snapshotId, Guid.NewGuid(), SignalType.StrategicPartnership, SignalDirection.Positive,
            "Joint development partnership signed.", observedAtUtc: InPeriod, evidenceId: missingB,
            storeEvidence: false);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        // Exactly ONE load-side warning for the snapshot, carrying the count, the distinct-link denominator
        // and both ids — the per-evidence line is gone.
        var warning = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("linked evidence item(s) were not found in the store", StringComparison.Ordinal));
        Assert.Contains("2 of 3 distinct linked evidence item(s)", warning.Message, StringComparison.Ordinal);
        Assert.Contains(missingA.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(missingB.ToString(), warning.Message, StringComparison.Ordinal);

        // Nothing was dropped: both links still render their placeholder refs.
        var context = Assert.Single(h.Policy.Contexts);
        Assert.Equal(2, context.ContributingSignals.Count(s => s.SourceType is null));
        Assert.Single(result.Items);
        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        Assert.Equal(2, entry.Evidence.Count(r => r.Title == "(evidence unavailable)"));
    }

    // ---- Full-report label identity: provenance moves rationale text and nothing else ----------------

    private static readonly Guid FlooredCompanyId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid InvestigateCompanyId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid FollowedCompanyId = Guid.Parse("a0000000-0000-0000-0000-000000000003");

    /// <summary>
    /// Three companies, seeded identically except for whether the signals' evidence is stored: stored ⇒
    /// populated provenance (source type known); not stored ⇒ null provenance (source unknown).
    /// </summary>
    private static async Task<WeeklyReportResult> GenerateThreeCompanyReportAsync(bool storeEvidence)
    {
        var h = new Harness();

        var flooredSnapshot = Guid.Parse("b0000000-0000-0000-0000-000000000001");
        await SeedFloorCandidateAsync(h, FlooredCompanyId, flooredSnapshot);
        await SeedTwoPositiveTypesAsync(h, flooredSnapshot, storeEvidence);

        await SeedCompanyAsync(
            h, InvestigateCompanyId, Guid.Parse("b0000000-0000-0000-0000-000000000002"),
            opportunity: 70, name: "Investigate Co", ticker: "INV");

        // Same corroborated set on an already-followed name: the tier gate keeps it at Ignore.
        var followedSnapshot = Guid.Parse("b0000000-0000-0000-0000-000000000003");
        await SeedCompanyAsync(
            h, FollowedCompanyId, followedSnapshot, opportunity: 30, trajectory: 55, evidenceConfidence: 70,
            name: "Followed Co", ticker: "FOL", followingTier: FollowingTier.Mega);
        await SeedTwoPositiveTypesAsync(h, followedSnapshot, storeEvidence);

        return await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);
    }

    private static async Task SeedTwoPositiveTypesAsync(Harness h, Guid snapshotId, bool storeEvidence)
    {
        await SeedSignalLinkAsync(
            h, snapshotId, DeriveGuid(snapshotId, 0xA1), SignalType.CustomerWin, SignalDirection.Positive,
            "Production order booked.", observedAtUtc: InPeriod, evidenceId: DeriveGuid(snapshotId, 0xC1),
            storeEvidence: storeEvidence);
        await SeedSignalLinkAsync(
            h, snapshotId, DeriveGuid(snapshotId, 0xA2), SignalType.StrategicPartnership,
            SignalDirection.Positive, "Joint development partnership signed.", observedAtUtc: InPeriod,
            evidenceId: DeriveGuid(snapshotId, 0xC2), storeEvidence: storeEvidence);
    }

    [Fact]
    public async Task FullReportLabelSetIsIdenticalWithNullAndPopulatedProvenanceOnlyRationaleDiffers()
    {
        var populated = await GenerateThreeCompanyReportAsync(storeEvidence: true);
        var bare = await GenerateThreeCompanyReportAsync(storeEvidence: false);

        // Same companies, same order, same label — provenance is not a label input.
        Assert.Equal(3, populated.Items.Count);
        Assert.Equal(
            populated.Items.Select(i => (i.CompanyId, i.SuggestedAction)).ToArray(),
            bare.Items.Select(i => (i.CompanyId, i.SuggestedAction)).ToArray());
        Assert.Equal(
            RadarReportAction.Watch,
            populated.Items.Single(i => i.CompanyId == FlooredCompanyId).SuggestedAction);
        Assert.Equal(
            RadarReportAction.Investigate,
            populated.Items.Single(i => i.CompanyId == InvestigateCompanyId).SuggestedAction);
        Assert.Equal(
            RadarReportAction.Ignore,
            populated.Items.Single(i => i.CompanyId == FollowedCompanyId).SuggestedAction);

        // Only the FLOORED entry's rationale differs, and only after the shared prefix.
        var flooredPopulated = populated.Items.Single(i => i.CompanyId == FlooredCompanyId).Summary;
        var flooredBare = bare.Items.Single(i => i.CompanyId == FlooredCompanyId).Summary;
        Assert.NotEqual(flooredPopulated, flooredBare);
        var prefixEnd = flooredPopulated.IndexOf(FloorRationaleMarker, StringComparison.Ordinal) + FloorRationaleMarker.Length;
        Assert.True(prefixEnd > FloorRationaleMarker.Length);
        Assert.Equal(flooredPopulated[..prefixEnd], flooredBare[..prefixEnd]);
        Assert.EndsWith(
            "CustomerWin (press release 2026-02-05) + StrategicPartnership (press release 2026-02-05).",
            flooredPopulated,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "CustomerWin (source unknown 2026-02-05) + StrategicPartnership (source unknown 2026-02-05).",
            flooredBare,
            StringComparison.Ordinal);

        foreach (var companyId in new[] { InvestigateCompanyId, FollowedCompanyId })
        {
            Assert.Equal(
                populated.Items.Single(i => i.CompanyId == companyId).Summary,
                bare.Items.Single(i => i.CompanyId == companyId).Summary);
        }
    }
}
