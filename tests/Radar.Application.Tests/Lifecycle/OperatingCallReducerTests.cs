using Radar.Application.Lifecycle;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Lifecycle;

/// <summary>
/// Spec 184 §2: the ONE deterministic reducer — file-only, gate-default-wins, override-wins and
/// zero-Lead→StopAll fixtures, each deterministic and order-independent — plus every validation failure
/// mode, each naming the file and the rule.
/// </summary>
public sealed class OperatingCallReducerTests
{
    private const string Source = "data/strategy-operating-calls.json";

    private static readonly DateTimeOffset CallAt = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReviewBy = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResolvedAt = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // Spec 186 §3: a verdict is identified SEMANTICALLY, never by an instant. These stand in for the
    // artifact's gateVerdictId column.
    private const string VerdictId = "9f1c0e7a2b4d";
    private const string OtherVerdictId = "0000deadbeef";

    // Spec 212: a Lead REQUIRES explicit label lines, so the arms these fixtures declare or promote as Lead
    // (default, alpha) carry them; beta deliberately does NOT — it is the arm the labels-rule fixtures
    // below promote or declare as Lead to prove the rule fires.
    private static readonly LabelThresholds AlphaLines = new(20, 15);

    private static ScoringStrategyDefinition Strategy(
        string name,
        bool isPrimary = false,
        StrategyPurpose purpose = StrategyPurpose.Research,
        LabelThresholds? labels = null) =>
        new(name, name, new ScoringWeights(), isPrimary) { Purpose = purpose, Labels = labels };

    private static readonly IReadOnlyList<ScoringStrategyDefinition> Strategies =
    [
        Strategy("default", isPrimary: true, labels: LabelThresholds.Default),
        Strategy("alpha", labels: AlphaLines),
        Strategy("beta"),
        Strategy("baseline-noise", purpose: StrategyPurpose.Comparator),
    ];

    private static StrategyOperatingCall Call(
        string strategy,
        OperatingCall call,
        bool overridesGate = false,
        string? overridesVerdictId = null,
        DateTimeOffset? asOf = null,
        string? resolutionRule = "rule text",
        OperatingCallResolution? resolution = null) =>
        new(
            strategy,
            call,
            asOf ?? CallAt,
            Basis: $"basis for {strategy}",
            Actor: OperatingCallActor.Human,
            OverridesGate: overridesGate,
            ReviewByUtc: ReviewBy,
            ResolutionRule: resolutionRule,
            Resolution: resolution,
            OverridesVerdictId: overridesVerdictId);

    private static StrategyOperatingCallsFile File(
        bool stopAll = false, params StrategyOperatingCall[] calls) =>
        new(Source, "strategy-operating-calls-v1", stopAll, calls);

    // ---- reduction ------------------------------------------------------------------------------------

    [Fact]
    public void FileOnly_LeadAppliesVerbatim_AndUncalledArmIsImplicitTrial()
    {
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("default", OperatingCall.DoNotLead));

        var resolved = OperatingCallReducer.Reduce(file, Strategies, []);

        Assert.True(resolved.HasDeclaredCalls);
        Assert.False(resolved.StopAll);
        Assert.Equal("alpha", resolved.LeadStrategyName);
        Assert.Equal(OperatingCall.Lead, resolved.For("alpha")!.Call);
        Assert.Equal(ResolvedCallProvenance.DeclaredCall, resolved.For("alpha")!.Provenance);
        Assert.Equal(OperatingCall.DoNotLead, resolved.For("default")!.Call);

        // beta has no declared call and no verdict → implicit Trial, stated as such.
        var beta = resolved.For("beta")!;
        Assert.Equal(OperatingCall.Trial, beta.Call);
        Assert.Equal(ResolvedCallProvenance.ImplicitTrial, beta.Provenance);

