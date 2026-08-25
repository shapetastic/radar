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
/// and the rendered <c>GateReasons</c> are DISPLAY DETAIL ONLY. An artifact carrying NO id falls back to the
/// isolated no-verdict-identity path (<see cref="NoVerdictIdStatusFromRenderedReasons"/>). That fallback is
/// LIVE, not merely historical: it serves pre-186 artifacts, whose column did not exist, AND every current
/// artifact whose writer correctly left the id empty because the gate has reached no verdict yet — the
/// state of every row in today's paired-comparison artifact. It fails CLOSED (spec 188 §2): the list must
/// be nonempty, EVERY rendered segment must parse as a member of the closed vocabulary, and every parsed
/// code must be a MERIT code (<c>median-paired-delta-not-positive</c> /
/// <c>interval-lower-bound-not-positive</c>) before it may read <c>GateFailed</c>. An empty or blank list, a
/// malformed segment, an unrecognised/future code, prose, any accrual/prerequisite code, or any mixture
/// ⇒ <see cref="StrategyEvidenceStatusKind.GatePending"/>. Noise is never converted into pass/fail ahead
/// of the precommitted gate: "not enough data yet" — and "not fully understood" — is pending, not
/// failed.</item>
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
    /// Since spec 187 §5 they are consulted ONLY on the no-verdict-id path: an artifact that STATES a
    /// verdict states it structurally, and re-deriving that decision from rendered prose is exactly what let
    /// a crafted baseline NAME change the answer.
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
    /// one the decision was taken from. A no-id artifact — pre-186, or current with no verdict yet —
    /// still yields the EMPTY id, which no override can match.
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
            // Blank (or whitespace) is "no identity available" and falls through to the no-verdict-id
            // path, which fails closed: an unusable id must never be treated as a verdict statement.
            if (!string.IsNullOrWhiteSpace(paired.GateVerdictId))
            {
                return paired.Qualifies
                    ? StrategyEvidenceStatus.GatePassed(ranked, detail: null)
                    : StrategyEvidenceStatus.GateFailed(
                        ranked, paired.GateReasons.Length > 0 ? paired.GateReasons : null);
            }

            return NoVerdictIdStatusFromRenderedReasons(paired, ranked);
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
    /// THE NO-VERDICT-IDENTITY PATH. It is reached whenever the artifact carries no <c>gateVerdictId</c>,
    /// which happens in TWO live situations rather than one: a pre-186 artifact, whose column did not exist,
    /// and a CURRENT artifact whose writer correctly left the id empty because the gate has reached no
    /// verdict — the state of every row in today's paired-comparison artifact. It is not historical-only.
    /// <para>
    /// With no structured verdict identity available, the only machine-readable statement the artifact makes
    /// about the gate is <c>Qualifies</c> plus the RENDERED reason text, so it fails CLOSED:
    /// <c>GateFailed</c> requires a nonblank list, at least one segment, EVERY segment parsed and recognised
    /// against the closed vocabulary, and every parsed code being a merit code. Anything else — an empty
    /// list, a blank segment, malformed baseline syntax, an unrecognised/future code, prose, any
    /// accrual/prerequisite code, or any mixture of those with a merit code — is <c>GatePending</c>.
    /// </para>
    /// <para>
    /// <b>Spec 188 §2 — why COMPLETE parsing, not merely EXACT parsing.</b> The pre-188 parser discarded
    /// an unrecognised segment silently, so "one recognised merit failure plus one segment we do not
    /// understand" collapsed to merit-only and became a negative VERDICT. Partly understood must never be a
    /// verdict: a future reason code read by an older vocabulary, a manually edited artifact and a malformed
    /// render all now hold at pending instead. A well-formed CURRENT artifact is unaffected either way —
    /// once its gate reaches a merit verdict it carries an id and never reaches here.
    /// </para>
    /// <para>
    /// No id is ever fabricated here (AD-8: "cannot tell" must never become a claim), so a verdict emitted
    /// off this path carries the artifact's EMPTY id and can never match an operating-call override — the
    /// gate default wins, visibly. A pending result contributes no <c>StrategyGateVerdict</c> at all.
    /// </para>
    /// </summary>
    private static StrategyEvidenceStatus NoVerdictIdStatusFromRenderedReasons(
        PairedGateFact paired, RankedEvidence? ranked)
    {
        if (paired.Qualifies)
        {
            return StrategyEvidenceStatus.GatePassed(ranked, detail: null);
        }

        var reasons = paired.GateReasons;
        var parsed = ParseRenderedReasonCodes(reasons);

        // The merit/non-merit test is the writer-side GateVerdictIdentity.VerdictExists test verbatim, so
        // the two sides cannot answer "is this a verdict?" differently.
        var hasMerit = parsed.Codes.Any(c => MeritFailureCodes.Contains(c, StringComparer.Ordinal));
        var hasNonMerit = parsed.Codes.Any(c => NonMeritCodes.Contains(c, StringComparer.Ordinal));

        // Failed ONLY when the WHOLE rendered list was understood AND every stated reason is a merit
        // reason. A blank list, an unparsed segment, or any non-merit code beside a merit one is incomplete
        // or inconsistent input — pending, never failed (neither an absent explanation nor a partly
        // understood one may read as a negative result). EverySegmentRecognised is what makes
        // `!hasNonMerit` mean "no other reason was stated" rather than "no other reason was understood".
        return parsed.EverySegmentRecognised && hasMerit && !hasNonMerit
            ? StrategyEvidenceStatus.GateFailed(ranked, reasons)
            : StrategyEvidenceStatus.GatePending(ranked, reasons.Length > 0 ? reasons : null);
    }

    /// <summary>
    /// The result of reading one rendered gate-reason list (spec 188 §2). It preserves BOTH facts, because
    /// the fallback needs both and the pre-188 <c>IReadOnlyList&lt;string&gt;</c> could only carry one:
    /// <list type="bullet">
    /// <item><see cref="Codes"/> — the exact recognised codes, in rendered order; and</item>
    /// <item><see cref="EverySegmentRecognised"/> — whether EVERY rendered segment was understood. A list
    /// that was only partly understood must never be reduced to the part that was, because that part alone
    /// could then produce a negative verdict.</item>
    /// </list>
    /// A blank list has nothing to understand, so it reports <c>false</c>: fail closed.
    /// </summary>
    private readonly record struct RenderedReasonParse(
        IReadOnlyList<string> Codes, bool EverySegmentRecognised)
    {
        public static readonly RenderedReasonParse Nothing = new([], false);
    }

    /// <summary>
    /// Splits rendered gate-reason text back into its CODE tokens, inverting <c>Ad15GateReason.Render()</c>
    /// exactly. The rendered form is the reasons joined by <c>"; "</c>, each segment being one of
    /// <c>code</c>, <c>code (detail)</c>, <c>baseline 'x': code</c> or <c>baseline 'x': code (detail)</c>,
    /// so each segment has its optional <c>baseline '…': </c> prefix and its optional trailing
    /// <c>(…)</c> detail stripped before the REMAINDER is compared ORDINALLY against the closed
    /// vocabulary.
    /// <para>
    /// Substring matching was the first defect (spec 187 §5): a baseline literally named
    /// <c>no-eligible-blocks-baseline</c>, or a free-form detail quoting a code, decided the verdict. Exact
    /// comparison against the closed vocabulary fixed that, and a code token embedded in a baseline name or
    /// a detail still contributes nothing.
    /// </para>
    /// <para>
    /// SILENTLY DISCARDING an unparsed segment was the second (spec 188 §2). A segment that is blank,
    /// structurally malformed, or simply not in this vocabulary is not a neutral absence: reporting only the
    /// codes that WERE recognised let "merit failure plus something we do not understand" read as
    /// merit-only. So the parse reports its own completeness and the caller refuses a negative verdict
    /// unless the whole list was understood.
    /// </para>
    /// </summary>
    private static RenderedReasonParse ParseRenderedReasonCodes(string reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
        {
            return RenderedReasonParse.Nothing;
        }

        var segments = reasons.Split(RenderedReasonSeparator, StringSplitOptions.None);
        var codes = new List<string>(segments.Length);
        var everySegmentRecognised = true;
        foreach (var segment in segments)
        {
            if (TryParseSegmentCode(segment, out var code))
            {
                codes.Add(code);
            }
            else
            {
                everySegmentRecognised = false;
            }
        }

        return new RenderedReasonParse(codes, everySegmentRecognised);
    }

    /// <summary>
    /// Reduces ONE rendered segment to its closed-vocabulary code, or reports that it could not be
    /// understood. Blank, structurally malformed and unrecognised/future segments all return <c>false</c>.
    /// </summary>
    private static bool TryParseSegmentCode(string segment, out string code)
    {
        code = string.Empty;
        var candidate = segment.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        // "baseline '<name>': <rest>" — a baseline name may itself contain "': ", so take the LAST
        // occurrence of the closing delimiter, which is what Render() emits directly before the code.
        if (candidate.StartsWith("baseline '", StringComparison.Ordinal))
        {
            var close = candidate.LastIndexOf("': ", StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
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
        if (!Ad15GateReasonCodes.All.Contains(candidate, StringComparer.Ordinal))
        {
            return false;
        }

        code = candidate;
        return true;
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
