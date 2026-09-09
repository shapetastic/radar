using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 215 §1 — the append-only reported-metrics ledger: one file per (company, accession, policy),
/// insert-if-new with a typed outcome, deterministic read order, an unreadable file skipped and named, a
/// blank accession refused without a throw, and content-derived record ids so a re-read is a durable no-op.
/// <para>
/// SPEC 216 §4/§5 add: the write commits with a temp file + no-overwrite rename, an existing file that does
/// NOT parse is <see cref="DurableWriteOutcome.Failed"/> (<c>corrupt-existing</c>) and NEVER
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/>, and the POLICY is an explicit argument that sits in
/// the file name and the record id so two policies coexist side by side.
/// </para>
/// </summary>
public sealed class FileReportedMetricStoreTests : IDisposable
{
    private const string V1 = "reported-metrics-v1";

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

    private static string Policy => ReportedMetricsPolicy.Version;

    private static ReportedMetricRecord Record(
        string accession = "0001049521-26-000011",
        ReportedMetric metric = ReportedMetric.Revenue,
        string period = "second quarter of fiscal 2027",
        DateTimeOffset? filed = null,
        Guid? evidenceId = null,
        string? policy = null) => new(
        Id: ReportedMetricRecord.IdentityFor(accession, metric, period, policy ?? Policy),
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
        Policy: policy ?? Policy);

    [Fact]
    public async Task WriteIfNew_WritesOnce_ThenReportsAlreadyOnDisk_AndTheFileIsNeverOverwritten()
    {
        var store = NewStore();
        var first = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, first.Outcome);
        Assert.True(first.Written);

        // Spec 216 §5: the POLICY is in the file name, which is what lets a later policy write beside it.
        Assert.EndsWith(
            Path.Combine(Company.ToString("D"), $"0001049521-26-000011.{Policy}.json"),
            first.Path,
            StringComparison.Ordinal);
        var bytes = await File.ReadAllTextAsync(first.Path);

        // A second write of the same accession + policy — even with DIFFERENT content — is a durable no-op.
        var second = await store.WriteIfNewAsync(
            Company,
            "0001049521-26-000011",
            Policy,
            [Record(metric: ReportedMetric.Backlog)],
            CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, second.Outcome);
        Assert.True(second.Written);
        Assert.Equal(bytes, await File.ReadAllTextAsync(second.Path));

        // The on-disk shape is the shared token-based one: enum names, camelCase.
        Assert.Contains("\"metric\": \"Revenue\"", bytes, StringComparison.Ordinal);
        Assert.Contains("\"verification\": \"Verbatim\"", bytes, StringComparison.Ordinal);
        Assert.Contains($"\"policy\": \"{Policy}\"", bytes, StringComparison.Ordinal);

