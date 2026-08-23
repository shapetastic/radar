using Radar.Application.Scoring;

namespace Radar.Application.Lifecycle;

/// <summary>
/// THE one deterministic reducer over the operating-calls file, the configured strategy set and the
/// persisted gate verdicts (spec 184 §2) — implementations cannot disagree about which call wins because
/// there is exactly one implementation and it is pure (no clock, no I/O, no randomness; AD-3).
/// <para>
/// Rules, verbatim from the spec:
/// <list type="number">
/// <item>A persisted gate verdict for an arm wins unless the file's call carries <c>overridesGate: true</c>
/// AND its <c>overridesVerdictId</c> equals the artifact's CURRENT <c>gateVerdictId</c> (ordinal). The gate
/// default is <c>GatePassed → Lead</c>, <c>GateFailed → Stop</c>.
/// <para>
/// Spec 186 §3 replaced the pre-186 "the call must POST-DATE the verdict" rule outright: the verdict
/// instant was the artifact's filesystem mtime, which the daily efficacy re-write advanced, so a valid
/// override silently expired after one run (and a copy/restore did the same, machine-dependently). An
/// override is about a PARTICULAR verdict, so it binds to that verdict BY NAME. No timestamp is compared
/// anywhere in this reducer. An empty/absent current id (a pre-186 artifact, or no verdict at all) can
/// never match — fail closed toward the gate default. An override that names a verdict id which no longer
/// matches is STALE: the gate default re-arms (new evidence SHOULD re-open the call) and the mismatch is
/// recorded on <see cref="ResolvedStrategyCall.StaleOverride"/> so the report can state it — never
/// silently dropped.
/// </para></item>
/// <item>Otherwise the file's call applies verbatim; a Research arm with no call is an implicit Trial.</item>
/// <item>After reduction: a declared <c>globalCall: StopAll</c> means no Lead exists; otherwise exactly one
/// Research arm must be Lead. ZERO Leads after reduction (e.g. the Lead arm gate-failed) resolves to the
/// PREDECLARED fallback <c>StopAll</c> — if the declared hypothesis fails, no other arm has earned the
/// front page by default; a human makes the next Lead call explicitly.</item>
/// <item>Validation fails (an <see cref="InvalidOperationException"/> naming the file and the rule) on:
/// unknown strategy, a call on a Comparator, a duplicate call, multiple declared Leads, zero declared Leads
/// without StopAll, a declared Lead alongside StopAll, and a resolution block whose call lacks a
/// <c>resolutionRule</c>. (Unknown TOKENS fail earlier, in the file reader, equally naming the file.)</item>
/// </list>
/// Order-independence: the result is keyed by the CONFIGURED strategy order, never by file order, and every
/// per-arm decision depends only on that arm's (unique) call and verdict — shuffling the file's calls or
/// the verdict list cannot change the output.
/// </para>
/// </summary>
public static class OperatingCallReducer
{
    /// <summary>
    /// Validates <paramref name="file"/> against the configured strategy set. Every failure names the file
    /// (its <see cref="StrategyOperatingCallsFile.Source"/>) and the violated rule.
    /// </summary>
    public static void Validate(
        StrategyOperatingCallsFile file, IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(strategies);

        var byName = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredLeads = new List<string>();

        foreach (var call in file.Calls)
        {
            if (!byName.TryGetValue(call.Strategy, out var definition))
            {
                throw Fail(
                    file,
                    $"call names unknown strategy '{call.Strategy}' — every call must name a configured "
                        + "Radar strategy (the configured names are: "
                        + string.Join(", ", strategies.Select(s => s.Name)) + ")");
            }

            if (definition.Purpose == StrategyPurpose.Comparator)
            {
                throw Fail(
                    file,
                    $"call on comparator '{definition.Name}' — a Comparator exists to be beaten and can "
                        + "never carry an operating call (spec 184 §2 rule 4)");
            }

            if (!seen.Add(definition.Name))
            {
                throw Fail(
                    file,
                    $"duplicate call for strategy '{definition.Name}' — at most one call per strategy, or "
                        + "the reducer's order-independence guarantee is meaningless");
            }

            if (call.Resolution is not null && string.IsNullOrWhiteSpace(call.ResolutionRule))
            {
                throw Fail(
                    file,
                    $"call for '{definition.Name}' carries a resolution block but no resolutionRule — a "
                        + "call can only be resolved against the immutable rule declared with it");
            }

            if (call.Call == OperatingCall.Lead)
            {
                declaredLeads.Add(definition.Name);
            }
        }

        if (declaredLeads.Count > 1)
        {
            throw Fail(
                file,
                "multiple Lead calls (" + string.Join(", ", declaredLeads) + ") — exactly one Research arm "
                    + "may be Lead (spec 184 §2 rule 3)");
        }

        if (file.StopAll && declaredLeads.Count > 0)
        {
            throw Fail(
                file,
                $"globalCall StopAll is declared alongside a Lead call for '{declaredLeads[0]}' — StopAll "
                    + "means no Lead exists; the two declarations contradict each other");
        }

        if (!file.StopAll && declaredLeads.Count == 0)
        {
            throw Fail(
                file,
                "zero Lead calls without globalCall StopAll — declare exactly one Lead, or declare StopAll "
                    + "explicitly (spec 184 §2 rule 3)");
        }
    }

