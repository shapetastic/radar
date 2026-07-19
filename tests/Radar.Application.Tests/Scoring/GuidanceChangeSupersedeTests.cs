using Radar.Application.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Unit tests for the pure spec-113 assembly-time supersede: among GuidanceChange signals sharing one
/// EvidenceId, a directional one beats the deterministic Neutral, at most one survives per EvidenceId,
/// nothing else is touched, and the outcome is deterministic (AD-3) regardless of input order.
/// </summary>
public sealed class GuidanceChangeSupersedeTests
{
    private static readonly DateTimeOffset Observed = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Signal Guidance(
        Guid evidenceId,
        SignalDirection direction,
        DateTimeOffset? observedAt = null,
        Guid? id = null) =>
        new SignalBuilder()
            .WithId(id ?? Guid.NewGuid())
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithObservedAtUtc(observedAt ?? Observed)
            .Build();

    [Fact]
    public void DirectionalBeatsNeutral_SameEvidence_NeutralIsDropped()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, positive });

        var survivor = Assert.Single(result);
        Assert.Equal(positive.Id, survivor.Id);
    }

    [Fact]
    public void DirectionalBeatsNeutral_NegativeAlsoSupersedes()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var negative = Guidance(evidenceId, SignalDirection.Negative);

        var result = GuidanceChangeSupersede.Apply(new[] { negative, neutral });

        var survivor = Assert.Single(result);
        Assert.Equal(negative.Id, survivor.Id);
    }

    [Fact]
    public void DirectionalBeatsMultipleNeutrals_AtMostOnePerEvidence()
    {
        // Duplicate stale Neutral copies (cross-run re-mints) plus one directional: only the directional
        // survives — at most ONE GuidanceChange per EvidenceId, no double-count.
        var evidenceId = Guid.NewGuid();
        var neutralA = Guidance(evidenceId, SignalDirection.Neutral);
        var neutralB = Guidance(evidenceId, SignalDirection.Neutral, Observed.AddHours(1));
        var positive = Guidance(evidenceId, SignalDirection.Positive);

        var result = GuidanceChangeSupersede.Apply(new[] { neutralA, positive, neutralB });

        var survivor = Assert.Single(result);
        Assert.Equal(positive.Id, survivor.Id);
    }

    [Fact]
    public void NeutralOnly_PassesThroughUnchanged()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var other = new SignalBuilder().WithType(SignalType.CustomerWin).Build();
        var input = new[] { neutral, other };

        var result = GuidanceChangeSupersede.Apply(input);

        Assert.Equal(new[] { neutral.Id, other.Id }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void DuplicateNeutrals_NoDirectional_CollapseToOneDeterministically()
    {
        // Two stale Neutral copies with distinct ids, no directional read: exactly one survives — the
        // stable-order pick (earliest ObservedAtUtc, then lowest Id).
        var evidenceId = Guid.NewGuid();
        var idA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var neutralA = Guidance(evidenceId, SignalDirection.Neutral, Observed, idA);
        var neutralB = Guidance(evidenceId, SignalDirection.Neutral, Observed, idB);

        var result = GuidanceChangeSupersede.Apply(new[] { neutralB, neutralA });

        var survivor = Assert.Single(result);
        Assert.Equal(idA, survivor.Id);
    }

    [Fact]
    public void ContradictoryDirectionals_TieBreakByObservedThenId()
    {
        // Both directional (Positive + Negative) over the same evidence: the stable order picks the
        // EARLIEST ObservedAtUtc, independent of direction and of input order.
        var evidenceId = Guid.NewGuid();
        var earlierNegative = Guidance(evidenceId, SignalDirection.Negative, Observed);
        var laterPositive = Guidance(evidenceId, SignalDirection.Positive, Observed.AddHours(2));

        var forward = GuidanceChangeSupersede.Apply(new[] { earlierNegative, laterPositive });
        var reversed = GuidanceChangeSupersede.Apply(new[] { laterPositive, earlierNegative });

        Assert.Equal(earlierNegative.Id, Assert.Single(forward).Id);
        Assert.Equal(earlierNegative.Id, Assert.Single(reversed).Id);
    }

    [Fact]
    public void MixedDirection_CountsAsDirectional_AndSupersedesNeutral()
    {
        // Mixed is a directional read outcome (not the deterministic Neutral placeholder), matching the
        // spec-78 supersede where ANY directional read replaces the Neutral.
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var mixed = Guidance(evidenceId, SignalDirection.Mixed);

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, mixed });

        Assert.Equal(mixed.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void CrossEvidence_NeverInterferes()
    {
        // A directional over filing A never touches the Neutral over filing B.
        var evidenceA = Guid.NewGuid();
        var evidenceB = Guid.NewGuid();
        var positiveA = Guidance(evidenceA, SignalDirection.Positive);
        var neutralB = Guidance(evidenceB, SignalDirection.Neutral);

        var result = GuidanceChangeSupersede.Apply(new[] { positiveA, neutralB });

        Assert.Equal(new[] { positiveA.Id, neutralB.Id }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void NonGuidanceChange_Untouched_EvenOverTheSameEvidence()
    {
        // A CustomerWin over the same filing evidence is NOT a GuidanceChange and must survive intact.
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);
        var customerWin = new SignalBuilder()
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.CustomerWin)
            .WithDirection(SignalDirection.Positive)
            .Build();

        var result = GuidanceChangeSupersede.Apply(new[] { customerWin, neutral, positive });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Id == customerWin.Id);
        Assert.Contains(result, s => s.Id == positive.Id);
        Assert.DoesNotContain(result, s => s.Id == neutral.Id);
    }

    [Fact]
    public void PreservesInputRelativeOrderingOfSurvivors()
    {
        var evidenceId = Guid.NewGuid();
        var before = new SignalBuilder().WithType(SignalType.HiringActivity).Build();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);
        var after = new SignalBuilder().WithType(SignalType.CustomerWin).Build();

        var result = GuidanceChangeSupersede.Apply(new[] { before, neutral, positive, after });

        Assert.Equal(new[] { before.Id, positive.Id, after.Id }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(GuidanceChangeSupersede.Apply(Array.Empty<Signal>()));
        Assert.Empty(GuidanceChangeSupersede.Apply(Array.Empty<ScoringSignal>()));
    }

    [Fact]
    public void Determinism_InputOrderDoesNotChangeTheSurvivorSet()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);
        var other = new SignalBuilder().WithType(SignalType.CustomerWin).Build();

        var a = GuidanceChangeSupersede.Apply(new[] { neutral, positive, other });
        var b = GuidanceChangeSupersede.Apply(new[] { other, positive, neutral });
        var c = GuidanceChangeSupersede.Apply(new[] { positive, other, neutral });

        static HashSet<Guid> Ids(IReadOnlyList<Signal> signals) => signals.Select(s => s.Id).ToHashSet();

        Assert.True(Ids(a).SetEquals(Ids(b)));
        Assert.True(Ids(b).SetEquals(Ids(c)));
        Assert.Contains(positive.Id, Ids(a));
        Assert.DoesNotContain(neutral.Id, Ids(a));
    }

    [Fact]
    public void ScoringSignalOverload_AppliesTheSameRule_AndKeepsEvidencePairing()
    {
        var evidence = new EvidenceBuilder().WithId(Guid.NewGuid()).Build();
        var neutral = new ScoringSignal(Guidance(evidence.Id, SignalDirection.Neutral), evidence);
        var positive = new ScoringSignal(Guidance(evidence.Id, SignalDirection.Positive), evidence);
        var otherEvidence = new EvidenceBuilder().WithId(Guid.NewGuid()).Build();
        var other = new ScoringSignal(
            new SignalBuilder().WithEvidenceId(otherEvidence.Id).WithType(SignalType.CustomerWin).Build(),
            otherEvidence);

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, positive, other });

        Assert.Equal(2, result.Count);
        var survivingGuidance = Assert.Single(result, s => s.Signal.Type == SignalType.GuidanceChange);
        Assert.Equal(positive.Signal.Id, survivingGuidance.Signal.Id);
        Assert.Same(evidence, survivingGuidance.Evidence); // pairing intact (provenance)
        Assert.Contains(result, s => s.Signal.Id == other.Signal.Id);
    }

    [Fact]
    public void NoConflict_ReturnsTheInputInstance()
    {
        // Fast path: a single GuidanceChange (the healthy spec-78 shape) leaves the set untouched —
        // the same instance comes back, proving the healthy path is a strict no-op.
        var input = new[]
        {
            Guidance(Guid.NewGuid(), SignalDirection.Positive),
            new SignalBuilder().WithType(SignalType.CustomerWin).Build(),
        };

        var result = GuidanceChangeSupersede.Apply(input);

        Assert.Same(input, result);
    }
}
