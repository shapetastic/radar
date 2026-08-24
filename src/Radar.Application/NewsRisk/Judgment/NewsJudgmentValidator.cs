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
    IReadOnlyList<string> FindingDropReasons,
    // Spec 187 §1: the validated supplied FactIds that ESTABLISH the trajectory. Always non-null on a
    // Judged result (empty iff Unknown); empty on a failure, where no claim survived to be evidenced.
    IReadOnlyList<Guid> TrajectoryFactIds);

/// <summary>
/// Mechanical validation of one judge response (spec 185 §2, made STRICT by spec 187 §1), pure and
/// deterministic — mirroring <see cref="NewsRiskClaimValidator"/>. Rules:
/// <list type="bullet">
/// <item><c>BusinessTrajectory</c> must parse from the closed vocabulary (the digit-rejecting shared token
/// parse), or the whole response is <see cref="NewsJudgmentStatus.ValidationFailed"/>;</item>
/// <item><b>spec 187 §1 — the trajectory must CITE what establishes it</b>: every
/// <c>TrajectoryFactIds</c> entry must parse, be distinct and be a SUPPLIED representative fact;
/// <c>Improving</c>/<c>Deteriorating</c>/<c>Mixed</c> require at least one; <c>Unknown</c> requires NONE
/// (it means no supplied fact established a directional balance, not that provenance was omitted); at
/// least one cited fact must be at-or-above <c>reported</c> assertion status
/// (<see cref="IsBelowReported"/>, the SAME boundary the caveat rule uses); and the cited set may not be
/// made ENTIRELY of families confined to <see cref="NewsJudgmentContextOnlyEventTypes"/>;</item>
/// <item><b>spec 187 §1 — a <c>Judged</c> response requires a non-blank factual rationale of at most
/// <see cref="MaxRationaleLength"/> characters</b>. Missing, over-length, or scrubbed-to-empty by the
/// advice guard all FAIL the whole response — never a clean-looking zero-finding judgment;</item>
/// <item>per finding: category/severity parse against the REUSED spec-179 vocabularies, confidence in
/// [0,1], at least one cited FactId, and every cited FactId in the supplied set — an invalid finding is
/// dropped with a named reason, never silently;</item>
/// <item><b>the attribution-caveat rule</b>: when EVERY supporting fact's <c>AssertionStatus</c> sits below
/// <c>reported</c> (i.e. alleged, solicited or speculative — confirmed-filing/reported/announced count as
/// at-or-above), a missing/blank <c>AttributionCaveat</c> drops the finding: an alleged/solicited-only
/// finding must say so;</item>
/// <item><b>spec 187 §1 — <c>non-business-context-only</c></b>: a finding whose cited evidence is confined
/// to the context-only event types is dropped INDIVIDUALLY (a share-price fall is not an
/// <c>ExecutionOrMissedMilestone</c>). The "ALL findings invalid ⇒ ValidationFailed" rule then applies on
/// top, exactly as before;</item>
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
/// <para>
/// <b>Assertion status and event types are read from the REPRESENTATIVE fact actually supplied to the
/// judge</b> (<see cref="NewsJudgmentInputFamily"/>), never from an unprovided family member. That is
/// conservative by design: an unseen member cannot silently upgrade a <c>Speculative</c> representative to
/// <c>Reported</c>, and if the stronger member should govern then the family PROJECTION must select and
/// expose it rather than asking this validator to reason over evidence the judge never saw.
/// </para>
/// <para>
/// <b>There is deliberately NO phrase/keyword scanner over rationale prose.</b> This boundary verifies
/// PROVENANCE (which supplied facts are cited), EVIDENCE CLASS (context-only vs business) and STRENGTH OF
/// ASSERTION only. It cannot prove that a model weighed a revenue decline correctly, and a brittle keyword
/// rule pretending otherwise would be worse: bad semantic calls stay possible and VISIBLE in the persisted
/// rationale and the live artifact, which is where they can actually be judged.
/// </para>
/// Family size appears nowhere in this type: findings cite FactIds and validation is MemberCount-blind, so
/// syndication volume cannot multiply findings.
/// </summary>
public static class NewsJudgmentValidator
{
    /// <summary>
    /// The bound on a <c>Judged</c> response's rationale (spec 187 §1). It is a short factual explanation
    /// carried into Radar-surfaced artifacts, not an essay; an over-length one FAILS the response rather
    /// than being truncated, because truncating would silently change what the judge said.
    /// </summary>
    public const int MaxRationaleLength = 1_000;