    /// <summary>
    /// Reduces the file + verdicts to every Research arm's effective call and the single global answer.
    /// Validates first, so a caller cannot reduce an invalid file.
    /// </summary>
    public static ResolvedOperatingCalls Reduce(
        StrategyOperatingCallsFile file,
        IReadOnlyList<ScoringStrategyDefinition> strategies,
        IReadOnlyList<StrategyGateVerdict> gateVerdicts)
    {
        ArgumentNullException.ThrowIfNull(gateVerdicts);
        Validate(file, strategies);

        var callsByStrategy = file.Calls.ToDictionary(c => c.Strategy, StringComparer.OrdinalIgnoreCase);
        var verdictsByStrategy = new Dictionary<string, StrategyGateVerdict>(StringComparer.OrdinalIgnoreCase);
        foreach (var verdict in gateVerdicts)
        {
            // Two verdicts for one arm cannot come from the one paired artifact; last-write-wins here would
            // be order-dependent, so refuse loudly instead.
            if (!verdictsByStrategy.TryAdd(verdict.StrategyName, verdict))
            {
                throw new InvalidOperationException(
                    $"Two persisted gate verdicts were supplied for strategy '{verdict.StrategyName}'; the "
                        + "AD-15 composite gate judges one arm once per artifact, so duplicate verdicts are "
                        + "a wiring defect.");
            }
        }

        var resolved = new List<ResolvedStrategyCall>();
        foreach (var strategy in strategies)
        {
            if (strategy.Purpose == StrategyPurpose.Comparator)
            {
                continue; // comparators carry no call, ever (validated above)
            }

            callsByStrategy.TryGetValue(strategy.Name, out var declared);
            verdictsByStrategy.TryGetValue(strategy.Name, out var verdict);

            if (verdict is not null && !OverrideApplies(declared, verdict))
            {
                var gateCall = verdict.Passed ? OperatingCall.Lead : OperatingCall.Stop;

                // A declared override that did not bind to the CURRENT verdict is reported, never silently
                // dropped (spec 186 §3).
                var stale = StaleOverrideFor(strategy.Name, declared, verdict);

                // Under a DECLARED StopAll, a gate-passed arm is still not promoted to Lead: StopAll is the
                // human's explicit "no arm holds the front page", and rule 3 says no Lead may exist. The
                // verdict itself stays attached so the report can state it.
                if (file.StopAll && gateCall == OperatingCall.Lead)
                {
                    resolved.Add(new ResolvedStrategyCall(
                        strategy.Name,
                        declared?.Call ?? OperatingCall.Trial,
                        declared is null ? ResolvedCallProvenance.ImplicitTrial : ResolvedCallProvenance.DeclaredCall,
                        declared,
                        verdict,
                        stale));
                }
                else
                {
                    resolved.Add(new ResolvedStrategyCall(
                        strategy.Name, gateCall, ResolvedCallProvenance.GateDefault, declared, verdict, stale));
                }

                continue;
            }

            if (declared is not null)
            {
                resolved.Add(new ResolvedStrategyCall(
                    strategy.Name, declared.Call, ResolvedCallProvenance.DeclaredCall, declared, verdict));
                continue;
            }

            resolved.Add(new ResolvedStrategyCall(
                strategy.Name, OperatingCall.Trial, ResolvedCallProvenance.ImplicitTrial, null, verdict));
        }

        if (file.StopAll)
        {
            return ResolvedOperatingCalls.Stopped(
                "declared: globalCall StopAll is present in " + file.Source, resolved);
        }

        var leads = resolved.Where(c => c.Call == OperatingCall.Lead).ToList();
        if (leads.Count == 1)
        {
            return ResolvedOperatingCalls.WithLead(leads[0].StrategyName, resolved);
        }

        if (leads.Count == 0)
        {
            // The PREDECLARED fallback (spec 184 §2 rule 3): the declared Lead was demoted by a gate
            // verdict, and no other arm has earned the front page by default.
            return ResolvedOperatingCalls.Stopped(
                "fallback: zero Leads after reduction (the declared Lead arm was demoted by a persisted "
                    + "gate verdict) — no other arm has earned the front page by default; a human makes the "
                    + "next Lead call explicitly",
                resolved);
        }

        // Reachable only via gate defaults promoting a second arm beside the declared Lead. There is no
        // predeclared tie-break, and inventing one here would be a policy decision smuggled into a reducer.
        throw new InvalidOperationException(
            $"Operating-calls file '{file.Source}': reduction produced {leads.Count} Leads ("
                + string.Join(", ", leads.Select(l => l.StrategyName))
                + ") — a persisted GatePassed verdict promoted an arm beside the declared Lead. Record an "
                + "explicit human call (with overridesGate: true where it contradicts a verdict) so exactly "
                + "one Research arm is Lead.");
    }