        // No temp file survives a successful commit.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, Company.ToString("D")), "*.tmp"));
    }

    [Fact]
    public async Task TwoPolicies_CoexistSideBySide_AndNeitherIsOverwritten()
    {
        // SPEC 216 §5 — the migration story the v1 layout could not keep: a re-analysis under a LATER
        // policy used to collide with the v1 file and be reported AlreadyOnDisk forever. Now it is a new
        // file beside the old one, and nothing is deleted (append-only).
        var store = NewStore();

        var v1 = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", V1, [Record(policy: V1)], CancellationToken.None);
        var v2 = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, v1.Outcome);
        Assert.Equal(DurableWriteOutcome.Written, v2.Outcome);
        Assert.NotEqual(v1.Path, v2.Path);
        Assert.True(File.Exists(v1.Path));
        Assert.True(File.Exists(v2.Path));

        // The store returns BOTH policies' records; filtering to the current one is the projector's job.
        var records = await store.GetForCompanyAsync(Company, CancellationToken.None);
        Assert.Equal([V1, Policy], records.Select(r => r.Policy).Order(StringComparer.Ordinal).ToList());

        // …and the ids differ, so the two are distinct records rather than one record written twice.
        Assert.Equal(2, records.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task AnExistingFileThatDoesNotParse_IsNotPersisted_NeverAlreadyOnDisk()
    {
        // SPEC 216 §4 — THE DEFECT THIS CLOSES. v1 wrote straight to the final path and then reported any
        // IOException over an existing file as AlreadyOnDisk, so a half-written file it had created itself
        // was reported as a concurrent writer's success and every later run treated the fragment as the
        // record. A fragment must be NotPersisted so the outbox retries it.
        var store = NewStore();
        var directory = Path.Combine(_root, Company.ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"0001049521-26-000011.{Policy}.json");
        await File.WriteAllTextAsync(path, "[{ \"metric\": \"Reven");

        var write = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);

        // Nothing was overwritten or deleted: recovery is a conscious maintainer action.
        Assert.Equal("[{ \"metric\": \"Reven", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AnEmptyRecordList_StillClaimsTheFile()
    {
        // A release that verified nothing is a recorded fact, so the accession is not re-filed later — and
        // the POLICY still has to be supplied, because an empty list carries no record to infer it from.
        var store = NewStore();
        var write = await store.WriteIfNewAsync(
            Company, "0001049521-26-000099", Policy, [], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Written, write.Outcome);
        Assert.Empty(await store.GetForCompanyAsync(Company, CancellationToken.None));
        Assert.Equal(
            DurableWriteOutcome.AlreadyAvailable,
            (await store.WriteIfNewAsync(
                Company, "0001049521-26-000099", Policy, [Record()], CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task GetForCompany_ReturnsEveryFilesRecords_InDeterministicOrder_AndEmptyForAnUnknownCompany()
    {
        var store = NewStore();
        var older = Filed.AddMonths(-3);
        await store.WriteIfNewAsync(
            Company,
            "0001049521-26-000005",
            Policy,
            [
                Record("0001049521-26-000005", ReportedMetric.Revenue, "first quarter of fiscal 2027", older),
                Record("0001049521-26-000005", ReportedMetric.Backlog, "as of April 30, 2026", older),
            ],
            CancellationToken.None);
        await store.WriteIfNewAsync(
            Company,
            "0001049521-26-000011",
            Policy,
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
        await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, Company.ToString("D"), $"0001049521-26-000012.{Policy}.json"), "{ not json");

        var records = await store.GetForCompanyAsync(Company, CancellationToken.None);

        Assert.Single(records);
    }

    [Fact]
    public async Task AnIntegerMetricOnDisk_MakesTheFileUnreadable_NeverASilentlyDefaultedMetric()
    {
        // The strict enum converter refuses integers, and ReportedMetric has no zero member, so a
        // hand-edited ordinal can never hydrate as a meaningful metric.
        var store = NewStore();
        var write = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);
        var text = await File.ReadAllTextAsync(write.Path);
        await File.WriteAllTextAsync(
            write.Path, text.Replace("\"metric\": \"Revenue\"", "\"metric\": 1", StringComparison.Ordinal));

        Assert.Empty(await NewStore().GetForCompanyAsync(Company, CancellationToken.None));
    }

    [Fact]
    public async Task ABlankOrInvalidAccession_IsNotPersisted_AndDoesNotThrow()
    {
        var store = NewStore();

        var blank = await store.WriteIfNewAsync(Company, "  ", Policy, [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, blank.Outcome);
        Assert.False(blank.Written);

        var invalid = await store.WriteIfNewAsync(Company, "a/b", Policy, [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, invalid.Outcome);

        var blankPolicy = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", "  ", [Record()], CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.Failed, blankPolicy.Outcome);

        Assert.False(Directory.Exists(Path.Combine(_root, Company.ToString("D"))));
    }

    [Fact]
    public async Task ADiskFailure_IsATypedFailure_NeverAThrow()
    {
        // The ROOT exists as a FILE, so the company directory cannot be created.
        await File.WriteAllTextAsync(_root, "not a directory");
        var store = NewStore();

        var write = await store.WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);
    }

    [Fact]
    public async Task AFailedWrite_LeavesNoTempFileBehind()
    {
        // SPEC 216 §4 - the OTHER half of the atomic-write guarantee, pinned here because only the SUCCESS
        // path was covered: when the commit fails, nothing partial and nothing inert may survive. A
        // DIRECTORY occupying the target path lets the temp file be created (so the finally-block cleanup
        // is genuinely exercised) and then fails the rename, which is the closest reproducible stand-in for
        // a disk failure mid-commit.
        var companyDirectory = Path.Combine(_root, Company.ToString("D"));
        var fileName = FileReportedMetricStore.FileNameFor("0001049521-26-000011", Policy);
        Assert.NotNull(fileName);
        Directory.CreateDirectory(Path.Combine(companyDirectory, fileName!));

        var write = await NewStore().WriteIfNewAsync(
            Company, "0001049521-26-000011", Policy, [Record()], CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);

        // The temp name is a bare {guid}.tmp in the same directory, so this enumeration would catch it.
        Assert.Empty(Directory.GetFiles(companyDirectory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(companyDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStore().WriteIfNewAsync(Company, "0001049521-26-000011", Policy, [Record()], cts.Token));
    }

    [Fact]
    public void RecordIdentity_IsAPureFunctionOfAccessionMetricPeriodAndPolicy()
    {
        Assert.Equal(
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", Policy),
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", Policy));
        Assert.NotEqual(
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", Policy),
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of April 30, 2026", Policy));
        Assert.NotEqual(
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", Policy),
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000012", ReportedMetric.Backlog, "as of July 31, 2026", Policy));

        // SPEC 216 §5: the POLICY is part of the identity, so a re-analysis under a later policy is a NEW
        // record rather than a colliding one.
        Assert.NotEqual(
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", Policy),
            ReportedMetricRecord.IdentityFor(
                "0001049521-26-000011", ReportedMetric.Backlog, "as of July 31, 2026", V1));
    }

    [Fact]
    public void StatedPriorIdentity_IsDistinctFromTheRecordsOwnId_AndNullWhenThePairIsIncomplete()
    {
        // SPEC 216 §1: the newest filing's STATED prior pair is its own citable reference, so it must not
        // share the current value's id — otherwise "which figure did the judge compare against" is
        // unanswerable.
        var complete = Record();
        Assert.NotNull(complete.StatedPriorIdentity);
        Assert.NotEqual(complete.Id, complete.StatedPriorIdentity!.Value);

        Assert.Null((complete with { PriorValue = null }).StatedPriorIdentity);
        Assert.Null((complete with { PriorPeriod = null }).StatedPriorIdentity);

        // …and it is INJECTIVE over records. Two rows of ONE release can state the same metric for two
        // periods against the SAME stated prior period ("first six months" and "second quarter", both
        // "compared with the prior-year period"). Keying the stated prior on (policy, accession, metric,
        // PRIOR period) collapsed those two figures onto one ReferenceId, which the id-keyed lookups over
        // the projected references throw on. Deriving it from the record's own Id cannot collide.
        var quarter = Record(metric: ReportedMetric.Revenue, period: "second quarter of fiscal 2027");
        var halfYear = quarter with
        {
            Id = ReportedMetricRecord.IdentityFor(
                quarter.Accession, ReportedMetric.Revenue, "first six months of fiscal 2027", Policy),
            Period = "first six months of fiscal 2027",
            Value = "701.0",
        };

        Assert.NotEqual(quarter.Id, halfYear.Id);
        Assert.Equal(quarter.PriorPeriod, halfYear.PriorPeriod);
        Assert.NotEqual(quarter.StatedPriorIdentity, halfYear.StatedPriorIdentity);

        // The stated prior stays content-derived: the SAME record re-read is the same reference, and a
        // re-worded prior period is a new one (matching IdentityFor's stance on the record's own period).
        Assert.Equal(quarter.StatedPriorIdentity, Record().StatedPriorIdentity);
        Assert.NotEqual(
            quarter.StatedPriorIdentity,
            (quarter with { PriorPeriod = "second quarter of fiscal 2026" }).StatedPriorIdentity);
    }

    [Fact]
    public void TheClosedEnums_HaveNoZeroMember_AndTheDisplayNamesCarryNoDirectionWord()
    {
        Assert.False(Enum.IsDefined(default(ReportedMetric)));
        Assert.False(Enum.IsDefined(default(ReportedMetricVerification)));

        // SPEC 216 §3 moved the policy token: the verification rule is part of it.
        Assert.Equal("reported-metrics-v2", ReportedMetricsPolicy.Version);
        Assert.Equal("disabled", ReportedMetricsPolicy.DisabledToken);

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
