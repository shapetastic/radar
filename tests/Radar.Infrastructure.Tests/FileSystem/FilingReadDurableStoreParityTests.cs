using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// SPEC 205 §3 + §4 on the REAL durable read seam: the spec-204 parity fixture re-run through
/// <see cref="FileSignalStore"/> (as both <c>ISignalRepository</c> and the previous-window
/// <c>ISignalFileStore</c> — the spec-142 production shape), never only an in-memory list.
/// <para>
/// One company, one filing evidence id, two persisted <c>GuidanceChange Neutral</c> signals over it: the
/// spec-57 KEYWORD copy created at T0 and the spec-204 AI READ (carrying the <c>filingReadOutcome</c>
/// envelope) created at T1 &gt; T0. This pins both sides of spec 142's ordering contract:
/// </para>
/// <list type="number">
/// <item><c>GetByCompanyAsync</c> keeps BOTH provenance classes (the spec-205 fifth key field) — before 205
/// they shared one dedupe key and the earliest-known collapse silently discarded the read before
/// <c>GuidanceChangeSupersede</c> could prefer it — while repeated copies WITHIN each class still collapse
/// (the vacuity guard: "returns both" is the fifth field working, not the collapse not running).</item>
/// <item>Scoring at an as-of BETWEEN T0 and T1: the known-at predicate exposes only the keyword — dedupe
/// must not leak later knowledge into an earlier replay.</item>
/// <item>Scoring at/after T1: both reach <c>GuidanceChangeSupersede</c>, the read wins, and the keyword's
/// removal is counted and attributed on the read's surviving link (the spec-193 accounting shape).</item>
/// <item>Reversed insertion order and a fresh store REHYDRATED from the same files give byte-identical
/// results (order independence on the durable path).</item>
/// </list>
/// <para>
/// §4 NUMERICAL STABILITY: at every as-of, all five score components, the Explanation and the
/// <c>ComponentJson</c> of the keyword+read store are BYTE-IDENTICAL to a keyword-only control scored at the
/// same as-of — the correction changes which link survives and what its reason says, never a number. (The
/// magnitude-mutation proof that keeps this parity non-vacuous lives in the spec-204
/// <c>FilingReadScoreParityTests.MixedReadAtStrength8_DivergesFromKeywordOnly</c> and is not duplicated here.)
/// </para>
/// </summary>
public sealed class FilingReadDurableStoreParityTests : IDisposable
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>T0: the keyword Neutral's knowledge instant (== both signals' observed instant).</summary>
    private static readonly DateTimeOffset T0 = WindowEnd.AddDays(-3);

    /// <summary>Between T0 and T1: a replay instant at which only the keyword was known.</summary>
    private static readonly DateTimeOffset BetweenT0AndT1 = WindowEnd.AddDays(-2);

    /// <summary>T1: the AI read's knowledge instant (its CreatedAtUtc; ObservedAtUtc stays T0).</summary>
    private static readonly DateTimeOffset T1 = WindowEnd.AddDays(-1);

    // Fixed ids (AD-3). The read's id is deliberately HIGHER than the keyword's, so it can only win via the
    // spec-204 read-preference step, never the Id tie-break; and the keyword's CreatedAtUtc is EARLIER, so
    // a keyless (pre-205) collapse would keep the keyword — which is exactly the mutation these tests catch.
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FilingEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KeywordSignalId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ReadSignalId = new("77777777-7777-7777-7777-777777777777");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; ignore transient filesystem locks and permission errors.
            }
        }
    }

    [Fact]
    public async Task GetByCompanyAsync_KeepsBothProvenanceClasses_InEitherOrder_AndAfterRehydration()
    {
        var root = NewTempDir();
        var forward = CreateStore(root);
        await PersistAsync(forward, KeywordNeutral());
        await PersistAsync(forward, ReadNeutral());

        var reversedRoot = NewTempDir();
        var reversed = CreateStore(reversedRoot);
        await PersistAsync(reversed, ReadNeutral());
        await PersistAsync(reversed, KeywordNeutral());

        // A FRESH store over the forward root: everything comes back through hydration (file enumeration),
        // not the in-process index the writes populated.
        var rehydrated = CreateStore(root);

        foreach (var store in new[] { forward, reversed, rehydrated })
        {
            var signals = await store.GetByCompanyAsync(CompanyId, CancellationToken.None);

            // No collapse to the T0 keyword: both provenance classes survive the durable read.
            Assert.Equal(2, signals.Count);
            Assert.Contains(signals, s => s.Id == KeywordSignalId);
            Assert.Contains(signals, s => s.Id == ReadSignalId);
        }
    }

    [Fact]
    public async Task GetByCompanyAsync_StillCollapsesRepeatedCopiesWithinEachProvenanceClass()
    {
        // The vacuity guard AND spec 205 §2's "repeated copies of the SAME AI read still collapse": a later
        // re-minted copy of the keyword and a later re-minted copy of the read (fresh ids, later CreatedAt —
        // exactly what a re-run persists) both collapse onto their earliest-known originals, leaving TWO
        // survivors, not four. If this returned four, the two-provenance-classes test above would be passing
        // because the collapse never ran, not because the fifth key field works.
        var store = CreateStore(NewTempDir());
        await PersistAsync(store, KeywordNeutral());
        await PersistAsync(store, ReadNeutral());
        await PersistAsync(store, KeywordNeutral() with
        {
            Id = new Guid("44444444-4444-4444-4444-444444444444"),
            CreatedAtUtc = T1,
        });
        await PersistAsync(store, ReadNeutral() with
        {
            Id = new Guid("88888888-8888-8888-8888-888888888888"),
            CreatedAtUtc = T1.AddHours(1),
        });

        var signals = await store.GetByCompanyAsync(CompanyId, CancellationToken.None);

        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Id == KeywordSignalId); // earliest-known keyword copy.
        Assert.Contains(signals, s => s.Id == ReadSignalId);    // earliest-known read copy.
    }

    [Fact]
    public async Task ScoringBetweenT0AndT1_ExposesOnlyTheKeyword_AndMatchesTheKeywordOnlyControl()
    {
        var control = await ScoreAsync(withRead: false, asOf: BetweenT0AndT1);
        var withRead = await ScoreAsync(withRead: true, asOf: BetweenT0AndT1);

        // Known-at honesty: at an instant before the read existed, the replay sees ONLY the keyword — the
        // durable dedupe kept the read, and the engine's CreatedAtUtc <= asOf predicate excluded it.
        var link = Assert.Single(withRead.Links);
        Assert.Equal(KeywordSignalId, link.SignalId);
        Assert.Equal(FilingEvidenceId, link.EvidenceId);
        Assert.DoesNotContain("superseded", link.ContributionReason, StringComparison.OrdinalIgnoreCase);

        AssertNumericallyByteIdentical(control, withRead);
    }

    [Fact]
    public async Task ScoringAtOrAfterT1_TheReadWins_TheKeywordRemovalIsCountedOnIt_AndNumbersMatchTheControl()
    {
        var control = await ScoreAsync(withRead: false, asOf: WindowEnd);
        var withRead = await ScoreAsync(withRead: true, asOf: WindowEnd);

        // Both provenance classes reached GuidanceChangeSupersede; the read won by the spec-204
        // read-preference step (its id is higher and its ObservedAtUtc is equal, so no tie-break could have
        // picked it), and the keyword's removal is counted and attributed on the surviving read link.
        var link = Assert.Single(withRead.Links);
        Assert.Equal(ReadSignalId, link.SignalId);
        Assert.Equal(FilingEvidenceId, link.EvidenceId);
        Assert.StartsWith("GuidanceChange (Neutral)", link.ContributionReason, StringComparison.Ordinal);
        Assert.EndsWith(
            " (superseded 1 stale GuidanceChange signal(s) for this evidence)",
            link.ContributionReason,
            StringComparison.Ordinal);

        AssertNumericallyByteIdentical(control, withRead);
    }

    [Fact]
    public async Task ReversedInsertionOrder_AndRehydration_GiveTheSameSurvivorAndByteIdenticalNumbers()
    {
        var forward = await ScoreAsync(withRead: true, asOf: WindowEnd);
        var reversed = await ScoreAsync(withRead: true, asOf: WindowEnd, reverseWriteOrder: true);
        var rehydrated = await ScoreAsync(withRead: true, asOf: WindowEnd, rehydrateBeforeScoring: true);

        foreach (var variant in new[] { reversed, rehydrated })
        {
            var link = Assert.Single(variant.Links);
            Assert.Equal(ReadSignalId, link.SignalId);
            Assert.Equal(Assert.Single(forward.Links).ContributionReason, link.ContributionReason);
            AssertNumericallyByteIdentical(forward, variant);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The fixture.
    // ---------------------------------------------------------------------------------------------

    /// <summary>§4's pin: every numeric/rendered score field byte-identical; only provenance may differ.</summary>
    private static void AssertNumericallyByteIdentical(CompanyScoreResult expected, CompanyScoreResult actual)
    {
        Assert.Equal(expected.Snapshot.TrajectoryScore, actual.Snapshot.TrajectoryScore);
        Assert.Equal(expected.Snapshot.OpportunityScore, actual.Snapshot.OpportunityScore);
        Assert.Equal(expected.Snapshot.AttentionScore, actual.Snapshot.AttentionScore);
        Assert.Equal(expected.Snapshot.EvidenceConfidenceScore, actual.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(expected.Snapshot.SignalVelocityScore, actual.Snapshot.SignalVelocityScore);
        // Byte-identical, not merely equal-valued: the explanation embeds the (post-supersede) signal count
        // and every component, and ComponentJson is the serialized components.
        Assert.Equal(expected.Snapshot.Explanation, actual.Snapshot.Explanation);
        Assert.Equal(expected.Snapshot.ComponentJson, actual.Snapshot.ComponentJson);
    }

    private async Task<CompanyScoreResult> ScoreAsync(
        bool withRead,
        DateTimeOffset asOf,
        bool reverseWriteOrder = false,
        bool rehydrateBeforeScoring = false)
    {
        var root = NewTempDir();
        var store = CreateStore(root);

        var toWrite = new List<Signal> { KeywordNeutral() };
        if (withRead)
        {
            toWrite.Add(ReadNeutral());
        }

        if (reverseWriteOrder)
        {
            toWrite.Reverse();
        }

        foreach (var signal in toWrite)
        {
            await PersistAsync(store, signal);
        }

        if (rehydrateBeforeScoring)
        {
            // Score through a FRESH store over the same files: every signal arrives via hydration's file
            // enumeration rather than the writer's in-process index.
            store = CreateStore(root);
        }

        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        await companies.AddAsync(new CompanyBuilder().WithId(CompanyId).Build(), CancellationToken.None);
        await evidence.AddIfNewAsync(
            new EvidenceBuilder()
                .WithId(FilingEvidenceId)
                .WithContentHash(FilingEvidenceId.ToString("N"))
                .WithSourceType(EvidenceSourceType.Filing)
                .WithSourceName("Acme — SEC")
                .WithQuality(EvidenceQuality.High)
                .WithPublishedAtUtc(T0)
                .WithCollectedAtUtc(T0)
                .Build(),
            CancellationToken.None);

        // The ONE store serves both seams, exactly as production wires it (spec 142): the durable
        // repository read AND the previous-window activity read.
        var engine = new ScoringEngine(
            store,
            store,
            evidence,
            new InMemoryScoreRepository(),
            companies,
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance);

        return await engine.ScoreCompanyAsync(CompanyId, asOf, CancellationToken.None);
    }

    /// <summary>Exactly what the spec-57 keyword fallback stores for an earnings 8-K: Neutral, 3/4/0.4, no envelope.</summary>
    private static Signal KeywordNeutral() =>
        GuidanceBuilder(KeywordSignalId)
            .WithCreatedAtUtc(T0)
            .WithMetadataJson(null)
            .Build();

    /// <summary>
    /// The spec-204 Neutral read over the SAME evidence, known only from T1 — same magnitudes and observed
    /// instant as the keyword copy, envelope composed through the REAL producer.
    /// </summary>
    private static Signal ReadNeutral() =>
        GuidanceBuilder(ReadSignalId)
            .WithCreatedAtUtc(T1)
            .WithMetadataJson(FilingReadSignalMetadata.Compose(
                FilingNoSignalCause.Unknown, "Unknown", 0.85m, "openai:test-model"))
            .Build();

    private static SignalBuilder GuidanceBuilder(Guid id) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(FilingEvidenceId)
            .WithCompanyId(CompanyId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Neutral)
            // The keyword fallback's magnitudes — asserted equal to the extractor's own output by
            // FilingReadSignalMetadataTests, so this fixture cannot silently drift from production.
            .WithStrength(FilingReadSignalMetadata.Strength)
            .WithNovelty(FilingReadSignalMetadata.Novelty)
            .WithConfidence(FilingReadSignalMetadata.Confidence)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(T0);

    private FileSignalStore CreateStore(string root) =>
        new(new FileSignalStoreOptions { RootDirectory = root }, NullLogger<FileSignalStore>.Instance);

    private static Task PersistAsync(FileSignalStore store, Signal signal) =>
        store.WriteAsync(
            signal,
            new SignalReview(
                Id: Guid.NewGuid(),
                SignalId: signal.Id,
                ReviewerName: "DeterministicSignalReviewer",
                Decision: SignalReviewDecision.Approve,
                Summary: "Approved for the spec-205 durable ordering fixture.",
                IssuesJson: null,
                ReviewedAtUtc: signal.CreatedAtUtc),
            CancellationToken.None);

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-read-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input here.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=sec;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec"];
    }
}
