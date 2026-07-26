using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// The durable <see cref="ISignalRepository"/> side of spec 142: <see cref="FileSignalStore"/> IS the
/// repository, so a FRESH instance over the same directory sees every signal a previous run persisted —
/// which is what makes spec 136's point-in-time predicate and spec 139's replay mean anything.
/// </summary>
public sealed class FileSignalStoreHydrationTests : IDisposable
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Observed = new(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FileSignalStoreHydrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private FileSignalStore CreateStore() =>
        new(
            new FileSignalStoreOptions { RootDirectory = _tempDir },
            NullLogger<FileSignalStore>.Instance);

    private static SignalReview ReviewFor(Signal signal) => new(
        Id: Guid.NewGuid(),
        SignalId: signal.Id,
        ReviewerName: "deterministic-reviewer-v1",
        Decision: SignalReviewDecision.Approve,
        Summary: "Approve: looks fine.",
        IssuesJson: null,
        ReviewedAtUtc: signal.CreatedAtUtc);

    private async Task<Signal> PersistAsync(FileSignalStore store, Signal signal)
    {
        await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);
        return signal;
    }

    // -------------------------------------------------------------------------------------------------
    // A fresh process sees what a previous run persisted.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FreshInstance_SeesSignalsPersistedByAPreviousRun()
    {
        var run1 = CreateStore();
        var signal = await PersistAsync(run1, new SignalBuilder()
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(Observed.AddDays(1))
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build());

        ISignalRepository run2 = CreateStore();

        Assert.Equal(signal, await run2.GetByIdAsync(signal.Id, CancellationToken.None));
        Assert.Equal([signal], await run2.GetByCompanyAsync(Company, CancellationToken.None));
        Assert.Equal(
            [signal],
            await run2.GetObservedBetweenAsync(
                Observed.AddDays(-1), Observed.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task LegacyFileWithoutCreatedAt_HydratesCreatedAtFromObservedAt()
    {
        // The earliest honest stand-in (the event date), never a fabricated knowledge date — the same rule
        // the previous-window read already documents.
        var dir = Path.Combine(_tempDir, "2026", "02");
        Directory.CreateDirectory(dir);
        var id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), $$"""
            {
              "signalId": "{{id}}",
              "evidenceId": "cccccccc-0000-0000-0000-000000000001",
              "companyId": "{{Company}}",
              "companyMention": "Acme Corp",
              "type": "CustomerWin",
              "direction": "Positive",
              "strength": 6,
              "novelty": 6,
              "confidence": 0.8,
              "supportingExcerpt": "signed a multi-year deal",
              "reason": "Customer win phrase detected.",
              "reviewStatus": "Approved",
              "observedAt": "2026-02-06T09:30:00+00:00",
              "review": {
                "reviewId": "dddddddd-0000-0000-0000-000000000001",
                "signalId": "{{id}}",
                "reviewerName": "r",
                "decision": "Approve",
                "summary": "s",
                "issuesJson": null,
                "reviewedAt": "2026-02-06T09:30:00+00:00"
              }
            }
            """);

        ISignalRepository repo = CreateStore();
        var read = Assert.Single(await repo.GetByCompanyAsync(Company, CancellationToken.None));

        Assert.Equal(Observed, read.ObservedAtUtc);
        Assert.Equal(Observed, read.CreatedAtUtc);
    }

    [Fact]
    public async Task MalformedFile_IsSkipped_AndTheRestStillHydrate()
    {
        var run1 = CreateStore();
        var good = await PersistAsync(run1, new SignalBuilder()
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .Build());

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "2026", "02", "broken.json"), "{ not json");

        ISignalRepository run2 = CreateStore();
        Assert.Equal([good.Id], (await run2.GetByCompanyAsync(Company, CancellationToken.None))
            .Select(s => s.Id).ToArray());
    }

    // -------------------------------------------------------------------------------------------------
    // Cross-run duplicate collapse — the correctness requirement the in-memory repository never faced.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByCompanyAsync_CollapsesCrossRunCopies_KeepingTheEarliestKnown()
    {
        // ONE underlying signal, re-minted with a fresh SignalId + CreatedAt on each of three runs.
        var evidenceId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
        var store = CreateStore();

        // Deliberately: the LATEST copy has the LOWEST Guid, so a lowest-Id tie-break would pick it and
        // this test would fail. The survivor must be chosen by CreatedAtUtc.
        var latest = await PersistAsync(store, Copy("00000000-0000-0000-0000-000000000001", days: 3));
        var earliest = await PersistAsync(store, Copy("ffffffff-0000-0000-0000-000000000003", days: 1));
        await PersistAsync(store, Copy("99999999-0000-0000-0000-000000000002", days: 2));

        ISignalRepository fresh = CreateStore();
        var collapsed = Assert.Single(await fresh.GetByCompanyAsync(Company, CancellationToken.None));

        Assert.Equal(earliest.Id, collapsed.Id);
        Assert.NotEqual(latest.Id, collapsed.Id);

        Signal Copy(string id, int days) => new SignalBuilder()
            .WithId(Guid.Parse(id))
            .WithEvidenceId(evidenceId)
            .WithCompanyId(Company)
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(Observed.AddDays(days))
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build();
    }

    [Fact]
    public async Task GetByCompanyAsync_DoesNotCollapseGenuinelyDistinctSignalsFromOneEvidenceItem()
    {
        var evidenceId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");
        var store = CreateStore();

        await PersistAsync(store, Distinct(SignalType.CustomerWin, SignalDirection.Positive));
        await PersistAsync(store, Distinct(SignalType.GuidanceChange, SignalDirection.Positive));
        await PersistAsync(store, Distinct(SignalType.GuidanceChange, SignalDirection.Neutral));

        ISignalRepository fresh = CreateStore();
        Assert.Equal(3, (await fresh.GetByCompanyAsync(Company, CancellationToken.None)).Count);

        Signal Distinct(SignalType type, SignalDirection direction) => new SignalBuilder()
            .WithEvidenceId(evidenceId)
            .WithCompanyId(Company)
            .WithType(type)
            .WithDirection(direction)
            .WithObservedAtUtc(Observed)
            .Build();
    }

    [Fact]
    public async Task EarliestKnownSurvivor_KeepsTheCopyThatWasActuallyKnownByTheAsOfInstant()
    {
        // The interaction that makes the tie-break load-bearing: the spec-136 predicate
        // (CreatedAtUtc <= windowEndUtc) is applied AFTER this read. Keeping a later-created copy would
        // hide, from an as-of read at T, a signal Radar demonstrably knew about at T.
        var evidenceId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000003");
        var asOf = Observed.AddDays(2);
        var store = CreateStore();

        await PersistAsync(store, Copy("11111111-0000-0000-0000-000000000001", Observed.AddDays(1)));
        await PersistAsync(store, Copy("00000000-0000-0000-0000-000000000002", Observed.AddDays(5)));

        ISignalRepository fresh = CreateStore();
        var collapsed = Assert.Single(await fresh.GetByCompanyAsync(Company, CancellationToken.None));

        Assert.True(
            collapsed.CreatedAtUtc <= asOf,
            "The surviving copy must still satisfy the known-at predicate at the as-of instant.");

        Signal Copy(string id, DateTimeOffset createdAt) => new SignalBuilder()
            .WithId(Guid.Parse(id))
            .WithEvidenceId(evidenceId)
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(createdAt)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build();
    }

    [Fact]
    public async Task GetObservedBetweenAsync_CollapsesCrossRunCopiesToo()
    {
        var evidenceId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000004");
        var store = CreateStore();

        await PersistAsync(store, Copy(Observed.AddDays(1)));
        await PersistAsync(store, Copy(Observed.AddDays(2)));

        ISignalRepository fresh = CreateStore();
        Assert.Single(await fresh.GetObservedBetweenAsync(
            Observed.AddDays(-1), Observed.AddDays(1), CancellationToken.None));

        Signal Copy(DateTimeOffset createdAt) => new SignalBuilder()
            .WithEvidenceId(evidenceId)
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(createdAt)
            .WithReviewStatus(SignalReviewStatus.NeedsHumanReview)
            .Build();
    }

    // -------------------------------------------------------------------------------------------------
    // AddAsync is index-only, by design.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddAsync_WritesNoFile_ButIsImmediatelyReadable()
    {
        // ISignalRepository.AddAsync carries no SignalReview, and the durable format requires one
        // (WriteAsync refuses a mismatched pair). So AddAsync updates the index only; durability keeps
        // coming from the pipeline's ISignalFileStore.WriteAsync call right after it. This preserves both
        // append-only (AD-8) and the review→signal provenance guard.
        var store = CreateStore();
        ISignalRepository repo = store;

        var signal = new SignalBuilder().WithCompanyId(Company).WithObservedAtUtc(Observed).Build();
        await repo.AddAsync(signal, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.json", SearchOption.AllDirectories));
        Assert.Equal(signal, await repo.GetByIdAsync(signal.Id, CancellationToken.None));

        // …and once the runner mirrors it through WriteAsync, the file appears.
        await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(_tempDir, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ProcessLocalWrite_WinsOverItsOwnOnDiskCopy_WhenHydrationRunsLater()
    {
        // Hydration only ever TryAdds, so a signal this process wrote is never clobbered by re-reading it.
        var store = CreateStore();
        var signal = new SignalBuilder()
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build();

        await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);

        ISignalRepository repo = store; // first READ ⇒ hydrates, and finds its own file on disk
        Assert.Equal([signal], await repo.GetByCompanyAsync(Company, CancellationToken.None));
    }

    [Fact]
    public async Task ReadsOverAnEmptyRoot_ReturnEmpty_AndDoNotThrow()
    {
        ISignalRepository repo = new FileSignalStore(
            new FileSignalStoreOptions { RootDirectory = Path.Combine(_tempDir, "does-not-exist") },
            NullLogger<FileSignalStore>.Instance);

        Assert.Empty(await repo.GetByCompanyAsync(Company, CancellationToken.None));
        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 136's known-at predicate, exercised against a HYDRATED durable store.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task KnownAsOf_ExcludesASignalCreatedAfterTheAsOfInstant_OnAHydratedRead()
    {
        // Applied by ScoringEngine over what GetByCompanyAsync returns, so assert it over the real
        // hydrated set rather than trusting the predicate in isolation.
        var store = CreateStore();
        var known = await PersistAsync(store, Sig("aaaaaaaa-1111-0000-0000-000000000001", Observed.AddDays(1)));
        var future = await PersistAsync(store, Sig("aaaaaaaa-1111-0000-0000-000000000002", Observed.AddDays(9)));

        ISignalRepository fresh = CreateStore();
        var all = await fresh.GetByCompanyAsync(Company, CancellationToken.None);

        var asOf = Observed.AddDays(2);
        var visible = all.Where(s => s.CreatedAtUtc <= asOf).Select(s => s.Id).ToArray();

        Assert.Equal([known.Id], visible);
        Assert.Contains(future.Id, all.Select(s => s.Id));

        Signal Sig(string id, DateTimeOffset createdAt) => new SignalBuilder()
            .WithId(Guid.Parse(id))
            .WithEvidenceId(Guid.NewGuid())
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(createdAt)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build();
    }

    [Fact]
    public async Task KnownAsOf_EqualityBoundary_IsANoOpForAForwardRun()
    {
        // AD-7: one run, one instant. This run's signals carry CreatedAtUtc == asOfUtc == windowEndUtc
        // exactly, so the predicate must be satisfied BY EQUALITY or a forward run would score nothing.
        var asOf = Observed.AddDays(3);
        var store = CreateStore();
        var signal = await PersistAsync(store, new SignalBuilder()
            .WithCompanyId(Company)
            .WithObservedAtUtc(Observed)
            .WithCreatedAtUtc(asOf)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build());

        ISignalRepository fresh = CreateStore();
        var all = await fresh.GetByCompanyAsync(Company, CancellationToken.None);

        Assert.Equal([signal.Id], all.Where(s => s.CreatedAtUtc <= asOf).Select(s => s.Id).ToArray());
    }
}
