using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 215 §1 — the append-only reported-metrics ledger: one file per (company, accession), insert-if-new
/// with a typed outcome, deterministic read order, an unreadable file skipped and named, a blank accession
/// refused without a throw, and content-derived record ids so a re-read is a durable no-op.
/// </summary>
public sealed class FileReportedMetricStoreTests : IDisposable
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-reported-metrics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private FileReportedMetricStore NewStore() => new(
        new FileReportedMetricStoreOptions { RootDirectory = _root },
        NullLogger<FileReportedMetricStore>.Instance);

    private static ReportedMetricRecord Record(
        string accession = "0001049521-26-000011",
        ReportedMetric metric = ReportedMetric.Revenue,
        string period = "second quarter of fiscal 2027",
        DateTimeOffset? filed = null,
        Guid? evidenceId = null) => new(
        Id: ReportedMetricRecord.IdentityFor(accession, metric, period),
        CompanyId: Company,
        Accession: accession,
        EvidenceId: evidenceId ?? Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        FilingDateUtc: filed ?? Filed,
        Form: "8-K",
        Metric: metric,
        Value: "384.0",
        Unit: "million",
        Period: period,
        PriorValue: "227.0",
        PriorPeriod: "prior-year quarter",
        Quote: "Revenues of $384.0 million, up 68% from $227.0 million in the prior-year quarter.",
        ReaderIdentity: "openai:deepseek-ai/DeepSeek-V4-Flash",
        Verification: ReportedMetricVerification.Verbatim,
        Policy: ReportedMetricsPolicy.Version);

    [Fact]
    public async Task WriteIfNew_WritesOnce_ThenReportsAlreadyOnDisk_AndTheFileIsNeverOverwritten()
    {
        var store = NewStore();
        var first = await store.WriteIfNewAsync(Company, "0001049521-26-000011", [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, first.Outcome);
        Assert.True(first.Written);
        Assert.EndsWith(Path.Combine(Company.ToString("D"), "0001049521-26-000011.json"), first.Path, StringComparison.Ordinal);
        var bytes = await File.ReadAllTextAsync(first.Path);

        // A second write of the same accession — even with DIFFERENT content — is a durable no-op.
        var second = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", [Record(metric: ReportedMetric.Backlog)], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, second.Outcome);
        Assert.True(second.Written);
        Assert.Equal(bytes, await File.ReadAllTextAsync(second.Path));

        // The on-disk shape is the shared token-based one: enum names, camelCase.
        Assert.Contains("\"metric\": \"Revenue\"", bytes, StringComparison.Ordinal);
        Assert.Contains("\"verification\": \"Verbatim\"", bytes, StringComparison.Ordinal);
        Assert.Contains("\"policy\": \"reported-metrics-v1\"", bytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyRecordList_StillClaimsTheFile()
    {
        // A release that verified nothing is a recorded fact, so the accession is not re-filed later.
        var store = NewStore();
        var write = await store.WriteIfNewAsync(Company, "0001049521-26-000099", [], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, write.Outcome);
        Assert.Empty(await store.GetForCompanyAsync(Company, CancellationToken.None));
        Assert.Equal(
            DurableWriteOutcome.AlreadyAvailable,
            (await store.WriteIfNewAsync(Company, "0001049521-26-000099", [Record()], CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task GetForCompany_ReturnsEveryFilesRecords_InDeterministicOrder_AndEmptyForAnUnknownCompany()
    {
        var store = NewStore();
        var older = Filed.AddMonths(-3);
        await store.WriteIfNewAsync(
            Company,
            "0001049521-26-000005",
            [
                Record("0001049521-26-000005", ReportedMetric.Revenue, "first quarter of fiscal 2027", older),
                Record("0001049521-26-000005", ReportedMetric.Backlog, "as of April 30, 2026", older),
            ],
            CancellationToken.None);
        await store.WriteIfNewAsync(
            Company,
            "0001049521-26-000011",
            [
                Record(metric: ReportedMetric.Backlog, period: "as of July 31, 2026"),
                Record(),
            ],
            CancellationToken.None);

        var records = await store.GetForCompanyAsync(Company, CancellationToken.None);

        // FilingDateUtc DESC, then Metric (enum order), then Period, then Id.
        Assert.Equal(
            [
                (ReportedMetric.Revenue, "second quarter of fiscal 2027"),
                (ReportedMetric.Backlog, "as of July 31, 2026"),
                (ReportedMetric.Revenue, "first quarter of fiscal 2027"),
                (ReportedMetric.Backlog, "as of April 30, 2026"),
            ],
            records.Select(r => (r.Metric, r.Period)).ToList());

        Assert.Empty(await store.GetForCompanyAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreadableLedgerFile_IsSkipped_AndTheOthersStillRead()
    {
        var store = NewStore();
        await store.WriteIfNewAsync(Company, "0001049521-26-000011", [Record()], CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, Company.ToString("D"), "0001049521-26-000012.json"), "{ not json");

        var records = await store.GetForCompanyAsync(Company, CancellationToken.None);

        Assert.Single(records);
    }

    [Fact]
    public async Task AnIntegerMetricOnDisk_MakesTheFileUnreadable_NeverASilentlyDefaultedMetric()
    {
        // The strict enum converter refuses integers, and ReportedMetric has no zero member, so a
        // hand-edited ordinal can never hydrate as a meaningful metric.
        var store = NewStore();
        var write = await store.WriteIfNewAsync(Company, "0001049521-26-000011", [Record()], CancellationToken.None);
        var text = await File.ReadAllTextAsync(write.Path);
        await File.WriteAllTextAsync(
            write.Path, text.Replace("\"metric\": \"Revenue\"", "\"metric\": 1", StringComparison.Ordinal));

        Assert.Empty(await NewStore().GetForCompanyAsync(Company, CancellationToken.None));
    }

    [Fact]
    public async Task ABlankOrInvalidAccession_IsNotPersisted_AndDoesNotThrow()
    {
        var store = NewStore();

        var blank = await store.WriteIfNewAsync(Company, "  ", [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, blank.Outcome);
        Assert.False(blank.Written);

        var invalid = await store.WriteIfNewAsync(Company, "a/b", [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, invalid.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_root, Company.ToString("D"))));
    }

    [Fact]
    public async Task ADiskFailure_IsATypedFailure_NeverAThrow()
    {
        // The ROOT exists as a FILE, so the company directory cannot be created.
        await File.WriteAllTextAsync(_root, "not a directory");
        var store = NewStore();

        var write = await store.WriteIfNewAsync(Company, "0001049521-26-000011", [Record()], CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStore().WriteIfNewAsync(Company, "0001049521-26-000011", [Record()], cts.Token));
    }

    [Fact]
    public void RecordIdentity_IsAPureFunctionOfAccessionMetricAndPeriod()
    {
        Assert.Equal(
            ReportedMetricRecord.IdentityFor("0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026"),
            ReportedMetricRecord.IdentityFor("0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026"));
        Assert.NotEqual(
            ReportedMetricRecord.IdentityFor("0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026"),
            ReportedMetricRecord.IdentityFor("0001049521-26-000011", ReportedMetric.Backlog, "as of April 30, 2026"));
        Assert.NotEqual(
            ReportedMetricRecord.IdentityFor("0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026"),
            ReportedMetricRecord.IdentityFor("0001049521-26-000012", ReportedMetric.Backlog, "as of July 31, 2026"));
    }

    [Fact]
    public void TheClosedEnums_HaveNoZeroMember_AndTheDisplayNamesCarryNoDirectionWord()
    {
        Assert.False(Enum.IsDefined(default(ReportedMetric)));
        Assert.False(Enum.IsDefined(default(ReportedMetricVerification)));
        Assert.Equal("reported-metrics-v1", ReportedMetricsPolicy.Version);

        foreach (var metric in Enum.GetValues<ReportedMetric>())
        {
            var name = ReportedMetricDisplay.NameOf(metric);
            Assert.Equal(name, name.ToLowerInvariant());
            foreach (var banned in new[] { "up", "down", "improv", "deterior", "buy", "sell" })
            {
                Assert.DoesNotContain(" " + banned, " " + name, StringComparison.Ordinal);
            }
        }
    }
}