        // A comparator carries no call at all.
        Assert.Null(resolved.For("baseline-noise"));
    }

    [Fact]
    public void GateDefault_WinsOverACallThatLacksOverridesGate()
    {
        // alpha is the declared Lead; the gate FAILED it. The call does not carry overridesGate, so the
        // gate default (GateFailed → Stop) applies — and with zero Leads left, the PREDECLARED fallback
        // StopAll resolves (spec 184 §2 rule 3).
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("default", OperatingCall.DoNotLead));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictId) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        var alpha = resolved.For("alpha")!;
        Assert.Equal(OperatingCall.Stop, alpha.Call);
        Assert.Equal(ResolvedCallProvenance.GateDefault, alpha.Provenance);
        Assert.NotNull(alpha.Declared); // the demoted declared call stays attached for the record

        Assert.True(resolved.StopAll);
        Assert.Null(resolved.LeadStrategyName);
        Assert.Contains("zero Leads after reduction", resolved.StopAllReason);
    }

    [Fact]
    public void GateDefault_WinsOverAnOverrideBoundToADifferentVerdict()
    {
        // overridesGate: true, but the call binds to a verdict id the artifact no longer carries — new
        // admitted evidence (or an AD-16 prerequisite transition) re-armed the gate default, and the stale
        // binding is REPORTED rather than silently dropped (spec 186 §3).
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead, overridesGate: true, overridesVerdictId: OtherVerdictId));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictId) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.Equal(OperatingCall.Stop, resolved.For("alpha")!.Call);
        Assert.True(resolved.StopAll);

        var stale = Assert.Single(resolved.StaleOverrides);
        Assert.Equal("alpha", stale.StrategyName);
        Assert.Equal(OtherVerdictId, stale.BoundVerdictId);
        Assert.Equal(VerdictId, stale.CurrentVerdictId);
    }

    [Fact]
    public void GateDefault_WinsOverAnOverrideWhenTheArtifactCarriesNoVerdictIdentity()
    {
        // The pre-186 artifact path: the identity is UNKNOWN, so nothing can match it. Fail closed toward
        // the gate default, and say so — never fabricate an id (AD-8).
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead, overridesGate: true, overridesVerdictId: VerdictId));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictId: "") };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.Equal(OperatingCall.Stop, resolved.For("alpha")!.Call);
        Assert.True(resolved.StopAll);
        Assert.Equal(string.Empty, Assert.Single(resolved.StaleOverrides).CurrentVerdictId);
    }

    [Fact]
    public void Override_Wins_WhenItBindsToTheCurrentVerdictId_WhateverTheCallInstant()
    {
        // The call's asOfUtc is deliberately a year BEFORE and a year AFTER in the two cases: no timestamp
        // participates in the override rule any more, so both must resolve identically (spec 186 §3).
        foreach (var asOf in new[] { CallAt.AddYears(-1), CallAt.AddYears(1) })
        {
            var file = File(
                stopAll: false,
                Call(
                    "alpha",
                    OperatingCall.Lead,
                    overridesGate: true,
                    overridesVerdictId: VerdictId,
                    asOf: asOf));
            var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictId) };

            var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

            var alpha = resolved.For("alpha")!;
            Assert.Equal(OperatingCall.Lead, alpha.Call);
            Assert.Equal(ResolvedCallProvenance.DeclaredCall, alpha.Provenance);
            Assert.NotNull(alpha.GateVerdict); // the overridden verdict stays visible
            Assert.Null(alpha.StaleOverride);  // a bound override is not stale
            Assert.Empty(resolved.StaleOverrides);
            Assert.Equal("alpha", resolved.LeadStrategyName);
            Assert.False(resolved.StopAll);
        }
    }

    [Fact]
    public void GatePassed_PromotesTheArm_WhenTheDeclaredLeadIsTheSameArm()
    {
        var file = File(stopAll: false, Call("alpha", OperatingCall.Lead));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: true, VerdictId) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.Equal("alpha", resolved.LeadStrategyName);
        Assert.Equal(ResolvedCallProvenance.GateDefault, resolved.For("alpha")!.Provenance);
    }

    [Fact]
    public void GatePassed_BesideADifferentDeclaredLead_FailsLoudly_NeverPicksSilently()
    {
        var file = File(stopAll: false, Call("alpha", OperatingCall.Lead));
        var verdicts = new[] { new StrategyGateVerdict("beta", Passed: true, VerdictId) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Reduce(file, Strategies, verdicts));
        Assert.Contains("2 Leads", ex.Message);
        Assert.Contains(Source, ex.Message);
    }

    [Fact]
    public void DeclaredStopAll_YieldsNoLead_AndRendersTheDiagnosticResolution()
    {
        var file = File(stopAll: true, Call("default", OperatingCall.DoNotLead));

        var resolved = OperatingCallReducer.Reduce(file, Strategies, []);

        Assert.True(resolved.StopAll);
        Assert.Null(resolved.LeadStrategyName);
        Assert.Contains("declared", resolved.StopAllReason);
    }

    [Fact]
    public void DeclaredStopAll_IsNotUndoneByAGatePassedVerdict()
    {
        var file = File(stopAll: true, Call("default", OperatingCall.DoNotLead));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: true, VerdictId) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.True(resolved.StopAll);
        Assert.Null(resolved.LeadStrategyName);
        Assert.NotEqual(OperatingCall.Lead, resolved.For("alpha")!.Call);
    }

    [Fact]
    public void Reduction_IsOrderIndependent_OverFileAndVerdictOrder()
    {
        var calls = new[]
        {
            Call("alpha", OperatingCall.Lead),
            Call("default", OperatingCall.DoNotLead),
            Call("beta", OperatingCall.Trial),
        };
        var verdicts = new[]
        {
            new StrategyGateVerdict("beta", Passed: false, VerdictId),
        };

        var forward = OperatingCallReducer.Reduce(File(false, calls), Strategies, verdicts);
        var shuffledCalls = new[] { calls[2], calls[0], calls[1] };
        var shuffled = OperatingCallReducer.Reduce(File(false, shuffledCalls), Strategies, verdicts);

        Assert.Equal(forward.LeadStrategyName, shuffled.LeadStrategyName);
        Assert.Equal(forward.StopAll, shuffled.StopAll);
        Assert.Equal(
            forward.Calls.Select(c => (c.StrategyName, c.Call, c.Provenance)),
            shuffled.Calls.Select(c => (c.StrategyName, c.Call, c.Provenance)));

        // …and the resolved order is the CONFIGURED strategy order, not the file's.
        Assert.Equal(new[] { "default", "alpha", "beta" }, shuffled.Calls.Select(c => c.StrategyName));
    }

    // ---- validation (spec 184 §2 rule 4: every failure names the file and the rule) -------------------

    [Fact]
    public void Validation_UnknownStrategy_Fails()
    {
        var file = File(stopAll: false, Call("ghost", OperatingCall.Lead));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("unknown strategy 'ghost'", ex.Message);
    }

    [Fact]
    public void Validation_CallOnAComparator_Fails()
    {
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("baseline-noise", OperatingCall.Trial));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains(Source, ex.Message);
        Assert.Contains("comparator 'baseline-noise'", ex.Message);
    }

    [Fact]
    public void Validation_DuplicateCall_Fails()
    {
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("ALPHA", OperatingCall.Trial));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("duplicate call", ex.Message);
    }

    [Fact]
    public void Validation_MultipleLeads_Fails()
    {
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("beta", OperatingCall.Lead));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("multiple Lead calls", ex.Message);
    }

    [Fact]
    public void Validation_ZeroLeadsWithoutStopAll_Fails()
    {
        var file = File(stopAll: false, Call("alpha", OperatingCall.Trial));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("zero Lead calls without globalCall StopAll", ex.Message);
    }

    [Fact]
    public void Validation_LeadAlongsideStopAll_Fails()
    {
        var file = File(stopAll: true, Call("alpha", OperatingCall.Lead));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("StopAll is declared alongside a Lead", ex.Message);
    }

    // ---- spec 212: a Lead requires explicit label lines ------------------------------------------------

    [Fact]
    public void Validation_DeclaredLeadWithoutLabels_Fails_NamingArmAndConfigPath()
    {
        // (f) beta is a configured Research arm with Labels == null (index 2 in the configured order).
        var file = File(stopAll: false, Call("beta", OperatingCall.Lead));

        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));

        Assert.Contains(Source, ex.Message);
        Assert.Contains("declared Lead 'beta' has no Labels", ex.Message);
        Assert.Contains("Radar:Strategies:2:Labels", ex.Message);
        Assert.Contains("spec 212", ex.Message);
    }

    [Fact]
    public void Validation_DeclaredLeadWithLabels_Passes_AndNonLeadArmsMayOmitThem()
    {
        // alpha is labelled; beta (Trial, unlabelled) and default (DoNotLead) carry no requirement.
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("beta", OperatingCall.Trial),
            Call("default", OperatingCall.DoNotLead));

        OperatingCallReducer.Validate(file, Strategies);
        Assert.Equal("alpha", OperatingCallReducer.Reduce(file, Strategies, []).LeadStrategyName);
    }

    [Fact]
    public void Validation_DeclaredLeadWithExplicitDefaultLines_Passes_OmittedIsWhatFails()
    {
        // Omitted ≠ explicit 60/40: a Lead that explicitly chose the defaults satisfies the rule.
        var explicitDefault = new List<ScoringStrategyDefinition>(Strategies)
        {
            [2] = Strategy("beta", labels: new LabelThresholds(60, 40)),
        };
        var file = File(stopAll: false, Call("beta", OperatingCall.Lead));

        OperatingCallReducer.Validate(file, explicitDefault);
        Assert.Equal("beta", OperatingCallReducer.Reduce(file, explicitDefault, []).LeadStrategyName);
    }

    [Fact]
    public void Reduce_GatePromotedLeadWithoutLabels_Fails_NamingArmProvenanceAndConfigPath()
    {
        // (f2) alpha is the declared Lead WITH labels and receives a FAILING verdict (gate default wins:
        // alpha → Stop). beta is Trial with NO labels and receives a PASSING verdict, becoming the SOLE
        // effective Lead. Validate passes (the declared Lead is labelled); Reduce must fail naming beta,
        // its GateDefault provenance and the config path — a report labelled on lines nobody chose is
        // exactly the fail-open shape.
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("beta", OperatingCall.Trial));
        var verdicts = new[]
        {
            new StrategyGateVerdict("alpha", Passed: false, VerdictId),
            new StrategyGateVerdict("beta", Passed: true, OtherVerdictId),
        };

        OperatingCallReducer.Validate(file, Strategies); // the declared Lead is labelled

        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Reduce(file, Strategies, verdicts));

        Assert.Contains(Source, ex.Message);
        Assert.Contains("gate-promoted Lead 'beta' has no Labels", ex.Message);
        Assert.Contains("Radar:Strategies:2:Labels", ex.Message);
    }

    [Fact]
    public void Reduce_GatePromotedLeadWithLabels_Passes()
    {
        // The same fixture with beta labelled reduces to beta as the gate-promoted Lead.
        var labelledBeta = new List<ScoringStrategyDefinition>(Strategies)
        {
            [2] = Strategy("beta", labels: new LabelThresholds(30, 10)),
        };
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead),
            Call("beta", OperatingCall.Trial));
        var verdicts = new[]
        {
            new StrategyGateVerdict("alpha", Passed: false, VerdictId),
            new StrategyGateVerdict("beta", Passed: true, OtherVerdictId),
        };

        var resolved = OperatingCallReducer.Reduce(file, labelledBeta, verdicts);

        Assert.Equal("beta", resolved.LeadStrategyName);
        Assert.Equal(ResolvedCallProvenance.GateDefault, resolved.For("beta")!.Provenance);
        Assert.Equal(OperatingCall.Stop, resolved.For("alpha")!.Call);
    }

    [Fact]
    public void Reduce_OverriddenLeadWithoutLabels_Fails_AtValidation_NamingTheDeclaredLead()
    {
        // A declared override that HOLDS beta as Lead against a failing verdict is still a DECLARED Lead,
        // so Validate catches it first — one rule covers declared, overridden and gate-promoted Leads.
        var file = File(
            stopAll: false,
            Call("beta", OperatingCall.Lead, overridesGate: true, overridesVerdictId: VerdictId));
        var verdicts = new[] { new StrategyGateVerdict("beta", Passed: false, VerdictId) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Reduce(file, Strategies, verdicts));

        Assert.Contains("Lead 'beta' has no Labels", ex.Message);
        Assert.Contains("Radar:Strategies:2:Labels", ex.Message);
    }

    [Fact]
    public void StopAll_NeverTripsTheLabelsRule_DeclaredOrGateProduced()
    {
        // (f3) With EVERY arm unlabelled: declared StopAll reduces without touching the labels rule — no
        // Lead, no labels, nothing to require.
        var unlabelled = new List<ScoringStrategyDefinition>
        {
            Strategy("default", isPrimary: true),
            Strategy("alpha"),
            Strategy("beta"),
        };

        var declared = OperatingCallReducer.Reduce(
            File(stopAll: true, Call("default", OperatingCall.DoNotLead)), unlabelled, []);
        Assert.True(declared.StopAll);

        // Gate-produced: the declared Lead HAS labels (it must, or Validate stops the fixture) and is
        // demoted with no other arm promoted — the zero-Lead fallback applies, no labels rule fires.
        var fallback = OperatingCallReducer.Reduce(
            File(stopAll: false, Call("alpha", OperatingCall.Lead)),
            Strategies,
            [new StrategyGateVerdict("alpha", Passed: false, VerdictId)]);
        Assert.True(fallback.StopAll);
        Assert.Contains("zero Leads after reduction", fallback.StopAllReason);
    }

    [Fact]
    public void Validation_ResolutionWithoutResolutionRule_Fails()
    {
        var file = File(
            stopAll: false,
            Call(
                "alpha",
                OperatingCall.Lead,
                resolutionRule: null,
                resolution: new OperatingCallResolution(
                    OperatingCallOutcome.Wrong, ResolvedAt, "data/efficacy/strategy-paired-comparison.md")));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("resolution block but no resolutionRule", ex.Message);
    }
}
