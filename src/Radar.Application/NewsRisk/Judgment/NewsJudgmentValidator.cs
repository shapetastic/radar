using Radar.Application.Filings;
using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>The validated projection of one judge response, with the full drop accounting the spec requires.</summary>
public sealed record NewsJudgmentValidationResult(
    NewsJudgmentStatus Status,
    NewsJudgmentTrajectory? BusinessTrajectory,
    int? ChallengeStrength,
    IReadOnlyList<NewsJudgmentValidatedFinding> Findings,
    string? Rationale,
    int FindingsTotal,
    int FindingsAccepted,
    int FindingsDropped,
    IReadOnlyList<string> FindingDropReasons);

/// <summary>
/// Mechanical validation of one judge response (spec 185 §2), pure and deterministic — mirroring
/// <see cref="NewsRiskClaimValidator"/>. Rules:
/// <list type="bullet">
/// <item><c>BusinessTrajectory</c> must parse from the closed vocabulary (the digit-rejecting shared token
/// parse), or the whole response is <see cref="NewsJudgmentStatus.ValidationFailed"/>;</item>
/// <item>per finding: category/severity parse against the REUSED spec-179 vocabularies, confidence in
/// [0,1], at least one cited FactId, and every cited FactId in the supplied set — an invalid finding is
/// dropped with a named reason, never silently;</item>
/// <item><b>the attribution-caveat rule</b>: when EVERY supporting fact's <c>AssertionStatus</c> sits below
/// <c>reported</c> (i.e. alleged, solicited or speculative — confirmed-filing/reported/announced count as
/// at-or-above), a missing/blank <c>AttributionCaveat</c> drops the finding: an alleged/solicited-only
/// finding must say so;</item>
/// <item>the advice-language guard runs on the RATIONALE and on every caveat (Radar-surfaced free text);
/// a violating rationale/caveat is blanked and counted — a blanked caveat that the rule above requires
/// then drops its finding;</item>
/// <item><c>ChallengeStrength</c> must be 0..100 while any finding survives; with zero surviving findings
/// it is normalized to <c>null</c>;</item>
/// <item><b>a response whose findings are ALL invalid is <see cref="NewsJudgmentStatus.ValidationFailed"/></b>
/// — never a no-challenge result (fail closed: an unverifiable warning is not evidence of support); and</item>
/// <item>a validated response with ZERO emitted findings and a parsed trajectory IS the supportive read
/// (<see cref="NewsJudgmentStatus.Judged"/> with no findings) — expressed factually, never as "safe".</item>
/// </list>
/// Family size appears nowhere in this type: findings cite FactIds and validation is MemberCount-blind, so
/// syndication volume cannot multiply findings.
/// </summary>
public static class NewsJudgmentValidator
{
    public static NewsJudgmentValidationResult Validate(
        NewsJudgmentModelResponse response, IReadOnlyList<NewsJudgmentInputFamily> suppliedFamilies)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(suppliedFamilies);

        var assertionByFactId = suppliedFamilies.ToDictionary(
            f => f.RepresentativeFactId, f => f.AssertionStatus);
        var dropReasons = new List<string>();

        // Rationale: trimmed and scrubbed through the ONE shared advice-language guard.
        var rationale = response.Rationale?.Trim();
        if (!string.IsNullOrEmpty(rationale) && AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            dropReasons.Add("rationale-advice-language: rationale contained advice language and was dropped");
            rationale = null;
        }

        var rawFindings = response.Findings ?? [];
        if (!NewsTypingTokens.TryParse<NewsJudgmentTrajectory>(response.BusinessTrajectory, out var trajectory))
        {
            dropReasons.Add(
                $"trajectory-token-invalid: '{response.BusinessTrajectory}' is not a defined trajectory");
            return Failed(rawFindings.Count, rationale, dropReasons);
        }