    /// <summary>
    /// Rule 1's override condition (spec 186 §3): the gate default applies UNLESS the file's call carries
    /// <c>overridesGate: true</c> AND binds — by identity, ordinal — to the artifact's CURRENT verdict id.
    /// No timestamp is consulted. An empty current id (pre-186 artifact, or no verdict) and an empty bound
    /// id can never match, so both fail closed toward the gate default.
    /// </summary>
    private static bool OverrideApplies(StrategyOperatingCall? declared, StrategyGateVerdict verdict) =>
        declared is { OverridesGate: true, OverridesVerdictId: { Length: > 0 } bound }
        && verdict.VerdictId.Length > 0
        && string.Equals(bound, verdict.VerdictId, StringComparison.Ordinal);

    /// <summary>
    /// The stale-override record for a declared override that did NOT bind to the current verdict, or null
    /// when the call declares no override. Reported, never silently dropped: the maintainer must be able to
    /// see WHY their override stopped applying and re-declare against the current id.
    /// </summary>
    private static StaleGateOverride? StaleOverrideFor(
        string strategyName, StrategyOperatingCall? declared, StrategyGateVerdict verdict) =>
        declared is { OverridesGate: true } && !OverrideApplies(declared, verdict)
            ? new StaleGateOverride(
                strategyName, declared.OverridesVerdictId ?? string.Empty, verdict.VerdictId)
            : null;

    private static InvalidOperationException Fail(StrategyOperatingCallsFile file, string rule) =>
        new($"Operating-calls file '{file.Source}': {rule}.");
}
