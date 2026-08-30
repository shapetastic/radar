using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 203 §2/§3: <see cref="FileSignalStore.ReadApprovedInWindowAsync"/> now serves from the hydration
/// index instead of a per-call month-directory scan, and <see cref="ISignalRepository.GetByCompanyAsync"/>
/// filters a per-company bucket instead of the whole index. Both are asserted OUTPUT-IDENTICAL to the pre-203
/// implementations — the disk scan is reconstructed here, in the test project only
/// (<see cref="LegacyDiskWindowRead"/>), so the equivalence is checked against the real deleted code path
/// rather than against a restatement of the new one.
/// </summary>
public sealed class FileSignalStoreWindowReadEquivalenceTests : IDisposable
{
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid CompanyC = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
    private static readonly Guid[] Companies = [CompanyA, CompanyB, CompanyC];

    private readonly string _tempDir;

    public FileSignalStoreWindowReadEquivalenceTests()
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

    private FileSignalStore CreateStore(TimeProvider? timeProvider = null) =>
        new(
            new FileSignalStoreOptions { RootDirectory = _tempDir },
            NullLogger<FileSignalStore>.Instance,
            timeProvider);

    // -------------------------------------------------------------------------------------------------
    // §2 — the index read reproduces the disk scan across month boundaries and known-at edges.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadApprovedInWindow_IndexRead_IsElementForElementIdenticalToTheLegacyDiskScan()
    {
        await SeedFixtureAsync();

        var store = CreateStore();
        var grid = WindowGrid().ToList();
        Assert.True(grid.Count > 100, "the grid must be dense enough to cover every boundary shape");

        var nonEmptyCases = 0;
        foreach (var (start, end, knownAsOf) in grid)
        {
            // THE PRODUCTION CONTRACT, asserted rather than assumed: ScoringEngine passes
            // knownAsOf = windowEndUtc ≥ endInclusive = windowStartUtc. The one semantic edge between the two
            // implementations (a legacy file with no createdAt) can only appear OUTSIDE this contract.
            Assert.True(knownAsOf >= end);

            foreach (var company in Companies)
            {
                var legacy = await LegacyDiskWindowRead(_tempDir, company, start, end, knownAsOf);
                var actual = await store.ReadApprovedInWindowAsync(company, start, end, knownAsOf, CancellationToken.None);

                // Signal is a record with value semantics over EVERY field, so sequence equality is a
                // field-for-field comparison — id, evidence id, company, type, direction, strength, novelty,
                // confidence, excerpt, reason, status, both instants and the metadata envelope.
                Assert.Equal(legacy, actual);
                nonEmptyCases += actual.Count > 0 ? 1 : 0;
            }
        }

        // The equivalence must be exercised on real matches, not vacuously on empty windows.
        Assert.True(nonEmptyCases > 50, $"only {nonEmptyCases} non-empty cases — the fixture is too sparse");
    }

