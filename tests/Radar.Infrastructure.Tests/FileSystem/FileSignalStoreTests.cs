using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileSignalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Observed = new(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FileSignalStoreTests()
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
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private FileSignalStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileSignalStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileSignalStore>.Instance);

    private static SignalReview ReviewFor(
        Signal signal,
        SignalReviewDecision decision = SignalReviewDecision.Approve,
        string summary = "Material customer win, well evidenced.") =>
        new(
            Id: Guid.NewGuid(),
            SignalId: signal.Id,
            ReviewerName: "DeterministicSignalReviewer",
            Decision: decision,
            Summary: summary,
            IssuesJson: null,
            ReviewedAtUtc: new DateTimeOffset(2026, 2, 7, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task WriteAsync_NewSignal_WritesFileAtExpectedPathAndRoundTrips()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithCompanyMention("Northwind Robotics")
            .WithSupportingExcerpt("major new customer win")
            .WithObservedAtUtc(Observed)
            .Build();
        var review = ReviewFor(signal);

        var store = CreateStore();
        var path = await store.WriteAsync(signal, review, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "2026", "02", signal.Id + ".json");
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        await using var stream = File.OpenRead(expectedPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(signal.Id.ToString(), root.GetProperty("signalId").GetString());
        Assert.Equal(signal.EvidenceId.ToString(), root.GetProperty("evidenceId").GetString());
        Assert.Equal(signal.CompanyId!.Value.ToString(), root.GetProperty("companyId").GetString());
        Assert.Equal("Northwind Robotics", root.GetProperty("companyMention").GetString());
        Assert.Equal("major new customer win", root.GetProperty("supportingExcerpt").GetString());
        Assert.Equal(signal.Reason, root.GetProperty("reason").GetString());

        // Embedded review traces back to the signal (provenance) and carries the decision/summary.
        var reviewElement = root.GetProperty("review");
        Assert.Equal(review.Id.ToString(), reviewElement.GetProperty("reviewId").GetString());
        Assert.Equal(signal.Id.ToString(), reviewElement.GetProperty("signalId").GetString());
        Assert.Equal("DeterministicSignalReviewer", reviewElement.GetProperty("reviewerName").GetString());
        Assert.Equal("Approve", reviewElement.GetProperty("decision").GetString());
        Assert.Equal(review.Summary, reviewElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task WriteAsync_CalledTwiceForSameSignalId_IsOverwriteAllowedLastWriteWins()
    {
        var id = Guid.NewGuid();
        var first = new SignalBuilder()
            .WithId(id)
            .WithReviewStatus(SignalReviewStatus.Pending)
            .WithObservedAtUtc(Observed)
            .Build();
        var second = first with { ReviewStatus = SignalReviewStatus.Approved };

        var store = CreateStore();
        await store.WriteAsync(first, ReviewFor(first, SignalReviewDecision.EscalateToHuman), CancellationToken.None);
        await store.WriteAsync(second, ReviewFor(second, SignalReviewDecision.Approve), CancellationToken.None);

        // Exactly one file for the signal id — proves it is NOT insert-only.
        var dir = Path.Combine(_tempDir, "2026", "02");
        var files = Directory.GetFiles(dir);
        Assert.Single(files);

        await using var stream = File.OpenRead(Path.Combine(dir, id + ".json"));
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        // Contents reflect the second write (last-write-wins).
        Assert.Equal("Approved", root.GetProperty("reviewStatus").GetString());
        Assert.Equal("Approve", root.GetProperty("review").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WriteAsync_PersistsEnumsAsReadableStringNames()
    {
        var signal = new SignalBuilder()
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(Observed)
            .Build();
        var review = ReviewFor(signal, SignalReviewDecision.Approve);

        var path = await CreateStore().WriteAsync(signal, review, CancellationToken.None);

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("type").ValueKind);
        Assert.Equal("CustomerWin", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("direction").ValueKind);
        Assert.Equal("Positive", root.GetProperty("direction").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("reviewStatus").ValueKind);
        Assert.Equal("Approved", root.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("review").GetProperty("decision").ValueKind);
        Assert.Equal("Approve", root.GetProperty("review").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WriteAsync_ReviewBelongsToDifferentSignal_ThrowsAndWritesNothing()
    {
        var signal = new SignalBuilder().WithObservedAtUtc(Observed).Build();
        var otherSignal = new SignalBuilder().WithObservedAtUtc(Observed).Build();
        // Review targets a different signal id — persisting it would break the review→signal trace.
        var mismatchedReview = ReviewFor(otherSignal);

        var store = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(signal, mismatchedReview, CancellationToken.None));
        Assert.Equal("review", ex.ParamName);

        // Nothing was written for either signal.
        Assert.False(File.Exists(Path.Combine(_tempDir, "2026", "02", signal.Id + ".json")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "2026", "02", otherSignal.Id + ".json")));
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var signal = new SignalBuilder().WithObservedAtUtc(Observed).Build();
        var review = ReviewFor(signal);

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(signal, review, CancellationToken.None);

        // The attempted path is returned (no throw); the in-memory copy still exists in production.
        var expectedPath = Path.Combine(rootAsFile, "2026", "02", signal.Id + ".json");
        Assert.Equal(expectedPath, path);
    }

    private static Signal SignalFor(
        Guid companyId,
        DateTimeOffset observedAt,
        SignalReviewStatus status = SignalReviewStatus.Approved) =>
        new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithReviewStatus(status)
            .WithObservedAtUtc(observedAt)
            .Build();

    [Fact]
    public async Task ReadApprovedInWindow_ReturnsInWindowApproved_OrderedAndBoundaryHonoured()
    {
        var companyId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        // Exactly at start -> EXCLUDED (exclusive start).
        var atStart = SignalFor(companyId, start);
        // Inside the window (two, seeded out of chronological order to prove ordering).
        var later = SignalFor(companyId, new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero));
        var earlier = SignalFor(companyId, new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero));
        // Exactly at end -> INCLUDED (inclusive end).
        var atEnd = SignalFor(companyId, end);
        // After the end -> EXCLUDED.
        var afterEnd = SignalFor(companyId, new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));

        foreach (var s in new[] { atStart, later, earlier, atEnd, afterEnd })
        {
            await store.WriteAsync(s, ReviewFor(s), CancellationToken.None);
        }

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        // In-window (start, end]: earlier, later, atEnd — ordered by ObservedAtUtc.
        Assert.Equal(
            new[] { earlier.Id, later.Id, atEnd.Id },
            result.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(result, s => s.Id == atStart.Id);
        Assert.DoesNotContain(result, s => s.Id == afterEnd.Id);
    }

    [Fact]
    public async Task ReadApprovedInWindow_ExcludesNonApproved()
    {
        var companyId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        var approved = SignalFor(companyId, inWindow);
        var needsReview = SignalFor(companyId, inWindow, SignalReviewStatus.NeedsHumanReview);
        var rejected = SignalFor(companyId, inWindow, SignalReviewStatus.Rejected);
        var pending = SignalFor(companyId, inWindow, SignalReviewStatus.Pending);

        foreach (var s in new[] { approved, needsReview, rejected, pending })
        {
            await store.WriteAsync(s, ReviewFor(s), CancellationToken.None);
        }

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        var only = Assert.Single(result);
        Assert.Equal(approved.Id, only.Id);
    }

    [Fact]
    public async Task ReadApprovedInWindow_NoMatches_ReturnsEmpty()
    {
        var companyId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        // An Approved signal OUTSIDE the window for this company.
        var outside = SignalFor(companyId, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await store.WriteAsync(outside, ReviewFor(outside), CancellationToken.None);

        // Window with no matching signals.
        Assert.Empty(await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None));

        // Unknown company (no matching files).
        Assert.Empty(await store.ReadApprovedInWindowAsync(
            Guid.NewGuid(), start, end, CancellationToken.None));

        // Root directory that does not exist.
        var missingRootStore = CreateStore(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(await missingRootStore.ReadApprovedInWindowAsync(
            companyId, start, end, CancellationToken.None));
    }

    [Fact]
    public async Task ReadApprovedInWindow_SkipsMalformedFile_ReturnsValid()
    {
        var companyId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        var valid = SignalFor(companyId, inWindow);
        await store.WriteAsync(valid, ReviewFor(valid), CancellationToken.None);

        // Drop a garbage *.json into the same {yyyy}/{MM} directory as the valid signal.
        var garbagePath = Path.Combine(_tempDir, "2026", "02", "garbage.json");
        await File.WriteAllTextAsync(garbagePath, "{ this is not valid json ]");

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        var only = Assert.Single(result);
        Assert.Equal(valid.Id, only.Id);
    }

    private static Signal DuplicateSignalFor(
        Guid companyId,
        Guid evidenceId,
        DateTimeOffset observedAt,
        SignalType type = SignalType.CustomerWin,
        SignalDirection direction = SignalDirection.Positive,
        int strength = 6,
        Guid? id = null,
        DateTimeOffset? createdAt = null) =>
        new SignalBuilder()
            .WithId(id ?? Guid.NewGuid())
            .WithEvidenceId(evidenceId)
            .WithCompanyId(companyId)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAt)
            .WithCreatedAtUtc(createdAt ?? new DateTimeOffset(2026, 2, 7, 12, 0, 0, TimeSpan.Zero))
            .Build();

    [Fact]
    public async Task ReadApprovedInWindow_CrossRunDuplicatesOfOneIdentity_CountedOnce()
    {
        // Three cross-run copies of the SAME underlying signal: identical EvidenceId, Type, Direction,
        // Strength, ObservedAt — differing ONLY in SignalId (fresh Guid per run) and CreatedAt. The read
        // must collapse them to exactly one so the activity-only previous window is not run-count-inflated.
        var companyId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();
        for (var run = 0; run < 3; run++)
        {
            var copy = DuplicateSignalFor(
                companyId, evidenceId, inWindow,
                createdAt: new DateTimeOffset(2026, 2, 6, run, 0, 0, TimeSpan.Zero));
            await store.WriteAsync(copy, ReviewFor(copy), CancellationToken.None);
        }

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        var only = Assert.Single(result);
        Assert.Equal(evidenceId, only.EvidenceId);
    }

    [Fact]
    public async Task ReadApprovedInWindow_DistinctSignals_AreNotCollapsed()
    {
        // Genuinely-distinct signals one evidence item can produce, plus a same-(Type,Direction) signal on a
        // DIFFERENT evidence — none may be collapsed. The identity key is (CompanyId, EvidenceId, Type,
        // Direction), so all four survive.
        var companyId = Guid.NewGuid();
        var evidenceA = Guid.NewGuid();
        var evidenceB = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        // Same evidence, differing Type.
        var customerWin = DuplicateSignalFor(companyId, evidenceA, inWindow, SignalType.CustomerWin, SignalDirection.Positive);
        var guidanceChange = DuplicateSignalFor(companyId, evidenceA, inWindow, SignalType.GuidanceChange, SignalDirection.Positive);
        // Same evidence + Type, differing Direction.
        var guidanceNeutral = DuplicateSignalFor(companyId, evidenceA, inWindow, SignalType.GuidanceChange, SignalDirection.Neutral);
        // Same (Type, Direction) as customerWin but DIFFERENT evidence -> distinct signal.
        var customerWinOtherEvidence = DuplicateSignalFor(companyId, evidenceB, inWindow, SignalType.CustomerWin, SignalDirection.Positive);

        foreach (var s in new[] { customerWin, guidanceChange, guidanceNeutral, customerWinOtherEvidence })
        {
            await store.WriteAsync(s, ReviewFor(s), CancellationToken.None);
        }

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        var ids = result.Select(s => s.Id).ToHashSet();
        Assert.Equal(4, ids.Count);
        Assert.Contains(customerWin.Id, ids);
        Assert.Contains(guidanceChange.Id, ids);
        Assert.Contains(guidanceNeutral.Id, ids);
        Assert.Contains(customerWinOtherEvidence.Id, ids);
    }

    [Fact]
    public async Task ReadApprovedInWindow_StaleNeutralAndDirectionalGuidanceChange_SameEvidence_BothSurvive()
    {
        // Spec 113 regression: a filing whose deterministic Neutral GuidanceChange was persisted while the
        // directional read failed later gets its directional Positive persisted too. The cross-run dedupe
        // key (CompanyId, EvidenceId, Type, Direction) keeps Direction, so the directional copy must NOT be
        // dropped against the stale Neutral — both come back and the assembly-time supersede picks the
        // directional one. This pins the dedupe key's Direction component (do not weaken it).
        var companyId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        var staleNeutral = DuplicateSignalFor(
            companyId, evidenceId, inWindow, SignalType.GuidanceChange, SignalDirection.Neutral, strength: 3);
        var directional = DuplicateSignalFor(
            companyId, evidenceId, inWindow, SignalType.GuidanceChange, SignalDirection.Positive, strength: 8);

        await store.WriteAsync(staleNeutral, ReviewFor(staleNeutral), CancellationToken.None);
        await store.WriteAsync(directional, ReviewFor(directional), CancellationToken.None);

        var result = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Id == staleNeutral.Id && s.Direction == SignalDirection.Neutral);
        Assert.Contains(result, s => s.Id == directional.Id && s.Direction == SignalDirection.Positive);
    }

    [Fact]
    public async Task ReadApprovedInWindow_DeterministicTieBreak_KeepsLowestSignalIdAcrossReads()
    {
        // Several duplicate copies of one identity, with KNOWN SignalIds. The survivor must be the lowest
        // SignalId (the fixed total order over Guid) and must be identical across repeated reads (AD-3),
        // independent of write/enumeration order.
        var companyId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var inWindow = new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero);

        var ids = new[]
        {
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
        };
        var expectedSurvivor = ids.Min();

        var store = CreateStore();
        // Write in an order NOT matching the sort so the tie-break, not enumeration order, decides.
        foreach (var id in ids)
        {
            var copy = DuplicateSignalFor(companyId, evidenceId, inWindow, id: id);
            await store.WriteAsync(copy, ReviewFor(copy), CancellationToken.None);
        }

        var first = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);
        var second = await store.ReadApprovedInWindowAsync(companyId, start, end, CancellationToken.None);

        Assert.Equal(expectedSurvivor, Assert.Single(first).Id);
        Assert.Equal(expectedSurvivor, Assert.Single(second).Id);
    }

    [Fact]
    public async Task ReadApprovedInWindow_AlreadyCancelledToken_Throws()
    {
        var companyId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);

        var store = CreateStore();

        // Seed at least one file so the per-file cancellation check runs.
        var seeded = SignalFor(companyId, new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero));
        await store.WriteAsync(seeded, ReviewFor(seeded), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadApprovedInWindowAsync(companyId, start, end, cts.Token));
    }
}
