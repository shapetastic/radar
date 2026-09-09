using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// SPEC 216 §2 — the durable reported-metrics OUTBOX on disk: an envelope carries the COMPLETE
/// ready-to-write ledger payload, is enumerable while pending, records its attempts durably, is KEPT (moved,
/// never deleted) once acknowledged, and answers "does an envelope exist for this accession?" so a cache
/// policy stamp with nothing behind it can fail closed.
/// </summary>
public sealed class FileReportedMetricOutboxTests : IDisposable
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);
    private const string Accession = "0001049521-26-000011";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-reported-metrics-outbox-" + Guid.NewGuid().ToString("N"));

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

    private FileReportedMetricOutbox NewOutbox() => new(
        new FileReportedMetricStoreOptions { RootDirectory = _root },
        NullLogger<FileReportedMetricOutbox>.Instance);

    private static ReportedMetricOutboxEnvelope Envelope(
        Guid? companyId = null, string accession = Accession, string? policy = null, bool unresolved = false) => new(
        OutboxId: ReportedMetricOutboxEnvelope.IdentityFor(
            policy ?? ReportedMetricsPolicy.Version, accession),
        Policy: policy ?? ReportedMetricsPolicy.Version,
        CompanyId: unresolved ? null : companyId ?? Company,
        CompanyMention: "Argan, Inc.",
        CompanyHints: ["AGX"],
        EvidenceId: Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        Accession: accession,
        Form: "8-K",
        FilingDateUtc: Filed,
        ReaderIdentity: "openai:deepseek-ai/DeepSeek-V4-Flash",
        Metrics:
        [
            new ReportedMetricReading(
                ReportedMetric.Revenue, "384.0", "million", "Q2 FY27", "227.0", "prior-year quarter",
                "Revenues for the second quarter of fiscal 2027 were $384.0 million."),
        ],
        DroppedUnrecognised: 1,
        DroppedUnverified: 2,
        DroppedDuplicate: 3,
        PriorPairsDroppedIncomplete: 4,
        DroppedMetricNotInQuote: 5,
        DroppedPeriodNotInQuote: 6,
        DroppedFragment: 7,
        DroppedNotAssociated: 8,
        State: ReportedMetricOutboxState.Pending,
        Attempts: 0,
        CreatedAtUtc: Filed,
        LastAttemptAtUtc: null);

    [Fact]
    public async Task Enqueue_PersistsTheCompletePayload_AndItRoundTripsFieldForField()
    {
        // The whole point of the envelope: replay must need NOTHING it does not already carry — no fetch,
        // no model call, no re-derivation. If a field does not round-trip, a replay is not the same write.
        var outbox = NewOutbox();
        var envelope = Envelope();

        var write = await outbox.EnqueueAsync(envelope, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, write.Outcome);
        var stored = Assert.Single(await outbox.EnumeratePendingAsync(CancellationToken.None));
        // Field for field (record equality would compare the LIST INSTANCES, which never survive a
        // round-trip) — every routing field a ledger record needs must come back exactly as written.
        Assert.Equal(envelope.OutboxId, stored.OutboxId);
        Assert.Equal(envelope.Policy, stored.Policy);
        Assert.Equal(envelope.CompanyId, stored.CompanyId);
        Assert.Equal(envelope.CompanyMention, stored.CompanyMention);
        Assert.Equal(envelope.CompanyHints, stored.CompanyHints);
        Assert.Equal(envelope.EvidenceId, stored.EvidenceId);
        Assert.Equal(envelope.Accession, stored.Accession);
        Assert.Equal(envelope.Form, stored.Form);
        Assert.Equal(envelope.FilingDateUtc, stored.FilingDateUtc);
        Assert.Equal(envelope.ReaderIdentity, stored.ReaderIdentity);
        Assert.Equal(envelope.Metrics, stored.Metrics);
        Assert.Equal(envelope.DroppedUnrecognised, stored.DroppedUnrecognised);
        Assert.Equal(envelope.DroppedUnverified, stored.DroppedUnverified);
        Assert.Equal(envelope.DroppedDuplicate, stored.DroppedDuplicate);
        Assert.Equal(envelope.PriorPairsDroppedIncomplete, stored.PriorPairsDroppedIncomplete);
        Assert.Equal(envelope.DroppedMetricNotInQuote, stored.DroppedMetricNotInQuote);
        Assert.Equal(envelope.DroppedPeriodNotInQuote, stored.DroppedPeriodNotInQuote);
        Assert.Equal(envelope.DroppedFragment, stored.DroppedFragment);
        Assert.Equal(envelope.DroppedNotAssociated, stored.DroppedNotAssociated);
        Assert.Equal(envelope.State, stored.State);
        Assert.Equal(envelope.Attempts, stored.Attempts);
        Assert.Equal(envelope.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(envelope.LastAttemptAtUtc, stored.LastAttemptAtUtc);

        // …and the records it produces are the ledger's, built in ONE place so no second construction site
        // can drift from it.
        var records = stored.ToRecords();
        Assert.NotNull(records);
        var record = Assert.Single(records!);
        Assert.Equal(
            ReportedMetricRecord.IdentityFor(
                Accession, ReportedMetric.Revenue, "Q2 FY27", ReportedMetricsPolicy.Version),
            record.Id);
        Assert.Equal(Company, record.CompanyId);
        Assert.Equal(Filed, record.FilingDateUtc);
        Assert.Equal(ReportedMetricVerification.Verbatim, record.Verification);
        Assert.Equal(ReportedMetricsPolicy.Version, record.Policy);
    }

    [Fact]
    public async Task Enqueue_IsInsertIfNew_AndAnAcknowledgedAccessionIsNeverResurrected()
    {
        var outbox = NewOutbox();
        var envelope = Envelope();
        await outbox.EnqueueAsync(envelope, CancellationToken.None);

        var again = await outbox.EnqueueAsync(envelope, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, again.Outcome);
        Assert.True(again.Written);

        Assert.True(await outbox.AcknowledgeAsync(envelope, CancellationToken.None));

        // Acknowledged work is DONE, not pending — and re-enqueueing it must not resurrect it.
        Assert.Empty(await outbox.EnumeratePendingAsync(CancellationToken.None));
        var afterAck = await outbox.EnqueueAsync(envelope, CancellationToken.None);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, afterAck.Outcome);
        Assert.Empty(await outbox.EnumeratePendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Acknowledge_KeepsTheEnvelope_SoTheProvenanceChainStaysWalkable()
    {
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Envelope(), CancellationToken.None);

        Assert.True(await outbox.AcknowledgeAsync(Envelope(), CancellationToken.None));

        // Append-only: it MOVED, it was not deleted, and the kept copy says it is acknowledged.
        var acknowledged = Directory.GetFiles(
            Path.Combine(_root, "outbox", "acknowledged"), "*.json", SearchOption.TopDirectoryOnly);
        var kept = JsonSerializer.Deserialize<ReportedMetricOutboxEnvelope>(
            await File.ReadAllTextAsync(Assert.Single(acknowledged)),
            Radar.Infrastructure.FileSystem.RadarFileStoreJson.Options);
        Assert.NotNull(kept);
        Assert.Equal(ReportedMetricOutboxState.Acknowledged, kept!.State);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "outbox", "pending")));
    }

    [Fact]
    public async Task MarkAttempt_PersistsTheCount_SoTheThreeAttemptWarningSurvivesARestart()
    {
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Envelope(), CancellationToken.None);

        var first = await outbox.MarkAttemptAsync(Envelope(), Filed.AddDays(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Attempts);
        Assert.Equal(Filed.AddDays(1), first.LastAttemptAtUtc);

        // A FRESH outbox instance over the same directory reads the count back — the whole reason it is
        // persisted rather than held in memory.
        var reloaded = Assert.Single(await NewOutbox().EnumeratePendingAsync(CancellationToken.None));
        Assert.Equal(1, reloaded.Attempts);

        var second = await outbox.MarkAttemptAsync(reloaded, Filed.AddDays(2), CancellationToken.None);
        Assert.Equal(2, second!.Attempts);
        Assert.Equal(2, Assert.Single(await NewOutbox().EnumeratePendingAsync(CancellationToken.None)).Attempts);
    }

    [Fact]
    public async Task AnUnresolvedCompanyEnvelope_LivesInItsOwnArea_AndDoesNotBlockTheResolvedOne()
    {
        // The unresolved copy is REPLACED by a resolved one, not blocked by it: the enqueue guard is
        // area-scoped, so a company that resolves on a later pass can be re-enqueued under itself.
        var outbox = NewOutbox();
        var unresolved = Envelope(unresolved: true);
        Assert.Equal(
            DurableWriteOutcome.Written, (await outbox.EnqueueAsync(unresolved, CancellationToken.None)).Outcome);
        Assert.True(File.Exists(
            Path.Combine(_root, "outbox", "unresolved", $"{Accession}.{ReportedMetricsPolicy.Version}.json")));

        var resolved = unresolved with { CompanyId = Company };
        Assert.Equal(
            DurableWriteOutcome.Written, (await outbox.EnqueueAsync(resolved, CancellationToken.None)).Outcome);
        Assert.True(await outbox.AcknowledgeAsync(unresolved, CancellationToken.None));

        var pending = Assert.Single(await outbox.EnumeratePendingAsync(CancellationToken.None));
        Assert.Equal(Company, pending.CompanyId);

        // Both acknowledgements can coexist: the area prefix keeps them from colliding.
        Assert.True(await outbox.AcknowledgeAsync(resolved, CancellationToken.None));
        Assert.Equal(
            2,
            Directory.GetFiles(Path.Combine(_root, "outbox", "acknowledged"), "*.json").Length);
    }

    [Fact]
    public async Task EnumeratePending_IsDeterministic_UnresolvedFirstThenCompanyThenAccession()
    {
        var outbox = NewOutbox();
        var later = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        await outbox.EnqueueAsync(Envelope(companyId: later, accession: "0001049521-26-000020"), CancellationToken.None);
        await outbox.EnqueueAsync(Envelope(companyId: Company, accession: "0001049521-26-000012"), CancellationToken.None);
        await outbox.EnqueueAsync(Envelope(companyId: Company, accession: "0001049521-26-000011"), CancellationToken.None);
        await outbox.EnqueueAsync(Envelope(accession: "0001049521-26-000099", unresolved: true), CancellationToken.None);

        var pending = await outbox.EnumeratePendingAsync(CancellationToken.None);

        Assert.Equal(
            [
                "0001049521-26-000099", // unresolved sorts first — the work most in need of another try
                "0001049521-26-000011",
                "0001049521-26-000012",
                "0001049521-26-000020",
            ],
            pending.Select(e => e.Accession).ToList());
    }

    [Fact]
    public async Task Exists_AnswersForPendingAndAcknowledged_SoAStampWithNoEnvelopeIsVisible()
    {
        var outbox = NewOutbox();

        Assert.False(await outbox.ExistsAsync(ReportedMetricsPolicy.Version, Accession, CancellationToken.None));

        await outbox.EnqueueAsync(Envelope(), CancellationToken.None);
        Assert.True(await outbox.ExistsAsync(ReportedMetricsPolicy.Version, Accession, CancellationToken.None));

        await outbox.AcknowledgeAsync(Envelope(), CancellationToken.None);
        Assert.True(await outbox.ExistsAsync(ReportedMetricsPolicy.Version, Accession, CancellationToken.None));

        // …and a DIFFERENT policy is a different envelope, exactly as it is a different ledger file.
        Assert.False(await outbox.ExistsAsync("reported-metrics-v1", Accession, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreadableEnvelopeFile_IsSkipped_NotReplayed_AndTheOthersStillEnumerate()
    {
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Envelope(), CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "outbox", "pending", $"0001049521-26-000012.{ReportedMetricsPolicy.Version}.json"),
            "{ not json");

        var pending = await outbox.EnumeratePendingAsync(CancellationToken.None);

        Assert.Equal(Accession, Assert.Single(pending).Accession);
    }

    [Fact]
    public async Task ABlankAccession_IsNotPersisted_AndDoesNotThrow()
    {
        var write = await NewOutbox().EnqueueAsync(Envelope() with { Accession = "  " }, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.False(write.Written);
    }

    [Fact]
    public async Task ADiskFailure_IsATypedFailure_NeverAThrow()
    {
        // The ROOT exists as a FILE, so no outbox directory can be created.
        await File.WriteAllTextAsync(_root, "not a directory");

        var write = await NewOutbox().EnqueueAsync(Envelope(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, write.Outcome);
        Assert.Empty(await NewOutbox().EnumeratePendingAsync(CancellationToken.None));
    }

    [Fact]
    public void TheEnvelopeIdentity_FoldsThePolicy_AndTheStateDefaultIsPending()
    {
        Assert.Equal(
            ReportedMetricOutboxEnvelope.IdentityFor("reported-metrics-v2", Accession),
            ReportedMetricOutboxEnvelope.IdentityFor("reported-metrics-v2", Accession));
        Assert.NotEqual(
            ReportedMetricOutboxEnvelope.IdentityFor("reported-metrics-v2", Accession),
            ReportedMetricOutboxEnvelope.IdentityFor("reported-metrics-v1", Accession));

        // The DEGRADED default must be "retry", never "done": a record whose state cannot be read is
        // replayed rather than assumed acknowledged.
        Assert.Equal(ReportedMetricOutboxState.Pending, default(ReportedMetricOutboxState));

        // An unresolved envelope has nothing to file under and must never produce records.
        Assert.Null(Envelope(unresolved: true).ToRecords());
    }
}
