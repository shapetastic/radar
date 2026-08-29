using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// The spec-141 startup tripwire. The fingerprint is no longer an invariant — it is a drift detector — and
/// the thing that must not drift is a NAMED strategy's identity, because the name is now the score-series
/// key. These tests pin the four cases the spec calls out: first sighting records, unchanged passes, an
/// edited-in-place strategy fails fast naming itself, and a collector toggle does NOT trip it.
/// </summary>
public sealed class StrategyIdentityGuardTests
{
    /// <summary>A fake collector exposing a fixed name; CollectAsync is never invoked by the descriptor.</summary>
    private sealed class FakeCollector(string name) : IEvidenceCollector
    {
        public string CollectorName { get; } = name;

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The descriptor must never call CollectAsync.");
    }

    private sealed class AllGenuineWeights : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);

        public string CanonicalDescriptor() => "test-all-genuine";
    }

    /// <summary>
    /// An in-memory <see cref="IScoringConfigStore"/>: records what was written per strategy NAME and counts
    /// the writes, so a test can tell "recorded a first sighting" from "re-recorded silently".
    /// </summary>
    private sealed class FakeScoringConfigStore : IScoringConfigStore
    {
        public Dictionary<string, string> Recorded { get; } = new(StringComparer.Ordinal);

        public int RecordCallCount { get; private set; }

        /// <summary>Spec 201 §1: when set, the record write reports <see cref="DurableWriteOutcome.Failed"/>.</summary>
        public bool FailRecordWrites { get; set; }

        public Task<DurableWriteResult> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded($"written/{config.Fingerprint}.json"));

        public Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct) =>
            Task.FromResult(Recorded.GetValueOrDefault(strategyName));

        public Task<DurableWriteResult> RecordStrategyFingerprintAsync(
            string strategyName, string fingerprint, CancellationToken ct)
        {
            RecordCallCount++;
            if (FailRecordWrites)
            {
                return Task.FromResult(
                    DurableWriteResult.NotPersisted($"written/strategies/{strategyName}.json"));
            }

            Recorded[strategyName] = fingerprint;
            return Task.FromResult(DurableWriteResult.Succeeded($"written/strategies/{strategyName}.json"));
        }
    }

    /// <summary>
    /// Builds one runtime for a named strategy over the given weights and collector set, through the REAL
    /// engine + REAL SignalSourceDescriptor, so the fingerprint under test is the one a live run stamps.
    /// </summary>
    private static ScoringStrategyRuntime Runtime(
        string name, ScoringWeights weights, params string[] collectors)
    {
        var attention = new AllGenuineWeights();
        var definition = new ScoringStrategyDefinition(
            Name: name, ScoringProfile: name, Weights: weights, IsPrimary: true);
        var engine = new ScoringEngine(
            new InMemorySignalRepository(),
            new NullSignalFileStore(),
            new InMemoryEvidenceRepository(),
            new InMemoryScoreRepository(),
            new InMemoryCompanyRepository(),
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new SignalSourceDescriptor(EnabledCollectorVocabulary.FromCollectors(
                collectors.Select(c => (IEvidenceCollector)new FakeCollector(c)))),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance,
            name);

        return new ScoringStrategyRuntime(definition, engine);
    }

    private static Task VerifyAsync(IScoringConfigStore store, params ScoringStrategyRuntime[] runtimes) =>
        StrategyIdentityGuard.VerifyAsync(runtimes, store, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task FirstRun_RecordsTheStrategyIdentity_AndContinues()
    {
        var store = new FakeScoringConfigStore();
        var runtime = Runtime("momentum", new ScoringWeights(), "rss", "sec-edgar");

        await VerifyAsync(store, runtime);

        Assert.Equal(1, store.RecordCallCount);
        Assert.Equal(runtime.Engine.EffectiveConfig.Fingerprint, store.Recorded["momentum"]);
    }

    /// <summary>
    /// Spec 201 §1: a first-sighting record whose write degraded must NOT be reported as recorded. The run
    /// still continues (best-effort, AD-8), but the log says the tripwire is unarmed — no Information
    /// "Recorded first identity" line, one Warning naming the strategy and the attempted path.
    /// </summary>
    [Fact]
    public async Task FirstRun_FailedRecordWrite_WarnsInsteadOfClaimingRecorded_AndContinues()
    {
        var store = new FakeScoringConfigStore { FailRecordWrites = true };
        var runtime = Runtime("momentum", new ScoringWeights(), "rss", "sec-edgar");
        var logger = new CapturingLogger();

        await StrategyIdentityGuard.VerifyAsync([runtime], store, logger, CancellationToken.None);

        Assert.Equal(1, store.RecordCallCount);
        Assert.DoesNotContain("momentum", store.Recorded.Keys);
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Recorded first identity", StringComparison.Ordinal));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("momentum", warning.Message);
        Assert.Contains("written/strategies/momentum.json", warning.Message);
        Assert.Contains("not armed", warning.Message);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task UnchangedStrategy_Passes_AndDoesNotReRecord()
    {
        var store = new FakeScoringConfigStore();
        var weights = new ScoringWeights();

        await VerifyAsync(store, Runtime("momentum", weights, "rss", "sec-edgar"));
        var afterFirst = store.RecordCallCount;

        // A second process, same configuration: the record matches, so nothing is written and nothing throws.
        await VerifyAsync(store, Runtime("momentum", weights, "rss", "sec-edgar"));

        Assert.Equal(afterFirst, store.RecordCallCount);
    }

    [Fact]
    public async Task EditedInPlaceStrategy_ThrowsNamingTheStrategy_AndBothFingerprints()
    {
        var store = new FakeScoringConfigStore();

        await VerifyAsync(store, Runtime("momentum", new ScoringWeights(), "rss", "sec-edgar"));
        var recorded = store.Recorded["momentum"];

        // The SAME name, a genuinely different effective config (a tuned weight). The immutability convention
        // says this should have been a NEW strategy name, so the run must fail fast.
        var edited = Runtime(
            "momentum", new ScoringWeights { AttentionHalfSaturation = 12.0 }, "rss", "sec-edgar");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(store, edited));

        Assert.Contains("momentum", ex.Message, StringComparison.Ordinal);
        Assert.Contains(recorded, ex.Message, StringComparison.Ordinal);
        Assert.Contains(edited.Engine.EffectiveConfig.Fingerprint, ex.Message, StringComparison.Ordinal);
        // The message must tell the operator what to do instead, not merely that something differs.
        Assert.Contains("momentum-v2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectorToggle_DoesNotTripTheGuard()
    {
        // THE acceptance criterion this guard is only safe because of: enabling a collector must not read as
        // "the strategy was edited". Pre-141 the collector set was hashed into the fingerprint, so this exact
        // scenario would have failed every run after a collector change.
        var store = new FakeScoringConfigStore();
        var weights = new ScoringWeights();

        await VerifyAsync(store, Runtime("momentum", weights, "rss", "sec-edgar"));
        var recorded = store.Recorded["momentum"];

        var withExtraCollector = Runtime("momentum", weights, "rss", "sec-edgar", "fda");
        var withFewerCollectors = Runtime("momentum", weights, "rss");

        await VerifyAsync(store, withExtraCollector);
        await VerifyAsync(store, withFewerCollectors);

        Assert.Equal(recorded, store.Recorded["momentum"]);
        Assert.Equal(recorded, withExtraCollector.Engine.EffectiveConfig.Fingerprint);
        Assert.Equal(recorded, withFewerCollectors.Engine.EffectiveConfig.Fingerprint);
    }

    [Fact]
    public async Task PurposeOnlyEdit_DoesNotTripTheGuard()
    {
        // Spec 176: Purpose is report metadata, not scoring identity. Re-declaring an arm as a Comparator
        // (or back) must read as "nothing changed" to the tripwire — the same effective config computes the
        // same fingerprint, and nothing is re-recorded.
        var store = new FakeScoringConfigStore();
        var weights = new ScoringWeights();

        await VerifyAsync(store, Runtime("momentum", weights, "rss", "sec-edgar"));
        var recorded = store.Recorded["momentum"];
        var afterFirst = store.RecordCallCount;

        var fresh = Runtime("momentum", weights, "rss", "sec-edgar");
        var repurposed = new ScoringStrategyRuntime(
            fresh.Definition with { Purpose = StrategyPurpose.Comparator },
            fresh.Engine);

        await VerifyAsync(store, repurposed);

        Assert.Equal(recorded, store.Recorded["momentum"]);
        Assert.Equal(recorded, repurposed.Engine.EffectiveConfig.Fingerprint);
        Assert.Equal(afterFirst, store.RecordCallCount);
    }

    [Fact]
    public async Task DistinctStrategyNames_AreTrackedIndependently()
    {
        // Adding a NEW strategy name is the sanctioned way to change a strategy: it records its own identity
        // and never disturbs the existing one.
        var store = new FakeScoringConfigStore();

        var v1 = Runtime("momentum", new ScoringWeights(), "rss");
        var v2 = Runtime("momentum-v2", new ScoringWeights { AttentionHalfSaturation = 12.0 }, "rss");

        await VerifyAsync(store, v1, v2);

        Assert.Equal(v1.Engine.EffectiveConfig.Fingerprint, store.Recorded["momentum"]);
        Assert.Equal(v2.Engine.EffectiveConfig.Fingerprint, store.Recorded["momentum-v2"]);
        Assert.NotEqual(store.Recorded["momentum"], store.Recorded["momentum-v2"]);
    }

    [Fact]
    public async Task UnreadableRecord_ReadsAsUnrecorded_AndDoesNotTrip()
    {
        // Graceful degrade (AD-8): the real store logs + returns null when a record cannot be read. "Cannot
        // tell" must never be reported as "changed", so the guard records and continues rather than failing a
        // run on a disk hiccup.
        var store = new UnreadableScoringConfigStore();

        await VerifyAsync(store, Runtime("momentum", new ScoringWeights(), "rss"));

        Assert.Equal(1, store.RecordCallCount);
    }

    private sealed class UnreadableScoringConfigStore : IScoringConfigStore
    {
        public int RecordCallCount { get; private set; }

        public Task<DurableWriteResult> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written.json"));

        public Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<DurableWriteResult> RecordStrategyFingerprintAsync(
            string strategyName, string fingerprint, CancellationToken ct)
        {
            RecordCallCount++;
            return Task.FromResult(DurableWriteResult.Succeeded("written.json"));
        }
    }

    /// <summary>A no-op signal file store: the guard never scores, so nothing is read or written.</summary>
    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }
}
