using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 204 §4, end to end through the real <see cref="ScoringEngine"/> — the proof that persisting the
/// non-directional AI read moves NOTHING but provenance.
/// <para>
/// One company, one earnings filing, one evidence id. Scored three ways: (a) the spec-57 keyword Neutral
/// GuidanceChange alone — what the store held pre-204; (b) the keyword Neutral PLUS the §1 Mixed read signal
/// over the SAME evidence (keyword magnitudes, real metadata envelope); (c) the keyword Neutral PLUS the §1
/// Neutral read signal. All five <c>ScoreComponents</c>, the explanation and the <c>ComponentJson</c> must be
/// byte-identical across (a)/(b)/(c); the ONLY differences the pin admits are the surviving link's signal id,
/// its contribution reason (the supersede note plus the read's own reason text) and its metadata.
/// </para>
/// <para>
/// <b>MUTATION PROOF.</b> <see cref="MixedReadAtStrength8_DivergesFromKeywordOnly"/> re-runs (b) with the
/// Mixed read at Strength 8 and asserts the result is NOT identical to (a) — SignalVelocity consumes raw
/// strength, so the divergence is real and measured. That is what proves the MAGNITUDES, not the direction,
/// are what keep (a)/(b)/(c) identical: if the parity tests passed because Mixed happened to be invisible to
/// every component, the strength-8 variant would be identical too, and this test would fail instead.
/// </para>
/// <para>
/// Every fixture is CONSTRUCTED in this file (nothing copied from the live store) and the read signal's
/// envelope is composed through the REAL <see cref="FilingReadSignalMetadata.Compose"/>, so the pin cannot
/// drift from what the producer persists.
/// </para>
/// </summary>
public sealed class FilingReadScoreParityTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution order (observed instant, then signal id) is stable across
    // machines (AD-3). The read signal's id is deliberately HIGHER than the keyword one's, so the supersede
    // can only pick it via the spec-204 read-preference step, never via the Id tie-break.
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FilingEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KeywordSignalId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ReadSignalId = new("77777777-7777-7777-7777-777777777777");

    private enum ReadVariant
    {
        None,
        MixedRead,
        NeutralRead,
        MixedReadAtStrength8, // the mutation arm — NOT a valid §1 signal, deliberately.
    }

    [Fact]
    public async Task AllFiveComponents_ExplanationAndComponentJson_AreByteIdentical_AcrossKeywordMixedAndNeutralRead()
    {
        var keywordOnly = await ScoreAsync(ReadVariant.None);
        var withMixed = await ScoreAsync(ReadVariant.MixedRead);
        var withNeutral = await ScoreAsync(ReadVariant.NeutralRead);

        foreach (var variant in new[] { withMixed, withNeutral })
        {
            Assert.Equal(keywordOnly.Snapshot.TrajectoryScore, variant.Snapshot.TrajectoryScore);
            Assert.Equal(keywordOnly.Snapshot.OpportunityScore, variant.Snapshot.OpportunityScore);
            Assert.Equal(keywordOnly.Snapshot.AttentionScore, variant.Snapshot.AttentionScore);
            Assert.Equal(keywordOnly.Snapshot.EvidenceConfidenceScore, variant.Snapshot.EvidenceConfidenceScore);
            Assert.Equal(keywordOnly.Snapshot.SignalVelocityScore, variant.Snapshot.SignalVelocityScore);
            // Byte-identical, not merely equal-valued: the explanation embeds the (post-supersede) signal
            // count and every component, and ComponentJson is the serialized components.
            Assert.Equal(keywordOnly.Snapshot.Explanation, variant.Snapshot.Explanation);
            Assert.Equal(keywordOnly.Snapshot.ComponentJson, variant.Snapshot.ComponentJson);
        }
    }

    [Fact]
    public async Task OnlyTheSurvivingLinksProvenanceDiffers()
    {
        var keywordOnly = await ScoreAsync(ReadVariant.None);
        var withMixed = await ScoreAsync(ReadVariant.MixedRead);
        var withNeutral = await ScoreAsync(ReadVariant.NeutralRead);

        // (a): the keyword Neutral is the one contribution, no supersede note.
        var baseline = Assert.Single(keywordOnly.Links);
        Assert.Equal(KeywordSignalId, baseline.SignalId);
        Assert.DoesNotContain("superseded", baseline.ContributionReason, StringComparison.OrdinalIgnoreCase);

        // (b)/(c): still exactly ONE contribution over the same evidence — the READ — and the removal of the
        // keyword copy is named on it (the spec-193 accounting shape, unchanged).
        foreach (var (variant, expectedDirection) in new[] { (withMixed, "Mixed"), (withNeutral, "Neutral") })
        {
            var link = Assert.Single(variant.Links);
            Assert.Equal(FilingEvidenceId, link.EvidenceId);
            Assert.Equal(ReadSignalId, link.SignalId);
            Assert.StartsWith($"GuidanceChange ({expectedDirection})", link.ContributionReason, StringComparison.Ordinal);
            Assert.EndsWith(
                " (superseded 1 stale GuidanceChange signal(s) for this evidence)",
                link.ContributionReason,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MixedReadAtStrength8_DivergesFromKeywordOnly()
    {
        // THE MUTATION PROOF. A Mixed read at Strength 8 (what naively reusing the directional
        // DirectionalFilingSignalOptions.Strength would produce) is NOT score-identical to the keyword
        // baseline: SignalVelocity sums raw strength over the current window, so the survivor's 8 vs 3
        // moves it — measured here, not argued. If this assertion ever fails, the parity above has gone
        // vacuous (Mixed became invisible to every component) and the pin no longer proves anything.
        var keywordOnly = await ScoreAsync(ReadVariant.None);
        var mutated = await ScoreAsync(ReadVariant.MixedReadAtStrength8);

        Assert.NotEqual(keywordOnly.Snapshot.SignalVelocityScore, mutated.Snapshot.SignalVelocityScore);
        Assert.NotEqual(keywordOnly.Snapshot.ComponentJson, mutated.Snapshot.ComponentJson);
        Assert.NotEqual(keywordOnly.Snapshot.Explanation, mutated.Snapshot.Explanation);
    }

    // ---------------------------------------------------------------------------------------------
    // The fixture.
    // ---------------------------------------------------------------------------------------------

    private static async Task<CompanyScoreResult> ScoreAsync(ReadVariant variant)
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        var engine = new ScoringEngine(
            signals,
            new EmptyPreviousWindowSignalFileStore(),
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

        await companies.AddAsync(new CompanyBuilder().WithId(CompanyId).Build(), CancellationToken.None);

        await evidence.AddIfNewAsync(
            new EvidenceBuilder()
                .WithId(FilingEvidenceId)
                .WithContentHash(FilingEvidenceId.ToString("N"))
                .WithSourceType(EvidenceSourceType.Filing)
                .WithSourceName("Acme — SEC")
                .WithQuality(EvidenceQuality.High)
                .WithPublishedAtUtc(WindowEnd.AddDays(-3))
                .WithCollectedAtUtc(WindowEnd.AddDays(-3))
                .Build(),
            CancellationToken.None);

        await signals.AddAsync(KeywordNeutral(), CancellationToken.None);
        switch (variant)
        {
            case ReadVariant.MixedRead:
                await signals.AddAsync(ReadSignal(SignalDirection.Mixed, FilingNoSignalCause.Mixed, "Mixed",
                    FilingReadSignalMetadata.Strength), CancellationToken.None);
                break;
            case ReadVariant.NeutralRead:
                await signals.AddAsync(ReadSignal(SignalDirection.Neutral, FilingNoSignalCause.Unknown, "Unknown",
                    FilingReadSignalMetadata.Strength), CancellationToken.None);
                break;
            case ReadVariant.MixedReadAtStrength8:
                await signals.AddAsync(ReadSignal(SignalDirection.Mixed, FilingNoSignalCause.Mixed, "Mixed",
                    strength: 8), CancellationToken.None);
                break;
        }

        return await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
    }

    /// <summary>Exactly what the spec-57 keyword fallback stores for an earnings 8-K: Neutral, 3/4/0.4, no envelope.</summary>
    private static Signal KeywordNeutral() =>
        GuidanceBuilder(KeywordSignalId)
            .WithDirection(SignalDirection.Neutral)
            .WithMetadataJson(null)
            .Build();

    /// <summary>
    /// The §1 read signal over the SAME evidence — same magnitudes as the keyword copy (unless the mutation
    /// arm overrides strength), with the envelope composed through the REAL producer.
    /// </summary>
    private static Signal ReadSignal(
        SignalDirection direction, FilingNoSignalCause cause, string readDirection, int strength) =>
        GuidanceBuilder(ReadSignalId)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithMetadataJson(FilingReadSignalMetadata.Compose(cause, readDirection, 0.85m, "openai:test-model"))
            .Build();

    private static SignalBuilder GuidanceBuilder(Guid id) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(FilingEvidenceId)
            .WithCompanyId(CompanyId)
            .WithType(SignalType.GuidanceChange)
            // The keyword fallback's magnitudes — asserted equal to the extractor's own output by
            // FilingReadSignalMetadataTests, so this fixture cannot silently drift from production.
            .WithStrength(FilingReadSignalMetadata.Strength)
            .WithNovelty(FilingReadSignalMetadata.Novelty)
            .WithConfidence(FilingReadSignalMetadata.Confidence)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-3))
            .WithCreatedAtUtc(WindowEnd.AddDays(-3));

    /// <summary>
    /// An EMPTY previous/velocity window, deliberately: with a mirrored previous window the strength-8
    /// mutation would cancel out of the velocity RATIO ((8+s)/(8+s) == (3+s)/(3+s)) and the mutation proof
    /// would go vacuous. An empty previous window makes velocity a direct function of current-window
    /// strength, which is exactly the lever the mutation must be seen to move.
    /// </summary>
    private sealed class EmptyPreviousWindowSignalFileStore : ISignalFileStore
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

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input here.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=sec;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec"];
    }
}
