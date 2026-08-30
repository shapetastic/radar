using Microsoft.Extensions.Logging;

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

namespace Radar.Application.Tests.Pipeline;

/// <summary>
/// SPEC 197 §5.2 items 12 and 14 — the forward/standalone scoring pass aggregates the two score-assembly
/// diagnostic categories into AT MOST ONE Warning each, while the engine that produced them emits none.
/// <para>
/// The measured motivation: the otherwise-green live baseline run
/// <c>0b48b865-76b8-4485-996c-9b9139b694aa</c> emitted ~462 Warnings — 397 unresolved-evidence lines and 63
/// spec-191-neutralization lines — because <c>ScoringEngine</c> is ONE STRATEGY and its "one Warning per
/// company" is really one Warning per strategy × company. Two genuine RSS transport failures were buried in
/// them.
/// </para>
/// <para>
/// <b>MUTATION PROOFS (§5.2 item 14), each RUN rather than asserted.</b>
/// <list type="number">
/// <item><description>Remove the diagnostic return — have <c>ScoringEngine</c> pass
/// <see cref="ScoreAssemblyDiagnostics.None"/> to <c>CompanyScoreResult</c> instead of the computed record —
/// and <see cref="Pass_AffectedCompaniesAcrossStrategies_EmitOneWarningPerCategory_WithHonestlyLabelledAxes"/>
/// plus <see cref="ScoreAssemblyDiagnosticsAggregationReplayTests"/>'s affected test turn red, while
/// <see cref="Pass_ScoresAreUnchangedByTheDiagnostics_SnapshotComponentsContributionsAndLinksAreIdentical"/>
/// stays green — which is exactly the point: the diagnostics are transient and carry no score.</description>
/// </item>
/// <item><description>Remove the aggregation — delete the <c>assemblyDiagnostics.Record(...)</c> call or the
/// <c>LogAggregates</c> call in <c>ScoringPass</c>/<c>ReplayRunner</c> — and the same tests turn red on the
/// missing pass-level Warning.</description></item>
/// <item><description>Restore either engine Warning (the spec-145 dropped-signal line or the spec-194 §1.4
/// neutralization line) and <see cref="Engine_EmitsNoWarningForEitherCategory_TheAggregateIsTheOnlyReport"/>
/// turns red, together with <c>ScoringEngineTests</c> and
/// <c>LegacyNewsInheritanceNeutralizationTests</c>'s engine-level assertions.</description></item>
/// </list>
/// In every mutation the snapshots, components, contributions, evidence links and persisted files stay
/// byte-identical — asserted directly by
/// <see cref="Pass_ScoresAreUnchangedByTheDiagnostics_SnapshotComponentsContributionsAndLinksAreIdentical"/>.
/// </para>
/// </summary>
public sealed class ScoreAssemblyDiagnosticsAggregationTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>§5.2 item 12, and the shape of the live failure: N companies × M strategies, two lines.</summary>
    [Fact]
    public async Task Pass_AffectedCompaniesAcrossStrategies_EmitOneWarningPerCategory_WithHonestlyLabelledAxes()
    {
        var fixture = await DiagnosticsFixture.BuildAsync(strategyNames: ["default", "alt"]);

        await fixture.Pass.RunAsync(fixture.Companies, AsOf, CancellationToken.None);

        // The engine — the type that used to own both Warnings — now emits NONE of either category.
        Assert.DoesNotContain(fixture.EngineLog.Entries, e => e.Level == LogLevel.Warning);

        var warnings = fixture.PassLog.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .ToList();

        // EXACTLY two Warnings for the whole pass: one per affected category, never one per evaluation.
        Assert.Equal(2, warnings.Count);

        var unresolved = Assert.Single(
            warnings, m => m.Contains("could not be resolved", StringComparison.Ordinal));
        var neutralized = Assert.Single(
            warnings, m => m.Contains("neutralized", StringComparison.Ordinal));

        // ---- Unresolved evidence -------------------------------------------------------------------
        // Two affected companies (A: 3 signals over 2 evidence ids; B: 1 signal over 1) scored by two
        // strategies ⇒ 8 signal-evaluation incidences over 4 affected evaluations, 2 distinct companies and
        // 2 distinct strategies, with the per-evaluation distinct-evidence counts summing to 6.
        Assert.Contains(
            "Scoring pass: 8 signal-evaluation incidence(s) were dropped", unresolved, StringComparison.Ordinal);
        Assert.Contains(
            "across 4 affected strategy-company evaluation(s), 2 distinct company/companies and 2 distinct "
                + "strateg(ies).",
            unresolved,
            StringComparison.Ordinal);
        Assert.Contains(
            "per-evaluation distinct-evidence-id counts SUM to 6", unresolved, StringComparison.Ordinal);

        // THE HONESTY CLAUSES — the load-bearing part of §3. A summed count is an INCIDENCE count, and a
        // summed distinct-evidence count is a sum of per-evaluation counts, never a global distinct total.
        Assert.Contains(
            "These are signal-evaluation INCIDENCES, not globally distinct signals",
            unresolved,
            StringComparison.Ordinal);
        Assert.Contains(
            "is a sum of per-evaluation counts and is NOT a globally distinct evidence total",
            unresolved,
            StringComparison.Ordinal);

        // A forward pass scores at ONE instant, so the as-of axis would be a constant 1: it is omitted.
        Assert.DoesNotContain("as-of instant(s)", unresolved, StringComparison.Ordinal);

        // ---- Neutralization ------------------------------------------------------------------------
        // One affected company × two strategies, with all four axes deliberately DIFFERENT per evaluation
        // (current 2 legacy / 1 malformed, previous 4 legacy / 3 malformed) so a test cannot pass by
        // reporting one number four times.
        Assert.Contains(
            "Scoring pass: neutralized 4 accrued spec-191 inherited news direction(s) and 2 unverifiable "
                + "judgment-signal envelope(s) in the current window (and 8 / 6 in the previous/velocity "
                + "window)",
            neutralized,
            StringComparison.Ordinal);
        Assert.Contains(
            "across 2 affected strategy-company evaluation(s), 1 distinct company/companies and 2 distinct "
                + "strateg(ies).",
            neutralized,
            StringComparison.Ordinal);
        Assert.Contains(
            "All four counts are signal-evaluation INCIDENCES, not globally distinct signals",
            neutralized,
            StringComparison.Ordinal);

        // The two urgency axes stay separate: a CURRENT writer producing unverifiable provenance must never
        // disappear inside the expected spec-191 residue.
        Assert.Contains(
            "is a CURRENT writer producing provenance that cannot be verified", neutralized,
            StringComparison.Ordinal);
    }

    /// <summary>§5.2 item 12's negative half: an unaffected pass emits NEITHER line.</summary>
    [Fact]
    public async Task Pass_WithNothingToReport_EmitsNeitherWarning()
    {
        var fixture = await DiagnosticsFixture.BuildAsync(
            strategyNames: ["default", "alt"], healthyOnly: true);

        await fixture.Pass.RunAsync(fixture.Companies, AsOf, CancellationToken.None);

        Assert.DoesNotContain(fixture.PassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(fixture.EngineLog.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// §5.2 item 14 / mutation 3: the engine is silent about BOTH categories, and the per-evaluation detail
    /// lives at Debug — one bounded line per AFFECTED evaluation (4 unresolved + 2 neutralized, with company
    /// B affected by both, so 3 affected companies × 2 strategies = 6 lines).
    /// </summary>
    [Fact]
    public async Task Engine_EmitsNoWarningForEitherCategory_TheAggregateIsTheOnlyReport()
    {
        var fixture = await DiagnosticsFixture.BuildAsync(strategyNames: ["default", "alt"]);

        await fixture.Pass.RunAsync(fixture.Companies, AsOf, CancellationToken.None);

        Assert.DoesNotContain(fixture.EngineLog.Entries, e => e.Level == LogLevel.Warning);

        var debugSummaries = fixture.EngineLog.Entries
            .Where(e => e.Level == LogLevel.Debug
                && e.Message.StartsWith("Score assembly diagnostics", StringComparison.Ordinal))
            .ToList();

        // Two affected companies × two strategies. The healthy company logs nothing at all.
        Assert.Equal(4, debugSummaries.Count);
        Assert.DoesNotContain(
            debugSummaries, e => e.Message.Contains(DiagnosticsFixture.HealthyCompanyId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>
    /// §3's neutrality claim, asserted directly rather than argued: the SAME fixture scored twice produces
    /// byte-identical snapshot scalars, component JSON, contribution reasons, weights and the whole ordered
    /// evidence-link chain. Diagnostics are transient orchestration state and touch no score.
    /// </summary>
    [Fact]
    public async Task Pass_ScoresAreUnchangedByTheDiagnostics_SnapshotComponentsContributionsAndLinksAreIdentical()
    {
        var first = await DiagnosticsFixture.BuildAsync(strategyNames: ["default", "alt"]);
        await first.Pass.RunAsync(first.Companies, AsOf, CancellationToken.None);

        var second = await DiagnosticsFixture.BuildAsync(strategyNames: ["default", "alt"]);
        await second.Pass.RunAsync(second.Companies, AsOf, CancellationToken.None);

        var a = first.ScoreStores.Written;
        var b = second.ScoreStores.Written;

        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);

        for (var i = 0; i < a.Count; i++)
        {
            AssertScoringEquivalent(a[i], b[i]);
        }
    }

    /// <summary>Shared with <see cref="ScoringPassLoopOrderTests"/> (spec 203 §4) — one definition of "same score".</summary>
    internal static void AssertScoringEquivalent(
        (CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links) expected,
        (CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links) actual)
    {
        // Every field that encodes what the score SAYS. The snapshot/link Guids are minted per call (two
        // consecutive forward runs differ in them too), so they identify an event, not a result.
        Assert.Equal(expected.Snapshot.CompanyId, actual.Snapshot.CompanyId);
        Assert.Equal(expected.Snapshot.ScoringVersion, actual.Snapshot.ScoringVersion);
        Assert.Equal(expected.Snapshot.ScoringConfigVersion, actual.Snapshot.ScoringConfigVersion);
        Assert.Equal(expected.Snapshot.StrategyName, actual.Snapshot.StrategyName);
        Assert.Equal(expected.Snapshot.CollectionProvenance, actual.Snapshot.CollectionProvenance);
        Assert.Equal(expected.Snapshot.TrajectoryScore, actual.Snapshot.TrajectoryScore);
        Assert.Equal(expected.Snapshot.OpportunityScore, actual.Snapshot.OpportunityScore);
        Assert.Equal(expected.Snapshot.AttentionScore, actual.Snapshot.AttentionScore);
        Assert.Equal(expected.Snapshot.EvidenceConfidenceScore, actual.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(expected.Snapshot.SignalVelocityScore, actual.Snapshot.SignalVelocityScore);
        Assert.Equal(expected.Snapshot.Explanation, actual.Snapshot.Explanation);
        Assert.Equal(expected.Snapshot.ComponentJson, actual.Snapshot.ComponentJson);
        Assert.Equal(expected.Snapshot.WindowStartUtc, actual.Snapshot.WindowStartUtc);
        Assert.Equal(expected.Snapshot.WindowEndUtc, actual.Snapshot.WindowEndUtc);
        Assert.Equal(expected.Snapshot.CreatedAtUtc, actual.Snapshot.CreatedAtUtc);

        Assert.Equal(expected.Links.Count, actual.Links.Count);
        for (var i = 0; i < expected.Links.Count; i++)
        {
            Assert.Equal(expected.Links[i].SignalId, actual.Links[i].SignalId);
            Assert.Equal(expected.Links[i].EvidenceId, actual.Links[i].EvidenceId);
            Assert.Equal(expected.Links[i].ContributionReason, actual.Links[i].ContributionReason);
            Assert.Equal(expected.Links[i].ContributionWeight, actual.Links[i].ContributionWeight);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The fixture: CONSTRUCTED records only. No live artifact is read, copied or regenerated.
    // -------------------------------------------------------------------------------------------------

    internal sealed class DiagnosticsFixture
    {
        internal static readonly Guid UnresolvedOnlyCompanyId = new("aaaaaaaa-0000-0000-0000-00000000000a");
        internal static readonly Guid BothCategoriesCompanyId = new("bbbbbbbb-0000-0000-0000-00000000000b");
        internal static readonly Guid HealthyCompanyId = new("cccccccc-0000-0000-0000-00000000000c");

        private DiagnosticsFixture(
            ScoringPass pass,
            IReadOnlyList<Company> companies,
            CapturingLogger EngineLog,
            CapturingLogger passLog,
            RecordingScoreStoreFactory scoreStores)
        {
            Pass = pass;
            Companies = companies;
            this.EngineLog = EngineLog;
            PassLog = passLog;
            ScoreStores = scoreStores;
        }

        public ScoringPass Pass { get; }

        public IReadOnlyList<Company> Companies { get; }

        public CapturingLogger EngineLog { get; }

        public CapturingLogger PassLog { get; }

        public RecordingScoreStoreFactory ScoreStores { get; }

        public static async Task<DiagnosticsFixture> BuildAsync(
            IReadOnlyList<string> strategyNames, bool healthyOnly = false)
        {
            // Sequential, build-local ids: two fixtures built from the same arguments hold BYTE-IDENTICAL
            // signals and evidence, which is what makes the score-neutrality comparison meaningful rather
            // than a comparison of two different inputs (AD-3).
            var ids = new SequentialIds();

            var signals = new InMemorySignalRepository();
            var evidence = new InMemoryEvidenceRepository();
            var companyRepo = new InMemoryCompanyRepository();
            var previousWindow = new StubSignalFileStore();
            var engineLog = new CapturingLogger();
            var passLog = new CapturingLogger();
            var scoreStores = new RecordingScoreStoreFactory();

            var companies = new List<Company>();
            foreach (var id in new[] { UnresolvedOnlyCompanyId, BothCategoriesCompanyId, HealthyCompanyId })
            {
                var company = new CompanyBuilder().WithId(id).Build();
                await companyRepo.AddAsync(company, CancellationToken.None);
                companies.Add(company);
            }

            // Always present: one perfectly healthy company, so "affected" is never everything.
            await SeedResolvableAsync(ids, signals, evidence, HealthyCompanyId, SignalType.CustomerWin, -5);

            if (!healthyOnly)
            {
                // Company A — unresolved evidence only: THREE signals over TWO distinct evidence ids, so the
                // two counts can never be satisfied by reporting one number twice.
                var sharedMissingEvidenceId = ids.Next();
                await SeedUnresolvableAsync(
                    ids, signals, UnresolvedOnlyCompanyId, sharedMissingEvidenceId, SignalType.CustomerWin, -6);
                await SeedUnresolvableAsync(
                    ids, signals, UnresolvedOnlyCompanyId, sharedMissingEvidenceId, SignalType.ProductLaunch, -5);
                await SeedUnresolvableAsync(
                    ids, signals, UnresolvedOnlyCompanyId, ids.Next(), SignalType.ExecutiveHire, -4);

                // Company B — BOTH categories, with all four neutralization axes at DIFFERENT magnitudes:
                // current 2 accrued-legacy + 1 malformed, previous 4 accrued-legacy + 3 malformed.
                await SeedUnresolvableAsync(
                    ids, signals, BothCategoriesCompanyId, ids.Next(), SignalType.CustomerWin, -7);

                for (var i = 0; i < 2; i++)
                {
                    await SeedNewsAsync(ids, signals, evidence, BothCategoriesCompanyId, -10 - i, LegacyEnvelope());
                }

                await SeedNewsAsync(ids, signals, evidence, BothCategoriesCompanyId, -3, MalformedEnvelope());

                // The previous/velocity window is activity-only (no links, AD-6) and is read from the signal
                // FILE store, so it is stubbed independently of the current-window repository.
                previousWindow.PreviousWindow[BothCategoriesCompanyId] =
                [
                    .. Enumerable.Range(0, 4).Select(i => NewsSignal(
                        ids, BothCategoriesCompanyId, AsOf.AddDays(-40 - i), LegacyEnvelope())),
                    .. Enumerable.Range(0, 3).Select(i => NewsSignal(
                        ids, BothCategoriesCompanyId, AsOf.AddDays(-50 - i), MalformedEnvelope())),
                ];
            }

            var weights = new ScoringWeights();
            var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
            var scoreRepository = new InMemoryScoreRepository();

            var runtimes = strategyNames.Select((name, index) => new ScoringStrategyRuntime(
                new ScoringStrategyDefinition(
                    Name: name, ScoringProfile: name, Weights: weights, IsPrimary: index == 0),
                new ScoringEngine(
                    signals,
                    previousWindow,
                    evidence,
                    scoreRepository,
                    companyRepo,
                    new RadarScoreFormulaV8(weights, attention),
                    weights,
                    attention,
                    new StubSourceDescriptor(),
                    new InsiderMaterialityWeights(),
                    new MediaAttentionCollapse(new MediaCollapseOptions()),
                    new ScoringOptions(),
                    engineLog,
                    strategyName: name))).ToList();

            var pass = new ScoringPass(
                new StubStrategyFactory(runtimes),
                scoreStores,
                new StubScoringConfigStore(),
                TimeProvider.System,
                passLog);

            return new DiagnosticsFixture(pass, companies, engineLog, passLog, scoreStores);
        }

        private static async Task SeedResolvableAsync(
            SequentialIds ids,
            InMemorySignalRepository signals,
            InMemoryEvidenceRepository evidence,
            Guid companyId,
            SignalType type,
            int dayOffset)
        {
            var item = NewEvidence(ids, dayOffset);
            await evidence.AddIfNewAsync(item, CancellationToken.None);
            await signals.AddAsync(
                BaseSignal(ids, companyId, item.Id, type, dayOffset)
                    .WithDirection(SignalDirection.Positive)
                    .WithStrength(6)
                    .Build(),
                CancellationToken.None);
        }

        private static Task SeedUnresolvableAsync(
            SequentialIds ids,
            InMemorySignalRepository signals,
            Guid companyId,
            Guid missingEvidenceId,
            SignalType type,
            int dayOffset) =>
            signals.AddAsync(
                BaseSignal(ids, companyId, missingEvidenceId, type, dayOffset)
                    .WithDirection(SignalDirection.Positive)
                    .WithStrength(6)
                    .Build(),
                CancellationToken.None);

        private static async Task SeedNewsAsync(
            SequentialIds ids,
            InMemorySignalRepository signals,
            InMemoryEvidenceRepository evidence,
            Guid companyId,
            int dayOffset,
            string metadataJson)
        {
            var item = NewEvidence(ids, dayOffset);
            await evidence.AddIfNewAsync(item, CancellationToken.None);
            await signals.AddAsync(
                BaseSignal(ids, companyId, item.Id, SignalType.MediaAttention, dayOffset)
                    .WithDirection(SignalDirection.Negative)
                    .WithStrength(6)
                    .WithNovelty(4)
                    .WithConfidence(0.5m)
                    .WithMetadataJson(metadataJson)
                    .Build(),
                CancellationToken.None);
        }

        private static Signal NewsSignal(
            SequentialIds ids, Guid companyId, DateTimeOffset observedAtUtc, string metadataJson) =>
            new SignalBuilder()
                .WithId(ids.Next())
                .WithEvidenceId(ids.Next())
                .WithCompanyId(companyId)
                .WithType(SignalType.MediaAttention)
                .WithDirection(SignalDirection.Negative)
                .WithStrength(6)
                .WithNovelty(4)
                .WithConfidence(0.5m)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(observedAtUtc)
                .WithCreatedAtUtc(observedAtUtc)
                .WithMetadataJson(metadataJson)
                .Build();

        private static SignalBuilder BaseSignal(
            SequentialIds ids, Guid companyId, Guid evidenceId, SignalType type, int dayOffset) =>
            new SignalBuilder()
                .WithId(ids.Next())
                .WithEvidenceId(evidenceId)
                .WithCompanyId(companyId)
                .WithType(type)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(AsOf.AddDays(dayOffset))
                .WithCreatedAtUtc(AsOf.AddDays(dayOffset));

        private static EvidenceItem NewEvidence(SequentialIds ids, int dayOffset)
        {
            var id = ids.Next();
            return new EvidenceBuilder()
                .WithId(id)
                .WithContentHash(id.ToString("N"))
                .WithSourceType(EvidenceSourceType.NewsArticle)
                .WithSourceName($"Outlet {id:N}")
                .WithQuality(EvidenceQuality.Medium)
                .WithPublishedAtUtc(AsOf.AddDays(dayOffset))
                .WithCollectedAtUtc(AsOf.AddDays(dayOffset))
                .Build();
        }

        /// <summary>The accrued spec-191 shape: judgment/cohort/observation provenance, no version token.</summary>
        private static string LegacyEnvelope() => EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = "9c8f7e6d-3333-4c33-9333-cccccccccccc",
                [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "judge|p|s|stage1|families",
                [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
            },
            []);

        /// <summary>Claims the current version but carries no cohort key: unverifiable, fails closed.</summary>
        private static string MalformedEnvelope() => EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] =
                    NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = "e6e1d0f4-1111-4a11-9111-aaaaaaaaaaaa",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Improving",
            },
            []);
    }

    /// <summary>
    /// Build-local sequential ids. Two fixtures built from the same arguments therefore hold IDENTICAL
    /// signal/evidence ids, which is what lets the score-neutrality test compare two real scoring runs
    /// instead of two different inputs. Deterministic and clock-free (AD-3).
    /// </summary>
    internal sealed class SequentialIds
    {
        private int _next;

        public Guid Next() => new(++_next, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    }

    internal sealed class CapturingLogger : ILogger<ScoringEngine>, ILogger<ScoringPass>
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

    private sealed class StubStrategyFactory(IReadOnlyList<ScoringStrategyRuntime> runtimes)
        : IScoringStrategyFactory
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

    /// <summary>Records what the pass wrote, in order, so score neutrality is checked on real output.</summary>
    internal sealed class RecordingScoreStoreFactory : IScoreSnapshotFileStoreFactory, IScoreSnapshotFileStore
    {
        public List<(CompanyScoreSnapshot Snapshot, IReadOnlyList<ScoreEvidenceLink> Links)> Written { get; } =
            [];

        public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy) => this;

        public Task<DurableWriteResult> WriteAsync(
            CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct)
        {
            Written.Add((snapshot, links));
            return Task.FromResult(DurableWriteResult.Succeeded("written/snapshot.json"));
        }

        public Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            Task.FromResult<CompanyScoreSnapshot?>(null);

        public Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanyScoreSnapshot>>([]);
    }

    /// <summary>The previous/velocity window, supplied per company. Writes are inert.</summary>
    private sealed class StubSignalFileStore : ISignalFileStore
    {
        public Dictionary<Guid, IReadOnlyList<Signal>> PreviousWindow { get; } = [];

        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult(PreviousWindow.GetValueOrDefault(companyId, []));
    }

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input here.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=newssearch;";

        public IReadOnlyList<string> EnabledCollectors() => ["newssearch"];
    }
}