    [Fact]
    public async Task ReadApprovedInWindow_OpensNoFileAfterHydration()
    {
        await SeedFixtureAsync();

        var store = CreateStore();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var knownAsOf = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // The first read hydrates (the one and only disk walk).
        var before = new Dictionary<Guid, IReadOnlyList<Signal>>();
        foreach (var company in Companies)
        {
            before[company] = await store.ReadApprovedInWindowAsync(company, start, end, knownAsOf, CancellationToken.None);
        }

        Assert.Contains(before.Values, list => list.Count > 0);

        // Remove the ENTIRE tree. If the read still touched a file, it would now return nothing.
        Directory.Delete(_tempDir, recursive: true);
        Assert.False(Directory.Exists(_tempDir));

        foreach (var company in Companies)
        {
            var after = await store.ReadApprovedInWindowAsync(company, start, end, knownAsOf, CancellationToken.None);
            Assert.Equal(before[company], after);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // §3 — the per-company bucket reproduces the whole-index filter.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByCompany_BucketRead_EqualsThePre203WholeIndexExpression()
    {
        var written = await SeedFixtureAsync();

        ISignalRepository repo = CreateStore();

        foreach (var company in Companies)
        {
            // The pre-203 expression, verbatim, over the set of persisted signals (last write per id wins,
            // which is what the on-disk upsert semantics and the hydration index both hold).
            var expected = SignalCrossRunDedupe
                .Collapse(written.Values.Where(s => s.CompanyId == company), SignalCopySurvivor.EarliestKnown)
                .OrderBy(s => s.ObservedAtUtc)
                .ThenBy(s => s.Id)
                .ToList();

            var actual = await repo.GetByCompanyAsync(company, CancellationToken.None);

            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }

        // A company-less signal is in the index (GetByIdAsync) but in no company's bucket — exactly what the
        // Guid? == Guid comparison produced before.
        var orphan = written.Values.Single(s => s.CompanyId is null);
        Assert.Equal(orphan, await repo.GetByIdAsync(orphan.Id, CancellationToken.None));
        foreach (var company in Companies)
        {
            Assert.DoesNotContain(await repo.GetByCompanyAsync(company, CancellationToken.None), s => s.Id == orphan.Id);
        }
    }

    [Fact]
    public async Task ReWritingASignalUnderANewCompany_MovesItBetweenBuckets()
    {
        var store = CreateStore();
        var observed = new DateTimeOffset(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);
        var signal = new SignalBuilder()
            .WithCompanyId(CompanyA)
            .WithObservedAtUtc(observed)
            .WithCreatedAtUtc(observed)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .Build();

        await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);
        ISignalRepository repo = store;
        Assert.Single(await repo.GetByCompanyAsync(CompanyA, CancellationToken.None));
        Assert.Empty(await repo.GetByCompanyAsync(CompanyB, CancellationToken.None));

        // Same id, different company: upsert-by-Id moves it (WriteAsync path).
        var moved = signal with { CompanyId = CompanyB };
        await store.WriteAsync(moved, ReviewFor(moved), CancellationToken.None);
        Assert.Empty(await repo.GetByCompanyAsync(CompanyA, CancellationToken.None));
        Assert.Equal(moved, Assert.Single(await repo.GetByCompanyAsync(CompanyB, CancellationToken.None)));

        // And again through the index-only AddAsync path, this time to a null company: it leaves every bucket.
        await repo.AddAsync(moved with { CompanyId = null }, CancellationToken.None);
        Assert.Empty(await repo.GetByCompanyAsync(CompanyA, CancellationToken.None));
        Assert.Empty(await repo.GetByCompanyAsync(CompanyB, CancellationToken.None));
        Assert.NotNull(await repo.GetByIdAsync(signal.Id, CancellationToken.None));

        // The window read follows the same bucket.
        var window = await store.ReadApprovedInWindowAsync(
            CompanyB, observed.AddDays(-1), observed.AddDays(1), observed.AddDays(1), CancellationToken.None);
        Assert.Empty(window);
    }

    [Fact]
    public async Task SameProcessWrite_WinsOverTheOnDiskCopy_InBothTheIdIndexAndTheCompanyBucket()
    {
        // A previous run persisted this id under company A with strength 3 ...
        var id = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");
        var observed = new DateTimeOffset(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);
        var onDisk = new SignalBuilder().WithId(id).WithCompanyId(CompanyA).WithStrength(3)
            .WithObservedAtUtc(observed).WithCreatedAtUtc(observed)
            .WithReviewStatus(SignalReviewStatus.Approved).Build();
        var previousRun = CreateStore();
        await previousRun.WriteAsync(onDisk, ReviewFor(onDisk), CancellationToken.None);

        // ... and THIS process writes the same id (strength 9) BEFORE its first read hydrates. Hydration's
        // TryAdd must lose in _byId AND in the company bucket — both views under one gate (spec 203 §3).
        var store = CreateStore();
        var written = onDisk with { Strength = 9 };
        await store.WriteAsync(written, ReviewFor(written), CancellationToken.None);

        ISignalRepository repo = store;
        Assert.Equal(written, await repo.GetByIdAsync(id, CancellationToken.None));
        Assert.Equal(written, Assert.Single(await repo.GetByCompanyAsync(CompanyA, CancellationToken.None)));
        Assert.Equal(
            written,
            Assert.Single(await store.ReadApprovedInWindowAsync(
                CompanyA, observed.AddDays(-1), observed.AddDays(1), observed.AddDays(1), CancellationToken.None)));
    }

    // -------------------------------------------------------------------------------------------------
    // §1 — hydration telemetry: null until hydrated, then the MEASURED elapsed.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HydrationElapsed_IsNullBeforeHydration_AndMeasuredAfter()
    {
        await SeedFixtureAsync();

        var clock = new FakeTimeProvider();
        var store = CreateStore(clock);
        IHydrationTelemetry telemetry = store;

        // Not hydrated yet: NOT RECORDED, never zero.
        Assert.Null(telemetry.HydrationElapsed);

        // A same-process write does not hydrate (writes stay cheap).
        var signal = new SignalBuilder().WithCompanyId(CompanyA).Build();
        await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);
        Assert.Null(telemetry.HydrationElapsed);

        // The first read hydrates; the fake clock does not advance during the walk, so the measured value
        // is exactly zero — and it is a MEASURED zero, distinguishable from the null above.
        await ((ISignalRepository)store).GetByIdAsync(signal.Id, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, telemetry.HydrationElapsed);
    }