    /// <summary>
    /// The named drop reason for a finding standing entirely on context-only evidence (spec 187 §1). A
    /// FINDING-level drop, not a whole-response failure — the "all findings invalid" rule applies on top.
    /// </summary>
    public const string NonBusinessContextOnlyReason = "non-business-context-only";

    /// <summary>
    /// The named failure reason for a non-<c>Unknown</c> trajectory whose cited evidence is entirely
    /// alleged/solicited/speculative (spec 187 §1). Weak evidence may still carry a CAVEATED challenge
    /// finding; it cannot alone establish the company's overall business direction.
    /// </summary>
    public const string TrajectoryAssertionTooWeakReason = "trajectory-assertion-too-weak";

    /// <summary>The context-only member list, rendered once for the drop-reason text (AD-3: declaration order).</summary>
    private static readonly string ContextOnlyTypeList =
        string.Join(", ", NewsJudgmentContextOnlyEventTypes.Members);

    public static NewsJudgmentValidationResult Validate(
        NewsJudgmentModelResponse response, IReadOnlyList<NewsJudgmentInputFamily> suppliedFamilies)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(suppliedFamilies);

        var familyByFactId = suppliedFamilies.ToDictionary(f => f.RepresentativeFactId);
        var dropReasons = new List<string>();
        var rawFindings = response.Findings ?? [];

        // Rationale: trimmed and scrubbed through the ONE shared advice-language guard.
        var rationale = response.Rationale?.Trim();
        if (rationale is { Length: > MaxRationaleLength })
        {
            dropReasons.Add(
                $"rationale-too-long: {rationale.Length} characters exceeds the {MaxRationaleLength}-"
                    + "character bound on a factual judgment rationale");
            return Failed(rawFindings.Count, rationale: null, dropReasons);
        }

        if (!string.IsNullOrEmpty(rationale) && AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            dropReasons.Add("rationale-advice-language: rationale contained advice language and was dropped");
            rationale = null;
        }

        if (string.IsNullOrEmpty(rationale))
        {
            // Spec 187 §1: a judgment Radar cannot explain is not a judgment. A missing (or
            // advice-scrubbed) rationale fails the WHOLE response rather than rendering a clean-looking
            // zero-finding read with nothing behind it.
            dropReasons.Add("rationale-missing: a judged response requires a non-blank factual rationale");
            return Failed(rawFindings.Count, rationale: null, dropReasons);
        }

        if (!NewsTypingTokens.TryParse<NewsJudgmentTrajectory>(response.BusinessTrajectory, out var trajectory))
        {
            dropReasons.Add(
                $"trajectory-token-invalid: '{response.BusinessTrajectory}' is not a defined trajectory");
            return Failed(rawFindings.Count, rationale, dropReasons);
        }

        if (!TryValidateTrajectoryEvidence(
            response.TrajectoryFactIds, trajectory, familyByFactId, dropReasons, out var trajectoryFactIds))
        {
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
                if (!Guid.TryParse(rawId, out var id) || !familyByFactId.ContainsKey(id))
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

            // Spec 187 §1: a finding standing ENTIRELY on other people's views, price/trading behaviour or
            // content mechanics is dropped individually — the YORW shape, where a 52-week share-price low
            // became a high-confidence business-execution finding. If a supplied business fact sits behind
            // the reaction, the finding must cite THAT.
            if (citedIds.All(id => NewsJudgmentContextOnlyEventTypes.IsConfinedTo(
                familyByFactId[id].EventTypes)))
            {
                dropReasons.Add(
                    $"finding[{i}] {NonBusinessContextOnlyReason}: every cited fact is confined to "
                        + $"context-only event types ({ContextOnlyTypeList}), which describe views, price "
                        + "or content mechanics rather than the business");
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
            var allBelowReported = citedIds.All(
                id => IsBelowReported(familyByFactId[id].AssertionStatus));
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
                // The whole response fails, so the individually-accepted findings are discarded with it:
                // FindingsAccepted must equal Findings.Count (0), never a pre-failure count.
                return Failed(total, rationale, dropReasons);
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
            FindingDropReasons: dropReasons,
            TrajectoryFactIds: trajectoryFactIds);
    }

