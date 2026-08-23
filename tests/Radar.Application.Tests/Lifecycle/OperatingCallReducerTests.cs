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
    private static readonly DateTimeOffset VerdictAt = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static ScoringStrategyDefinition Strategy(
        string name, bool isPrimary = false, StrategyPurpose purpose = StrategyPurpose.Research) =>
        new(name, name, new ScoringWeights(), isPrimary) { Purpose = purpose };

    private static readonly IReadOnlyList<ScoringStrategyDefinition> Strategies =
    [
        Strategy("default", isPrimary: true),
        Strategy("alpha"),
        Strategy("beta"),
        Strategy("baseline-noise", purpose: StrategyPurpose.Comparator),
    ];

    private static StrategyOperatingCall Call(
        string strategy,
        OperatingCall call,
        bool overridesGate = false,
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
            Resolution: resolution);

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
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictAt) };

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
    public void GateDefault_WinsOverAnOverrideThatPredatesTheVerdict()
    {
        // overridesGate: true, but the call PREDATES the verdict — you cannot pre-override a verdict that
        // did not exist yet (spec 184 §2 rule 1).
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead, overridesGate: true, asOf: VerdictAt.AddDays(-1)));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictAt) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.Equal(OperatingCall.Stop, resolved.For("alpha")!.Call);
        Assert.True(resolved.StopAll);
    }

    [Fact]
    public void Override_Wins_WhenItPostdatesTheVerdictAndDeclaresOverridesGate()
    {
        var file = File(
            stopAll: false,
            Call("alpha", OperatingCall.Lead, overridesGate: true, asOf: VerdictAt.AddDays(1)));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: false, VerdictAt) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        var alpha = resolved.For("alpha")!;
        Assert.Equal(OperatingCall.Lead, alpha.Call);
        Assert.Equal(ResolvedCallProvenance.DeclaredCall, alpha.Provenance);
        Assert.NotNull(alpha.GateVerdict); // the overridden verdict stays visible
        Assert.Equal("alpha", resolved.LeadStrategyName);
        Assert.False(resolved.StopAll);
    }

    [Fact]
    public void GatePassed_PromotesTheArm_WhenTheDeclaredLeadIsTheSameArm()
    {
        var file = File(stopAll: false, Call("alpha", OperatingCall.Lead));
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: true, VerdictAt) };

        var resolved = OperatingCallReducer.Reduce(file, Strategies, verdicts);

        Assert.Equal("alpha", resolved.LeadStrategyName);
        Assert.Equal(ResolvedCallProvenance.GateDefault, resolved.For("alpha")!.Provenance);
    }

    [Fact]
    public void GatePassed_BesideADifferentDeclaredLead_FailsLoudly_NeverPicksSilently()
    {
        var file = File(stopAll: false, Call("alpha", OperatingCall.Lead));
        var verdicts = new[] { new StrategyGateVerdict("beta", Passed: true, VerdictAt) };

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
        var verdicts = new[] { new StrategyGateVerdict("alpha", Passed: true, VerdictAt) };

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
            new StrategyGateVerdict("beta", Passed: false, VerdictAt),
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
                    OperatingCallOutcome.Wrong, VerdictAt, "data/efficacy/strategy-paired-comparison.md")));
        var ex = Assert.Throws<InvalidOperationException>(
            () => OperatingCallReducer.Validate(file, Strategies));
        Assert.Contains("resolution block but no resolutionRule", ex.Message);
    }
}