        var accepted = new List<NewsJudgmentValidatedFinding>();
        for (var i = 0; i < rawFindings.Count; i++)
        {
            var finding = rawFindings[i];
            if (finding is null)
            {
                dropReasons.Add($"finding[{i}] null-finding");
                continue;
            }

            if (!NewsTypingTokens.TryParse<NewsRiskCategory>(finding.Category, out var category))
            {
                dropReasons.Add($"finding[{i}] category-invalid: '{finding.Category}'");
                continue;
            }

            if (!NewsTypingTokens.TryParse<NewsRiskSeverity>(finding.Severity, out var severity))
            {
                dropReasons.Add($"finding[{i}] severity-invalid: '{finding.Severity}'");
                continue;
            }

            if (finding.Confidence is not { } confidence || confidence is < 0.0 or > 1.0
                || double.IsNaN(confidence))
            {
                dropReasons.Add($"finding[{i}] confidence-out-of-range: '{finding.Confidence}'");
                continue;
            }

            var citedIds = new List<Guid>();
            var citedOk = true;
            foreach (var rawId in finding.FactIds ?? [])
            {
                if (!Guid.TryParse(rawId, out var id) || !assertionByFactId.ContainsKey(id))
                {
                    dropReasons.Add($"finding[{i}] cited-fact-not-supplied: '{rawId}'");
                    citedOk = false;
                    break;
                }

                citedIds.Add(id);
            }

            if (!citedOk)
            {
                continue;
            }

            if (citedIds.Count == 0)
            {
                dropReasons.Add($"finding[{i}] no-cited-fact");
                continue;
            }

            var caveat = finding.AttributionCaveat?.Trim();
            if (!string.IsNullOrEmpty(caveat) && AdviceLanguageGuard.ContainsAdviceLanguage(caveat))
            {
                dropReasons.Add(
                    $"finding[{i}] caveat-advice-language: attribution caveat contained advice language "
                        + "and was dropped");
                caveat = null;
            }

            // The attribution rule (spec 185 §2): every supporting fact below `reported` assertion status
            // ⇒ the finding must SAY so. Dropping (not inventing a caveat) is the fail-closed choice.
            var allBelowReported = citedIds.All(id => IsBelowReported(assertionByFactId[id]));
            if (allBelowReported && string.IsNullOrEmpty(caveat))
            {
                dropReasons.Add(
                    $"finding[{i}] missing-attribution-caveat: every supporting fact is below 'reported' "
                        + "assertion status (alleged/solicited/speculative) and no caveat was given");
                continue;
            }

            accepted.Add(new NewsJudgmentValidatedFinding(
                category, severity, confidence, citedIds, string.IsNullOrEmpty(caveat) ? null : caveat));
        }

        var total = rawFindings.Count;
        var dropped = total - accepted.Count;

        if (total > 0 && accepted.Count == 0)
        {
            // Every finding failed: fail closed. NEVER the no-challenge/supportive read.
            return Failed(total, rationale, dropReasons);
        }

        int? strength;
        if (accepted.Count > 0)
        {
            if (response.ChallengeStrength is not { } s || s is < 0 or > 100)
            {
                dropReasons.Add(
                    $"challenge-strength-out-of-range: '{response.ChallengeStrength}' with "
                        + $"{accepted.Count} surviving finding(s)");
                return Failed(total, rationale, dropReasons, accepted.Count);
            }

            strength = s;
        }
        else
        {
            // Zero findings survive ⇒ strength is normalized to null (spec 185 §2), whatever the model sent.
            strength = null;
        }

        return new NewsJudgmentValidationResult(
            NewsJudgmentStatus.Judged,
            BusinessTrajectory: trajectory,
            ChallengeStrength: strength,
            Findings: accepted,
            Rationale: rationale,
            FindingsTotal: total,
            FindingsAccepted: accepted.Count,
            FindingsDropped: dropped,
            FindingDropReasons: dropReasons);
    }

    /// <summary>
    /// The at-or-above-<c>reported</c> boundary (spec 185 §2): confirmed-filing, reported and announced
    /// count as at-or-above; alleged, solicited and speculative sit below it.
    /// </summary>
    public static bool IsBelowReported(NewsFactAssertionStatus status) => status
        is NewsFactAssertionStatus.Alleged
        or NewsFactAssertionStatus.Solicited
        or NewsFactAssertionStatus.Speculative;

    private static NewsJudgmentValidationResult Failed(
        int total, string? rationale, List<string> dropReasons, int accepted = 0) =>
        new(
            NewsJudgmentStatus.ValidationFailed,
            BusinessTrajectory: null,
            ChallengeStrength: null,
            Findings: [],
            Rationale: rationale,
            FindingsTotal: total,
            FindingsAccepted: accepted,
            FindingsDropped: total - accepted,
            FindingDropReasons: dropReasons);
}
