using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.Reporting;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 215 §4 — the builder joins the company's reported-metrics ledger to each entry's evidence refs by
/// <c>EvidenceId</c>: an evidence item with ledger records carries them as display lines (metric display
/// name, value/unit as stated, period), one without carries <c>null</c>; the ledger is read once per
/// surfaced entry; no ledger registered ⇒ every ref is <c>null</c>; a failing ledger read degrades to no
/// clause, never a missing report.
/// </summary>
public sealed partial class WeeklyReportBuilderTests
{
    private sealed class FakeLedger : IReportedMetricStore
    {
        public List<ReportedMetricRecord> Records { get; } = [];
        public int Reads { get; private set; }
        public bool Throw { get; set; }

        public Task<DurableWriteResult> WriteIfNewAsync(
            Guid companyId,
            string accession,
            string policy,
            IReadOnlyList<ReportedMetricRecord> records,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReportedMetricRecord>> GetForCompanyAsync(Guid companyId, CancellationToken ct)
        {
            Reads++;
            if (Throw)
            {
                throw new IOException("ledger unreadable");
            }

            return Task.FromResult<IReadOnlyList<ReportedMetricRecord>>(
                [.. Records.Where(r => r.CompanyId == companyId)]);
        }
    }

    private static ReportedMetricRecord LedgerRecord(
        Guid companyId, Guid evidenceId, ReportedMetric metric, string value, string unit, string period) => new(
        Id: ReportedMetricRecord.IdentityFor("0000100591-26-000011", metric, period, ReportedMetricsPolicy.Version),
        CompanyId: companyId,
        Accession: "0000100591-26-000011",
        EvidenceId: evidenceId,
        FilingDateUtc: InPeriod,
        Form: "8-K",
        Metric: metric,
        Value: value,
        Unit: unit,
        Period: period,
        PriorValue: null,
        PriorPeriod: null,
        Quote: "quote",
        ReaderIdentity: "openai:deepseek",
        Verification: ReportedMetricVerification.Verbatim,
        Policy: ReportedMetricsPolicy.Version);

    private static async Task<Guid> SeedLinkedFilingAsync(Harness h, Guid companyId, Guid snapshotId)
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new EvidenceBuilder()
            .WithId(evidenceId)
            .WithSourceType(EvidenceSourceType.Filing)
            .WithSourceName("Argan — SEC")
            .WithTitle("8-K — Report [items: 2.02]")
            .WithContentHash($"hash-{evidenceId}")
            .Build();
        Assert.True(await h.Evidence.AddIfNewAsync(evidence, default));
        await h.Scores.AddEvidenceLinkAsync(
            new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshotId,
                SignalId: Guid.NewGuid(),
                EvidenceId: evidenceId,
                ContributionReason: "GuidanceChange (Positive), strength 8, novelty 6",
                ContributionWeight: 8),
            default);
        return evidenceId;
    }

    private static async Task<Guid> SeedReportedMetricsSnapshotAsync(Harness h, Guid companyId)
    {
        await h.Companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithName("Argan").WithTicker("AGX").Build(), default);
        var snapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(60)
            .WithCreatedAtUtc(InPeriod)
            .Build();
        await h.Scores.AddSnapshotAsync(snapshot, default);
        return snapshot.Id;
    }

    [Fact]
    public async Task EvidenceRefs_CarryTheLedgerLines_ForTheirOwnEvidenceOnly_ReadOncePerEntry()
    {
        var ledger = new FakeLedger();
        var h = new Harness(reportedMetrics: ledger);
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedReportedMetricsSnapshotAsync(h, companyId);
        var filing = await SeedLinkedFilingAsync(h, companyId, snapshotId);
        var (news, _) = await SeedEvidenceLinkAsync(h, snapshotId);
        ledger.Records.Add(LedgerRecord(companyId, filing, ReportedMetric.Revenue, "384.0", "million", "Q2 FY27"));
        ledger.Records.Add(LedgerRecord(companyId, filing, ReportedMetric.Backlog, "2.518", "billion", "as of 2026-07-31"));
        ledger.Records.Add(LedgerRecord(Guid.NewGuid(), Guid.NewGuid(), ReportedMetric.Revenue, "1", "", "x")); // another company

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        var filingRef = Assert.Single(entry.Evidence, e => e.EvidenceId == filing);
        Assert.Equal(
            [("revenue", "384.0", "million", "Q2 FY27"), ("backlog", "2.518", "billion", "as of 2026-07-31")],
            filingRef.ReportedMetrics!.Select(l => (l.Metric, l.Value, l.Unit, l.Period)).ToList());
        var newsRef = Assert.Single(entry.Evidence, e => e.EvidenceId == news);
        Assert.Null(newsRef.ReportedMetrics);
        Assert.Equal(1, ledger.Reads);
    }

    [Fact]
    public async Task NoLedgerRegistered_LeavesEveryRefNull()
    {
        var h = new Harness();
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedReportedMetricsSnapshotAsync(h, companyId);
        await SeedLinkedFilingAsync(h, companyId, snapshotId);

        await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        Assert.All(entry.Evidence, e => Assert.Null(e.ReportedMetrics));
    }

    [Fact]
    public async Task AFailingLedgerRead_DegradesToNoClause_AndTheReportStillRenders()
    {
        var ledger = new FakeLedger { Throw = true };
        var h = new Harness(reportedMetrics: ledger);
        var companyId = Guid.NewGuid();
        var snapshotId = await SeedReportedMetricsSnapshotAsync(h, companyId);
        await SeedLinkedFilingAsync(h, companyId, snapshotId);

        var result = await h.Builder.GenerateAsync(PeriodEnd, CollectionSummary.Empty, null, default);

        Assert.Single(result.Items);
        var entry = Assert.Single(h.Renderer.LastModel!.Entries);
        Assert.All(entry.Evidence, e => Assert.Null(e.ReportedMetrics));
    }
}
