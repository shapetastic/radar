using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 187 §3: the durable PRE-CALL attempt ledger. These tests exercise the REAL
/// <see cref="FileMode.CreateNew"/> path on a real temp directory — an in-memory fake alone cannot prove
/// the property the whole section rests on, which is that two racers for one ordinal cannot both be told
/// "you may call the provider".
/// </summary>
public sealed class FileNewsTypingAttemptLedgerTests : IDisposable
{
    private static readonly Guid ObservationId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunA = new("dddddddd-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset ReservedAt =
        new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private const string CohortKey =
        "openai:test-model|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-newstyping-ledger-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private FileNewsTypingAttemptLedger NewLedger(string? root = null) => new(
        new FileNewsTypingAttemptLedgerOptions { RootDirectory = root ?? _root },
        NullLogger<FileNewsTypingAttemptLedger>.Instance);

    private static NewsTypingAttemptReservation Reservation(
        int ordinal = 1, Guid? runId = null, string payloadHash = "ph-1") =>
        NewsTypingAttemptReservation.For(
            CohortKey,
            ObservationId,
            payloadHash,
            ordinal,
            runId,
            "openai",
            "test-model",
            ReservedAt);

    [Fact]
    public async Task Reserve_CreatesTheDurableFile_AndRoundTripsThroughAFreshInstance()
    {
        var reservation = Reservation();

        Assert.True(await NewLedger().TryReserveAsync(reservation, CancellationToken.None));

        var hydrated = Assert.Single(await NewLedger().GetAllAsync(CancellationToken.None));
        Assert.Equal(reservation.ReservationId, hydrated.ReservationId);
        Assert.Equal(NewsTypingAttemptReservation.CurrentSchemaVersion, hydrated.SchemaVersion);
        Assert.Equal(CohortKey, hydrated.CohortKey);
        Assert.Equal(ObservationId, hydrated.ObservationId);
        Assert.Equal("ph-1", hydrated.PayloadHash);
        Assert.Equal(1, hydrated.AttemptOrdinal);
        Assert.Null(hydrated.RunId);
        Assert.Equal("openai", hydrated.Provider);
        Assert.Equal("test-model", hydrated.ModelId);
        Assert.Equal(ReservedAt, hydrated.ReservedAtUtc);
    }

