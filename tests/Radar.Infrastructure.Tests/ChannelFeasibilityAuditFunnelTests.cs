using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

using AuditEngine = Radar.ChannelFeasibilityAudit.ChannelFeasibilityAudit;

namespace Radar.Infrastructure.Tests;

/// <summary>
/// Spec 158 §3 — the audit's eligibility-funnel CLASSIFICATION rules, driven through the real audit engine
/// over in-memory repositories:
/// <list type="bullet">
/// <item>a signal whose evidence id does not resolve is <c>evidence-unresolvable</c> — dropped BEFORE
/// collector attribution and NEVER relabelled <c>unattributed</c>;</item>
/// <item>a resolved signal with no establishable collector is <c>resolved-unattributed</c> — consumed by no
/// collector channel;</item>
/// <item>recorded and inferred attribution are counted separately;</item>
/// <item>the window/known-at/Approved predicates match <c>ScoringEngine</c>'s.</item>
/// </list>
/// </summary>
public sealed class ChannelFeasibilityAuditFunnelTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(60);
    private static readonly DateTimeOffset InWindow = AsOf.AddDays(-10);

    private sealed class AllGenuineWeights : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);
        public string CanonicalDescriptor() => "test-all-genuine";
    }

    /// <summary>A resolver stub with an explicit per-evidence answer, defaulting to unattributed.</summary>
    private sealed class MapResolver(IReadOnlyDictionary<Guid, CollectorAttribution> map)
        : ICollectorAttributionResolver
    {
        public CollectorAttribution Resolve(EvidenceItem? evidence) =>
            evidence is not null && map.TryGetValue(evidence.Id, out var attribution)
                ? attribution
                : CollectorAttribution.Unattributed;
    }

    private static AuditEngine EngineOf(
        InMemorySignalRepository signals,
        InMemoryEvidenceRepository evidence,
        InMemoryCompanyRepository companies,
        ICollectorAttributionResolver resolver) =>
        new(
            signals,
            evidence,
            companies,
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringWeights(),
            new AllGenuineWeights(),
            resolver);

    private static Signal ApprovedSignal(
        Guid companyId,
        Guid evidenceId,
        SignalDirection direction = SignalDirection.Neutral,
        SignalType type = SignalType.InsiderBuying,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? createdAt = null,
        int strength = 6,
        decimal confidence = 0.9m) =>
        new SignalBuilder()
            .WithCompanyId(companyId)
            .WithEvidenceId(evidenceId)
            .WithDirection(direction)
            .WithType(type)
            .WithStrength(strength)
            .WithConfidence(confidence)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAt ?? InWindow)
            .WithCreatedAtUtc(createdAt ?? InWindow)
            .Build();

    [Fact]
    public async Task EvidenceUnresolvable_IsClassifiedSeparately_NeverAsUnattributed()
    {
        var company = new CompanyBuilder().Build();
        var companies = new InMemoryCompanyRepository();
        await companies.AddAsync(company, CancellationToken.None);

        var evidenceRepo = new InMemoryEvidenceRepository();
        var resolvedEvidence = new EvidenceBuilder().WithContentHash(Guid.NewGuid().ToString("N")).Build();
        await evidenceRepo.AddIfNewAsync(resolvedEvidence, CancellationToken.None);

        var signals = new InMemorySignalRepository();
        // One signal whose evidence id resolves to nothing on the durable store...
        await signals.AddAsync(
            ApprovedSignal(company.Id, Guid.NewGuid()), CancellationToken.None);
        // ...and one whose evidence resolves but carries no establishable collector.
        await signals.AddAsync(
            ApprovedSignal(company.Id, resolvedEvidence.Id), CancellationToken.None);

        var report = await EngineOf(signals, evidenceRepo, companies,
                RecordedOnlyCollectorAttributionResolver.Instance)
            .RunAsync(AsOf, Window, CancellationToken.None);

        var row = Assert.Single(report.Companies);
        Assert.Equal(2, row.ApprovedInWindow);
        // The unresolvable signal is dropped BEFORE attribution and counted as evidence-unresolvable...
        Assert.Equal(1, row.EvidenceUnresolvableSignals);
        Assert.Equal(1, row.DistinctUnresolvableEvidenceIds);
        Assert.Equal(1, row.ResolvedBeforeSupersede);
        // ...and the attribution split covers ONLY the resolved inputs: the missing-evidence signal must not
        // leak into the unattributed pool (1, not 2).
        Assert.Equal(0, row.RecordedAttribution);
        Assert.Equal(0, row.InferredAttribution);
        Assert.Equal(1, row.UnattributedAttribution);
        // A resolved-but-unattributed signal is consumed by NO collector channel.
        Assert.All(row.Channels, c => Assert.Equal(0, c.SignalCount));
    }

    [Fact]
    public async Task RecordedAndInferred_AreCountedSeparately_AndBothSelectIntoChannels()
    {
        var company = new CompanyBuilder().Build();
        var companies = new InMemoryCompanyRepository();
        await companies.AddAsync(company, CancellationToken.None);

        var evidenceRepo = new InMemoryEvidenceRepository();
        var recordedEvidence = new EvidenceBuilder().WithSourceType(EvidenceSourceType.Filing).WithContentHash(Guid.NewGuid().ToString("N")).Build();
        var inferredEvidence = new EvidenceBuilder().WithSourceType(EvidenceSourceType.Filing).WithContentHash(Guid.NewGuid().ToString("N")).Build();
        var unattributedEvidence = new EvidenceBuilder().WithContentHash(Guid.NewGuid().ToString("N")).Build();
        await evidenceRepo.AddIfNewAsync(recordedEvidence, CancellationToken.None);
        await evidenceRepo.AddIfNewAsync(inferredEvidence, CancellationToken.None);
        await evidenceRepo.AddIfNewAsync(unattributedEvidence, CancellationToken.None);

        var signals = new InMemorySignalRepository();
        await signals.AddAsync(
            ApprovedSignal(company.Id, recordedEvidence.Id, SignalDirection.Positive), CancellationToken.None);
        await signals.AddAsync(
            ApprovedSignal(company.Id, inferredEvidence.Id, SignalDirection.Negative), CancellationToken.None);
        await signals.AddAsync(
            ApprovedSignal(company.Id, unattributedEvidence.Id), CancellationToken.None);

        var resolver = new MapResolver(new Dictionary<Guid, CollectorAttribution>
        {
            [recordedEvidence.Id] = CollectorAttribution.Recorded(RadarCollectorNames.SecForm4),
            [inferredEvidence.Id] = CollectorAttribution.Inferred(RadarCollectorNames.SecForm4),
        });

        var report = await EngineOf(signals, evidenceRepo, companies, resolver)
            .RunAsync(AsOf, Window, CancellationToken.None);

        var row = Assert.Single(report.Companies);
        Assert.Equal(1, row.RecordedAttribution);
        Assert.Equal(1, row.InferredAttribution);
        Assert.Equal(1, row.UnattributedAttribution);

        var form4 = row.Channels.Single(c => c.Collector == RadarCollectorNames.SecForm4);
        Assert.Equal(2, form4.SignalCount);
        Assert.Equal(1, form4.RecordedSignals);
        Assert.Equal(1, form4.InferredSignals);
        // One Positive + one Negative at equal strength/confidence/recency/quality: directional mass on
        // both sides, netting to exactly zero — the BALANCED state, distinct from all-neutral.
        Assert.True(form4.DirectionalActivityMass > 0);
        Assert.Equal(ChannelDirectionState.Balanced, form4.DirectionState);
    }

    [Fact]
    public async Task WindowKnownAtAndReviewPredicates_MatchScoringEngine()
    {
        var company = new CompanyBuilder().Build();
        var companies = new InMemoryCompanyRepository();
        await companies.AddAsync(company, CancellationToken.None);

        var evidenceRepo = new InMemoryEvidenceRepository();
        var evidence = new EvidenceBuilder().WithContentHash(Guid.NewGuid().ToString("N")).Build();
        await evidenceRepo.AddIfNewAsync(evidence, CancellationToken.None);

        var windowStart = AsOf - Window;
        var signals = new InMemorySignalRepository();
        // Exactly at the exclusive window start ⇒ OUT (belongs to the previous window).
        await signals.AddAsync(
            ApprovedSignal(company.Id, evidence.Id, observedAt: windowStart), CancellationToken.None);
        // Observed after the as-of instant ⇒ OUT.
        await signals.AddAsync(
            ApprovedSignal(company.Id, evidence.Id, observedAt: AsOf.AddSeconds(1)), CancellationToken.None);
        // Created after the as-of instant ⇒ OUT (spec-136 known-at honesty).
        await signals.AddAsync(
            ApprovedSignal(company.Id, evidence.Id, createdAt: AsOf.AddSeconds(1)), CancellationToken.None);
        // Not Approved ⇒ OUT.
        var pending = new SignalBuilder()
            .WithCompanyId(company.Id)
            .WithEvidenceId(evidence.Id)
            .WithReviewStatus(SignalReviewStatus.Pending)
            .WithObservedAtUtc(InWindow)
            .WithCreatedAtUtc(InWindow)
            .Build();
        await signals.AddAsync(pending, CancellationToken.None);
        // Exactly at the inclusive as-of instant, created exactly at it ⇒ IN (equality is satisfied).
        await signals.AddAsync(
            ApprovedSignal(company.Id, evidence.Id, observedAt: AsOf, createdAt: AsOf),
            CancellationToken.None);

        var report = await EngineOf(signals, evidenceRepo, companies,
                RecordedOnlyCollectorAttributionResolver.Instance)
            .RunAsync(AsOf, Window, CancellationToken.None);

        var row = Assert.Single(report.Companies);
        Assert.Equal(1, row.ApprovedInWindow);
        Assert.Equal(1, row.ResolvedBeforeSupersede);
    }

    [Fact]
    public async Task FilingsLedV11Evaluation_NetNegativeInsiderFloorsAtZero_NetPositiveScores()
    {
        var companies = new InMemoryCompanyRepository();
        var negativeCompany = new CompanyBuilder().WithName("Net Negative Co").Build();
        var positiveCompany = new CompanyBuilder().WithName("Net Positive Co").Build();
        await companies.AddAsync(negativeCompany, CancellationToken.None);
        await companies.AddAsync(positiveCompany, CancellationToken.None);

        var evidenceRepo = new InMemoryEvidenceRepository();
        var signals = new InMemorySignalRepository();
        var attributionMap = new Dictionary<Guid, CollectorAttribution>();

        async Task AddInsiderSignal(Guid companyId, SignalDirection direction)
        {
            var evidence = new EvidenceBuilder().WithSourceType(EvidenceSourceType.Filing).WithContentHash(Guid.NewGuid().ToString("N")).Build();
            await evidenceRepo.AddIfNewAsync(evidence, CancellationToken.None);
            attributionMap[evidence.Id] = CollectorAttribution.Recorded(RadarCollectorNames.SecForm4);
            await signals.AddAsync(
                ApprovedSignal(companyId, evidence.Id, direction, SignalType.InsiderBuying, strength: 8),
                CancellationToken.None);
        }

        await AddInsiderSignal(negativeCompany.Id, SignalDirection.Negative);
        await AddInsiderSignal(negativeCompany.Id, SignalDirection.Negative);
        await AddInsiderSignal(positiveCompany.Id, SignalDirection.Positive);
        await AddInsiderSignal(positiveCompany.Id, SignalDirection.Positive);

        var report = await EngineOf(signals, evidenceRepo, companies, new MapResolver(attributionMap))
            .RunAsync(AsOf, Window, CancellationToken.None);

        var negative = report.Companies.Single(c => c.CompanyId == negativeCompany.Id);
        var positive = report.Companies.Single(c => c.CompanyId == positiveCompany.Id);

        // max(0, preponderance) floors the net-negative channel at 0 ⇒ the whole predeclared budget lands
        // at integer 0 (the other two channels are dark for this fixture).
        Assert.Equal(ChannelDirectionState.Negative,
            negative.Channels.Single(c => c.Collector == RadarCollectorNames.SecForm4).DirectionState);
        Assert.Equal(0, negative.FilingsLedV11.OpportunityScore);

        // The net-positive company scores above 0 through the same in-memory pass.
        Assert.Equal(ChannelDirectionState.Positive,
            positive.Channels.Single(c => c.Collector == RadarCollectorNames.SecForm4).DirectionState);
        Assert.True(positive.FilingsLedV11.OpportunityScore > 0);
        Assert.True(positive.FilingsLedV11.Composite > 0);
    }
}
