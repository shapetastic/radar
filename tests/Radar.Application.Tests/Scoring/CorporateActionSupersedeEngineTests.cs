using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Signals;
using Radar.Application.Acquisitions;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Storage;
using Radar.Application.Tests.Acquisitions;
using Radar.Application.Tests.Pipeline;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 226 §1 / acceptance 5 — through the REAL <see cref="ScoringEngine"/>: a recognised pending-acquisition
/// filing reaches the scored set as EXACTLY ONE <see cref="SignalType.CorporateAction"/> whichever rule-set minted
/// its keyword read, and a recognition that rewrites nothing is COUNTED on the assembly diagnostics and stated
/// by the pass-boundary aggregator rather than skipped silently.
/// </summary>
public sealed class CorporateActionSupersedeEngineTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid CompanyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid MergerEvidenceId = Guid.Parse("db2132fb-c4b5-d2e7-6449-4716a6186a83");

    private sealed class Fixture
    {
        public InMemorySignalRepository Signals { get; } = new();

        public InMemoryEvidenceRepository Evidence { get; } = new();

        public InMemoryScoreRepository Scores { get; } = new();

        public ScoringEngine Engine(IPendingAcquisitionSource acquisitions) =>
            new(
                Signals,
                new EmptySignalFileStore(),
                Evidence,
                Scores,
                new InMemoryCompanyRepository(),
                new RadarScoreFormulaV8(new ScoringWeights(), new Radar.Infrastructure.Attention.ConfiguredAttentionSourceWeights(
                    Radar.Infrastructure.Attention.AttentionSourceTierOptions.Default)),
                new ScoringWeights(),
                new Radar.Infrastructure.Attention.ConfiguredAttentionSourceWeights(
                    Radar.Infrastructure.Attention.AttentionSourceTierOptions.Default),
                new StubDescriptor(),
                new InsiderMaterialityWeights(),
                new MediaAttentionCollapse(new MediaCollapseOptions()),
                new InsiderActivityCollapse(new InsiderCollapseOptions(), new InsiderMaterialityWeights()),
                new ScoringOptions { Window = TimeSpan.FromDays(30) },
                NullLogger<ScoringEngine>.Instance,
                strategyName: "default",
                pendingAcquisitions: acquisitions);

        public async Task SeedAsync(Guid evidenceId, SignalType type, SignalDirection direction, string reason)
        {
            var observed = WindowEnd.AddDays(-5);
            await Evidence.AddIfNewAsync(
                new EvidenceBuilder()
                    .WithId(evidenceId)
                    .WithContentHash(evidenceId.ToString("N"))
                    .WithSourceType(EvidenceSourceType.Filing)
                    .WithQuality(EvidenceQuality.High)
                    .WithPublishedAtUtc(observed)
                    .WithCollectedAtUtc(observed)
                    .Build(),
                CancellationToken.None);
            await Signals.AddAsync(
                new SignalBuilder()
                    .WithId(Guid.NewGuid())
                    .WithEvidenceId(evidenceId)
                    .WithCompanyId(CompanyId)
                    .WithType(type)
                    .WithDirection(direction)
                    .WithStrength(4)
                    .WithNovelty(5)
                    .WithConfidence(0.5m)
                    .WithReason(reason)
                    .WithReviewStatus(SignalReviewStatus.Approved)
                    .WithObservedAtUtc(observed)
                    .WithCreatedAtUtc(observed)
                    .Build(),
                CancellationToken.None);
        }
    }

    private static IPendingAcquisitionSource Recognised() =>
        new FixedSource(PendingAcquisitionsTests.From(PendingAcquisitionsTests.Record(
            companyId: CompanyId, evidenceId: MergerEvidenceId, announced: WindowEnd.AddDays(-5))));

    public static TheoryData<SignalType, SignalDirection> ReadsOfTheRecognisedFiling => new()
    {
        { SignalType.StrategicPartnership, SignalDirection.Positive },   // accrued radar-keyword-rules-v8
        { SignalType.CorporateAction, SignalDirection.Neutral },         // radar-keyword-rules-v9
    };

    [Theory]
    [MemberData(nameof(ReadsOfTheRecognisedFiling))]
    public async Task RecognisedFiling_ScoresAsExactlyOneStrengthZeroCorporateAction_WhicheverRuleSetMintedIt(
        SignalType storedType, SignalDirection storedDirection)
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(
            MergerEvidenceId,
            storedType,
            storedDirection,
            KeywordSignalReasons.ItemHeading([SecItemHeadingPhrases.MaterialDefinitiveAgreement]));

        var result = await fixture.Engine(Recognised()).ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);

        var link = Assert.Single(result.Links);
        Assert.StartsWith("CorporateAction (Neutral), strength 0", link.ContributionReason, StringComparison.Ordinal);
        Assert.Contains(CorporateActionSupersede.Version, link.ContributionReason, StringComparison.Ordinal);
        Assert.Equal(0, link.ContributionWeight);
        Assert.Equal(0, result.Diagnostics.RecognisedAcquisitionNothingRewritten);
    }

    [Fact]
    public async Task RecognitionWhoseFilingIsNotInTheWindow_IsCounted_NotSilent()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive, "Matched phrase 'contract win'");

        var result = await fixture.Engine(Recognised()).ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);

        Assert.Equal(1, result.Diagnostics.RecognisedAcquisitionNothingRewritten);
        Assert.Equal(0, result.Diagnostics.RecognisedAcquisitionFilingHadNoRewritableSignal);
    }

    [Fact]
    public async Task RecognisedFilingWithOnlyANonRewritableRead_IsCountedOnItsOwnAxis()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(MergerEvidenceId, SignalType.GuidanceChange, SignalDirection.Neutral, "Matched phrase 'results of operations'");

        var result = await fixture.Engine(Recognised()).ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);

        Assert.Equal(1, result.Diagnostics.RecognisedAcquisitionNothingRewritten);
        Assert.Equal(1, result.Diagnostics.RecognisedAcquisitionFilingHadNoRewritableSignal);
    }

    [Fact]
    public async Task NoRecognition_AddsNothingToTheDiagnostics()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync(MergerEvidenceId, SignalType.CorporateAction, SignalDirection.Neutral, "x");

        var result = await fixture.Engine(new FixedSource(PendingAcquisitions.None))
            .ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);

        Assert.Equal(ScoreAssemblyDiagnostics.None, result.Diagnostics);
        // An ordinary v9 item-heading read keeps its rule strength when no recognition applies.
        Assert.StartsWith("CorporateAction (Neutral), strength 4", Assert.Single(result.Links).ContributionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregator_NamesTheIdleRecognitions_InOneInformationLine()
    {
        var aggregator = new ScoreAssemblyDiagnosticsAggregator("Scoring pass");
        var aged = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var unrewritable = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var idle = ScoreAssemblyDiagnostics.None with { RecognisedAcquisitionNothingRewritten = 1 };

        aggregator.Record("default", aged, WindowEnd, idle);
        aggregator.Record("alt", aged, WindowEnd, idle);
        aggregator.Record("default", unrewritable, WindowEnd, idle with { RecognisedAcquisitionFilingHadNoRewritableSignal = 1 });
        aggregator.Record("default", Guid.NewGuid(), WindowEnd, ScoreAssemblyDiagnostics.None);

        var log = new ScoreAssemblyDiagnosticsAggregationTests.CapturingLogger();
        aggregator.LogAggregates(log);

        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(
            $"Scoring pass: {CorporateActionSupersede.Version} rewrote NOTHING for 2 company/companies under a recognised "
                + $"pending acquisition ({aged}, {unrewritable}), across 3 strategy-company evaluation(s) and 2 distinct strateg(ies)",
            line.Message,
            StringComparison.Ordinal);
        Assert.Contains($"Of these, 1 company/companies ({unrewritable}) had signals", line.Message, StringComparison.Ordinal);

        var silent = new ScoreAssemblyDiagnosticsAggregator("Scoring pass");
        silent.Record("default", Guid.NewGuid(), WindowEnd, ScoreAssemblyDiagnostics.None);
        var silentLog = new ScoreAssemblyDiagnosticsAggregationTests.CapturingLogger();
        silent.LogAggregates(silentLog);
        Assert.Empty(silentLog.Entries);
    }

    private sealed class FixedSource(PendingAcquisitions acquisitions) : IPendingAcquisitionSource
    {
        public Task<PendingAcquisitions> GetAsync(CancellationToken ct) => Task.FromResult(acquisitions);
    }

    private sealed class EmptySignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class StubDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-test;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