    /// <summary>
    /// The whole point of the section: a SECOND claim on the same ordinal returns <c>false</c>. Unlike the
    /// outcome store — where an existing file is a benign re-run dedupe returning <c>true</c> — here the
    /// return value is the permission to spend a hosted provider call, so the loser must be refused.
    /// </summary>
    [Fact]
    public async Task ASecondClaimOnTheSameOrdinal_IsRefused_EvenFromAnIndependentInstance()
    {
        Assert.True(await NewLedger().TryReserveAsync(Reservation(), CancellationToken.None));

        // A fresh instance (a different process, in effect) hydrates the file and refuses.
        Assert.False(await NewLedger().TryReserveAsync(Reservation(), CancellationToken.None));

        // And so does the same instance twice.
        var ledger = NewLedger();
        Assert.False(await ledger.TryReserveAsync(Reservation(), CancellationToken.None));

        Assert.Single(await NewLedger().GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// Two instances that BOTH hydrated before either wrote — the genuine race. The in-memory index cannot
    /// help here; only <see cref="FileMode.CreateNew"/> can, and exactly one caller may win.
    /// </summary>
    [Fact]
    public async Task TwoRacingInstances_ProduceExactlyOneWinner_ThroughTheRealCreateNewPath()
    {
        var a = NewLedger();
        var b = NewLedger();

        // Force both to hydrate against the (empty) directory before either writes.
        Assert.Empty(await a.GetAllAsync(CancellationToken.None));
        Assert.Empty(await b.GetAllAsync(CancellationToken.None));

        var first = await a.TryReserveAsync(Reservation(), CancellationToken.None);
        var second = await b.TryReserveAsync(Reservation(), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Single(await NewLedger().GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// The reservation's FULL PATH — not merely its file name — is a pure function of its deterministic
    /// identity (AD-3). A date-partitioned layout would have let two genuinely concurrent processes
    /// straddling a UTC month boundary write <c>…/2026/08/{id}.json</c> and <c>…/2026/09/{id}.json</c> and
    /// BOTH win <see cref="FileMode.CreateNew"/>, which is mutual exclusion on the name and none at all on
    /// the directory. So: the SAME identity with wildly different <c>ReservedAtUtc</c> values must resolve
    /// to the SAME file through the real create-new path, and the later claim must be refused.
    /// </summary>
    [Fact]
    public async Task ReservationPath_IsClockIndependent_SoClocksMonthsApartContendForOneFile()
    {
        var august = Reservation() with { ReservedAtUtc = new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero) };
        var november = Reservation() with { ReservedAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 1, TimeSpan.Zero) };

        // Same deterministic identity — the clock enters neither the id nor the path.
        Assert.Equal(august.ReservationId, november.ReservationId);

        // Two instances that both hydrated against the empty directory: only FileMode.CreateNew can
        // separate them, and it can only do so if they compute the same path.
        var a = NewLedger();
        var b = NewLedger();
        Assert.Empty(await a.GetAllAsync(CancellationToken.None));
        Assert.Empty(await b.GetAllAsync(CancellationToken.None));

        Assert.True(await a.TryReserveAsync(august, CancellationToken.None));
        Assert.False(await b.TryReserveAsync(november, CancellationToken.None));

        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories));

        // …and no path segment is a clock: the layout is {policy-segment}/{shard}/{id}.json.
        var segments = Path.GetRelativePath(Path.Combine(_root, "attempt-reservations"), file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(
            ["openai-test-model", august.ReservationId.ToString("N")[..2], august.ReservationId.ToString("D") + ".json"],
            segments);
    }

    [Fact]
    public async Task DistinctOrdinals_AndDistinctPayloads_AreDistinctReservations()
    {
        var ledger = NewLedger();
        Assert.True(await ledger.TryReserveAsync(Reservation(1), CancellationToken.None));
        Assert.True(await ledger.TryReserveAsync(Reservation(2), CancellationToken.None));
        Assert.True(await ledger.TryReserveAsync(
            Reservation(1, payloadHash: "ph-2"), CancellationToken.None));

        Assert.Equal(3, (await NewLedger().GetAllAsync(CancellationToken.None)).Count);
    }

    /// <summary>
    /// Identity is over (cohort, observation, payload, ordinal) and DELIBERATELY not the run id: two
    /// processes attempting the same ordinal under different run ids must collide, not each get a private
    /// namespace. The run id is provenance only.
    /// </summary>
    [Fact]
    public async Task RunId_IsProvenanceOnly_SoADifferentRunCannotClaimTheSameOrdinalTwice()
    {
        Assert.Equal(
            Reservation(1, RunA).ReservationId,
            Reservation(1, Guid.NewGuid()).ReservationId);

        Assert.True(await NewLedger().TryReserveAsync(Reservation(1, RunA), CancellationToken.None));
        Assert.False(await NewLedger().TryReserveAsync(
            Reservation(1, Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedFile_IsSkipped_NeverThrown()
    {
        Assert.True(await NewLedger().TryReserveAsync(Reservation(1), CancellationToken.None));
        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(file, "{ not json");

        Assert.Empty(await NewLedger().GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FileMissingIdentityFields_IsSkipped_NeverTreatedAsAClaimedOrdinal()
    {
        Assert.True(await NewLedger().TryReserveAsync(Reservation(1), CancellationToken.None));
        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        document["attemptOrdinal"] = 0;
        await File.WriteAllTextAsync(file, document.ToJsonString());

        Assert.Empty(await NewLedger().GetAllAsync(CancellationToken.None));

        // A skipped file still occupies its PATH, so re-claiming that ordinal fails through the real
        // create-new path: "cannot tell" degrades towards NOT calling the provider.
        Assert.False(await NewLedger().TryReserveAsync(Reservation(1), CancellationToken.None));
    }

    /// <summary>
    /// A disk failure degrades to <c>false</c> (Warning), never an exception — and <c>false</c> means the
    /// generator makes no hosted call, which is the conservative direction.
    /// </summary>
    [Fact]
    public async Task DiskFailure_DegradesToRefusal_NeverAnException()
    {
        // A FILE where the reservations directory must be: every create under it fails.
        var root = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "attempt-reservations"), "not a directory");

        var ledger = NewLedger(root);

        Assert.False(await ledger.TryReserveAsync(Reservation(), CancellationToken.None));
        Assert.Empty(await ledger.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetAll_IsDeterministicallyOrdered()
    {
        var ledger = NewLedger();
        Assert.True(await ledger.TryReserveAsync(
            Reservation(2) with { ReservedAtUtc = ReservedAt.AddHours(1) }, CancellationToken.None));
        Assert.True(await ledger.TryReserveAsync(Reservation(1), CancellationToken.None));

        var all = await NewLedger().GetAllAsync(CancellationToken.None);

        Assert.Equal([1, 2], all.Select(r => r.AttemptOrdinal));
    }
}