    /// <summary>
    /// The at-or-above-<c>reported</c> boundary (spec 185 §2): confirmed-filing, reported and announced
    /// count as at-or-above; alleged, solicited and speculative sit below it.
    /// </summary>
    public static bool IsBelowReported(NewsFactAssertionStatus status) => status
        is NewsFactAssertionStatus.Alleged
        or NewsFactAssertionStatus.Solicited
        or NewsFactAssertionStatus.Speculative;

    /// <summary>
    /// Spec 187 §1's trajectory-provenance gate, in ONE place: parse + distinctness + supplied-set
    /// membership, the presence/absence rule against the trajectory value, the assertion-strength rule and
    /// the context-only rule. Returns <c>false</c> with a NAMED reason already appended whenever the whole
    /// response must fail.
    /// </summary>
    private static bool TryValidateTrajectoryEvidence(
        IReadOnlyList<string>? rawTrajectoryFactIds,
        NewsJudgmentTrajectory trajectory,
        IReadOnlyDictionary<Guid, NewsJudgmentInputFamily> familyByFactId,
        List<string> dropReasons,
        out IReadOnlyList<Guid> trajectoryFactIds)
    {
        trajectoryFactIds = [];

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var rawId in rawTrajectoryFactIds ?? [])
        {
            // Guid.TryParse IS the ordinal-preserving normalization: two spellings of one id canonicalise
            // onto the same value, so "distinct" cannot be defeated by casing or brace/hyphen formatting.
            if (!Guid.TryParse(rawId, out var id) || !familyByFactId.ContainsKey(id))
            {
                dropReasons.Add(
                    $"trajectory-fact-not-supplied: '{rawId}' is not a supplied representative fact id");
                return false;
            }

            if (!seen.Add(id))
            {
                dropReasons.Add($"trajectory-fact-duplicate: '{rawId}' was cited more than once");
                return false;
            }

            ids.Add(id);
        }

        if (trajectory == NewsJudgmentTrajectory.Unknown)
        {
            if (ids.Count > 0)
            {
                // Unknown means "the supplied facts did not establish a directional balance". Citing
                // evidence FOR that non-claim is a contradiction, not extra provenance.
                dropReasons.Add(
                    $"trajectory-evidence-with-unknown: {ids.Count} fact(s) were cited as establishing an "
                        + "Unknown trajectory, which by definition establishes no direction");
                return false;
            }

            trajectoryFactIds = ids;
            return true;
        }

        if (ids.Count == 0)
        {
            // The MNRO shape: a directional label with no identified evidence behind it. Under v2 the
            // honest answer when nothing establishes direction is Unknown, not a manufactured call.
            dropReasons.Add(
                $"trajectory-evidence-missing: a {trajectory} trajectory must cite the supplied fact(s) "
                    + "that establish it (cite none only for an Unknown trajectory)");
            return false;
        }

        if (ids.All(id => IsBelowReported(familyByFactId[id].AssertionStatus)))
        {
            // The EOSE shape: a plaintiff-firm solicitation may carry a CAVEATED challenge finding, but it
            // cannot by itself establish the company's overall business direction.
            dropReasons.Add(
                $"{TrajectoryAssertionTooWeakReason}: every cited trajectory fact is below 'reported' "
                    + "assertion status (alleged/solicited/speculative), which cannot alone establish the "
                    + "overall business direction");
            return false;
        }

        if (ids.All(id => NewsJudgmentContextOnlyEventTypes.IsConfinedTo(familyByFactId[id].EventTypes)))
        {
            // The YORW shape: a share-price move or an analyst action is not a business trajectory.
            dropReasons.Add(
                $"trajectory-{NonBusinessContextOnlyReason}: every cited trajectory fact is confined to "
                    + $"context-only event types ({ContextOnlyTypeList}), which describe views, price or "
                    + "content mechanics rather than the business");
            return false;
        }

        trajectoryFactIds = ids;
        return true;
    }

    private static NewsJudgmentValidationResult Failed(
        int total, string? rationale, List<string> dropReasons) =>
        new(
            NewsJudgmentStatus.ValidationFailed,
            BusinessTrajectory: null,
            ChallengeStrength: null,
            Findings: [],
            Rationale: rationale,
            FindingsTotal: total,
            FindingsAccepted: 0,
            FindingsDropped: total,
            FindingDropReasons: dropReasons,
            TrajectoryFactIds: []);
}
