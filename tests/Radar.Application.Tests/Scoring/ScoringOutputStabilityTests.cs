using Microsoft.Extensions.Logging.Abstractions;

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
/// SPEC 148's "scores must be byte-identical" criterion, made checkable instead of argued.
/// <para>
/// Spec 148 moved BOTH pinned fingerprints on purpose — it folded two output-affecting inputs (the scoring
/// window and <see cref="ScoringWeights.TrajectoryCorroborationK"/>) that had been hashed into nothing. The
/// obligation that comes with a deliberate pin move is to prove the SCORES did not move with it: the slice is
/// identity and record-keeping, not a recalibration.
/// </para>
/// <para>
/// This file pins the whole scoring OUTPUT of one fixed fixture under the REAL <c>radar-formula-v8</c> — all
/// five components, the explanation, the component JSON and the ordered evidence-link chain — at the code
/// defaults. The values were not merely captured on the branch and declared unchanged: this exact file was
/// compiled and run against the pre-148 production sources (<c>origin/main</c> @ <c>b9b3f65</c>) and passed
/// there too, so "the scores did not move" is measured rather than argued. It compiles against pre-148 code
/// by construction — it touches no API this slice changed.
/// </para>
/// <para>
/// If a future change moves a number here, it moved a score, and no fingerprint bookkeeping can explain that
/// away. A deliberate formula change must update these pins consciously, exactly as it must the fingerprint
/// pins in <c>ScoringConfigFingerprintTests</c>.
/// </para>
/// <para>
/// SPEC 149 REUSED THIS FILE RATHER THAN WRITING A THIRD HARNESS, and it is the reason this file is not
/// merely historical. That slice EXTRACTED v8's notedness/following discount into
/// <see cref="ScoreSignalMath.NotednessDiscount"/> so <c>radar-formula-v9</c> could apply the same one, and
/// its constraint was that v8 stays byte-identical. This file is untouched by that slice and still passes —
/// which is exactly the evidence required, because the fixture below exercises the discount (a Reuters media
/// item gives it a non-zero Attention to discount by).
/// </para>
/// </summary>
public sealed class ScoringOutputStabilityTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution ORDER (observed instant, then signal id) is stable across
    // runs and machines — otherwise the pinned link chain below would be a coin toss (AD-3).
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FilingSignalId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FilingEvidenceId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PressSignalId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PressEvidenceId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid MediaSignalId = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid MediaEvidenceId = new("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task DefaultConfig_ScoresAreUnchangedByTheSpec148FingerprintFold()
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        var engine = new ScoringEngine(
            signals,
            new NullSignalFileStore(),
            evidence,
            new InMemoryScoreRepository(),
            companies,
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            // The CODE DEFAULT window — the one the pins are computed at (30 days).
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance);

        await companies.AddAsync(new CompanyBuilder().WithId(CompanyId).Build(), CancellationToken.None);

        // A deliberately mixed set: a positive high-quality filing, a NEGATIVE press release (so the
        // corroboration-smoothed Trajectory is exercised rather than saturated) and a third-party media item
        // (so the Attention breadth and its inverse Opportunity discount are exercised too).
        //
        // HONEST LIMIT, stated so nobody reads more into these pins than they carry: the components are ints,
        // so this fixture is only sensitive to a LARGE change in TrajectoryCorroborationK — k 10 → 12 leaves
        // every pinned value identical, k 10 → 20 moves Trajectory 57 → 55. These pins prove the slice did not
        // move the scores; they are NOT a fine-grained detector of a small k recalibration. That detection
        // belongs to the FINGERPRINT (which now folds k by value, spec 148), not to a rounded component.
        await SeedAsync(
            signals, evidence, FilingSignalId, FilingEvidenceId, SignalType.GuidanceChange,
            SignalDirection.Positive, strength: 8, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-3));
        await SeedAsync(
            signals, evidence, PressSignalId, PressEvidenceId, SignalType.CustomerWin,
            SignalDirection.Negative, strength: 5, EvidenceSourceType.PressRelease, "Acme Newsroom",
            EvidenceQuality.High, WindowEnd.AddDays(-10));
        await SeedAsync(
            signals, evidence, MediaSignalId, MediaEvidenceId, SignalType.MediaAttention,
            SignalDirection.Neutral, strength: 3, EvidenceSourceType.NewsArticle, "Reuters",
            EvidenceQuality.Medium, WindowEnd.AddDays(-20));

        var result = await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
        var snapshot = result.Snapshot;

        // ---- the five components -------------------------------------------------------------------
        Assert.Equal(57, snapshot.TrajectoryScore);
        Assert.Equal(41, snapshot.OpportunityScore);
        Assert.Equal(27, snapshot.AttentionScore);
        Assert.Equal(80, snapshot.EvidenceConfidenceScore);
        Assert.Equal(100, snapshot.SignalVelocityScore);

        // ---- the narrative + the machine-readable breakdown ------------------------------------------
        Assert.Equal(
            "radar-formula-v8: 3 signal(s) over 30d → Trajectory 57, Opportunity 41 "
                + "(Attention 27, Confidence 80, Velocity 100).",
            snapshot.Explanation);
        Assert.Equal(
            "{\"TrajectoryScore\":57,\"OpportunityScore\":41,\"AttentionScore\":27,"
                + "\"EvidenceConfidenceScore\":80,\"SignalVelocityScore\":100}",
            snapshot.ComponentJson);

        // ---- the provenance chain, in order ----------------------------------------------------------
        Assert.Equal(
            [
                (MediaSignalId, MediaEvidenceId, 0),
                (PressSignalId, PressEvidenceId, -3),
                (FilingSignalId, FilingEvidenceId, 6),
            ],
            result.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight)).ToArray());

        // ---- and the stamp that DID move, recorded here so the two facts sit side by side -------------
        Assert.Equal("mvp-engine-v1+radar-formula-v8", snapshot.ScoringVersion);
    }

    private static async Task SeedAsync(
        InMemorySignalRepository signals,
        InMemoryEvidenceRepository evidence,
        Guid signalId,
        Guid evidenceId,
        SignalType type,
        SignalDirection direction,
        int strength,
        EvidenceSourceType sourceType,
        string sourceName,
        EvidenceQuality quality,
        DateTimeOffset observedAtUtc)
    {
        var item = new EvidenceBuilder()
            .WithId(evidenceId)
            .WithContentHash(evidenceId.ToString("N"))
            .WithSourceType(sourceType)
            .WithSourceName(sourceName)
            .WithQuality(quality)
            .WithPublishedAtUtc(observedAtUtc)
            .WithCollectedAtUtc(observedAtUtc)
            .Build();

        var signal = new SignalBuilder()
            .WithId(signalId)
            .WithEvidenceId(evidenceId)
            .WithCompanyId(CompanyId)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAtUtc)
            .WithCreatedAtUtc(observedAtUtc)
            .Build();

        await evidence.AddIfNewAsync(item, CancellationToken.None);
        await signals.AddAsync(signal, CancellationToken.None);
    }

    /// <summary>The previous/velocity window is deliberately empty: this fixture pins the CURRENT window.</summary>
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

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v6;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
