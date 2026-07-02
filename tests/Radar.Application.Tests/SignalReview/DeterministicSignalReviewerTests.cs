using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.SignalReview;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.SignalReview;

public class DeterministicSignalReviewerTests
{
    private const string ExpectedReviewerName = "deterministic-rules-v1";

    private static readonly DateTimeOffset FixedNow =
        new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ObservedAt =
        new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid EvidenceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> always returns a constant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DeterministicSignalReviewer CreateSut() =>
        new(new FixedTimeProvider(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance);

    private static EvidenceItem MakeEvidence(
        EvidenceQuality quality = EvidenceQuality.High,
        Guid? id = null) =>
        new EvidenceBuilder()
            .WithId(id ?? EvidenceId)
            .WithRawText("Acme signed a multi-year deal with a major customer.")
            .WithPublishedAtUtc(ObservedAt)
            .WithCollectedAtUtc(CreatedAt)
            .WithQuality(quality)
            .Build();

    private static Signal MakeSignal(
        Guid? companyId = null,
        int strength = 6,
        int novelty = 6,
        decimal confidence = 0.8m,
        Guid? evidenceId = null) =>
        new SignalBuilder()
            .WithEvidenceId(evidenceId ?? EvidenceId)
            .WithCompanyId(companyId)
            .WithStrength(strength)
            .WithNovelty(novelty)
            .WithConfidence(confidence)
            .WithObservedAtUtc(ObservedAt)
            .WithCreatedAtUtc(CreatedAt)
            .Build();

    [Fact]
    public async Task CleanSignal_Approves_LeavesConfidence_AndEmptyIssues()
    {
        var signal = MakeSignal(companyId: CompanyId, strength: 6, novelty: 6, confidence: 0.8m);
        var evidence = MakeEvidence(EvidenceQuality.High);

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.Approve, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.Approved, outcome.ReviewedSignal.ReviewStatus);
        Assert.Equal(0.8m, outcome.ReviewedSignal.Confidence);
        Assert.Equal("[]", outcome.Review.IssuesJson);
        Assert.Equal("All checks passed.", outcome.Review.Summary);
    }

    [Fact]
    public async Task NullCompany_EscalatesToHuman()
    {
        var signal = MakeSignal(companyId: null);
        var evidence = MakeEvidence();

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.EscalateToHuman, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.NeedsHumanReview, outcome.ReviewedSignal.ReviewStatus);
    }

    [Fact]
    public async Task ImmaterialStrength_WithResolvedCompany_NeedsMoreEvidence()
    {
        var signal = MakeSignal(companyId: CompanyId, strength: 2);
        var evidence = MakeEvidence();

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.NeedsMoreEvidence, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.NeedsHumanReview, outcome.ReviewedSignal.ReviewStatus);
    }

    [Fact]
    public async Task GovContractFloorStrength_IsFlaggedImmaterialByExistingGuardrail()
    {
        // Spec 66: a sub-material (< $100k) GovernmentContract award is emitted at floor Strength 2.
        // With novelty/confidence/source otherwise clean, materiality is the deciding issue: the
        // EXISTING MinMaterialStrength guardrail (strict < 3) flags it NeedsMoreEvidence — the floor
        // reuses that guardrail rather than adding a new drop path.
        var signal = MakeSignal(companyId: CompanyId, strength: 2, novelty: 6, confidence: 0.8m);
        var evidence = MakeEvidence(EvidenceQuality.High);

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.NeedsMoreEvidence, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.NeedsHumanReview, outcome.ReviewedSignal.ReviewStatus);
    }

    [Fact]
    public async Task LowConfidenceOnly_ReducesConfidence_AndApproves()
    {
        var signal = MakeSignal(companyId: CompanyId, confidence: 0.2m);
        var evidence = MakeEvidence(EvidenceQuality.High);

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.ReduceConfidence, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.Approved, outcome.ReviewedSignal.ReviewStatus);
        Assert.True(outcome.ReviewedSignal.Confidence < 0.2m);
        Assert.InRange(outcome.ReviewedSignal.Confidence, 0m, 1m);
    }

    [Fact]
    public async Task WeakSourceOnly_ReducesConfidence()
    {
        var signal = MakeSignal(companyId: CompanyId, confidence: 0.8m);
        var evidence = MakeEvidence(EvidenceQuality.Unknown);

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.ReduceConfidence, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.Approved, outcome.ReviewedSignal.ReviewStatus);
        Assert.True(outcome.ReviewedSignal.Confidence < 0.8m);
    }

    [Fact]
    public async Task Precedence_UnresolvedCompanyAndLowConfidence_EscalatesToHuman()
    {
        var signal = MakeSignal(companyId: null, confidence: 0.2m);
        var evidence = MakeEvidence();

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.EscalateToHuman, outcome.Review.Decision);
        Assert.Equal(SignalReviewStatus.NeedsHumanReview, outcome.ReviewedSignal.ReviewStatus);
        // Confidence unchanged for escalation.
        Assert.Equal(0.2m, outcome.ReviewedSignal.Confidence);
    }

    [Fact]
    public async Task EvidenceMismatch_EscalatesToHuman_AndRecordsIssue()
    {
        var signal = MakeSignal(companyId: CompanyId);
        var evidence = MakeEvidence(id: Guid.Parse("33333333-3333-3333-3333-333333333333"));

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(SignalReviewDecision.EscalateToHuman, outcome.Review.Decision);
        var issues = JsonSerializer.Deserialize<List<string>>(outcome.Review.IssuesJson!)!;
        Assert.Contains("Evidence does not match signal.EvidenceId", issues);
    }

    [Fact]
    public async Task AuditFields_ReflectFixedClock_SignalId_AndVersionedReviewer()
    {
        var signal = MakeSignal(companyId: null, confidence: 0.2m, strength: 1, novelty: 1);
        var evidence = MakeEvidence(EvidenceQuality.Low);

        var outcome = await CreateSut().ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(FixedNow, outcome.Review.ReviewedAtUtc);
        Assert.Equal(signal.Id, outcome.Review.SignalId);
        Assert.Equal(ExpectedReviewerName, outcome.Review.ReviewerName);

        var issues = JsonSerializer.Deserialize<List<string>>(outcome.Review.IssuesJson!)!;
        // unresolved company, low strength, low novelty, low confidence, weak source = 5 issues.
        Assert.Equal(5, issues.Count);
    }

    [Fact]
    public async Task Determinism_SameInputs_ProduceEqualDecisionStatusAndConfidence()
    {
        var signal = MakeSignal(companyId: CompanyId, confidence: 0.3m);
        var evidence = MakeEvidence(EvidenceQuality.Low);
        var sut = CreateSut();

        var first = await sut.ReviewAsync(signal, evidence, CancellationToken.None);
        var second = await sut.ReviewAsync(signal, evidence, CancellationToken.None);

        Assert.Equal(first.Review.Decision, second.Review.Decision);
        Assert.Equal(first.ReviewedSignal.ReviewStatus, second.ReviewedSignal.ReviewStatus);
        Assert.Equal(first.ReviewedSignal.Confidence, second.ReviewedSignal.Confidence);
    }

    [Fact]
    public async Task NullSignal_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreateSut().ReviewAsync(null!, MakeEvidence(), CancellationToken.None));
    }

    [Fact]
    public async Task NullEvidence_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreateSut().ReviewAsync(MakeSignal(companyId: CompanyId), null!, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateSut().ReviewAsync(MakeSignal(companyId: CompanyId), MakeEvidence(), cts.Token));
    }
}
