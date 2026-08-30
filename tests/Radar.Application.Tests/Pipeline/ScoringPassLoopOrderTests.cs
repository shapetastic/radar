using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

using static Radar.Application.Tests.Pipeline.ScoreAssemblyDiagnosticsAggregationTests;

namespace Radar.Application.Tests.Pipeline;

/// <summary>
/// Spec 203 §4: <see cref="ScoringPass"/> now loops company → strategy, reading each company's stores ONCE
/// (<see cref="IScoringEngine.ReadCompanyAsync"/>) and handing the same materialised inputs to every
/// strategy's engine. The pre-203 strategy → company loop is reconstructed here, in the test project only
/// (<see cref="LegacyStrategyMajorLoop"/>), calling the per-call
/// <see cref="IScoringEngine.ScoreCompanyAsync(Guid, DateTimeOffset, CancellationToken)"/> overload — and
/// every snapshot the two loops write is diffed field-for-field (components, explanation, ComponentJson,
/// fingerprint, provenance and the ordered link chain), excluding only the per-call minted Guids.
/// </summary>
public sealed class ScoringPassLoopOrderTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid CompanyA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = new("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid CompanyC = new("cccccccc-0000-0000-0000-00000000000c");

    [Fact]
    public async Task CompanyMajorPass_WritesByteIdenticalSnapshotsToTheLegacyStrategyMajorLoop()
    {
        // Two fixtures from the same deterministic seed hold BYTE-IDENTICAL signals and evidence (AD-3), so
        // the comparison is between two loop orders over one input, not between two inputs.
        var legacy = await LoopOrderFixture.BuildAsync();
        var current = await LoopOrderFixture.BuildAsync();

        await LegacyStrategyMajorLoop(legacy);
        await current.Pass.RunAsync(current.Companies, AsOf, CancellationToken.None);

        // Three strategies × three companies on each side.
        Assert.Equal(9, legacy.ScoreStores.Written.Count);
        Assert.Equal(9, current.ScoreStores.Written.Count);

        // The WRITE ORDER differs (strategy-major vs company-major) — that is the one intended observable
        // change — so the diff is keyed by (strategy, company) rather than by position.
        var expectedByKey = legacy.ScoreStores.Written
            .ToDictionary(w => (w.Snapshot.StrategyName, w.Snapshot.CompanyId));
        Assert.Equal(9, expectedByKey.Count);

        foreach (var actual in current.ScoreStores.Written)
        {
            var expected = expectedByKey[(actual.Snapshot.StrategyName, actual.Snapshot.CompanyId)];
            AssertScoringEquivalent(expected, actual);
            // File path parity: the recording store writes every strategy to one path shape, so the
            // strategy-scoped store selection is what decides the path — asserted identical by definition
            // name here.
            Assert.Equal(expected.Strategy, actual.Strategy);
        }

        // The fixture is not vacuous: the filtered strategy scored fewer signals than the default one, the
        // channel strategy stamped a different fingerprint, and at least one company linked evidence.
        var byStrategy = current.ScoreStores.Written.GroupBy(w => w.Snapshot.StrategyName).ToDictionary(g => g.Key!, g => g.ToList());
        Assert.True(byStrategy["default"].Sum(w => w.Links.Count) > byStrategy["filtered"].Sum(w => w.Links.Count));
        Assert.NotEqual(byStrategy["default"][0].Snapshot.ScoringConfigVersion, byStrategy["channels"][0].Snapshot.ScoringConfigVersion);
        Assert.Contains(current.ScoreStores.Written, w => w.Links.Count > 0);
    }

    [Fact]
    public async Task CompanyMajorPass_ReadsEachCompanyOnce_NotOncePerStrategy()
    {
        var fixture = await LoopOrderFixture.BuildAsync();

        await fixture.Pass.RunAsync(fixture.Companies, AsOf, CancellationToken.None);

        // Three strategies, three companies: the legacy loop made 9 GetByCompany reads and 9 previous-window
        // reads; the company-major pass makes exactly one of each per company.
        Assert.Equal(3, fixture.Signals.GetByCompanyCalls);
        Assert.Equal(3, fixture.PreviousWindow.ReadCalls);
    }

    [Fact]
    public async Task CompanyMajorPass_ReportsPerStrategyTimings_AndTheWholePassElapsed()
    {
        var fixture = await LoopOrderFixture.BuildAsync();

        var result = await fixture.Pass.RunAsync(fixture.Companies, AsOf, CancellationToken.None);

        // Spec 203 §1: the pass always measures itself — populated, never null, on a pass that ran.
        Assert.NotNull(result.ScoringElapsed);

        var timingLines = fixture.PassLog.Entries
            .Where(e => e.Level == LogLevel.Information
                && e.Message.StartsWith("Scoring stage: strategy ", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();

        // ONE line per strategy (never per company), naming the strategy and the company count.
        Assert.Equal(3, timingLines.Count);
        Assert.Contains(timingLines, m => m.Contains("strategy default scored 3 company/companies in ", StringComparison.Ordinal));
        Assert.Contains(timingLines, m => m.Contains("strategy filtered scored 3 company/companies in ", StringComparison.Ordinal));
        Assert.Contains(timingLines, m => m.Contains("strategy channels scored 3 company/companies in ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScoreCompanyAsync_RefusesReadsTakenForADifferentWindow()
    {
        var fixture = await LoopOrderFixture.BuildAsync();
        var engine = fixture.Runtimes[0].Engine;

        var reads = await engine.ReadCompanyAsync(CompanyA, AsOf, CancellationToken.None);
        var foreign = reads with { Window = reads.Window + TimeSpan.FromDays(1) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.ScoreCompanyAsync(foreign, CancellationToken.None));
        Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 203 §4: the aggregate is ORDER-INDEPENDENT — the same evaluations recorded strategy-major and
    /// company-major render byte-identical lines, so moving the loop order cannot change what is reported.
    /// </summary>
    [Fact]
    public void ScoreAssemblyDiagnosticsAggregator_IsOrderIndependent()
    {
        string[] strategies = ["default", "filtered", "channels"];
        Guid[] companies = [CompanyA, CompanyB, CompanyC];
        ScoreAssemblyDiagnostics Diag(int s, int c) => new(
            UnresolvedEvidenceSignalCount: s + c,
            UnresolvedEvidenceDistinctEvidenceCount: c,
            CurrentWindowLegacyInheritanceNeutralized: s,
            CurrentWindowMalformedEnvelopeNeutralized: c == 1 ? 1 : 0,
            PreviousWindowLegacyInheritanceNeutralized: 2 * s,
            PreviousWindowMalformedEnvelopeNeutralized: c);

        var strategyMajor = new ScoreAssemblyDiagnosticsAggregator("Scoring pass");
        for (var s = 0; s < strategies.Length; s++)
        {
            for (var c = 0; c < companies.Length; c++)
            {
                strategyMajor.Record(strategies[s], companies[c], AsOf, Diag(s, c));
            }
        }

        var companyMajor = new ScoreAssemblyDiagnosticsAggregator("Scoring pass");
        for (var c = 0; c < companies.Length; c++)
        {
            for (var s = 0; s < strategies.Length; s++)
            {
                companyMajor.Record(strategies[s], companies[c], AsOf, Diag(s, c));
            }
        }

        var first = new CapturingLogger();
        var second = new CapturingLogger();
        strategyMajor.LogAggregates(first);
        companyMajor.LogAggregates(second);

        Assert.NotEmpty(first.Entries);
        Assert.Equal(first.Entries, second.Entries);
    }

    // -------------------------------------------------------------------------------------------------
    // The pre-203 loop, reconstructed: strategy → company, per-call ScoreCompanyAsync(companyId, asOf).
    // -------------------------------------------------------------------------------------------------

    private static async Task LegacyStrategyMajorLoop(LoopOrderFixture fixture)
    {
        foreach (var strategy in fixture.Runtimes)
        {
            var store = fixture.ScoreStores.ForStrategy(strategy.Definition);
            foreach (var company in fixture.Companies)
            {
                var result = await strategy.Engine.ScoreCompanyAsync(company.Id, AsOf, CancellationToken.None);
                await store.WriteAsync(result.Snapshot, result.Links, CancellationToken.None);
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The fixture: three companies; three strategies — the primary v8 over all types, a v8 strategy with a
    // SignalTypes filter, and a v10 channel strategy — over CONSTRUCTED signals that exercise every
    // strategy-dependent step (a type the filter excludes, an unresolvable evidence id, accrued spec-191
    // envelopes for neutralization, same-event media for the collapse, a previous window for velocity).
    // -------------------------------------------------------------------------------------------------

    private sealed class LoopOrderFixture
    {
        private LoopOrderFixture(
            ScoringPass pass,
            IReadOnlyList<Company> companies,
            IReadOnlyList<ScoringStrategyRuntime> runtimes,
            CountingSignalRepository signals,
            CountingSignalFileStore previousWindow,
            CapturingLogger passLog,
            StrategyRecordingScoreStoreFactory scoreStores)
        {
            Pass = pass;
            Companies = companies;
            Runtimes = runtimes;
            Signals = signals;
            PreviousWindow = previousWindow;
            PassLog = passLog;
            ScoreStores = scoreStores;
        }

        public ScoringPass Pass { get; }
        public IReadOnlyList<Company> Companies { get; }
        public IReadOnlyList<ScoringStrategyRuntime> Runtimes { get; }
        public CountingSignalRepository Signals { get; }
        public CountingSignalFileStore PreviousWindow { get; }
        public CapturingLogger PassLog { get; }
        public StrategyRecordingScoreStoreFactory ScoreStores { get; }

        public static async Task<LoopOrderFixture> BuildAsync()
        {
            var ids = new SequentialIds();
            var inner = new InMemorySignalRepository();
            var signals = new CountingSignalRepository(inner);
            var evidence = new InMemoryEvidenceRepository();
            var companyRepo = new InMemoryCompanyRepository();
            var previousWindow = new CountingSignalFileStore();
            var engineLog = new CapturingLogger();
            var passLog = new CapturingLogger();
            var scoreStores = new StrategyRecordingScoreStoreFactory();

            var companies = new List<Company>();
            foreach (var (id, tier) in new[]
                     {
                         (CompanyA, FollowingTier.Small), (CompanyB, FollowingTier.Mid), (CompanyC, FollowingTier.Small),
                     })
            {
                var company = new CompanyBuilder().WithId(id).WithFollowingTier(tier).Build();
                await companyRepo.AddAsync(company, CancellationToken.None);
                companies.Add(company);
            }

            // Company A: a rich mix — the types the filtered strategy keeps AND drops, a collector-attributed
            // news article for the channel strategy, two same-event media items for the collapse.
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.CustomerWin, SignalDirection.Positive, -3);
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.ProductLaunch, SignalDirection.Positive, -5);
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.ExecutiveHire, SignalDirection.Neutral, -9);
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.MediaAttention, SignalDirection.Neutral, -2, collector: "newssearch");
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.MediaAttention, SignalDirection.Neutral, -2, collector: "newssearch", hourOffset: 1);
            await SeedAsync(ids, inner, evidence, CompanyA, SignalType.GuidanceChange, SignalDirection.Positive, -12);
            // An unresolvable evidence id: dropped identically under both loops.
            await inner.AddAsync(
                Base(ids, CompanyA, ids.Next(), SignalType.CustomerWin, -6).WithDirection(SignalDirection.Positive).Build(),
                CancellationToken.None);

            // Company B: accrued spec-191 inherited directions (neutralized on read) and a Negative signal.
            await SeedAsync(ids, inner, evidence, CompanyB, SignalType.MediaAttention, SignalDirection.Negative, -4, metadata: LegacyEnvelope());
            await SeedAsync(ids, inner, evidence, CompanyB, SignalType.CustomerWin, SignalDirection.Negative, -7);
            await SeedAsync(ids, inner, evidence, CompanyB, SignalType.ProductLaunch, SignalDirection.Positive, -1);
            // Outside the window / after known-at: must be ignored identically.
            await SeedAsync(ids, inner, evidence, CompanyB, SignalType.CustomerWin, SignalDirection.Positive, -45);
            await inner.AddAsync(
                Base(ids, CompanyB, ids.Next(), SignalType.CustomerWin, -2).WithCreatedAtUtc(AsOf.AddDays(1)).Build(),
                CancellationToken.None);

            // Company C: nothing in the current window — the neutral zero-link snapshot on every strategy.

            // Previous/velocity windows for A and B.
            previousWindow.PreviousWindow[CompanyA] =
            [
                Base(ids, CompanyA, ids.Next(), SignalType.CustomerWin, -40).WithDirection(SignalDirection.Positive).Build(),
                Base(ids, CompanyA, ids.Next(), SignalType.MediaAttention, -35).WithDirection(SignalDirection.Neutral).Build(),
            ];
            previousWindow.PreviousWindow[CompanyB] =
            [
                Base(ids, CompanyB, ids.Next(), SignalType.ProductLaunch, -50).WithDirection(SignalDirection.Positive).Build(),
            ];

            var weights = new ScoringWeights();
            var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
            var options = new ScoringOptions();
            var descriptor = new StubSourceDescriptor();

            ScoringEngine Engine(
                ScoringStrategyDefinition definition, IScoreFormula formula, SignalTypeFilter? filter, ScoringChannelSet? channels) =>
                new(
                    signals,
                    previousWindow,
                    evidence,
                    new InMemoryScoreRepository(),
                    companyRepo,
                    formula,
                    weights,
                    attention,
                    descriptor,
                    new InsiderMaterialityWeights(),
                    new MediaAttentionCollapse(new MediaCollapseOptions()),
                    options,
                    engineLog,
                    definition.Name,
                    filter,
                    channels);

            var defaultDefinition = new ScoringStrategyDefinition("default", "default", weights, IsPrimary: true);
            var filteredTypes = SignalTypeFilter.Create([SignalType.CustomerWin, SignalType.MediaAttention]);
            var filteredDefinition = new ScoringStrategyDefinition("filtered", "default", weights, IsPrimary: false)
            {
                SignalTypes = filteredTypes,
            };
            var channels = ScoringChannelSet.Create(
                [
                    ScoringChannel.Collector("news", ["newssearch"], weight: 0.6, saturation: 4.0),
                    ScoringChannel.Breadth("breadth", weight: 0.4, saturation: 3.0),
                ],
                "channels");
            var channelDefinition = new ScoringStrategyDefinition("channels", "default", weights, IsPrimary: false)
            {
                Formula = ScoreFormulaVersions.V10,
                Channels = channels,
            };

            var runtimes = new List<ScoringStrategyRuntime>
            {
                new(defaultDefinition, Engine(defaultDefinition, new RadarScoreFormulaV8(weights, attention), null, null)),
                new(filteredDefinition, Engine(filteredDefinition, new RadarScoreFormulaV8(weights, attention), filteredTypes, null)),
                new(channelDefinition, Engine(channelDefinition, new RadarScoreFormulaV10(weights, attention, channels), null, channels)),
            };

            var pass = new ScoringPass(
                new StubStrategyFactory(runtimes),
                scoreStores,
                new StubScoringConfigStore(),
                TimeProvider.System,
                passLog);

            return new LoopOrderFixture(pass, companies, runtimes, signals, previousWindow, passLog, scoreStores);
        }

        private static async Task SeedAsync(
            SequentialIds ids,
            InMemorySignalRepository signals,
            InMemoryEvidenceRepository evidence,
            Guid companyId,
            SignalType type,
            SignalDirection direction,
            int dayOffset,
            string? collector = null,
            string? metadata = null,
            int hourOffset = 0)
        {
            var id = ids.Next();
            var builder = new EvidenceBuilder()
                .WithId(id)
                .WithContentHash(id.ToString("N"))
                .WithSourceType(type == SignalType.MediaAttention ? EvidenceSourceType.NewsArticle : EvidenceSourceType.PressRelease)
                .WithSourceName($"Outlet {id:N}")
                .WithQuality(EvidenceQuality.Medium)
                .WithPublishedAtUtc(AsOf.AddDays(dayOffset).AddHours(hourOffset))
                .WithCollectedAtUtc(AsOf.AddDays(dayOffset).AddHours(hourOffset));
            if (collector is not null)
            {
                builder = builder.WithMetadataJson(
                    EvidenceMetadata.Compose(new Dictionary<string, string> { ["collector"] = collector }, []));
            }

            var item = builder.Build();
            await evidence.AddIfNewAsync(item, CancellationToken.None);
            await signals.AddAsync(
                Base(ids, companyId, item.Id, type, dayOffset)
                    .WithObservedAtUtc(AsOf.AddDays(dayOffset).AddHours(hourOffset))
                    .WithCreatedAtUtc(AsOf.AddDays(dayOffset).AddHours(hourOffset))
                    .WithDirection(direction)
                    .WithStrength(6)
                    .WithNovelty(4)
                    .WithConfidence(0.6m)
                    .WithMetadataJson(metadata)
                    .Build(),
                CancellationToken.None);
        }

        private static SignalBuilder Base(SequentialIds ids, Guid companyId, Guid evidenceId, SignalType type, int dayOffset) =>
            new SignalBuilder()
                .WithId(ids.Next())
                .WithEvidenceId(evidenceId)
                .WithCompanyId(companyId)
                .WithType(type)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(AsOf.AddDays(dayOffset))
                .WithCreatedAtUtc(AsOf.AddDays(dayOffset));

        private static string LegacyEnvelope() => EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = "9c8f7e6d-3333-4c33-9333-cccccccccccc",
                [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "judge|p|s|stage1|families",
                [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
            },
            []);
    }

    /// <summary>Counts the per-company repository reads so "once per company" is asserted, not assumed.</summary>
    private sealed class CountingSignalRepository(ISignalRepository inner) : ISignalRepository
    {
        public int GetByCompanyCalls { get; private set; }

        public Task AddAsync(Signal signal, CancellationToken ct) => inner.AddAsync(signal, ct);

        public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct)
        {
            GetByCompanyCalls++;
            return inner.GetByCompanyAsync(companyId, ct);
        }

        public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
            DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
            inner.GetObservedBetweenAsync(startUtc, endUtc, ct);
    }

    private sealed class CountingSignalFileStore : ISignalFileStore
    {
        public Dictionary<Guid, IReadOnlyList<Signal>> PreviousWindow { get; } = [];

        public int ReadCalls { get; private set; }

        public Task<DurableWriteResult> WriteAsync(Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(PreviousWindow.GetValueOrDefault(companyId, []));
        }
    }

    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=newssearch;";

        public IReadOnlyList<string> EnabledCollectors() => ["newssearch"];
    }

    private sealed class StubStrategyFactory(IReadOnlyList<ScoringStrategyRuntime> runtimes) : IScoringStrategyFactory
    {
        public IReadOnlyList<ScoringStrategyRuntime> Runtimes { get; } = runtimes;

        public ScoringStrategyRuntime Primary => Runtimes.First(r => r.Definition.IsPrimary);
    }

    private sealed class StubScoringConfigStore : IScoringConfigStore
    {
        public Task<DurableWriteResult> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/config.json"));

        public Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<DurableWriteResult> RecordStrategyFingerprintAsync(
            string strategyName, string fingerprint, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/strategies.json"));
    }

    /// <summary>
    /// Records every write WITH the strategy whose store it went to, so path/store parity is part of the diff.
    /// </summary>
    private sealed class StrategyRecordingScoreStoreFactory : IScoreSnapshotFileStoreFactory
    {
        public List<(string Strategy, CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links)> Written { get; } = [];

        public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) => new Store(this, strategy.Name);

        private sealed class Store(StrategyRecordingScoreStoreFactory owner, string strategy) : IScoreSnapshotFileStore
        {
            public Task<DurableWriteResult> WriteAsync(
                CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct)
            {
                owner.Written.Add((strategy, snapshot, links));
                return Task.FromResult(DurableWriteResult.Succeeded($"written/{strategy}/{snapshot.CompanyId}.json"));
            }

            public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
                Task.FromResult<CompanyScoreSnapshot?>(null);

            public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(Guid companyId, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<CompanyScoreSnapshot>>([]);
        }
    }

    private static void AssertScoringEquivalent(
        (string Strategy, CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links) expected,
        (string Strategy, CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links) actual) =>
        ScoreAssemblyDiagnosticsAggregationTests.AssertScoringEquivalent(
            (expected.Snapshot, expected.Links), (actual.Snapshot, actual.Links));
}
