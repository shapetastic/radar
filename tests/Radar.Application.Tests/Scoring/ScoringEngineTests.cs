using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class ScoringEngineTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The harness's scoring window. DELIBERATELY NOT the <see cref="ScoringOptions"/> default of 30 days
    /// (spec 148): every windowing test here expresses its boundaries relative to this constant, so the value
    /// itself is free — and choosing a non-default one makes
    /// <see cref="Versioning_StampsScoringConfigVersion"/> DISCRIMINATING. While it coincided with the default,
    /// that test could not tell the engine reading <c>ScoringOptions.Window</c> from the engine hard-coding
    /// 30 days, which is precisely the wiring spec 148 added.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(21);

    /// <summary>
    /// In-test <see cref="IAttentionSourceWeights"/> for the real formula: every publisher counts as a full
    /// genuine outlet (weight 1.0). These orchestration tests exercise Trajectory/Velocity over first-party
    /// (Filing/PressRelease) evidence, so Attention is 0 regardless — the weights only need to satisfy the
    /// RadarScoreFormulaV8 constructor.
    /// </summary>
    private static readonly IAttentionSourceWeights Weights = new AllGenuineWeights();

    private sealed class AllGenuineWeights : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);
        public string CanonicalDescriptor() => "test-all-genuine";
    }

    /// <summary>
    /// In-test <see cref="ISignalSourceDescriptor"/> with a fixed descriptor: the engine folds it into the
    /// fingerprint + EffectiveConfig (spec 95). Tests recomputing the fingerprint directly pass the same
    /// literal so equality holds.
    /// </summary>
    private const string SourceDescriptor = "test-src-desc";

    /// <summary>The collection-provenance string this stub records (spec 141): recorded, never hashed.</summary>
    private const string SourceProvenance = "collectors=test-a,test-b;";

    private sealed class StubSourceDescriptor(string provenance = SourceProvenance) : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => SourceDescriptor;

        public string CollectionProvenance() => provenance;

        /// <summary>Spec 146: recorded provenance only — hashed into nothing, never a scoring input.</summary>
        public IReadOnlyList<string> EnabledCollectors() => ["test-a", "test-b"];
    }

    private static readonly ISignalSourceDescriptor SourceDesc = new StubSourceDescriptor();

    /// <summary>
    /// In-test formula stub: returns a fixed, in-range computation and echoes exactly one
    /// provenance-carrying contribution per input signal. Keeps orchestration tests decoupled from any
    /// real formula's internals — assertions are about windowing/traceability/range, never weights.
    /// </summary>
    private sealed class StubScoreFormula : IScoreFormula
    {
        public string Version => "stub-formula-vX";

        public ScoreComputation Compute(ScoringInput input)
        {
            var contributions = input.Signals
                .Select(s => new ScoreContribution(
                    SignalId: s.Signal.Id,
                    EvidenceId: s.Evidence.Id,
                    ContributionReason: $"stub:{s.Signal.Id}",
                    ContributionWeight: 5))
                .ToList();

            var components = new ScoreComponents(
                TrajectoryScore: 50,
                OpportunityScore: 50,
                AttentionScore: 50,
                EvidenceConfidenceScore: 50,
                SignalVelocityScore: 50);

            return new ScoreComputation(
                components,
                Explanation: $"stub explanation: {contributions.Count} contribution(s).",
                ComponentJson: "{\"stub\":true}",
                Contributions: contributions);
        }
    }

    /// <summary>
    /// In-test formula that records the last <see cref="ScoringInput"/> it received so windowing-input
    /// tests can assert exactly what the engine handed the formula. Returns a valid all-zero
    /// computation with no contributions (provenance is asserted elsewhere).
    /// </summary>
    private sealed class CapturingScoreFormula : IScoreFormula
    {
        public ScoringInput? LastInput { get; private set; }

        public string Version => "capturing-formula-vX";

        public ScoreComputation Compute(ScoringInput input)
        {
            LastInput = input;

            var components = new ScoreComponents(
                TrajectoryScore: 0,
                OpportunityScore: 0,
                AttentionScore: 0,
                EvidenceConfidenceScore: 0,
                SignalVelocityScore: 0);

            return new ScoreComputation(
                components,
                Explanation: "capturing formula: zero.",
                ComponentJson: "{}",
                Contributions: new List<ScoreContribution>());
        }
    }

    /// <summary>
    /// An in-test <see cref="ISignalFileStore"/> standing in for the on-disk signal store. Records written
    /// signals and any test-seeded prior-run signals in a list, and implements
    /// <see cref="ReadApprovedInWindowAsync"/> by filtering that list exactly as the real store's contract
    /// (companyId + Approved + <c>(start, end]</c> + <c>CreatedAtUtc &lt;= knownAsOfUtc</c> — the spec-136
    /// point-in-time predicate — ordered by ObservedAtUtc then Id). Lets tests place prior-run signals
    /// "on disk" without touching the in-memory signal repository.
    /// </summary>
    private sealed class FakeSignalFileStore : ISignalFileStore
    {
        private readonly List<Signal> _signals = new();

        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct)
        {
            _signals.Add(signal);
            return Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));
        }

        /// <summary>Seeds a prior-run signal "on disk" only (not into the in-memory repo).</summary>
        public void Seed(Signal signal) => _signals.Add(signal);

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
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
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryCompanyRepository Companies { get; } = new();
        public ScoringEngine Engine { get; }

        public Harness(
            IScoreFormula? formula = null,
            ScoringWeights? weights = null,
            ILogger<ScoringEngine>? logger = null,
            ISignalSourceDescriptor? sourceDescriptor = null,
            ScoringChannelSet? channels = null,
            TimeSpan? window = null)
        {
            Engine = new ScoringEngine(
                Signals,
                SignalStore,
                Evidence,
                Scores,
                Companies,
                formula ?? new StubScoreFormula(),
                weights ?? new ScoringWeights(),
                Weights,
                sourceDescriptor ?? SourceDesc,
                new InsiderMaterialityWeights(),
                new MediaAttentionCollapse(new MediaCollapseOptions()),
                new ScoringOptions { Window = window ?? Window },
                logger ?? NullLogger<ScoringEngine>.Instance,
                channels: channels);
        }

        /// <summary>
        /// Seeds an Approved in-window signal pointing at an evidence id that was never stored, optionally
        /// SHARING that id with another signal. Lets a test distinguish "N dropped signals" from
        /// "N distinct unresolvable evidence items" — the two numbers the aggregated warning carries.
        /// </summary>
        public async Task<Signal> SeedSignalWithUnresolvableEvidenceAsync(
            Guid companyId, DateTimeOffset observedAt, Guid evidenceId, SignalType type = SignalType.CustomerWin)
        {
            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidenceId)
                .WithCompanyId(companyId)
                .WithType(type)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(observedAt)
                .Build();

            await Signals.AddAsync(signal, CancellationToken.None);
            return signal;
        }

        public async Task<(Signal signal, EvidenceItem evidence)> SeedPairAsync(
            Guid companyId,
            DateTimeOffset observedAt,
            SignalReviewStatus status = SignalReviewStatus.Approved,
            bool storeEvidence = true,
            DateTimeOffset? createdAt = null)
        {
            var evidence = new EvidenceBuilder()
                .WithId(Guid.NewGuid())
                .WithContentHash(Guid.NewGuid().ToString("N"))
                .Build();

            var builder = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidence.Id)
                .WithCompanyId(companyId)
                .WithReviewStatus(status)
                .WithObservedAtUtc(observedAt);
            if (createdAt is not null)
            {
                builder = builder.WithCreatedAtUtc(createdAt.Value);
            }

            var signal = builder.Build();

            if (storeEvidence)
            {
                await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
            }

            await Signals.AddAsync(signal, CancellationToken.None);
            return (signal, evidence);
        }

        /// <summary>
        /// Seeds an ALREADY-BUILT signal + evidence pair, so two harnesses can be given byte-identical inputs
        /// (same ids, same content) and their outputs compared field-for-field.
        /// </summary>
        public async Task SeedExistingAsync(Signal signal, EvidenceItem evidence)
        {
            await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
            await Signals.AddAsync(signal, CancellationToken.None);
        }

        /// <summary>
        /// Seeds a prior-run Approved signal ON DISK only (via the fake signal file store), representing a
        /// signal persisted by an earlier process. It is NOT added to the in-memory <see cref="Signals"/>
        /// repository, so it can only reach scoring through the cross-run read-back.
        /// </summary>
        public Signal SeedPriorRunSignalOnDisk(
            Guid companyId,
            DateTimeOffset observedAt,
            SignalReviewStatus status = SignalReviewStatus.Approved,
            int strength = 6,
            DateTimeOffset? createdAt = null)
        {
            var builder = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(Guid.NewGuid())
                .WithCompanyId(companyId)
                .WithReviewStatus(status)
                .WithStrength(strength)
                .WithObservedAtUtc(observedAt);
            if (createdAt is not null)
            {
                builder = builder.WithCreatedAtUtc(createdAt.Value);
            }

            var signal = builder.Build();
            SignalStore.Seed(signal);
            return signal;
        }
    }

    [Fact]
    public async Task WindowFilter_ExcludesSignalsOutsideTheWindow()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var inside = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-5));
        // Before the window (at or before exclusive start) and after the inclusive end.
        await harness.SeedPairAsync(companyId, WindowEnd - Window); // exactly at exclusive start -> excluded
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(1)); // after end -> excluded

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(inside.signal.Id, result.Links[0].SignalId);
    }

    [Fact]
    public async Task WindowFilter_IncludesSignalAtInclusiveEnd()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var atEnd = await harness.SeedPairAsync(companyId, WindowEnd);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(atEnd.signal.Id, result.Links[0].SignalId);
    }

    [Theory]
    [InlineData(SignalReviewStatus.Pending)]
    [InlineData(SignalReviewStatus.NeedsHumanReview)]
    [InlineData(SignalReviewStatus.Rejected)]
    public async Task ReviewFilter_ExcludesNonApprovedSignals(SignalReviewStatus status)
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var approved = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2), status);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(approved.signal.Id, result.Links[0].SignalId);
    }

    [Fact]
    public async Task MissingEvidence_IsExcluded_AndEngineSucceeds()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var withEvidence = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));
        var missing = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3), storeEvidence: false);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(withEvidence.signal.Id, result.Links[0].SignalId);
        Assert.DoesNotContain(result.Links, l => l.SignalId == missing.signal.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // Dropped-signal provenance: RETURNED, not warned (spec 145's counts, spec 197 §3's ownership).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DroppedSignals_AreReturnedOnTheDiagnostics_AndTheEngineEmitsNoWarning()
    {
        // Spec 145 replaced a per-signal Warning (~9,500 lines per run PER STRATEGY on the live store) with
        // one per COMPANY. But this engine IS one strategy, so that was still one line per strategy ×
        // company — 397 of them on the live baseline. Spec 197 §3 keeps every count and moves the Warning to
        // the caller that can see the whole grid. Nothing is silenced: an unresolvable evidence chain is a
        // real provenance defect and both numbers are on the returned record.
        var logger = new CapturingLogger<ScoringEngine>();
        var harness = new Harness(logger: logger);
        var companyId = Guid.NewGuid();

        var resolvable = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));

        // Three dropped signals over TWO distinct evidence ids — the two counts must differ, so a test
        // cannot pass by reporting one number twice.
        var sharedEvidenceId = Guid.NewGuid();
        await harness.SeedSignalWithUnresolvableEvidenceAsync(
            companyId, WindowEnd.AddDays(-3), sharedEvidenceId, SignalType.CustomerWin);
        await harness.SeedSignalWithUnresolvableEvidenceAsync(
            companyId, WindowEnd.AddDays(-3), sharedEvidenceId, SignalType.ProductLaunch);
        await harness.SeedSignalWithUnresolvableEvidenceAsync(
            companyId, WindowEnd.AddDays(-2), Guid.NewGuid(), SignalType.ExecutiveHire);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Only the resolvable signal is scored — the drop behaviour itself is unchanged.
        Assert.Equal(resolvable.signal.Id, Assert.Single(result.Links).SignalId);

        // Both counts survive, on the transient diagnostics record.
        Assert.Equal(3, result.Diagnostics.UnresolvedEvidenceSignalCount);
        Assert.Equal(2, result.Diagnostics.UnresolvedEvidenceDistinctEvidenceCount);
        Assert.True(result.Diagnostics.HasUnresolvedEvidence);
        Assert.False(result.Diagnostics.HasNeutralization);

        // The engine itself warns about NOTHING here — that is the whole point of §3.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);

        // Per-signal detail is retained, at Debug — nothing is silenced, only re-levelled (spec 145).
        Assert.Equal(
            3,
            logger.Entries.Count(e => e.Level == LogLevel.Debug
                && e.Message.Contains("Dropping signal", StringComparison.Ordinal)));

        // …plus ONE bounded per-evaluation Debug summary, so the pooled aggregate stays recoverable.
        var debugSummary = Assert.Single(
            logger.Entries
                .Where(e => e.Level == LogLevel.Debug
                    && e.Message.StartsWith("Score assembly diagnostics", StringComparison.Ordinal))
                .Select(e => e.Message));
        Assert.Contains("3 signal(s) dropped for unresolved evidence", debugSummary, StringComparison.Ordinal);
        Assert.Contains("2 distinct evidence id(s)", debugSummary, StringComparison.Ordinal);
        Assert.Contains(companyId.ToString(), debugSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDroppedSignals_ReportsNothing_AndLogsNoWarningOrDiagnosticLine()
    {
        var logger = new CapturingLogger<ScoringEngine>();
        var harness = new Harness(logger: logger);
        var companyId = Guid.NewGuid();

        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(ScoreAssemblyDiagnostics.None, result.Diagnostics);
        Assert.False(result.Diagnostics.HasAny);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);

        // The unaffected path logs nothing new at all — not even the bounded Debug summary.
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Message.StartsWith("Score assembly diagnostics", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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
    public async Task Traceability_OneLinkPerContribution_WithMatchingProvenance()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var a = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-10));
        var b = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-5));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(2, result.Links.Count);
        Assert.All(result.Links, l => Assert.Equal(result.Snapshot.Id, l.ScoreSnapshotId));

        var seeded = new[]
        {
            (a.signal.Id, a.evidence.Id),
            (b.signal.Id, b.evidence.Id),
        };
        foreach (var link in result.Links)
        {
            Assert.Contains((link.SignalId, link.EvidenceId), seeded);
        }
    }

    [Fact]
    public async Task MediaCollapse_ManySameEventMediaSignals_CollapseToOne_PositivesUnaffected_ProvenanceIntact()
    {
        // Spec 109: 23 same-event MediaAttention signals (all within the default 3-day window) collapse to ONE
        // representative link (carrying the "collapsed 22 same-event media items" note), while 5 positive
        // directional signals are unaffected — and every scored signal still has a provenance link.
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        // 23 MediaAttention signals bunched within a 2-day span (< 3-day EventWindow) → one event.
        var mediaSignalIds = new List<Guid>();
        for (var i = 0; i < 23; i++)
        {
            var (signal, _) = await SeedTypedPairAsync(
                harness, companyId, WindowEnd.AddDays(-10).AddHours(i * 2), SignalType.MediaAttention);
            mediaSignalIds.Add(signal.Id);
        }

        // 5 positive directional signals elsewhere in the window (distinct type, untouched by the collapse).
        var positiveSignalIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var (signal, _) = await SeedTypedPairAsync(
                harness, companyId, WindowEnd.AddDays(-3).AddHours(i), SignalType.CustomerWin);
            positiveSignalIds.Add(signal.Id);
        }

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // 1 media representative + 5 positives = 6 links.
        Assert.Equal(6, result.Links.Count);

        // Exactly one link traces to a media signal, and it names the collapsed count.
        var mediaLinks = result.Links.Where(l => mediaSignalIds.Contains(l.SignalId)).ToList();
        var mediaLink = Assert.Single(mediaLinks);
        Assert.Contains("collapsed 22 same-event media items", mediaLink.ContributionReason);

        // All 5 positives are present and unaffected (no collapse note).
        foreach (var positiveId in positiveSignalIds)
        {
            var link = Assert.Single(result.Links, l => l.SignalId == positiveId);
            Assert.DoesNotContain("collapsed", link.ContributionReason);
        }

        // Every scored signal still has full provenance (a non-empty SignalId + EvidenceId).
        Assert.All(result.Links, l =>
        {
            Assert.NotEqual(Guid.Empty, l.SignalId);
            Assert.NotEqual(Guid.Empty, l.EvidenceId);
        });
    }

    /// <summary>Seeds an Approved signal (with evidence) of a given type + observation time into the repo.</summary>
    private static async Task<(Signal signal, EvidenceItem evidence)> SeedTypedPairAsync(
        Harness harness, Guid companyId, DateTimeOffset observedAt, SignalType type)
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
            .WithDirection(type == SignalType.MediaAttention ? SignalDirection.Neutral : SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAt)
            .Build();

        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        await harness.Signals.AddAsync(signal, CancellationToken.None);
        return (signal, evidence);
    }

    [Fact]
    public async Task ComponentScores_AreWithinInclusiveRange()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var s = result.Snapshot;

        foreach (var score in new[]
        {
            s.TrajectoryScore, s.OpportunityScore, s.AttentionScore,
            s.EvidenceConfidenceScore, s.SignalVelocityScore,
        })
        {
            Assert.InRange(score, 0, 100);
        }
    }

    [Fact]
    public async Task Versioning_RecordsBothEngineAndFormulaVersions()
    {
        var formula = new StubScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Contains("mvp-engine-v1", result.Snapshot.ScoringVersion);
        Assert.Contains(formula.Version, result.Snapshot.ScoringVersion);
    }

    [Fact]
    public async Task Versioning_StampsScoringConfigVersion()
    {
        var formula = new StubScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // The stamp is now a deterministic content fingerprint of the effective resolved scoring config
        // (AD-10 amended): recompute it with the SAME inputs the engine used (engine version mvp-engine-v1,
        // the formula's Version, default weights, the source-weights descriptor) and assert equality.
        var expected = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", formula.Version, new ScoringWeights(), Weights.CanonicalDescriptor(),
            SourceDescriptor, new InsiderMaterialityWeights().CanonicalDescriptor(),
            new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor(),
            // Spec 148: the recent-signal window is a hashed field now. This is the HARNESS's window, which is
            // deliberately NOT the ScoringOptions default (see the constant's note) — so this assertion fails
            // if the engine ever stops reading _options.Window and hashes a hard-coded default instead.
            Window);
        Assert.Equal(expected, result.Snapshot.ScoringConfigVersion);
    }

    /// <summary>
    /// THE spec-148 acceptance criterion, at the level the spec actually asks for: <b>the same inputs scored
    /// under two window lengths</b>, through two REAL engines that differ in nothing else.
    /// <para>
    /// The pure <c>ScoringConfigFingerprint.Compute</c> tests prove the FIELD is hashed; they cannot prove the
    /// engine feeds it <c>ScoringOptions.Window</c> rather than a constant. Hard-coding 30 days at either of
    /// the two production sites (the <c>Compute</c> call and the <c>EffectiveScoringConfig</c> projection)
    /// silently reintroduces the whole defect — a 14-day and a 30-day run sharing one
    /// <c>ScoringConfigVersion</c> and persisting a false 30-day window — so this test exists to kill exactly
    /// those two mutations, and was verified against both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoEnginesDifferingOnlyInScoringWindow_StampDifferentConfigVersions_AndRecordTheirOwnWindow()
    {
        var thirtyDays = TimeSpan.FromDays(30);
        var fourteenDays = TimeSpan.FromDays(14);

        var wide = new Harness(new StubScoreFormula(), window: thirtyDays);
        var narrow = new Harness(new StubScoreFormula(), window: fourteenDays);

        // Byte-identical inputs on both sides — same ids, same content — so any difference below is the
        // window and nothing else.
        var companyId = Guid.NewGuid();
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash("window-fixture-hash")
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-2))
            .Build();

        await wide.SeedExistingAsync(signal, evidence);
        await narrow.SeedExistingAsync(signal, evidence);

        // (a) Two windows are two scorings: their identities must differ.
        Assert.NotEqual(
            wide.Engine.EffectiveConfig.Fingerprint, narrow.Engine.EffectiveConfig.Fingerprint);

        // (b) Each persisted config records ITS OWN window — not the default, and not the other engine's.
        //     Neither value may be assumed: 30 days happens to BE the ScoringOptions default, so only the
        //     14-day side can catch a hard-coded default, and only the 30-day side proves the field is
        //     carried rather than left null.
        Assert.Equal(thirtyDays, wide.Engine.EffectiveConfig.Window);
        Assert.Equal(fourteenDays, narrow.Engine.EffectiveConfig.Window);

        // (c) …and the difference reaches the SNAPSHOT, which is what a historical reader dereferences.
        var wideSnapshot = await wide.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var narrowSnapshot =
            await narrow.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.NotEqual(
            wideSnapshot.Snapshot.ScoringConfigVersion, narrowSnapshot.Snapshot.ScoringConfigVersion);
        Assert.Equal(
            wide.Engine.EffectiveConfig.Fingerprint, wideSnapshot.Snapshot.ScoringConfigVersion);
        Assert.Equal(
            narrow.Engine.EffectiveConfig.Fingerprint, narrowSnapshot.Snapshot.ScoringConfigVersion);

        // Not vacuous in the other direction either: the window really is the operative difference, so the
        // two engines slice the SAME window bounds they hashed.
        Assert.Equal(WindowEnd - thirtyDays, wideSnapshot.Snapshot.WindowStartUtc);
        Assert.Equal(WindowEnd - fourteenDays, narrowSnapshot.Snapshot.WindowStartUtc);
    }

    [Fact]
    public async Task CollectorToggle_SameScoringConfigVersion_DifferentCollectionProvenance_IdenticalScores()
    {
        // THE spec-141 acceptance criterion, end to end through the engine and under the REAL formula and the
        // REAL SignalSourceDescriptor: two engines that differ ONLY in the enabled collector set must
        //   * stamp the SAME ScoringConfigVersion (a collector toggle is not a strategy change),
        //   * stamp DIFFERENT CollectionProvenance (what was collected IS recorded), and
        //   * produce byte-identical scores — every component, the explanation, the component JSON, and the
        //     evidence links (provenance) — because nothing about the scoring math moved.
        // Identical inputs are seeded into both harnesses by VALUE (same signal id, same evidence id), so a
        // field-for-field comparison is meaningful rather than trivially true.
        var companyId = Guid.NewGuid();

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash("collector-toggle-hash")
            .WithSourceType(EvidenceSourceType.Filing)
            .WithQuality(EvidenceQuality.High)
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.CustomerWin)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-2))
            .Build();

        static ISignalSourceDescriptor DescriptorOver(params string[] names) =>
            new SignalSourceDescriptor(EnabledCollectorVocabulary.FromCollectors(
                names.Select(n => (IEvidenceCollector)new NamedFakeCollector(n))));

        var sixCollectors = new Harness(
            new RadarScoreFormulaV8(new ScoringWeights(), Weights),
            sourceDescriptor: DescriptorOver(
                "RssPressReleaseCollector", "newssearch", "sec-13dg", "sec-edgar", "sec-form4", "usaspending"));
        var sevenCollectors = new Harness(
            new RadarScoreFormulaV8(new ScoringWeights(), Weights),
            sourceDescriptor: DescriptorOver(
                "RssPressReleaseCollector", "fda", "newssearch", "sec-13dg", "sec-edgar", "sec-form4",
                "usaspending"));

        await sixCollectors.SeedExistingAsync(signal, evidence);
        await sevenCollectors.SeedExistingAsync(signal, evidence);

        var six = await sixCollectors.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var seven = await sevenCollectors.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Identity: unmoved. This is the whole point of the descriptor split.
        Assert.Equal(six.Snapshot.ScoringConfigVersion, seven.Snapshot.ScoringConfigVersion);
        Assert.Equal(
            sixCollectors.Engine.EffectiveConfig.Fingerprint,
            sevenCollectors.Engine.EffectiveConfig.Fingerprint);

        // Provenance: recorded, and different — "what was collected" is not lost, just relocated.
        Assert.NotEqual(six.Snapshot.CollectionProvenance, seven.Snapshot.CollectionProvenance);
        Assert.Equal(
            "collectors=RssPressReleaseCollector,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;",
            six.Snapshot.CollectionProvenance);
        Assert.Equal(
            "collectors=RssPressReleaseCollector,fda,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;",
            seven.Snapshot.CollectionProvenance);

        // Scores: byte-identical, component for component.
        Assert.Equal(six.Snapshot.TrajectoryScore, seven.Snapshot.TrajectoryScore);
        Assert.Equal(six.Snapshot.OpportunityScore, seven.Snapshot.OpportunityScore);
        Assert.Equal(six.Snapshot.AttentionScore, seven.Snapshot.AttentionScore);
        Assert.Equal(six.Snapshot.EvidenceConfidenceScore, seven.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(six.Snapshot.SignalVelocityScore, seven.Snapshot.SignalVelocityScore);
        Assert.Equal(six.Snapshot.Explanation, seven.Snapshot.Explanation);
        Assert.Equal(six.Snapshot.ComponentJson, seven.Snapshot.ComponentJson);
        Assert.Equal(six.Snapshot.ScoringVersion, seven.Snapshot.ScoringVersion);

        // Provenance chain: the same contributing signal/evidence with the same weight and reason. (Link Ids
        // and ScoreSnapshotIds are freshly minted per call by design, so they are excluded — exactly as the
        // spec-139 replay ⊆ forward comparison excludes them.)
        Assert.Equal(
            six.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)),
            seven.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)));
        Assert.NotEmpty(six.Links);
    }

    [Fact]
    public async Task AttributionModeToggle_SameScoringConfigVersion_DifferentCollectionProvenance_IdenticalScores()
    {
        // THE spec-151 no-fingerprint-move criterion, end to end through the engine and under the REAL
        // SignalSourceDescriptor — deliberately mirroring the spec-141 collector-toggle test above, because it
        // is the same claim about a different fact: collector ATTRIBUTION is data, not scoring configuration.
        // Two engines differing ONLY in Radar:Scoring:InferLegacyCollectorAttribution must
        //   * stamp the SAME ScoringConfigVersion (so the four pinned fingerprint pairs do not move, and
        //     StrategyIdentityGuard does not trip on an operator flipping the setting),
        //   * stamp DIFFERENT CollectionProvenance (a series scored over re-derived attribution must say so),
        //   * and produce byte-identical scores here — this is a v8 strategy, which never reads attribution at
        //     all, so the setting is inert for it.
        var companyId = Guid.NewGuid();

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash("attribution-toggle-hash")
            .WithSourceType(EvidenceSourceType.Filing)
            .WithQuality(EvidenceQuality.High)
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.CustomerWin)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-2))
            .Build();

        static ISignalSourceDescriptor DescriptorWith(bool infer) =>
            new SignalSourceDescriptor(
                EnabledCollectorVocabulary.FromCollectors(
                    new[] { "sec-edgar", "sec-form4" }.Select(n => (IEvidenceCollector)new NamedFakeCollector(n))),
                aiFilingSource: null,
                collectionPass: null,
                new CollectorAttributionOptions { InferLegacyAttribution = infer });

        var recordedOnlyHarness = new Harness(
            new RadarScoreFormulaV8(new ScoringWeights(), Weights),
            sourceDescriptor: DescriptorWith(infer: false));
        var inferringHarness = new Harness(
            new RadarScoreFormulaV8(new ScoringWeights(), Weights),
            sourceDescriptor: DescriptorWith(infer: true));

        await recordedOnlyHarness.SeedExistingAsync(signal, evidence);
        await inferringHarness.SeedExistingAsync(signal, evidence);

        var recordedOnly =
            await recordedOnlyHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var inferring =
            await inferringHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Identity: unmoved.
        Assert.Equal(recordedOnly.Snapshot.ScoringConfigVersion, inferring.Snapshot.ScoringConfigVersion);
        Assert.Equal(
            recordedOnlyHarness.Engine.EffectiveConfig.Fingerprint,
            inferringHarness.Engine.EffectiveConfig.Fingerprint);
        Assert.DoesNotContain(
            "attribution=",
            inferringHarness.Engine.EffectiveConfig.SignalSourceDescriptor,
            StringComparison.Ordinal);

        // Provenance: recorded, and different.
        Assert.Equal("collectors=sec-edgar,sec-form4;", recordedOnly.Snapshot.CollectionProvenance);
        Assert.Equal(
            "collectors=sec-edgar,sec-form4;attribution=inferred-legacy;",
            inferring.Snapshot.CollectionProvenance);

        // Scores: byte-identical, component for component.
        Assert.Equal(recordedOnly.Snapshot.TrajectoryScore, inferring.Snapshot.TrajectoryScore);
        Assert.Equal(recordedOnly.Snapshot.OpportunityScore, inferring.Snapshot.OpportunityScore);
        Assert.Equal(recordedOnly.Snapshot.AttentionScore, inferring.Snapshot.AttentionScore);
        Assert.Equal(
            recordedOnly.Snapshot.EvidenceConfidenceScore, inferring.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(recordedOnly.Snapshot.SignalVelocityScore, inferring.Snapshot.SignalVelocityScore);
        Assert.Equal(recordedOnly.Snapshot.Explanation, inferring.Snapshot.Explanation);
        Assert.Equal(recordedOnly.Snapshot.ComponentJson, inferring.Snapshot.ComponentJson);
        Assert.Equal(recordedOnly.Snapshot.ScoringVersion, inferring.Snapshot.ScoringVersion);

        // Provenance chain: identical contributing signal/evidence, weight and reason.
        Assert.Equal(
            recordedOnly.Links.Select(l =>
                (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)),
            inferring.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight)));
        Assert.NotEmpty(recordedOnly.Links);
    }

    [Fact]
    public async Task Versioning_ChangedWeight_StampsDifferentScoringConfigVersion()
    {
        var companyId = Guid.NewGuid();

        var defaultHarness = new Harness();
        await defaultHarness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));
        var defaultResult =
            await defaultHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var changedHarness = new Harness(weights: new ScoringWeights { AttentionHalfSaturation = 12.0 });
        await changedHarness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));
        var changedResult =
            await changedHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // A changed weight re-stamps the generation fingerprint automatically (AD-10 property, now automatic).
        Assert.NotEqual(
            defaultResult.Snapshot.ScoringConfigVersion,
            changedResult.Snapshot.ScoringConfigVersion);
    }

    [Fact]
    public async Task EffectiveConfig_MatchesStampedFingerprint_AndCarriesInjectedInputs()
    {
        // The engine's EffectiveConfig is a pure accessor built from the SAME inputs the fingerprint uses,
        // so EffectiveConfig.Fingerprint equals the ScoringConfigVersion stamped on every snapshot it
        // produces — the content-addressed persistence key (spec 91) dereferences back to these inputs.
        var defaultWeights = new ScoringWeights();
        var defaultHarness = new Harness(new StubScoreFormula(), defaultWeights);
        var companyId = Guid.NewGuid();
        await defaultHarness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var defaultResult =
            await defaultHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var defaultConfig = defaultHarness.Engine.EffectiveConfig;

        Assert.Equal(defaultResult.Snapshot.ScoringConfigVersion, defaultConfig.Fingerprint);

        // EffectiveConfig carries the injected structure identities, weights, and attention descriptor.
        Assert.Equal("mvp-engine-v1", defaultConfig.EngineVersion);
        Assert.Equal("stub-formula-vX", defaultConfig.FormulaVersion);
        Assert.Equal(defaultWeights, defaultConfig.Weights);
        Assert.Equal(Weights.CanonicalDescriptor(), defaultConfig.AttentionDescriptor);
        Assert.Equal(SourceDescriptor, defaultConfig.SignalSourceDescriptor);
        Assert.Equal(new InsiderMaterialityWeights().CanonicalDescriptor(), defaultConfig.InsiderMaterialityDescriptor);
        Assert.Equal(
            new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor(),
            defaultConfig.MediaCollapseDescriptor);

        // Under a changed weight a second engine's EffectiveConfig differs and still matches its own stamp.
        var changedWeights = new ScoringWeights { AttentionHalfSaturation = 12.0 };
        var changedHarness = new Harness(new StubScoreFormula(), changedWeights);
        await changedHarness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var changedResult =
            await changedHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var changedConfig = changedHarness.Engine.EffectiveConfig;

        Assert.Equal(changedResult.Snapshot.ScoringConfigVersion, changedConfig.Fingerprint);
        Assert.NotEqual(defaultConfig.Fingerprint, changedConfig.Fingerprint);
        Assert.Equal(12.0, changedConfig.Weights.AttentionHalfSaturation);
    }

    [Theory]
    [InlineData(SignalDirection.Positive, true)]   // a beat lifts Trajectory above the 50 baseline
    [InlineData(SignalDirection.Negative, false)]  // a miss lowers it below 50
    public async Task DirectionalGuidanceChange_OverFilingEvidence_MovesTrajectory(
        SignalDirection direction, bool aboveBaseline)
    {
        // Spec 75: a directional GuidanceChange (the AI earnings read) over Filing evidence moves
        // Trajectory the right way under the real radar-formula-v8 — a beat up, a miss down.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithSourceType(EvidenceSourceType.Filing)
            .WithQuality(EvidenceQuality.High)
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithStrength(6)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-1))
            .Build();

        // The signal passes domain validation (all fields in range).
        Assert.True(Radar.Domain.Validation.SignalValidation.IsValid(signal));

        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        await harness.Signals.AddAsync(signal, CancellationToken.None);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        if (aboveBaseline)
        {
            Assert.True(result.Snapshot.TrajectoryScore > 50);
        }
        else
        {
            Assert.True(result.Snapshot.TrajectoryScore < 50);
        }
    }

    [Fact]
    public async Task NeutralGuidanceChangeOnly_LeavesTrajectoryAtBaseline()
    {
        // The deterministic Neutral GuidanceChange (spec 57) contributes 0 to Trajectory, so a window whose
        // only signal is Neutral still scores the 50 baseline (coexistence with the directional read).
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithSourceType(EvidenceSourceType.Filing)
            .WithQuality(EvidenceQuality.High)
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Neutral)
            .WithStrength(3)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-1))
            .Build();

        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        await harness.Signals.AddAsync(signal, CancellationToken.None);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(50, result.Snapshot.TrajectoryScore);
    }

    [Fact]
    public async Task Versioning_ScoringConfigVersion_IsNonNullAndNonEmpty()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Guard the presence of the stamp, independent of its exact value: Versioning_StampsScoringConfigVersion
        // asserts the current version string (and is intentionally updated on each AD-10 bump), while this test
        // stays decoupled from that value so it survives bumps and only fails if a freshly-produced snapshot ever
        // silently regresses to null — which would disable the report's cross-run comparability gate (spec 69).
        Assert.False(string.IsNullOrEmpty(result.Snapshot.ScoringConfigVersion));
    }

    [Fact]
    public async Task WindowAndTimestamps_CreatedAtEqualsWindowEnd()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(WindowEnd - Window, result.Snapshot.WindowStartUtc);
        Assert.Equal(WindowEnd, result.Snapshot.WindowEndUtc);
        // CreatedAtUtc must track the run instant (windowEndUtc), NOT a separate clock read — so a
        // freshly-created snapshot is included by the report's inclusive (start, end] window (spec 49).
        Assert.Equal(WindowEnd, result.Snapshot.CreatedAtUtc);
    }

    [Fact]
    public async Task Persistence_SnapshotAndLinksAreRetrievable()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var snapshots = await harness.Scores.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);
        Assert.Contains(snapshots, s => s.Id == result.Snapshot.Id);

        var links = await harness.Scores.GetLinksForSnapshotAsync(result.Snapshot.Id, CancellationToken.None);
        Assert.Equal(result.Links.Count, links.Count);
        Assert.All(links, l => Assert.Equal(result.Snapshot.Id, l.ScoreSnapshotId));
    }

    [Fact]
    public async Task EmptyWindow_ProducesValidSnapshotWithZeroLinks()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        // No qualifying signals seeded.

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Empty(result.Links);
        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);

        var snapshots = await harness.Scores.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);
        Assert.Contains(snapshots, s => s.Id == result.Snapshot.Id);
    }

    [Fact]
    public async Task Reproducibility_SameStateAndClock_YieldsEquivalentScores()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-7));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));

        var first = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var second = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(first.Snapshot.TrajectoryScore, second.Snapshot.TrajectoryScore);
        Assert.Equal(first.Snapshot.OpportunityScore, second.Snapshot.OpportunityScore);
        Assert.Equal(first.Snapshot.AttentionScore, second.Snapshot.AttentionScore);
        Assert.Equal(first.Snapshot.EvidenceConfidenceScore, second.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(first.Snapshot.SignalVelocityScore, second.Snapshot.SignalVelocityScore);
        Assert.Equal(first.Snapshot.ComponentJson, second.Snapshot.ComponentJson);
        Assert.Equal(first.Snapshot.ScoringVersion, second.Snapshot.ScoringVersion);

        // Equal set of contribution tuples, ignoring freshly-generated snapshot/link Ids.
        static HashSet<(Guid, Guid, int, string)> Tuples(CompanyScoreResult r) =>
            r.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight, l.ContributionReason)).ToHashSet();

        Assert.True(Tuples(first).SetEquals(Tuples(second)));
    }

    [Fact]
    public async Task DiWiring_ResolvesEngine_AndScoresWithRealFormula()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        // The engine now depends on ISignalFileStore (cross-run previous-window read); wire the real
        // file store over a unique temp dir so the composition resolves.
        services.AddFileSignalStore(
            Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}"));

        using var provider = services.BuildServiceProvider();

        var signals = provider.GetRequiredService<ISignalRepository>();
        var evidence = provider.GetRequiredService<IEvidenceRepository>();
        var engine = provider.GetRequiredService<IScoringEngine>();

        var companyId = Guid.NewGuid();
        var ev = new EvidenceBuilder().WithId(Guid.NewGuid()).WithContentHash("wiring-hash").Build();
        var sig = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(ev.Id)
            .WithCompanyId(companyId)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-1))
            .Build();

        await evidence.AddIfNewAsync(ev, CancellationToken.None);
        await signals.AddAsync(sig, CancellationToken.None);

        var result = await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);
        Assert.Contains("mvp-engine-v1", result.Snapshot.ScoringVersion);
    }

    /// <summary>A fake collector exposing a fixed name; CollectAsync is never invoked by the descriptor.</summary>
    private sealed class NamedFakeCollector(string name) : IEvidenceCollector
    {
        public string CollectorName { get; } = name;

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The descriptor must never call CollectAsync.");
    }

    [Fact]
    public void DiWiring_SignalSourceDescriptor_SeesCollectorsRegisteredAfterApplicationServices()
    {
        // Spec 95: collectors are registered AFTER AddRadarApplicationServices in the real Worker graph, yet
        // the descriptor (resolving IEnumerable<IEvidenceCollector> lazily) must still see ALL of them.
        var services = new ServiceCollection();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        // Register several collectors AFTER the application services — mirrors the Worker composition order.
        services.AddSingleton<IEvidenceCollector>(new NamedFakeCollector("usaspending"));
        services.AddSingleton<IEvidenceCollector>(new NamedFakeCollector("sec-form4"));
        services.AddSingleton<IEvidenceCollector>(new NamedFakeCollector("rss"));
        services.AddSingleton<IEvidenceCollector>(new NamedFakeCollector("newssearch"));

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<ISignalSourceDescriptor>();

        // All late-registered collectors appear, sorted Ordinal — in the COLLECTION PROVENANCE string (spec
        // 141), which is what the collector set now feeds. The lazy-resolution property this test exists for
        // is unchanged; only which of the descriptor's two strings carries the collector names moved.
        Assert.Equal(
            "collectors=newssearch,rss,sec-form4,usaspending;",
            resolved.CollectionProvenance());

        // The IDENTITY descriptor (the fingerprint input) carries the rule-set identity, the spec-194 §2
        // news-read identity and the spec-198 news-QUERY identity, but nothing about which collectors are
        // registered. This composition never configured the stage-2 judgment, so the news segment is the
        // disabled form; it also never registered a NewsQueryScoringIdentity, which falls back to the
        // SHIPPED DEFAULT window (not "none") because the recency filter applies whenever the newssearch
        // collector runs.
        Assert.Equal(
            "rules=radar-keyword-rules-v8;"
                + NewsJudgmentScoringIdentity.Disabled.Segment
                + NewsQueryScoringIdentity.Default.Segment,
            resolved.CanonicalDescriptor());
    }

    [Fact]
    public async Task PreviousWindow_IsSlicedAndPassed_SeparateFromCurrentAndOlder()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        // Current window (WindowStart, WindowEnd] — in the in-memory repo (this run's signals).
        var windowStart = WindowEnd - Window;
        var current = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));
        // Previous window (WindowStart - Window, WindowStart] — ON DISK (a prior run's persisted signal).
        var previous = harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-3));
        // Older than the previous window ON DISK -> excluded by the window read.
        harness.SeedPriorRunSignalOnDisk(companyId, windowStart - Window - TimeSpan.FromDays(1));

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Equal(new[] { current.signal.Id }, input.Signals.Select(s => s.Signal.Id).ToArray());
        Assert.Equal(new[] { previous.Id }, input.PreviousSignals.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task PreviousWindow_BoundaryAtWindowStart_BelongsToPrevious()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        // A prior-run signal exactly at windowStart, ON DISK: the disk read's (start, end] boundary (AD-6)
        // must place it in the previous window, never the current one.
        var atStart = harness.SeedPriorRunSignalOnDisk(companyId, windowStart);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.DoesNotContain(input.Signals, s => s.Signal.Id == atStart.Id);
        Assert.Contains(input.PreviousSignals, s => s.Id == atStart.Id);
    }

    [Fact]
    public async Task PreviousWindow_ReviewFilter_ExcludesNonApproved()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        // Both prior-run signals ON DISK; only the Approved one survives the read's review filter.
        var approved = harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-5));
        var pending = harness.SeedPriorRunSignalOnDisk(
            companyId, windowStart.AddDays(-5), SignalReviewStatus.Pending);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Contains(input.PreviousSignals, s => s.Id == approved.Id);
        Assert.DoesNotContain(input.PreviousSignals, s => s.Id == pending.Id);
    }

    [Fact]
    public async Task PreviousWindow_DoesNotRequireEvidence_ButCurrentStillDoes()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        // Previous-window signal is sourced from disk and never needs evidence by construction.
        var previousOnDisk = harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-2));
        // Current-window signal with missing evidence -> still dropped.
        var currentNoEvidence = await harness.SeedPairAsync(
            companyId, WindowEnd.AddDays(-2), storeEvidence: false);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Contains(input.PreviousSignals, s => s.Id == previousOnDisk.Id);
        Assert.DoesNotContain(input.Signals, s => s.Signal.Id == currentNoEvidence.signal.Id);
    }

    [Fact]
    public async Task PreviousWindow_Empty_WhenNoSignalsBeforeWindowStart()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        // Only a current-window signal in the in-memory repo; nothing on disk -> the disk read returns empty.
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.NotNull(input.PreviousSignals);
        Assert.Empty(input.PreviousSignals);
        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);
    }

    [Fact]
    public async Task CrossRunVelocity_MoreCurrentActivityThanPriorOnDisk_ExceedsSteady()
    {
        // Real formula: velocity = 50·(actNow+10)/(actPrev+10) over Strength sums. Current-window strength
        // (in the in-memory repo) sums to 16; prior-window strength (only on disk) sums to 6 → ratio > 1 →
        // velocity > 50. This proves the previous window now comes from disk (cross-run).
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var windowStart = WindowEnd - Window;

        // Current window: two Approved signals in the in-memory repo (Strength 6 + 10 = 16).
        await SeedCurrentSignalWithStrengthAsync(harness, companyId, WindowEnd.AddDays(-3), strength: 6);
        await SeedCurrentSignalWithStrengthAsync(harness, companyId, WindowEnd.AddDays(-6), strength: 10);
        // Prior window: one Approved signal ONLY on disk (Strength 6).
        harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-3), strength: 6);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.True(
            result.Snapshot.SignalVelocityScore > 50,
            $"Expected velocity > 50, got {result.Snapshot.SignalVelocityScore}.");
    }

    [Fact]
    public async Task CrossRunVelocity_LessCurrentActivityThanPriorOnDisk_FallsBelowSteady()
    {
        // Mirror case: current-window strength (6) < prior-window strength (on disk: 12 + 12 = 24) → ratio
        // < 1 → velocity < 50.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var windowStart = WindowEnd - Window;

        await SeedCurrentSignalWithStrengthAsync(harness, companyId, WindowEnd.AddDays(-3), strength: 6);
        harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-3), strength: 12);
        harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-6), strength: 12);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.True(
            result.Snapshot.SignalVelocityScore < 50,
            $"Expected velocity < 50, got {result.Snapshot.SignalVelocityScore}.");
    }

    [Fact]
    public async Task CrossRunVelocity_NoPriorSignalsOnDisk_YieldsSteadyNoPreviousValue()
    {
        // Regression lock: with NO prior signals on disk (the pre-slice steady case), velocity is the
        // no-previous value 50·(actNow+10)/(0+10). With actNow == 0 (a Neutral has 0? no — pick actNow to
        // land exactly on steady) we assert against the same value a run with an empty previous window
        // computes, proving no fabricated movement without a prior on disk.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();

        // One current-window Approved signal, Strength 6; nothing on disk.
        await SeedCurrentSignalWithStrengthAsync(harness, companyId, WindowEnd.AddDays(-3), strength: 6);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // No previous window: velocity = 50·(6+10)/(0+10) = 80 (the current, safe no-previous behaviour).
        Assert.Equal(80, result.Snapshot.SignalVelocityScore);
    }

    [Fact]
    public async Task CrossRunVelocity_Provenance_LinksTraceOnlyToCurrentWindowEvidence()
    {
        // Spec Test 7: the disk-sourced previous signals are activity-only and contribute NO links. Only
        // the current-window signal's evidence produces a ScoreEvidenceLink.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var windowStart = WindowEnd - Window;

        var current = await SeedCurrentSignalWithStrengthAsync(
            harness, companyId, WindowEnd.AddDays(-3), strength: 6);
        // A prior-run signal on disk drives velocity but must not appear in the provenance links.
        var prior = harness.SeedPriorRunSignalOnDisk(companyId, windowStart.AddDays(-3), strength: 6);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var link = Assert.Single(result.Links);
        Assert.Equal(current.signal.Id, link.SignalId);
        Assert.Equal(current.evidence.Id, link.EvidenceId);
        Assert.DoesNotContain(result.Links, l => l.SignalId == prior.Id);
    }

    [Fact]
    public async Task CrossRunVelocity_StableRegardlessOfDuplicatePriorCopiesOnDisk()
    {
        // Spec 85 Test (d): with the REAL FileSignalStore deduping cross-run copies on read,
        // SignalVelocityScore must be identical whether ONE copy or MANY duplicate copies (same identity,
        // fresh ids) of a prior signal sit on disk — velocity no longer depends on how many times the
        // pipeline ran. Without dedup the many-copy case would inflate actPrev and drive velocity down.
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fileStore = new FileSignalStore(
                new FileSignalStoreOptions { RootDirectory = tempDir },
                NullLogger<FileSignalStore>.Instance);

            var signals = new InMemorySignalRepository();
            var evidence = new InMemoryEvidenceRepository();
            var scores = new InMemoryScoreRepository();
            var engine = new ScoringEngine(
                signals, fileStore, evidence, scores, new InMemoryCompanyRepository(),
                new RadarScoreFormulaV8(new ScoringWeights(), Weights),
                new ScoringWeights(), Weights, SourceDesc, new InsiderMaterialityWeights(),
                new MediaAttentionCollapse(new MediaCollapseOptions()),
                new ScoringOptions { Window = Window }, NullLogger<ScoringEngine>.Instance);

            var companyId = Guid.NewGuid();

            // Current window (in-memory repo, one clean run): one Approved signal + evidence, Strength 6.
            var currentEvidence = new EvidenceBuilder()
                .WithId(Guid.NewGuid())
                .WithContentHash(Guid.NewGuid().ToString("N"))
                .Build();
            var currentSignal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(currentEvidence.Id)
                .WithCompanyId(companyId)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithStrength(6)
                .WithObservedAtUtc(WindowEnd.AddDays(-3))
                .Build();
            await evidence.AddIfNewAsync(currentEvidence, CancellationToken.None);
            await signals.AddAsync(currentSignal, CancellationToken.None);

            // Prior window ON DISK: one canonical prior signal identity (Strength 12).
            var priorEvidenceId = Guid.NewGuid();
            var priorObserved = (WindowEnd - Window).AddDays(-3);
            await WritePriorRunCopyAsync(fileStore, companyId, priorEvidenceId, priorObserved);

            var oneCopy = await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

            // Add FIVE more cross-run duplicate copies (same identity, fresh SignalId/CreatedAt) on disk.
            for (var i = 0; i < 5; i++)
            {
                await WritePriorRunCopyAsync(fileStore, companyId, priorEvidenceId, priorObserved);
            }

            var manyCopies = await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

            Assert.Equal(
                oneCopy.Snapshot.SignalVelocityScore,
                manyCopies.Snapshot.SignalVelocityScore);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Writes one cross-run copy of a prior-run Approved signal to the real on-disk store: a fresh SignalId
    /// each call, but the SAME identity (companyId, evidenceId, Type, Direction) and Strength — exactly the
    /// duplicate shape the dedup collapses.
    /// </summary>
    private static async Task WritePriorRunCopyAsync(
        FileSignalStore fileStore, Guid companyId, Guid evidenceId, DateTimeOffset observedAt)
    {
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidenceId)
            .WithCompanyId(companyId)
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithStrength(12)
            .WithObservedAtUtc(observedAt)
            .Build();
        var review = new Radar.Domain.Signals.SignalReview(
            Id: Guid.NewGuid(),
            SignalId: signal.Id,
            ReviewerName: "DeterministicSignalReviewer",
            Decision: SignalReviewDecision.Approve,
            Summary: "prior run copy",
            IssuesJson: null,
            ReviewedAtUtc: observedAt.AddDays(1));
        await fileStore.WriteAsync(signal, review, CancellationToken.None);
    }

    // ---- Spec 113: assembly-time GuidanceChange supersede (persisted Neutral vs directional read) ----

    /// <summary>
    /// Seeds an Approved GuidanceChange signal over an EXISTING evidence item — the spec-113 shape where a
    /// stale deterministic Neutral and a directional read coexist over the SAME filing EvidenceId.
    /// </summary>
    private static async Task<Signal> SeedGuidanceChangeAsync(
        Harness harness,
        Guid companyId,
        EvidenceItem evidence,
        SignalDirection direction,
        int strength,
        int novelty,
        decimal confidence,
        DateTimeOffset observedAt)
    {
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithNovelty(novelty)
            .WithConfidence(confidence)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAt)
            .Build();

        await harness.Signals.AddAsync(signal, CancellationToken.None);
        return signal;
    }

    private static EvidenceItem FilingEvidence() =>
        new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithSourceType(EvidenceSourceType.Filing)
            .WithQuality(EvidenceQuality.High)
            .Build();

    [Theory]
    [InlineData(SignalDirection.Positive, true)]
    [InlineData(SignalDirection.Negative, false)]
    public async Task Supersede_PersistedNeutralAndDirectionalOverSameFiling_ScoresDirectionalOnly(
        SignalDirection direction, bool aboveBaseline)
    {
        // Spec 113: a stale deterministic Neutral GuidanceChange (persisted when the directional read
        // failed on first collection) AND the strength-8 directional read coexist over the SAME filing
        // EvidenceId. The company must be scored on the directional only — Trajectory moves, and the
        // Neutral gets NO contribution/ScoreEvidenceLink (at most one GuidanceChange per filing).
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var evidence = FilingEvidence();
        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);

        var neutral = await SeedGuidanceChangeAsync(
            harness, companyId, evidence, SignalDirection.Neutral,
            strength: 3, novelty: 4, confidence: 0.45m, WindowEnd.AddDays(-1));
        var directional = await SeedGuidanceChangeAsync(
            harness, companyId, evidence, direction,
            strength: 8, novelty: 6, confidence: 0.90m, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        if (aboveBaseline)
        {
            Assert.True(result.Snapshot.TrajectoryScore > 50);
        }
        else
        {
            Assert.True(result.Snapshot.TrajectoryScore < 50);
        }

        // Exactly one link — the directional signal's; the superseded Neutral has none.
        var link = Assert.Single(result.Links);
        Assert.Equal(directional.Id, link.SignalId);
        Assert.Equal(evidence.Id, link.EvidenceId);
        Assert.DoesNotContain(result.Links, l => l.SignalId == neutral.Id);
    }

    [Fact]
    public async Task Supersede_NeutralOnly_NoDirectionalRead_IsUnchanged()
    {
        // A filing with ONLY the deterministic Neutral (no directional read available) is scored exactly
        // as before: baseline Trajectory and the Neutral keeps its provenance link.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var evidence = FilingEvidence();
        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);

        var neutral = await SeedGuidanceChangeAsync(
            harness, companyId, evidence, SignalDirection.Neutral,
            strength: 3, novelty: 4, confidence: 0.45m, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(50, result.Snapshot.TrajectoryScore);
        var link = Assert.Single(result.Links);
        Assert.Equal(neutral.Id, link.SignalId);
    }

    [Fact]
    public async Task Supersede_RepeatedAssembly_YieldsIdenticalScoredSetsAndLinks()
    {
        // Determinism (AD-3): repeated scoring assembly over the same Neutral+directional inputs yields
        // identical component scores and identical contribution tuples.
        var harness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var companyId = Guid.NewGuid();
        var evidence = FilingEvidence();
        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);

        await SeedGuidanceChangeAsync(
            harness, companyId, evidence, SignalDirection.Neutral,
            strength: 3, novelty: 4, confidence: 0.45m, WindowEnd.AddDays(-1));
        await SeedGuidanceChangeAsync(
            harness, companyId, evidence, SignalDirection.Positive,
            strength: 8, novelty: 6, confidence: 0.90m, WindowEnd.AddDays(-1));

        var first = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var second = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(first.Snapshot.TrajectoryScore, second.Snapshot.TrajectoryScore);
        Assert.Equal(first.Snapshot.OpportunityScore, second.Snapshot.OpportunityScore);
        Assert.Equal(first.Snapshot.ComponentJson, second.Snapshot.ComponentJson);

        static HashSet<(Guid, Guid, int, string)> Tuples(CompanyScoreResult r) =>
            r.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight, l.ContributionReason)).ToHashSet();

        Assert.True(Tuples(first).SetEquals(Tuples(second)));
    }

    [Fact]
    public async Task Supersede_PreviousWindow_StaleNeutralAndDirectionalOnDisk_CountedOnceForVelocity()
    {
        // Spec 113, previous window (no double-count, ever): the on-disk read's dedupe key includes
        // Direction (spec 85), so a filing whose stale Neutral AND directional GuidanceChange both persist
        // comes back as TWO signals — the engine must hand the formula only the directional one, so the
        // filing counts once as activity.
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        var windowStart = WindowEnd - Window;
        var evidenceId = Guid.NewGuid();
        var observedAt = windowStart.AddDays(-3);

        Signal OnDiskGuidance(SignalDirection direction, int strength, decimal confidence)
        {
            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidenceId)
                .WithCompanyId(companyId)
                .WithType(SignalType.GuidanceChange)
                .WithDirection(direction)
                .WithStrength(strength)
                .WithConfidence(confidence)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(observedAt)
                .Build();
            harness.SignalStore.Seed(signal);
            return signal;
        }

        var staleNeutral = OnDiskGuidance(SignalDirection.Neutral, strength: 3, confidence: 0.45m);
        var directional = OnDiskGuidance(SignalDirection.Positive, strength: 8, confidence: 0.90m);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);
        var previous = Assert.Single(input.PreviousSignals);
        Assert.Equal(directional.Id, previous.Id);
        Assert.DoesNotContain(input.PreviousSignals, s => s.Id == staleNeutral.Id);
    }

    [Fact]
    public async Task Supersede_AcceptanceFixture_DirectionalReadLiftsOpportunityOverTheInvestigateGate()
    {
        // AEHR-shaped acceptance (generic company — NO ticker-specific logic): a filing whose only scored
        // GuidanceChange is the stale deterministic Neutral sits under the Investigate 40 gate; once the
        // strength-8/novelty-6/confidence-0.90 directional read supersedes that Neutral, OpportunityScore
        // clears 40. Same evidence, same window — only which already-available signal is scored changes.
        var companyId = Guid.NewGuid();

        // Case A: the stale Neutral alone (the stuck pre-113 read).
        var neutralHarness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var neutralEvidence = FilingEvidence();
        await neutralHarness.Evidence.AddIfNewAsync(neutralEvidence, CancellationToken.None);
        await SeedGuidanceChangeAsync(
            neutralHarness, companyId, neutralEvidence, SignalDirection.Neutral,
            strength: 3, novelty: 4, confidence: 0.45m, WindowEnd.AddDays(-1));

        var neutralResult =
            await neutralHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Case B: the SAME stale Neutral persisted alongside the directional Positive read (spec 113).
        var supersededHarness = new Harness(new RadarScoreFormulaV8(new ScoringWeights(), Weights));
        var evidence = FilingEvidence();
        await supersededHarness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        var neutral = await SeedGuidanceChangeAsync(
            supersededHarness, companyId, evidence, SignalDirection.Neutral,
            strength: 3, novelty: 4, confidence: 0.45m, WindowEnd.AddDays(-1));
        await SeedGuidanceChangeAsync(
            supersededHarness, companyId, evidence, SignalDirection.Positive,
            strength: 8, novelty: 6, confidence: 0.90m, WindowEnd.AddDays(-1));

        var supersededResult =
            await supersededHarness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.True(
            neutralResult.Snapshot.OpportunityScore < 40,
            $"Neutral-only fixture must sit under the Investigate 40 gate; got {neutralResult.Snapshot.OpportunityScore}.");
        Assert.True(
            supersededResult.Snapshot.OpportunityScore >= 40,
            $"Superseding directional read must clear the Investigate 40 gate; got {supersededResult.Snapshot.OpportunityScore}.");
        Assert.DoesNotContain(supersededResult.Links, l => l.SignalId == neutral.Id);
    }

    // ---- Spec 117: the engine loads the company's curated FollowingTier into the formula input ----

    [Fact]
    public async Task FollowingTier_CompanyTier_IsPassedIntoTheFormulaInput()
    {
        // The engine loads the company via ICompanyRepository and hands its curated tier (spec 117 — the
        // non-price notedness input, AD-14) to the formula through ScoringInput.FollowingTier.
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        await harness.Companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithFollowingTier(FollowingTier.Mega).Build(),
            CancellationToken.None);
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);
        Assert.Equal(FollowingTier.Mega, input.FollowingTier);
    }

    [Fact]
    public async Task FollowingTier_MissingCompany_FailSafesToSmall()
    {
        // A company the repository does not know (never seeded) degrades to Small — no extra discount,
        // no throw (the fail-safe default).
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);
        Assert.Equal(FollowingTier.Small, input.FollowingTier);
    }

    // ---- Spec 136: point-in-time honesty (CreatedAtUtc <= windowEndUtc on both read paths) ----

    [Fact]
    public async Task PointInTime_SignalCreatedExactlyAtWindowEnd_IsIncluded()
    {
        // THE forward-run no-op regression lock: AD-7 gives one run one instant, so this run's own signals
        // carry CreatedAtUtc == asOfUtc == windowEndUtc EXACTLY. The known-at predicate must include the
        // equality boundary — otherwise every forward run would drop its fresh signals and score from zero.
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var fresh = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2), createdAt: WindowEnd);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(fresh.signal.Id, result.Links[0].SignalId);
    }

    [Fact]
    public async Task PointInTime_ObservedInWindowButCreatedAfterWindowEnd_IsExcluded()
    {
        // The replay-leak shape: the event happened inside the window (ObservedAtUtc), but Radar only
        // learned it AFTER the scoring instant (CreatedAtUtc > windowEndUtc) — e.g. a later collector
        // backfill. Scoring at asOf must not see it.
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        await harness.SeedPairAsync(
            companyId, WindowEnd.AddDays(-2), createdAt: WindowEnd.AddTicks(1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Empty(result.Links);
    }

    [Fact]
    public async Task PointInTime_PreviousWindow_ThresholdIsWindowEnd_NotWindowStart()
    {
        // The knowledge threshold for the PREVIOUS window is the scoring instant (windowEndUtc), NOT the
        // current window's start: a prior-period signal Radar learned mid-current-window (windowStartUtc <
        // CreatedAtUtc <= windowEndUtc) still counts as previous-window activity. This test FAILS if the
        // engine ever passes windowStartUtc instead (the fake applies CreatedAtUtc <= knownAsOfUtc).
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        var windowStart = WindowEnd - Window;

        // Observed in the previous window, but only KNOWN five days before asOf (after windowStart).
        var knownMidWindow = harness.SeedPriorRunSignalOnDisk(
            companyId, windowStart.AddDays(-3), createdAt: WindowEnd.AddDays(-5));
        // Observed in the previous window but only known AFTER asOf — must not count at all.
        var knownAfterAsOf = harness.SeedPriorRunSignalOnDisk(
            companyId, windowStart.AddDays(-5), createdAt: WindowEnd.AddDays(1));

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);
        Assert.Contains(input.PreviousSignals, s => s.Id == knownMidWindow.Id);
        Assert.DoesNotContain(input.PreviousSignals, s => s.Id == knownAfterAsOf.Id);
    }

    [Fact]
    public async Task PointInTime_ReplayAtEarlierAsOf_SeesStrictlyFewerSignals()
    {
        // Replay-shaped: ONE fixed signal set, scored at two asOf instants. Signal B was observed before
        // the earlier asOf but only entered the store after it, so the earlier asOf must see strictly
        // fewer signals — exactly what makes a historical backtest honest instead of silently inflated.
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        var earlierAsOf = WindowEnd;
        var laterAsOf = WindowEnd.AddDays(5);

        // A: observed and known before the earlier asOf.
        await harness.SeedPairAsync(
            companyId, earlierAsOf.AddDays(-3), createdAt: earlierAsOf.AddDays(-3));
        // B: observed before the earlier asOf, but created (known) only after it.
        await harness.SeedPairAsync(
            companyId, earlierAsOf.AddDays(-2), createdAt: earlierAsOf.AddDays(2));

        await harness.Engine.ScoreCompanyAsync(companyId, earlierAsOf, CancellationToken.None);
        var earlierSeen = Assert.IsType<ScoringInput>(formula.LastInput).Signals.Count;

        await harness.Engine.ScoreCompanyAsync(companyId, laterAsOf, CancellationToken.None);
        var laterSeen = Assert.IsType<ScoringInput>(formula.LastInput).Signals.Count;

        Assert.Equal(1, earlierSeen);
        Assert.Equal(2, laterSeen);
        Assert.True(earlierSeen < laterSeen, "The earlier asOf must see strictly fewer signals.");
    }

    /// <summary>
    /// Seeds a current-window Approved signal (with evidence) of a given Strength into the in-memory repo,
    /// so the real formula's velocity numerator (Strength sum) is controllable.
    /// </summary>
    private static async Task<(Signal signal, EvidenceItem evidence)> SeedCurrentSignalWithStrengthAsync(
        Harness harness, Guid companyId, DateTimeOffset observedAt, int strength)
    {
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .Build();

        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithStrength(strength)
            .WithObservedAtUtc(observedAt)
            .Build();

        await harness.Evidence.AddIfNewAsync(evidence, CancellationToken.None);
        await harness.Signals.AddAsync(signal, CancellationToken.None);
        return (signal, evidence);
    }

    // ---- Spec 146: the engine hands the v9 formula the run's ENABLED COLLECTORS ----

    [Fact]
    public async Task V9Channels_EngineSuppliesEnabledCollectors_SoProvenanceSeparatesRanFromDidNotRun()
    {
        // THE seam this test exists for. ScoringEngine is the ONLY production wiring of
        // ISignalSourceDescriptor.EnabledCollectors() into ScoringInput.EnabledCollectors, and
        // RadarScoreFormulaV9 splits each channel's declared collectors against exactly that set. If the
        // engine ever stopped supplying it (or supplied an empty set), every live v9 snapshot would report
        // "declared collector did not run" for collectors that demonstrably ran — silently, and with every
        // score unchanged, because the split is recorded provenance and never a scoring input. So it is
        // asserted END TO END through the engine and off the PERSISTED snapshot, rather than off a formula
        // call the test wired itself: a formula-level test hands EnabledCollectors in and by construction
        // cannot see this seam. It is also the engine → v9 path's first end-to-end exercise.
        //
        // StubSourceDescriptor.EnabledCollectors() returns ["test-a", "test-b"], so ONE channel declaring
        // "test-a" (enabled, and the recorded collector behind the seeded signal) plus "never-run" (declared
        // but not registered) exercises both halves of the distinction.
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("primary", ["test-a", "never-run"], 1.0, 3)],
            "engine-v9-test");
        var harness = new Harness(
            new RadarScoreFormulaV9(new ScoringWeights(), Weights, channels),
            channels: channels);
        var companyId = Guid.NewGuid();

        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithQuality(EvidenceQuality.High)
            .WithMetadataJson(EvidenceMetadata.Compose(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CollectionProvenanceMetadata.MetadataKey] = "test-a",
                },
                []))
            .Build();
        var signal = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithCompanyId(companyId)
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .WithStrength(6)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-2))
            .Build();

        await harness.SeedExistingAsync(signal, evidence);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Read the breakdown off the PERSISTED snapshot — what a reviewer or the report would actually see.
        var persisted = Assert.Single(
            await harness.Scores.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None));
        using var doc = JsonDocument.Parse(persisted.ComponentJson);
        Assert.Equal(ScoreFormulaVersions.V9, doc.RootElement.GetProperty("Formula").GetString());
        var channel = Assert.Single(doc.RootElement.GetProperty("Channels").EnumerateArray().ToList());

        // "Declared collector RAN" — this is the assertion that fails if the engine stops supplying the set.
        Assert.Equal(
            ["test-a"],
            channel.GetProperty("CollectorsRan").EnumerateArray().Select(e => e.GetString()));
        // ...distinguished from "declared collector DID NOT run", which the same snapshot records separately.
        Assert.Equal(
            ["never-run"],
            channel.GetProperty("CollectorsNotRun").EnumerateArray().Select(e => e.GetString()));

        // And the channel genuinely scored through the engine, so the provenance above describes a real run
        // rather than an empty one: the seeded signal was consumed, the channel is not dark, and it earned
        // some of its share — with the evidence chain intact.
        Assert.Equal(1, channel.GetProperty("SignalCount").GetInt32());
        Assert.False(channel.GetProperty("Dark").GetBoolean());
        Assert.True(channel.GetProperty("Score").GetDouble() > 0);
        var link = Assert.Single(result.Links);
        Assert.Equal(signal.Id, link.SignalId);
        Assert.Equal(evidence.Id, link.EvidenceId);
    }
}
