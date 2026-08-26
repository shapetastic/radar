using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 138 — the per-strategy signal-type filter as the <see cref="ScoringEngine"/> actually applies it:
/// the read→score gate (current AND previous window), the fingerprint fold, the zero-consumed-signals
/// semantics, and provenance for the signals that ARE consumed.
/// </summary>
public sealed class ScoringEngineSignalTypeFilterTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private const string SourceDescriptor = "test-src-desc";

    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => SourceDescriptor;

        public string CollectionProvenance() => "collectors=test;";

        public IReadOnlyList<string> EnabledCollectors() => ["test"];
    }

    private sealed class AllGenuineWeights : IAttentionSourceWeights
    {
        public double WeightFor(string? sourceName) => 1.0;
        public string CanonicalDescriptor() => "test-all-genuine";
    }

    /// <summary>One contribution per scored signal, so the filtered input set is directly observable.</summary>
    private sealed class StubScoreFormula : IScoreFormula
    {
        public ScoringInput? LastInput { get; private set; }

        public string Version => "stub-formula-vX";

        public ScoreComputation Compute(ScoringInput input)
        {
            LastInput = input;

            var contributions = input.Signals
                .Select(s => new ScoreContribution(
                    SignalId: s.Signal.Id,
                    EvidenceId: s.Evidence.Id,
                    ContributionReason: $"stub:{s.Signal.Type}",
                    ContributionWeight: 5))
                .ToList();

            return new ScoreComputation(
                new ScoreComponents(50, 50, 50, 50, 50),
                Explanation: $"stub explanation: {contributions.Count} contribution(s).",
                ComponentJson: "{\"stub\":true}",
                Contributions: contributions);
        }
    }

    /// <summary>The spec-136 point-in-time read contract, in memory (mirrors the real store's predicate).</summary>
    private sealed class FakeSignalFileStore : ISignalFileStore
    {
        private readonly List<Signal> _signals = new();

        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct)
        {
            _signals.Add(signal);
            return Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));
        }

        public void Seed(Signal signal) => _signals.Add(signal);

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct)
        {
            IReadOnlyList<Signal> result = _signals
                .Where(s => s.CompanyId == companyId)
                .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
                .Where(s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc)
                .Where(s => s.CreatedAtUtc <= knownAsOfUtc)
                .OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id)
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class Harness
    {
        public InMemorySignalRepository Signals { get; } = new();
        public FakeSignalFileStore SignalStore { get; } = new();
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public InMemoryCompanyRepository Companies { get; } = new();

        /// <summary>Builds one engine == one strategy, over the SAME seeded stores as its siblings.</summary>
        public (ScoringEngine Engine, InMemoryScoreRepository Scores, StubScoreFormula Formula) Strategy(
            SignalTypeFilter? filter, string? name = null)
        {
            var scores = new InMemoryScoreRepository();
            var formula = new StubScoreFormula();
            var engine = new ScoringEngine(
                Signals,
                SignalStore,
                Evidence,
                scores,
                Companies,
                formula,
                new ScoringWeights(),
                new AllGenuineWeights(),
                new StubSourceDescriptor(),
                new InsiderMaterialityWeights(),
                new MediaAttentionCollapse(new MediaCollapseOptions()),
                new ScoringOptions { Window = Window },
                NullLogger<ScoringEngine>.Instance,
                name,
                filter);
            return (engine, scores, formula);
        }

        public async Task<(Signal Signal, EvidenceItem Evidence)> SeedAsync(
            Guid companyId, SignalType type, DateTimeOffset observedAt)
        {
            var evidence = new EvidenceBuilder()
                .WithId(Guid.NewGuid())
                .WithContentHash(Guid.NewGuid().ToString("N"))
                .Build();

            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidence.Id)
                .WithCompanyId(companyId)
                .WithType(type)
                .WithDirection(SignalDirection.Positive)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(observedAt)
                .Build();

            await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
            await Signals.AddAsync(signal, CancellationToken.None);
            return (signal, evidence);
        }

        /// <summary>Seeds a PRIOR-RUN signal on disk only, so it can only reach scoring via the cross-run read.</summary>
        public Signal SeedPreviousWindow(
            Guid companyId,
            SignalType type,
            DateTimeOffset observedAt,
            SignalDirection direction = SignalDirection.Positive,
            Guid? evidenceId = null)
        {
            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidenceId ?? Guid.NewGuid())
                .WithCompanyId(companyId)
                .WithType(type)
                .WithDirection(direction)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(observedAt)
                .Build();

            SignalStore.Seed(signal);
            return signal;
        }
    }

    [Fact]
    public void NoFilter_And_AllFilter_StampTheSameScoringConfigVersion()
    {
        // The byte-identical default at the engine seam: passing SignalTypeFilter.All must be
        // indistinguishable from passing nothing at all, so the pinned default fingerprints cannot move.
        var harness = new Harness();

        var omitted = harness.Strategy(filter: null).Engine;
        var explicitAll = harness.Strategy(SignalTypeFilter.All).Engine;
        var exhaustive = harness.Strategy(SignalTypeFilter.Create(Enum.GetValues<SignalType>())).Engine;

        Assert.Equal(omitted.EffectiveConfig.Fingerprint, explicitAll.EffectiveConfig.Fingerprint);
        Assert.Equal(omitted.EffectiveConfig.Fingerprint, exhaustive.EffectiveConfig.Fingerprint);
        // And the descriptor it hashed is the raw source descriptor, unchanged.
        Assert.Equal(SourceDescriptor, omitted.EffectiveConfig.SignalSourceDescriptor);
        Assert.Equal(SourceDescriptor, exhaustive.EffectiveConfig.SignalSourceDescriptor);
    }

    [Fact]
    public void FilteredStrategies_StampDistinctScoringConfigVersions()
    {
        var harness = new Harness();

        var all = harness.Strategy(SignalTypeFilter.All).Engine;
        var a = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin])).Engine;
        var ab = harness.Strategy(
            SignalTypeFilter.Create([SignalType.CustomerWin, SignalType.ProductLaunch])).Engine;

        var fingerprints = new[]
        {
            all.EffectiveConfig.Fingerprint,
            a.EffectiveConfig.Fingerprint,
            ab.EffectiveConfig.Fingerprint,
        };

        Assert.Equal(3, fingerprints.Distinct(StringComparer.Ordinal).Count());
        // The effective config a snapshot's stamp dereferences to records WHY they differ.
        Assert.Equal($"{SourceDescriptor}signalTypes=CustomerWin;", a.EffectiveConfig.SignalSourceDescriptor);
        Assert.Equal(
            $"{SourceDescriptor}signalTypes=CustomerWin,ProductLaunch;",
            ab.EffectiveConfig.SignalSourceDescriptor);
    }

    [Fact]
    public async Task Filter_ScoresOnlyInSetSignals_SiblingStrategiesSeeTheirOwnSets()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var win = await harness.SeedAsync(companyId, SignalType.CustomerWin, WindowEnd.AddDays(-5));
        var launch = await harness.SeedAsync(companyId, SignalType.ProductLaunch, WindowEnd.AddDays(-4));
        var insider = await harness.SeedAsync(companyId, SignalType.InsiderBuying, WindowEnd.AddDays(-3));

        var onlyA = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin]), "a");
        var aAndB = harness.Strategy(
            SignalTypeFilter.Create([SignalType.CustomerWin, SignalType.ProductLaunch]), "ab");
        var everything = harness.Strategy(SignalTypeFilter.All, "all");

        var onlyAResult = await onlyA.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var aAndBResult = await aAndB.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var allResult = await everything.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal([win.Signal.Id], onlyAResult.Links.Select(l => l.SignalId).ToArray());
        Assert.Equal(
            new HashSet<Guid> { win.Signal.Id, launch.Signal.Id },
            aAndBResult.Links.Select(l => l.SignalId).ToHashSet());
        Assert.DoesNotContain(aAndBResult.Links, l => l.SignalId == insider.Signal.Id);
        Assert.Equal(3, allResult.Links.Count);
        Assert.Contains(allResult.Links, l => l.SignalId == insider.Signal.Id);

        // One collection pass, three independently-stamped scorings.
        Assert.NotEqual(
            onlyAResult.Snapshot.ScoringConfigVersion, aAndBResult.Snapshot.ScoringConfigVersion);
        Assert.NotEqual(
            onlyAResult.Snapshot.ScoringConfigVersion, allResult.Snapshot.ScoringConfigVersion);
    }

    [Fact]
    public async Task FilteredOutSignals_AreNotDeleted_AndOtherStrategiesStillSeeThem()
    {
        // The filter is a membership gate, not a provenance change: the excluded signal and its evidence are
        // untouched in the shared stores.
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var insider = await harness.SeedAsync(companyId, SignalType.InsiderBuying, WindowEnd.AddDays(-2));

        var narrow = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin]), "narrow");
        await narrow.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var stillThere = await harness.Signals.GetByCompanyAsync(companyId, CancellationToken.None);
        Assert.Contains(stillThere, s => s.Id == insider.Signal.Id);
        Assert.NotNull(
            await harness.Evidence.GetByIdAsync(insider.Evidence.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Filter_ExcludingEverySignal_ProducesTheSameNeutralOutcomeAsAZeroSignalCompany()
    {
        // The stated zero-consumed-signals semantics: a snapshot IS written, with zero evidence links, and it
        // is indistinguishable from what a company with no signals at all produces (which is exactly how
        // Radar already represents "no evidence for this company in this window").
        var harness = new Harness();

        var withSignals = Guid.NewGuid();
        await harness.SeedAsync(withSignals, SignalType.InsiderBuying, WindowEnd.AddDays(-2));
        await harness.SeedAsync(withSignals, SignalType.MediaAttention, WindowEnd.AddDays(-1));

        var noSignalsAtAll = Guid.NewGuid();

        var narrow = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin]), "narrow");

        var filteredOut = await narrow.Engine
            .ScoreCompanyAsync(withSignals, WindowEnd, CancellationToken.None);
        var genuinelyEmpty = await narrow.Engine
            .ScoreCompanyAsync(noSignalsAtAll, WindowEnd, CancellationToken.None);

        // A snapshot, no crash, zero evidence links — for BOTH.
        Assert.NotNull(filteredOut.Snapshot);
        Assert.Empty(filteredOut.Links);
        Assert.Empty(genuinelyEmpty.Links);
        Assert.Equal(genuinelyEmpty.Snapshot.Explanation, filteredOut.Snapshot.Explanation);
        Assert.Equal(
            genuinelyEmpty.Snapshot.ScoringConfigVersion, filteredOut.Snapshot.ScoringConfigVersion);
        Assert.Equal(genuinelyEmpty.Snapshot.TrajectoryScore, filteredOut.Snapshot.TrajectoryScore);

        // And it is persisted, so the strategy's series stays continuous across companies.
        var persisted = await narrow.Scores
            .GetSnapshotsForCompanyAsync(withSignals, CancellationToken.None);
        Assert.Equal(filteredOut.Snapshot.Id, Assert.Single(persisted).Id);
        Assert.Empty(
            await narrow.Scores.GetLinksForSnapshotAsync(filteredOut.Snapshot.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ConsumedSignals_KeepTheirFullEvidenceChain_InAFilteredStrategy()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var win = await harness.SeedAsync(companyId, SignalType.CustomerWin, WindowEnd.AddDays(-9));
        var launch = await harness.SeedAsync(companyId, SignalType.ProductLaunch, WindowEnd.AddDays(-8));
        await harness.SeedAsync(companyId, SignalType.MediaAttention, WindowEnd.AddDays(-7));

        var narrow = harness.Strategy(
            SignalTypeFilter.Create([SignalType.CustomerWin, SignalType.ProductLaunch]), "narrow");

        var result = await narrow.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(2, result.Links.Count);
        Assert.All(result.Links, l => Assert.Equal(result.Snapshot.Id, l.ScoreSnapshotId));

        var expected = new[]
        {
            (win.Signal.Id, win.Evidence.Id),
            (launch.Signal.Id, launch.Evidence.Id),
        };
        foreach (var link in result.Links)
        {
            // Each consumed signal links to ITS OWN evidence — the narrowing never weakens the trace.
            Assert.Contains((link.SignalId, link.EvidenceId), expected);
        }

        Assert.Equal(
            expected.OrderBy(e => e.Item1).ToArray(),
            result.Links.Select(l => (l.SignalId, l.EvidenceId)).OrderBy(e => e.Item1).ToArray());
    }

    [Fact]
    public async Task PreviousWindow_IsFilteredToo_SoVelocityIsLikeForLike()
    {
        // A filtered-out previous-window signal must not count as this strategy's prior activity — otherwise
        // the strategy would compare its narrow current window against the FULL previous one.
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var previousStart = WindowEnd - Window - Window;
        var inSet = harness.SeedPreviousWindow(
            companyId, SignalType.CustomerWin, previousStart.AddDays(5));
        harness.SeedPreviousWindow(companyId, SignalType.MediaAttention, previousStart.AddDays(6));
        harness.SeedPreviousWindow(companyId, SignalType.InsiderBuying, previousStart.AddDays(7));

        var narrow = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin]), "narrow");
        var everything = harness.Strategy(SignalTypeFilter.All, "all");

        await narrow.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        await everything.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var narrowPrevious = narrow.Formula.LastInput!.PreviousSignals;
        Assert.Equal([inSet.Id], narrowPrevious.Select(s => s.Id).ToArray());

        // The unfiltered sibling, over the same on-disk store, still sees all three.
        Assert.Equal(3, everything.Formula.LastInput!.PreviousSignals.Count);
    }

    [Fact]
    public async Task PreviousWindowFilter_DoesNotBypassTheGuidanceChangeSupersede_SoAFilingNeverCountsTwice()
    {
        // Pins that adding the spec-138 membership gate did not cost the previous window its spec-113
        // supersede. A filing first collected while the directional earnings read failed leaves a stale
        // Neutral GuidanceChange on disk alongside the directional one (the stores are append-only, AD-8),
        // and the spec-85 cross-run dedupe key includes Direction, so the read returns BOTH. If the filter
        // were ever applied to a set that had NOT been through the supersede (a re-read, or the supersede
        // dropped from this seam), that one filing would count as TWO previous-window activity items and
        // silently deflate the strategy's velocity.
        //
        // Scope, stated honestly: this pins the supersede's SURVIVAL, not its POSITION. The two orderings are
        // provably equivalent here by construction — GuidanceChangeSupersede only ever collapses signals that
        // are both GuidanceChange, so a SignalType gate can never separate the pair, and filter-then-supersede
        // yields these same results. No test at this seam can distinguish them.
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        var previousStart = WindowEnd - Window - Window;

        var filingEvidenceId = Guid.NewGuid();
        harness.SeedPreviousWindow(
            companyId, SignalType.GuidanceChange, previousStart.AddDays(5),
            SignalDirection.Neutral, filingEvidenceId);
        var directional = harness.SeedPreviousWindow(
            companyId, SignalType.GuidanceChange, previousStart.AddDays(6),
            SignalDirection.Positive, filingEvidenceId);
        var win = harness.SeedPreviousWindow(companyId, SignalType.CustomerWin, previousStart.AddDays(7));

        var guidanceOnly = harness.Strategy(
            SignalTypeFilter.Create([SignalType.GuidanceChange]), "guidance-only");
        var winOnly = harness.Strategy(SignalTypeFilter.Create([SignalType.CustomerWin]), "win-only");

        await guidanceOnly.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        await winOnly.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Includes GuidanceChange: the superseded pair is ONE activity item — the directional survivor.
        Assert.Equal(
            [directional.Id],
            guidanceOnly.Formula.LastInput!.PreviousSignals.Select(s => s.Id).ToArray());

        // Excludes GuidanceChange: neither copy of the filing counts, and the unrelated CustomerWin still does.
        Assert.Equal([win.Id], winOnly.Formula.LastInput!.PreviousSignals.Select(s => s.Id).ToArray());
    }
}