    [Fact]
    public async Task RawEvidenceStore_HydrationElapsed_IsNullBeforeHydration_AndMeasuredAfter()
    {
        var clock = new FakeTimeProvider();
        var store = new FileRawEvidenceStore(
            new FileRawEvidenceStoreOptions { RootDirectory = Path.Combine(_tempDir, "raw") },
            NullLogger<FileRawEvidenceStore>.Instance,
            clock);
        IHydrationTelemetry telemetry = store;

        Assert.Null(telemetry.HydrationElapsed);

        await ((IEvidenceRepository)store).GetAllAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, telemetry.HydrationElapsed);
    }

    // -------------------------------------------------------------------------------------------------
    // The fixture: 3 companies × 4 months (2026-01..04), Approved AND non-Approved, cross-run duplicates,
    // late-created signals, exact month-boundary instants, a hand-written legacy file with no createdAt, a
    // company-less signal and a garbage file. Returns the signals as the store should hold them (last write
    // per id).
    // -------------------------------------------------------------------------------------------------

    private async Task<Dictionary<Guid, Signal>> SeedFixtureAsync()
    {
        var store = CreateStore();
        var written = new Dictionary<Guid, Signal>();
        var ids = new SequentialIds();

        async Task Persist(Signal s)
        {
            await store.WriteAsync(s, ReviewFor(s), CancellationToken.None);
            written[s.Id] = s;
        }

        var months = new[] { 1, 2, 3, 4 };
        var days = new[] { 1, 10, 20, 28 };
        var types = new[] { SignalType.CustomerWin, SignalType.ProductLaunch, SignalType.MediaAttention };

        foreach (var company in Companies)
        {
            foreach (var month in months)
            {
                foreach (var day in days)
                {
                    var observed = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero);
                    var evidenceId = ids.Next();
                    var type = types[(month + day) % types.Length];

                    // The original copy, created a few days after it was observed.
                    await Persist(Build(ids, company, evidenceId, type, SignalDirection.Positive,
                        SignalReviewStatus.Approved, observed, observed.AddDays(2)));

                    // A cross-run DUPLICATE — same (CompanyId, EvidenceId, Type, Direction), later id and
                    // createdAt — the shape spec 85's key collapses (survivor rules differ per read).
                    await Persist(Build(ids, company, evidenceId, type, SignalDirection.Positive,
                        SignalReviewStatus.Approved, observed, observed.AddDays(9)));

                    // A copy created MUCH later (after most known-at instants in the grid).
                    await Persist(Build(ids, company, evidenceId, type, SignalDirection.Positive,
                        SignalReviewStatus.Approved, observed, observed.AddDays(45)));

                    // A different direction over the same evidence: a different identity, never collapsed.
                    await Persist(Build(ids, company, evidenceId, type, SignalDirection.Negative,
                        SignalReviewStatus.Approved, observed.AddHours(1), observed.AddHours(1)));

                    // Non-Approved siblings that every read must ignore.
                    await Persist(Build(ids, company, ids.Next(), type, SignalDirection.Neutral,
                        SignalReviewStatus.Pending, observed, observed));
                    await Persist(Build(ids, company, ids.Next(), type, SignalDirection.Neutral,
                        SignalReviewStatus.Rejected, observed, observed));
                }
            }
        }

        // Exact month-boundary instants: the last tick of January and the first instant of February, both
        // Approved, so a window bound landing exactly on a month edge is exercised on both sides.
        var lastTickJan = new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);
        var firstTickFeb = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var lastTickMar = new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);
        var firstTickApr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var company in Companies)
        {
            await Persist(Build(ids, company, ids.Next(), SignalType.CustomerWin, SignalDirection.Positive,
                SignalReviewStatus.Approved, lastTickJan, lastTickJan));
            await Persist(Build(ids, company, ids.Next(), SignalType.CustomerWin, SignalDirection.Positive,
                SignalReviewStatus.Approved, firstTickFeb, firstTickFeb));
            await Persist(Build(ids, company, ids.Next(), SignalType.CustomerWin, SignalDirection.Positive,
                SignalReviewStatus.Approved, lastTickMar, lastTickMar.AddDays(3)));
            await Persist(Build(ids, company, ids.Next(), SignalType.CustomerWin, SignalDirection.Positive,
                SignalReviewStatus.Approved, firstTickApr, firstTickApr.AddDays(3)));
        }

        // A company-less signal: indexed, never returned by a company read.
        await Persist(Build(ids, null, ids.Next(), SignalType.CustomerWin, SignalDirection.Positive,
            SignalReviewStatus.Approved, new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero)));

        // A hand-written LEGACY file with no createdAt (pre-136), for company A in March. Both readers map it
        // to CreatedAt = ObservedAt (the legacy reader only when it matters, i.e. never under the contract).
        var legacyId = ids.Next();
        var legacyEvidence = ids.Next();
        var legacyDir = Path.Combine(_tempDir, "2026", "03");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, legacyId + ".json"), $$"""
            {
              "signalId": "{{legacyId}}",
              "evidenceId": "{{legacyEvidence}}",
              "companyId": "{{CompanyA}}",
              "companyMention": "Acme Corp",
              "type": "CustomerWin",
              "direction": "Positive",
              "strength": 6,
              "novelty": 6,
              "confidence": 0.8,
              "supportingExcerpt": "signed a multi-year deal",
              "reason": "Customer win phrase detected.",
              "reviewStatus": "Approved",
              "observedAt": "2026-03-05T00:00:00+00:00",
              "review": {
                "reviewId": "{{Guid.NewGuid()}}",
                "signalId": "{{legacyId}}",
                "reviewerName": "DeterministicSignalReviewer",
                "decision": "Approve",
                "summary": "pre-136 legacy record",
                "issuesJson": null,
                "reviewedAt": "2026-03-06T00:00:00+00:00"
              }
            }
            """);
        written[legacyId] = new Signal(
            Id: legacyId,
            EvidenceId: legacyEvidence,
            CompanyId: CompanyA,
            CompanyMention: "Acme Corp",
            Type: SignalType.CustomerWin,
            Direction: SignalDirection.Positive,
            Strength: 6,
            Novelty: 6,
            Confidence: 0.8m,
            SupportingExcerpt: "signed a multi-year deal",
            Reason: "Customer win phrase detected.",
            ReviewStatus: SignalReviewStatus.Approved,
            ObservedAtUtc: new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
            CreatedAtUtc: new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero));

        // A garbage file, skipped by both readers.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "2026", "02", "garbage.json"), "{ not json ]");

        return written;
    }

    /// <summary>
    /// Every (startExclusive, endInclusive, knownAsOf) triple over a set of instants that includes exact month
    /// edges (first instant of a month, last tick of the previous one) and mid-month points, windows spanning
    /// one to three months, and known-at instants at/after the window end that fall before, at and after the
    /// fixture's createdAt values.
    /// </summary>
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset KnownAsOf)> WindowGrid()
    {
        DateTimeOffset[] instants =
        [
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 2, 10, 12, 0, 0, TimeSpan.Zero),
            new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero),
        ];

        for (var i = 0; i < instants.Length; i++)
        {
            for (var j = i + 1; j < instants.Length; j++)
            {
                var start = instants[i];
                var end = instants[j];
                foreach (var knownAsOf in new[]
                         {
                             end,
                             end.AddTicks(1),
                             end.AddDays(3),
                             end.AddDays(10),
                             end.AddDays(50),
                             new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                         })
                {
                    yield return (start, end, knownAsOf);
                }
            }
        }
    }

    private static Signal Build(
        SequentialIds ids,
        Guid? companyId,
        Guid evidenceId,
        SignalType type,
        SignalDirection direction,
        SignalReviewStatus status,
        DateTimeOffset observed,
        DateTimeOffset created) =>
        new SignalBuilder()
            .WithId(ids.Next())
            .WithEvidenceId(evidenceId)
            .WithCompanyId(companyId)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(5)
            .WithReviewStatus(status)
            .WithObservedAtUtc(observed)
            .WithCreatedAtUtc(created)
            .Build();

    private static SignalReview ReviewFor(Signal signal) => new(
        Id: Guid.NewGuid(),
        SignalId: signal.Id,
        ReviewerName: "deterministic-reviewer-v1",
        Decision: SignalReviewDecision.Approve,
        Summary: "Approve: looks fine.",
        IssuesJson: null,
        ReviewedAtUtc: signal.CreatedAtUtc);

    /// <summary>Deterministic, clock-free ids (AD-3) — lowest-id survivor rules then mean "first built".</summary>
    private sealed class SequentialIds
    {
        private int _next;

        public Guid Next() => new(++_next, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    }

    // -------------------------------------------------------------------------------------------------
    // THE PRE-203 DISK IMPLEMENTATION, reconstructed faithfully: month-directory enumeration, per-file
    // deserialize + filter (company, Approved, window, known-at with "null createdAt ⇒ included"), the
    // spec-85 LowestId collapse, then ObservedAt/Id ordering. Kept in the TEST project only.
    // -------------------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<Signal>> LegacyDiskWindowRead(
        string rootDirectory,
        Guid companyId,
        DateTimeOffset startExclusiveUtc,
        DateTimeOffset endInclusiveUtc,
        DateTimeOffset knownAsOfUtc)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return Array.Empty<Signal>();
        }

        var matches = new List<Signal>();
        foreach (var monthDirectory in EnumerateWindowMonthDirectories(rootDirectory, startExclusiveUtc, endInclusiveUtc))
        {
            if (!Directory.Exists(monthDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(monthDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                LegacySignalFile? parsed;
                try
                {
                    var text = await File.ReadAllTextAsync(file);
                    parsed = JsonSerializer.Deserialize<LegacySignalFile>(text, RadarFileStoreJson.Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is null
                    || parsed.CompanyId != companyId
                    || parsed.ReviewStatus != SignalReviewStatus.Approved
                    || parsed.ObservedAt <= startExclusiveUtc
                    || parsed.ObservedAt > endInclusiveUtc)
                {
                    continue;
                }

                if (parsed.CreatedAt is not null && parsed.CreatedAt > knownAsOfUtc)
                {
                    continue;
                }

                matches.Add(new Signal(
                    Id: parsed.SignalId,
                    EvidenceId: parsed.EvidenceId,
                    CompanyId: parsed.CompanyId,
                    CompanyMention: parsed.CompanyMention,
                    Type: parsed.Type,
                    Direction: parsed.Direction,
                    Strength: parsed.Strength,
                    Novelty: parsed.Novelty,
                    Confidence: parsed.Confidence,
                    SupportingExcerpt: parsed.SupportingExcerpt,
                    Reason: parsed.Reason,
                    ReviewStatus: parsed.ReviewStatus,
                    ObservedAtUtc: parsed.ObservedAt,
                    CreatedAtUtc: parsed.CreatedAt ?? parsed.ObservedAt,
                    MetadataJson: parsed.MetadataJson));
            }
        }

        var deduped = SignalCrossRunDedupe.Collapse(matches, SignalCopySurvivor.LowestId);
        return deduped.OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id).ToList();
    }

    private static IEnumerable<string> EnumerateWindowMonthDirectories(
        string rootDirectory, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc)
    {
        var startUtc = startExclusiveUtc.ToUniversalTime();
        var endUtc = endInclusiveUtc.ToUniversalTime();
        if (endUtc < startUtc)
        {
            yield break;
        }

        var cursor = new DateTime(startUtc.Year, startUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(endUtc.Year, endUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= last)
        {
            yield return Path.Combine(rootDirectory, cursor.ToString("yyyy"), cursor.ToString("MM"));
            cursor = cursor.AddMonths(1);
        }
    }

    /// <summary>A test-local mirror of the store's private persisted shape (camelCase via the shared options).</summary>
    private sealed record LegacySignalFile(
        Guid SignalId,
        Guid EvidenceId,
        Guid? CompanyId,
        string CompanyMention,
        SignalType Type,
        SignalDirection Direction,
        int Strength,
        int Novelty,
        decimal Confidence,
        string SupportingExcerpt,
        string Reason,
        SignalReviewStatus ReviewStatus,
        DateTimeOffset ObservedAt,
        DateTimeOffset? CreatedAt,
        string? MetadataJson = null);
}
