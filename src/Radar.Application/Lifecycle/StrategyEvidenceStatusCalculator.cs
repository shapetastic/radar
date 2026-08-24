using Radar.Application.Efficacy.Claims;
using Radar.Application.Scoring;

namespace Radar.Application.Lifecycle;

/// <summary>
/// The pure, deterministic mapping from persisted efficacy FACTS onto each strategy's
/// <see cref="StrategyEvidenceStatus"/> (spec 184 §1). No clock, no I/O, no randomness (AD-3).
/// <para>
/// Rules, in precedence order per strategy:
/// <list type="number">
/// <item>Gate statuses apply ONLY to the arm the paired artifact judges — the PREDECLARED paired primary
/// with a precommitted boundary. Within that arm the decision comes from the STRUCTURED verdict whenever
/// the artifact states one (spec 187 §5): a non-empty <c>GateVerdictId</c> IS the writer's statement that a
/// verdict exists (<c>GateVerdictIdentity.VerdictExists</c> already applied the merit/non-merit split over
/// the STRUCTURED reasons), so <c>Qualifies</c> alone selects
/// <see cref="StrategyEvidenceStatusKind.GatePassed"/> / <see cref="StrategyEvidenceStatusKind.GateFailed"/>
/// and the rendered <c>GateReasons</c> are DISPLAY DETAIL ONLY. A pre-186 artifact carrying no id falls back
/// to the isolated legacy path (<see cref="LegacyGateStatusFromRenderedReasons"/>), which fails CLOSED:
/// every parsed reason CODE being a MERIT code (<c>median-paired-delta-not-positive</c> /
/// <c>interval-lower-bound-not-positive</c>) ⇒ <c>GateFailed</c>; any accrual/prerequisite code — or a
/// blank/unparseable list — ⇒ <see cref="StrategyEvidenceStatusKind.GatePending"/>. Noise is never
/// converted into pass/fail ahead of the precommitted gate: "not enough data yet" is pending, not failed.</item>
/// <item>Otherwise the leaderboard: a ranked row ⇒ <c>Ranked</c> WITH its numbers; a dropped/absent row ⇒
/// <c>Accruing</c> with the drop reason as detail.</item>
/// <item>No readable artifact at all ⇒ <c>Accruing (evidence unavailable)</c> — the display degrades, the
/// arm is never hidden.</item>
/// </list>
/// Descriptive and confirmatory facts stay orthogonal: a gate status carries the leaderboard numbers
/// BESIDE it when they exist, and a CI spanning zero renders as a sentence, never a verdict.
/// </para>
/// </summary>
public static class StrategyEvidenceStatusCalculator
{
    /// <summary>
    /// The two closed spec-170 codes that mean the price gate evaluated ON ITS MERITS and came out
    /// negative. Every other code means the gate could not (yet) evaluate — accrual, missing
    /// predeclaration, or the AD-16 prerequisite — which is pending, never failed. The split itself lives
    /// in <see cref="Ad15GateReasonCodes"/> (spec 186 §3) so this calculator and the writer-side
    /// <c>GateVerdictIdentity</c> cannot disagree about when a VERDICT exists.
    /// <para>
    /// Since spec 187 §5 they are consulted ONLY on the legacy (no-id) path: a current artifact states its
    /// verdict structurally, and re-deriving that decision from rendered prose is exactly what let a
    /// crafted baseline NAME change the answer.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> MeritFailureCodes => Ad15GateReasonCodes.MeritFailureCodes;

    private static IReadOnlyList<string> NonMeritCodes => Ad15GateReasonCodes.NonMeritCodes;

    /// <summary>The separator the paired artifact joins rendered gate reasons with.</summary>
    private const string RenderedReasonSeparator = "; ";

    /// <summary>Computes every configured strategy's status. Keys are case-insensitive strategy names.</summary>
    public static IReadOnlyDictionary<string, StrategyEvidenceStatus> Compute(
        EfficacyEvidenceFacts facts, IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(strategies);

        var byName = new Dictionary<string, StrategyEvidenceStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies)
        {
            byName[strategy.Name] = ComputeOne(facts, strategy.Name);
        }

