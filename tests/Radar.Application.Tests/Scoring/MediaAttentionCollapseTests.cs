using Radar.Application.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class MediaAttentionCollapseTests
{
    private static readonly DateTimeOffset Base = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static MediaAttentionCollapse Collapse(double windowDays = 3.0) =>
        new(new MediaCollapseOptions { EventWindowDays = windowDays });

    /// <summary>Builds a ScoringSignal of a given type at a given observation time (fresh Ids each call).</summary>
    private static ScoringSignal SignalAt(
        DateTimeOffset observedAt, SignalType type = SignalType.MediaAttention, Guid? id = null)
    {
        var evidence = new EvidenceBuilder()
            .WithId(Guid.NewGuid())
            .WithContentHash(Guid.NewGuid().ToString("N"))
            .Build();
        var signal = new SignalBuilder()
            .WithId(id ?? Guid.NewGuid())
            .WithEvidenceId(evidence.Id)
            .WithType(type)
            .WithDirection(type == SignalType.MediaAttention ? SignalDirection.Neutral : SignalDirection.Positive)
            .WithObservedAtUtc(observedAt)
            .Build();
        return new ScoringSignal(signal, evidence);
    }

    [Fact]
    public void ManyMediaSignalsWithinWindow_CollapseToOneEarliestRepresentative()
    {
        // 5 MediaAttention signals over 2 days (< 3-day window) — one event, one representative.
        var signals = new List<ScoringSignal>
        {
            SignalAt(Base.AddHours(6)),
            SignalAt(Base.AddHours(2)),   // earliest by observation
            SignalAt(Base.AddDays(1)),
            SignalAt(Base.AddHours(30)),
            SignalAt(Base.AddDays(2)),
        };

        var result = Collapse().Collapse(signals);

        var representative = Assert.Single(result.Signals);
        // Earliest-observed is the representative.
        Assert.Equal(Base.AddHours(2), representative.Signal.ObservedAtUtc);
        // The representative names the collapsed count (N-1 duplicates).
        var count = Assert.Contains(representative.Signal.Id, (IDictionary<Guid, int>)result.CollapsedCounts);
        Assert.Equal(4, count);
    }

    [Fact]
    public void MediaSignalsWithinWindow_IdBreaksTie_WhenObservedTimesEqual()
    {
        var idA = new Guid("00000000-0000-0000-0000-0000000000AA");
        var idB = new Guid("00000000-0000-0000-0000-0000000000BB");
        // Same observation instant — the smaller Id is the deterministic representative.
        var signals = new List<ScoringSignal>
        {
            SignalAt(Base, id: idB),
            SignalAt(Base, id: idA),
        };

        var result = Collapse().Collapse(signals);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(idA, representative.Signal.Id);
        Assert.Equal(1, result.CollapsedCounts[idA]);
    }

    [Fact]
    public void MediaSignalsOutsideWindow_FormSeparateBuckets()
    {
        // Two distinct events: three signals ~day 0, three signals ~day 10 (well beyond the 3-day window).
        var event1First = SignalAt(Base.AddHours(1));
        var event2First = SignalAt(Base.AddDays(10));
        var signals = new List<ScoringSignal>
        {
            event1First,
            SignalAt(Base.AddHours(12)),
            SignalAt(Base.AddDays(2)),
            event2First,
            SignalAt(Base.AddDays(10).AddHours(6)),
            SignalAt(Base.AddDays(11)),
        };

        var result = Collapse().Collapse(signals);

        Assert.Equal(2, result.Signals.Count);
        var repIds = result.Signals.Select(s => s.Signal.Id).ToHashSet();
        Assert.Contains(event1First.Signal.Id, repIds);
        Assert.Contains(event2First.Signal.Id, repIds);
        // Each bucket collapsed two duplicates.
        Assert.Equal(2, result.CollapsedCounts[event1First.Signal.Id]);
        Assert.Equal(2, result.CollapsedCounts[event2First.Signal.Id]);
    }

    [Fact]
    public void NonMediaSignals_PassThroughUnchanged_AndOrderingIsStable()
    {
        var media = SignalAt(Base.AddDays(1));
        var mediaDup = SignalAt(Base.AddDays(2));
        var customerWin = SignalAt(Base.AddHours(5), SignalType.CustomerWin);
        var guidance = SignalAt(Base.AddDays(3), SignalType.GuidanceChange);

        var signals = new List<ScoringSignal> { media, mediaDup, customerWin, guidance };

        var result = Collapse().Collapse(signals);

        // media + mediaDup collapse to one; customerWin + guidance untouched → 3 total.
        Assert.Equal(3, result.Signals.Count);
        Assert.Contains(result.Signals, s => s.Signal.Id == customerWin.Signal.Id);
        Assert.Contains(result.Signals, s => s.Signal.Id == guidance.Signal.Id);
        Assert.DoesNotContain(result.Signals, s => s.Signal.Id == mediaDup.Signal.Id);

        // Overall ordering is stable: ObservedAtUtc ascending.
        var observed = result.Signals.Select(s => s.Signal.ObservedAtUtc).ToList();
        Assert.Equal(observed.OrderBy(t => t).ToList(), observed);
    }

    [Fact]
    public void Collapse_IsDeterministic_AcrossRepeatedRuns()
    {
        var signals = new List<ScoringSignal>
        {
            SignalAt(Base.AddHours(6)),
            SignalAt(Base.AddHours(2)),
            SignalAt(Base.AddDays(1), SignalType.CustomerWin),
            SignalAt(Base.AddDays(2)),
            SignalAt(Base.AddDays(20)),
        };
        var collapse = Collapse();

        var first = collapse.Collapse(signals);
        var second = collapse.Collapse(signals);

        Assert.Equal(
            first.Signals.Select(s => s.Signal.Id).ToList(),
            second.Signals.Select(s => s.Signal.Id).ToList());
        Assert.Equal(
            first.CollapsedCounts.OrderBy(kv => kv.Key).ToList(),
            second.CollapsedCounts.OrderBy(kv => kv.Key).ToList());
    }

    [Fact]
    public void EmptyInput_IsNoOp()
    {
        var result = Collapse().Collapse(new List<ScoringSignal>());

        Assert.Empty(result.Signals);
        Assert.Empty(result.CollapsedCounts);
    }

    [Fact]
    public void SingleMediaSignal_IsNoOp_WithNoCounts()
    {
        var only = SignalAt(Base);

        var result = Collapse().Collapse(new List<ScoringSignal> { only });

        var passed = Assert.Single(result.Signals);
        Assert.Equal(only.Signal.Id, passed.Signal.Id);
        Assert.Empty(result.CollapsedCounts);
    }
}
