using Radar.Application.Collectors;
using Radar.Application.Efficacy.Attention;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Efficacy.Attention;

/// <summary>
/// AD-16 §1's primary attention metric (spec 169): distinct third-party publishers with a resolving
/// <c>MediaAttention</c> signal in an exact half-open interval. The same builder produces BOTH the outcome and
/// the persistence comparator, so every rule here binds both windows.
/// </summary>
public sealed class AttentionPublisherCountBuilderTests
{
    private static readonly Guid CompanyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherCompanyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static readonly DateTimeOffset WindowStart = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private static AttentionPublisherCountBuilder Create(
        FakeSignalRepository signals,
        FakeEvidenceRepository evidence,
        ICollectorAttributionResolver? attribution = null) =>
        new(
            signals,
            evidence,
            attribution ?? new RecordedOnlyCollectorAttributionResolver(),
            AttentionTestFakes.Options());

    private static Task<AttentionPublisherCountResult> BuildAsync(
        FakeSignalRepository signals,
        FakeEvidenceRepository evidence,
        AttentionWindow window = AttentionWindow.Outcome,
        ICollectorAttributionResolver? attribution = null) =>
        Create(signals, evidence, attribution)
            .BuildAsync(CompanyId, WindowStart, WindowEnd, window, CancellationToken.None);

    [Fact]
    public async Task NoRelevantSignals_IsAValidIntegerZero_NotAFailure()
    {
        // The central negative case. Selecting only companies where attention arrived would select on the
        // outcome and destroy the test (AD-16 §5).
        var result = await BuildAsync(new FakeSignalRepository(), new FakeEvidenceRepository());

        Assert.True(result.IsDefined);
        Assert.Equal(0, result.Count);
        Assert.Equal(AttentionPublisherCountFailure.None, result.Failure);
    }