        return byName;
    }

    /// <summary>
    /// The PERSISTED gate verdicts the operating-call reducer consumes (spec 184 §2 rule 1): one per arm
    /// whose status is <c>GatePassed</c>/<c>GateFailed</c>, carrying the artifact's SEMANTIC verdict
    /// identity (spec 186 §3) — never its filesystem write instant. A pending gate is deliberately NOT a
    /// verdict: it must not demote or promote anything.
    /// <para>
    /// Since spec 187 §5 the id and the status cannot disagree BY CONSTRUCTION: for a current artifact the
    /// non-empty id is what selects passed/failed in the first place, so the id carried here is exactly the
    /// one the decision was taken from. A legacy (no-id) artifact still yields the EMPTY id, which no
    /// override can match.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StrategyGateVerdict> GateVerdicts(
        EfficacyEvidenceFacts facts, IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(strategies);

        var verdicts = new List<StrategyGateVerdict>(1);
        foreach (var strategy in strategies)
        {
            var status = ComputeOne(facts, strategy.Name);
            if (status.Kind == StrategyEvidenceStatusKind.GatePassed)
            {
                verdicts.Add(new StrategyGateVerdict(
                    strategy.Name, Passed: true, facts.Paired!.GateVerdictId));
            }
            else if (status.Kind == StrategyEvidenceStatusKind.GateFailed)
            {
                verdicts.Add(new StrategyGateVerdict(
                    strategy.Name, Passed: false, facts.Paired!.GateVerdictId));
            }
        }

        return verdicts;
    }

    private static StrategyEvidenceStatus ComputeOne(EfficacyEvidenceFacts facts, string strategyName)
    {
        var ranked = FindRanked(facts, strategyName, out var dropReason);

        // Gate statuses only for the arm actually under the precommitted confirmatory test.
        if (facts.PairedAvailable
            && facts.Paired is { } paired
            && paired.PrimaryPredeclared
            && paired.BoundaryDeclared
            && string.Equals(paired.PrimaryStrategyName, strategyName, StringComparison.OrdinalIgnoreCase))
        {
            // Spec 187 §5: a non-empty verdict id IS the structured statement that a verdict exists, and
            // Qualifies IS its outcome. Do not reconstruct that decision from the human-readable reasons
            // beside it — a baseline NAME containing a reason-code token was enough to make this status
            // disagree with the very id GateVerdicts(...) then carried for the same artifact.
            // Blank (or whitespace) is "no identity available" and falls through to the legacy path, which
            // fails closed: an unusable id must never be treated as a verdict statement.
            if (!string.IsNullOrWhiteSpace(paired.GateVerdictId))
            {
                return paired.Qualifies
                    ? StrategyEvidenceStatus.GatePassed(ranked, detail: null)
                    : StrategyEvidenceStatus.GateFailed(
                        ranked, paired.GateReasons.Length > 0 ? paired.GateReasons : null);
            }

            return LegacyGateStatusFromRenderedReasons(paired, ranked);
        }

        if (!facts.LeaderboardAvailable)
        {
            return StrategyEvidenceStatus.AccruingEvidenceUnavailable();
        }

        if (ranked is not null)
        {
            return StrategyEvidenceStatus.RankedStatus(ranked);
        }

        return StrategyEvidenceStatus.Accruing(dropReason);
    }

    /// <summary>
    /// THE LEGACY (pre-186) COMPATIBILITY PATH — and nothing else reaches it. An artifact written before
    /// spec 186 carries no <c>gateVerdictId</c> column, so the only machine-readable statement it makes
    /// about the gate is <c>Qualifies</c> plus the RENDERED reason text. It fails CLOSED: anything it
    /// cannot parse as an unambiguous merit-only failure stays PENDING, never failed.
    /// <para>
    /// It parses EXACT reason-code tokens rather than searching for substrings (spec 187 §5) — see
    /// <see cref="ParseRenderedReasonCodes"/>. The pre-186 precedence is otherwise preserved verbatim:
    /// <c>Qualifies</c> ⇒ passed; else merit-only ⇒ failed; any non-merit code, an unrecognised segment or
    /// a blank list ⇒ pending.
    /// </para>
    /// <para>
    /// No id is ever fabricated here (AD-8: "cannot tell" must never become a claim), so a verdict emitted
    /// off this path carries the artifact's EMPTY id and can never match an operating-call override — the
    /// gate default wins, visibly.
    /// </para>
    /// </summary>
    private static StrategyEvidenceStatus LegacyGateStatusFromRenderedReasons(
        PairedGateFact paired, RankedEvidence? ranked)
    {
        if (paired.Qualifies)
        {
            return StrategyEvidenceStatus.GatePassed(ranked, detail: null);
        }

        var reasons = paired.GateReasons;
        var codes = ParseRenderedReasonCodes(reasons);
        var hasMerit = codes.Any(c => MeritFailureCodes.Contains(c, StringComparer.Ordinal));
        var hasNonMerit = codes.Any(c => NonMeritCodes.Contains(c, StringComparer.Ordinal));

        // Failed ONLY when the gate evaluated everywhere and every stated reason is a merit reason.
        // A blank reason list beside a non-qualifying verdict is inconsistent input — pending, never
        // failed (absence of an explanation must not read as a negative result).
        return hasMerit && !hasNonMerit
            ? StrategyEvidenceStatus.GateFailed(ranked, reasons)
            : StrategyEvidenceStatus.GatePending(ranked, reasons.Length > 0 ? reasons : null);
    }

    /// <summary>
    /// Splits rendered gate-reason text back into its CODE tokens, inverting <c>Ad15GateReason.Render()</c>
    /// exactly. The rendered form is the reasons joined by <c>"; "</c>, each segment being one of
    /// <c>code</c>, <c>code (detail)</c>, <c>baseline 'x': code</c> or <c>baseline 'x': code (detail)</c>,
    /// so each segment has its optional <c>baseline '…': </c> prefix and its optional trailing
    /// <c>(…)</c> detail stripped before the REMAINDER is compared ORDINALLY against the closed
    /// vocabulary.
    /// <para>
    /// Substring matching was the defect: a baseline literally named <c>no-eligible-blocks-baseline</c>, or
    /// a free-form detail quoting a code, decided the verdict. A segment whose remainder is not in the
    /// vocabulary contributes NOTHING — it is neither merit nor non-merit, so it can only ever hold the
    /// status at pending (fail closed).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ParseRenderedReasonCodes(string reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
        {
            return [];
        }

        var codes = new List<string>();
        foreach (var segment in reasons.Split(RenderedReasonSeparator, StringSplitOptions.None))
        {
            var candidate = segment.Trim();

            // "baseline '<name>': <rest>" — a baseline name may itself contain "': ", so take the LAST
            // occurrence of the closing delimiter, which is what Render() emits directly before the code.
            if (candidate.StartsWith("baseline '", StringComparison.Ordinal))
            {
                var close = candidate.LastIndexOf("': ", StringComparison.Ordinal);
                if (close < 0)
                {
                    continue;
                }

                candidate = candidate[(close + 3)..];
            }

            // "<code> (<detail>)" — the detail is free-form, so strip the LAST parenthesised suffix.
            if (candidate.EndsWith(')'))
            {
                var open = candidate.LastIndexOf(" (", StringComparison.Ordinal);
                if (open >= 0)
                {
                    candidate = candidate[..open];
                }
            }

            candidate = candidate.Trim();
            if (Ad15GateReasonCodes.All.Contains(candidate, StringComparer.Ordinal))
            {
                codes.Add(candidate);
            }
        }

        return codes;
    }

    private static RankedEvidence? FindRanked(
        EfficacyEvidenceFacts facts, string strategyName, out string? dropReason)
    {
        dropReason = null;
        if (!facts.LeaderboardAvailable)
        {
            return null;
        }

        foreach (var row in facts.Leaderboard)
        {
            if (!string.Equals(row.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.Ranked && row.Numbers is not null)
            {
                return row.Numbers;
            }

            dropReason = row.DropReason;
            return null;
        }

        return null;
    }
}

/// <summary>
/// A PERSISTED gate verdict for one arm (spec 184 §2 reducer rule 1): the AD-15 composite gate evaluated
/// and the outcome is on disk.
/// <para>
/// <paramref name="VerdictId"/> is the artifact's SEMANTIC verdict identity (spec 186 §3) — the name a
/// human operating call must bind to (<c>overridesVerdictId</c>) for its override to apply. It replaces the
/// pre-186 <c>VerdictAtUtc</c> write instant outright: an override is about a PARTICULAR verdict, so it
/// binds by identity, never by timestamp. EMPTY means the identity is unavailable (a pre-186 artifact, or
/// one carrying no verdict), and an empty id can never match an override — fail closed.
/// </para>
/// </summary>
public sealed record StrategyGateVerdict(string StrategyName, bool Passed, string VerdictId);
