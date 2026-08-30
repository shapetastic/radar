using Radar.Application.Filings;
using Radar.Application.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Unit tests for the pure spec-113 assembly-time supersede: among GuidanceChange signals sharing one
/// EvidenceId, a directional one beats the deterministic Neutral, at most one survives per EvidenceId,
/// nothing else is touched, and the outcome is deterministic (AD-3) regardless of input order.
/// <para>
/// Spec 193 §2 added ACCOUNTING to the same call — every case here reads <c>.Signals</c>, and the survivor
/// set it asserts is unchanged. The counts themselves are covered by
/// <c>GuidanceChangeSupersedeAccountingTests</c>.
/// </para>
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

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, positive }).Signals;

        var survivor = Assert.Single(result);
        Assert.Equal(positive.Id, survivor.Id);
    }

    [Fact]
    public void DirectionalBeatsNeutral_NegativeAlsoSupersedes()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var negative = Guidance(evidenceId, SignalDirection.Negative);

        var result = GuidanceChangeSupersede.Apply(new[] { negative, neutral }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { neutralA, positive, neutralB }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(input).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { neutralB, neutralA }).Signals;

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

        var forward = GuidanceChangeSupersede.Apply(new[] { earlierNegative, laterPositive }).Signals;
        var reversed = GuidanceChangeSupersede.Apply(new[] { laterPositive, earlierNegative }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, mixed }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { positiveA, neutralB }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { customerWin, neutral, positive }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { before, neutral, positive, after }).Signals;

        Assert.Equal(new[] { before.Id, positive.Id, after.Id }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(GuidanceChangeSupersede.Apply(Array.Empty<Signal>()).Signals);
        Assert.Empty(GuidanceChangeSupersede.Apply(Array.Empty<ScoringSignal>()).Signals);
    }

    [Fact]
    public void Determinism_InputOrderDoesNotChangeTheSurvivorSet()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);
        var other = new SignalBuilder().WithType(SignalType.CustomerWin).Build();

        var a = GuidanceChangeSupersede.Apply(new[] { neutral, positive, other }).Signals;
        var b = GuidanceChangeSupersede.Apply(new[] { other, positive, neutral }).Signals;
        var c = GuidanceChangeSupersede.Apply(new[] { positive, other, neutral }).Signals;

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

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, positive, other }).Signals;

        Assert.Equal(2, result.Count);
        var survivingGuidance = Assert.Single(result, s => s.Signal.Type == SignalType.GuidanceChange);
        Assert.Equal(positive.Signal.Id, survivingGuidance.Signal.Id);
        Assert.Same(evidence, survivingGuidance.Evidence); // pairing intact (provenance)
        Assert.Contains(result, s => s.Signal.Id == other.Signal.Id);
    }

    // ---- spec 204: the persisted AI READ beats the deterministic keyword copy, ahead of every other rule ----

    /// <summary>A spec-204 read signal: the envelope is composed through the REAL producer, never hand-rolled.</summary>
    private static Signal ReadSignal(
        Guid evidenceId,
        SignalDirection direction,
        FilingNoSignalCause cause,
        string readDirection,
        DateTimeOffset? observedAt = null,
        Guid? id = null) =>
        new SignalBuilder()
            .WithId(id ?? Guid.NewGuid())
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithObservedAtUtc(observedAt ?? Observed)
            .WithMetadataJson(FilingReadSignalMetadata.Compose(cause, readDirection, 0.85m, "openai:test-model"))
            .Build();

    [Fact]
    public void NeutralReadSignal_BeatsKeywordNeutral_EvenWhenStableOrderFavoursTheKeywordCopy()
    {
        // The row the spec-204 step exists for: a NEUTRAL read would otherwise TIE with the keyword Neutral
        // on direction and fall to ObservedAtUtc/Id — provenance by GUID order. The fixture deliberately
        // gives the keyword copy the EARLIER ObservedAtUtc AND the LOWER Id (both old tie-breaks favour it),
        // so only the read-preference step can make the read win — and it must, in BOTH input orders.
        var evidenceId = Guid.NewGuid();
        var keyword = Guidance(
            evidenceId, SignalDirection.Neutral, Observed,
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var read = ReadSignal(
            evidenceId, SignalDirection.Neutral, FilingNoSignalCause.Unknown, "Unknown",
            Observed.AddHours(2),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

        var forward = GuidanceChangeSupersede.Apply(new[] { keyword, read }).Signals;
        var reversed = GuidanceChangeSupersede.Apply(new[] { read, keyword }).Signals;

        Assert.Equal(read.Id, Assert.Single(forward).Id);
        Assert.Equal(read.Id, Assert.Single(reversed).Id);
    }

    [Fact]
    public void MixedReadSignal_BeatsKeywordNeutral()
    {
        // The Mixed read already won under the existing directional-beats-Neutral rule; the spec-204 step
        // must not disturb that (the read carries the envelope too, so both steps agree).
        var evidenceId = Guid.NewGuid();
        var keyword = Guidance(evidenceId, SignalDirection.Neutral);
        var read = ReadSignal(evidenceId, SignalDirection.Mixed, FilingNoSignalCause.Mixed, "Mixed");

        var result = GuidanceChangeSupersede.Apply(new[] { keyword, read }).Signals;

        Assert.Equal(read.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void TwoReadSignals_FallToTheExistingStableOrder()
    {
        // Duplicate read copies (cross-run re-mints) tie on the read step AND on direction, so the existing
        // stable order (earliest ObservedAtUtc, then lowest Id) still decides — deterministically (AD-3).
        var evidenceId = Guid.NewGuid();
        var earlier = ReadSignal(
            evidenceId, SignalDirection.Neutral, FilingNoSignalCause.Unknown, "Unknown", Observed);
        var later = ReadSignal(
            evidenceId, SignalDirection.Neutral, FilingNoSignalCause.Unknown, "Unknown", Observed.AddHours(1));

        var forward = GuidanceChangeSupersede.Apply(new[] { later, earlier }).Signals;
        var reversed = GuidanceChangeSupersede.Apply(new[] { earlier, later }).Signals;

        Assert.Equal(earlier.Id, Assert.Single(forward).Id);
        Assert.Equal(earlier.Id, Assert.Single(reversed).Id);
    }

    [Fact]
    public void UnrelatedMetadataBag_DoesNotCountAsARead()
    {
        // A metadata envelope WITHOUT the filingReadOutcome key (e.g. collector metadata) earns no
        // preference: the two Neutrals tie on both new and old direction steps and fall to the stable order,
        // exactly the pre-204 outcome.
        var evidenceId = Guid.NewGuid();
        var idA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var plain = Guidance(evidenceId, SignalDirection.Neutral, Observed, idA);
        var unrelatedBag = new SignalBuilder()
            .WithId(idB)
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Neutral)
            .WithObservedAtUtc(Observed.AddHours(1))
            .WithMetadataJson("""{ "metadata": { "quality": "High" }, "companyHints": [] }""")
            .Build();

        var result = GuidanceChangeSupersede.Apply(new[] { unrelatedBag, plain }).Signals;

        Assert.Equal(idA, Assert.Single(result).Id); // stable order: earliest observed, lowest id.
    }

    [Fact]
    public void ReadPreference_KeepsTheSpec193AccountingShape()
    {
        // The superseded keyword copy is still COUNTED, charged to the surviving read signal — the spec-193
        // accounting is unchanged in shape by the new winner rule.
        var evidenceId = Guid.NewGuid();
        var keyword = Guidance(evidenceId, SignalDirection.Neutral);
        var read = ReadSignal(evidenceId, SignalDirection.Neutral, FilingNoSignalCause.Unknown, "Unknown");

        var result = GuidanceChangeSupersede.Apply(new[] { keyword, read });

        Assert.Equal(read.Id, Assert.Single(result.Signals).Id);
        Assert.Equal(1, result.SupersededCounts[read.Id]);
        Assert.Equal(1, result.TotalSuperseded);
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

        var result = GuidanceChangeSupersede.Apply(input).Signals;

        Assert.Same(input, result);
    }
}
