using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.SignalReview;

/// <summary>
/// Deterministic, rules-based Stage 5 signal reviewer. Applies a small set of conservative
/// guardrail checks to a <see cref="Signal"/> and its source <see cref="EvidenceItem"/>, then
/// produces a versioned <see cref="Radar.Domain.Signals.SignalReview"/> audit record and a reviewed signal with an
/// updated <see cref="SignalReviewStatus"/> (and, where appropriate, a reduced confidence — never an
/// increased one). Pure and offline: no AI, no I/O. The AI-assisted reviewer described in the schema
/// is deliberately deferred to a later, human-owned slice.
/// </summary>
public sealed class DeterministicSignalReviewer : ISignalReviewer
{
    /// <summary>Versioned reviewer identity recorded on every <see cref="Radar.Domain.Signals.SignalReview"/>.</summary>
    private const string ReviewerName = "deterministic-rules-v1";

    /// <summary>Strength below this threshold is treated as immaterial.</summary>
    private const int MinMaterialStrength = 3;

    /// <summary>Novelty below this threshold suggests repeated/recycled PR.</summary>
    private const int MinNovelty = 3;

    /// <summary>Below this confidence the signal reads as hype rather than evidence.</summary>
    private const decimal MinConfidence = 0.40m;

    /// <summary>Multiplier applied to confidence when a <see cref="SignalReviewDecision.ReduceConfidence"/> decision fires.</summary>
    private const decimal ConfidenceReductionFactor = 0.5m;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeterministicSignalReviewer> _logger;

    public DeterministicSignalReviewer(TimeProvider timeProvider, ILogger<DeterministicSignalReviewer> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<SignalReviewOutcome> ReviewAsync(Signal signal, EvidenceItem evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(evidence);

        var issues = new List<string>();

        // The caller is responsible for passing the matching evidence; record an issue rather than throw.
        var evidenceMismatch = evidence.Id != signal.EvidenceId;
        if (evidenceMismatch)
        {
            issues.Add("Evidence does not match signal.EvidenceId");
        }

        // Company match reliability.
        var unresolvedCompany = signal.CompanyId is null;
        if (unresolvedCompany)
        {
            issues.Add("Unresolved company mention");
        }

        // Materiality.
        var immaterialStrength = signal.Strength < MinMaterialStrength;
        if (immaterialStrength)
        {
            issues.Add("Strength below materiality threshold");
        }

        // Repeated PR / novelty.
        var lowNovelty = signal.Novelty < MinNovelty;
        if (lowNovelty)
        {
            issues.Add("Low novelty (possible repeated PR)");
        }

        // Hype vs evidence (confidence).
        var lowConfidence = signal.Confidence < MinConfidence;
        if (lowConfidence)
        {
            issues.Add("Confidence below evidence threshold");
        }

        // Weak source.
        var weakSource = evidence.Quality is EvidenceQuality.Unknown or EvidenceQuality.Low;
        if (weakSource)
        {
            issues.Add("Weak or unknown source quality");
        }

        // Decide a single decision by precedence (first match wins).
        // Note: Reject is reserved for hard contract violations; the task-09 mapper already rejects
        // malformed signals before they reach review, so the deterministic reviewer never emits Reject.
        SignalReviewDecision decision;
        if (unresolvedCompany || evidenceMismatch)
        {
            decision = SignalReviewDecision.EscalateToHuman;
        }
        else if (immaterialStrength)
        {
            decision = SignalReviewDecision.NeedsMoreEvidence;
        }
        else if (lowNovelty || lowConfidence || weakSource)
        {
            decision = SignalReviewDecision.ReduceConfidence;
        }
        else
        {
            decision = SignalReviewDecision.Approve;
        }

        var (newStatus, adjustedConfidence) = decision switch
        {
            SignalReviewDecision.Approve =>
                (SignalReviewStatus.Approved, signal.Confidence),
            SignalReviewDecision.ReduceConfidence =>
                (SignalReviewStatus.Approved, ReduceConfidence(signal.Confidence)),
            SignalReviewDecision.NeedsMoreEvidence =>
                (SignalReviewStatus.NeedsHumanReview, signal.Confidence),
            SignalReviewDecision.EscalateToHuman =>
                (SignalReviewStatus.NeedsHumanReview, signal.Confidence),
            _ => (SignalReviewStatus.NeedsHumanReview, signal.Confidence),
        };

        _logger.LogDebug(
            "Reviewed signal {SignalId}: decision {Decision}, confidence reduced {ConfidenceReduced}.",
            signal.Id,
            decision,
            adjustedConfidence < signal.Confidence);

        var reviewedSignal = signal with
        {
            ReviewStatus = newStatus,
            Confidence = adjustedConfidence,
        };

        var review = new Radar.Domain.Signals.SignalReview(
            Id: Guid.NewGuid(),
            SignalId: signal.Id,
            ReviewerName: ReviewerName,
            Decision: decision,
            Summary: issues.Count == 0
                ? "All checks passed."
                : $"{decision}: {issues.Count} issue(s).",
            IssuesJson: JsonSerializer.Serialize(issues),
            ReviewedAtUtc: _timeProvider.GetUtcNow());

        return Task.FromResult(new SignalReviewOutcome(reviewedSignal, review));
    }

    /// <summary>
    /// Applies the reduction factor, clamps to [0,1], and guarantees the result is never higher than
    /// the original confidence (provenance rule: a reviewer may lower but never raise confidence).
    /// </summary>
    private static decimal ReduceConfidence(decimal confidence)
    {
        var reduced = Math.Clamp(confidence * ConfidenceReductionFactor, 0m, 1m);
        return Math.Min(reduced, confidence);
    }
}