    [Fact]
    public async Task DistinctPublishers_AreCountedOnce_AndCanonicalisedByWhitespaceAndCaseOnly()
    {
        var e1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        var e2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var e3 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
        var e4 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");

        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(e1, "Reuters"),
            // Same outlet, different article: one outlet syndicating itself is not the market noticing.
            AttentionTestFakes.NewsEvidence(e2, "  reuters  "),
            AttentionTestFakes.NewsEvidence(e3, "Yahoo   Finance"),
            AttentionTestFakes.NewsEvidence(e4, "yahoo finance"));

        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, e1, WindowStart.AddDays(1)),
            AttentionTestFakes.MediaAttentionSignal(CompanyId, e2, WindowStart.AddDays(2)),
            AttentionTestFakes.MediaAttentionSignal(CompanyId, e3, WindowStart.AddDays(3)),
            AttentionTestFakes.MediaAttentionSignal(CompanyId, e4, WindowStart.AddDays(4)));

        var result = await BuildAsync(signals, evidence);

        Assert.True(result.IsDefined);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task IntervalIsHalfOpen_ExactlyAtStartIsOut_ExactlyAtEndIsIn()
    {
        var atStart = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
        var atEnd = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(atStart, "Excluded Wire"),
            AttentionTestFakes.NewsEvidence(atEnd, "Included Wire"));

        var signals = new FakeSignalRepository().With(
            // Exactly AT the exclusive start: out. Using the whole UTC date instead of the exact instant
            // would look ahead to articles published later on the scoring day.
            AttentionTestFakes.MediaAttentionSignal(CompanyId, atStart, WindowStart),
            // Exactly AT the inclusive end: in.
            AttentionTestFakes.MediaAttentionSignal(CompanyId, atEnd, WindowEnd));

        var result = await BuildAsync(signals, evidence);

        Assert.True(result.IsDefined);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task OneTickOutsideEitherEndpoint_IsExcluded()
    {
        var before = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var after = Guid.Parse("cccccccc-0000-0000-0000-000000000004");

        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(before, "Too Early"),
            AttentionTestFakes.NewsEvidence(after, "Too Late"));

        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, before, WindowStart.AddTicks(-1)),
            AttentionTestFakes.MediaAttentionSignal(CompanyId, after, WindowEnd.AddTicks(1)));

        Assert.Equal(0, (await BuildAsync(signals, evidence)).Count);
    }

    [Fact]
    public async Task OneTickInsideTheStart_IsIncluded()
    {
        var justInside = Guid.Parse("cccccccc-0000-0000-0000-000000000005");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(justInside, "Just In Time"));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, justInside, WindowStart.AddTicks(1)));

        Assert.Equal(1, (await BuildAsync(signals, evidence)).Count);
    }

    [Theory]
    [InlineData(AttentionWindow.Comparator, AttentionPublisherCountFailure.UnresolvedComparatorEvidence)]
    [InlineData(AttentionWindow.Outcome, AttentionPublisherCountFailure.UnresolvedOutcomeEvidence)]
    public async Task UnresolvedEvidence_DropsTheCompanyDate_WithAWindowScopedReason(
        AttentionWindow window, AttentionPublisherCountFailure expected)
    {
        // The evidence repository is EMPTY: the signal references evidence that does not resolve. It must
        // never be treated as a lower publisher count — that would turn missing data into a measurement.
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(
                CompanyId, Guid.Parse("dddddddd-0000-0000-0000-000000000001"), WindowStart.AddDays(1)));

        var result = await BuildAsync(signals, new FakeEvidenceRepository(), window);

        Assert.False(result.IsDefined);
        Assert.Equal(expected, result.Failure);
    }

    [Theory]
    [InlineData(AttentionWindow.Comparator, AttentionPublisherCountFailure.MissingComparatorPublisher)]
    [InlineData(AttentionWindow.Outcome, AttentionPublisherCountFailure.MissingOutcomePublisher)]
    public async Task BlankPublisher_IsAFailure_AndTheFeedNameFallbackIsNeverCounted(
        AttentionWindow window, AttentionPublisherCountFailure expected)
    {
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

        // publisher: null ⇒ the collector's SourceName carries the per-company FEED name instead. That is
        // Radar's own label, not a third party noticing the company, so counting it would manufacture an
        // outlet out of thin air.
        var item = AttentionTestFakes.NewsEvidence(evidenceId, publisher: null);
        Assert.Contains("News attention", item.SourceName);

        var evidence = new FakeEvidenceRepository().With(item);
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        var result = await BuildAsync(signals, evidence, window);

        Assert.False(result.IsDefined);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task WhitespaceOnlyPublisher_IsAlsoBlank()
    {
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000003");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(evidenceId, "   "));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        Assert.Equal(
            AttentionPublisherCountFailure.MissingOutcomePublisher,
            (await BuildAsync(signals, evidence)).Failure);
    }

    [Theory]
    [InlineData(AttentionWindow.Comparator, AttentionPublisherCountFailure.UnresolvedComparatorProvenance)]
    [InlineData(AttentionWindow.Outcome, AttentionPublisherCountFailure.UnresolvedOutcomeProvenance)]
    public async Task MissingCollectorAttribution_IsAProvenanceFailure(
        AttentionWindow window, AttentionPublisherCountFailure expected)
    {
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(evidenceId, "Reuters", collector: null));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        var result = await BuildAsync(signals, evidence, window);

        Assert.False(result.IsDefined);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task UnsupportedCollectorAttribution_IsAlsoAProvenanceFailure()
    {
        // GDELT news supplies no per-company coverage contract, so an article it retrieved cannot enter an
        // outcome whose completeness has to be provable.
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000005");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(evidenceId, "Reuters", collector: AttentionTestFakes.Gdelt));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        Assert.Equal(
            AttentionPublisherCountFailure.UnresolvedOutcomeProvenance,
            (await BuildAsync(signals, evidence)).Failure);
    }

    [Theory]
    [InlineData(AttentionWindow.Comparator, AttentionPublisherCountFailure.UnresolvedComparatorProvenance)]
    [InlineData(AttentionWindow.Outcome, AttentionPublisherCountFailure.UnresolvedOutcomeProvenance)]
    public async Task InferredCollectorAttribution_IsAlsoAProvenanceFailure_EvenForTheSupportedCollector(
        AttentionWindow window, AttentionPublisherCountFailure expected)
    {
        // AD-16 §5's "no inferred success": spec 151 re-DERIVES a collector for legacy evidence, and a
        // derivation cannot prove that this article's collection was complete. Only a RECORDED stamp can.
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000007");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(evidenceId, "Reuters", collector: null));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        var result = await BuildAsync(
            signals, evidence, window, new AttentionTestFakes.InferringResolver());

        Assert.False(result.IsDefined);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task TheMetric_IsInvariantToWhichAttributionResolverIsComposed()
    {
        // The screen is PRECOMMITTED, so its primary metric must not move when an operator flips the
        // scoring-only Radar:Scoring:InferLegacyCollectorAttribution flag between runs.
        var recordedId = Guid.Parse("dddddddd-0000-0000-0000-000000000008");
        var legacyId = Guid.Parse("dddddddd-0000-0000-0000-000000000009");
        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(recordedId, "Reuters"),
            AttentionTestFakes.NewsEvidence(legacyId, "Bloomberg", collector: null));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, recordedId, WindowStart.AddDays(1)),
            AttentionTestFakes.MediaAttentionSignal(CompanyId, legacyId, WindowStart.AddDays(2)));

        var recordedOnly = await BuildAsync(signals, evidence);
        var inferring = await BuildAsync(
            signals, evidence, attribution: new AttentionTestFakes.InferringResolver());

        Assert.Equal(recordedOnly, inferring);
        Assert.Equal(AttentionPublisherCountFailure.UnresolvedOutcomeProvenance, recordedOnly.Failure);
    }

    [Fact]
    public async Task NonNewsArticleEvidence_IsSkipped_NotFailed()
    {
        // A MediaAttention signal can attach to (say) a press release. That is outside the metric — AD-16 §1
        // counts the MARKET noticing — rather than a failure of it.
        var evidenceId = Guid.Parse("dddddddd-0000-0000-0000-000000000006");
        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(
                evidenceId, publisher: null, collector: null,
                sourceType: EvidenceSourceType.PressRelease));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(1)));

        var result = await BuildAsync(signals, evidence);

        Assert.True(result.IsDefined);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task NonApprovedOrNonMediaAttentionSignals_AndOtherCompanies_AreIgnored()
    {
        var approved = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
        var pending = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");
        var wrongType = Guid.Parse("eeeeeeee-0000-0000-0000-000000000003");
        var otherCompany = Guid.Parse("eeeeeeee-0000-0000-0000-000000000004");

        var evidence = new FakeEvidenceRepository().With(
            AttentionTestFakes.NewsEvidence(approved, "Reuters"),
            AttentionTestFakes.NewsEvidence(pending, "Bloomberg"),
            AttentionTestFakes.NewsEvidence(wrongType, "Associated Press"),
            AttentionTestFakes.NewsEvidence(otherCompany, "Financial Times"));

        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, approved, WindowStart.AddDays(1)),
            AttentionTestFakes.MediaAttentionSignal(
                CompanyId, pending, WindowStart.AddDays(1), status: SignalReviewStatus.NeedsHumanReview),
            AttentionTestFakes.MediaAttentionSignal(
                CompanyId, wrongType, WindowStart.AddDays(1), type: SignalType.CustomerWin),
            AttentionTestFakes.MediaAttentionSignal(OtherCompanyId, otherCompany, WindowStart.AddDays(1)));

        var result = await BuildAsync(signals, evidence);

        Assert.True(result.IsDefined);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task BothWindows_UseTheSameConstruction_OnlyTheReportedReasonDiffers()
    {
        // The comparator and the outcome must not acquire subtly different filters: the screen would then be
        // comparing two metrics rather than a score against a persistence baseline.
        var evidenceId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var evidence = new FakeEvidenceRepository()
            .With(AttentionTestFakes.NewsEvidence(evidenceId, "Reuters"));
        var signals = new FakeSignalRepository().With(
            AttentionTestFakes.MediaAttentionSignal(CompanyId, evidenceId, WindowStart.AddDays(3)));

        var comparator = await BuildAsync(signals, evidence, AttentionWindow.Comparator);
        var outcome = await BuildAsync(signals, evidence, AttentionWindow.Outcome);

        Assert.Equal(comparator.Count, outcome.Count);
        Assert.Equal(comparator.IsDefined, outcome.IsDefined);
    }
}
