using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 183's central computation: hand-computed excess arithmetic, the proportion+floor coverage rule with
/// full provenance, self-exclusion, outside-universe targets, member-level spec-152 window reuse (poison
/// bars, tolerance), and the once-per-(universe, D, horizon, tolerance) sharing guarantee.
/// </summary>
public sealed class UniverseBenchmarkTests
{
    private static readonly DateOnly AsOf = new(2026, 3, 2);
    private static readonly DateTimeOffset FrozenAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Horizon = 21;
    private const int Tolerance = 4;

    /// <summary>Two bars — entry at D+1, exit at D+21 — so the return is exactly (exit − entry) / entry.</summary>
    private static IReadOnlyList<PriceBar> TwoBar(decimal entry, decimal exit) =>
    [
        new(AsOf.AddDays(1), entry, entry, entry, entry, entry, 1000),
        new(AsOf.AddDays(Horizon), exit, exit, exit, exit, exit, 1000),
    ];

    private static readonly Guid Target = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PeerC = new("aaaaaaaa-0000-0000-0000-000000000004");

    /// <summary>
    /// 44 members: the target (+10%), three peers at +5% / −5% / +10%, and 40 flat peers (0%). Eligible
    /// peers 43, required max(40, ceil(0.9 × 43) = 39) = 40, resolved 43 — usable.
    /// </summary>
    private static UniverseBenchmark HandComputedUniverse()
    {
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>
        {
            (Target, "TGT", TwoBar(100m, 110m)),
            (new Guid("aaaaaaaa-0000-0000-0000-000000000002"), "PA", TwoBar(100m, 105m)),
            (new Guid("aaaaaaaa-0000-0000-0000-000000000003"), "PB", TwoBar(100m, 95m)),
            (PeerC, "PC", TwoBar(200m, 220m)),
        };
        for (var p = 0; p < 40; p++)
        {
            members.Add((BenchmarkTestUniverse.PeerId(p), $"FL{p:D2}", TwoBar(100m, 100m)));
        }

        return BenchmarkTestUniverse.Of("benchmark-universe-v1", FrozenAt, members);
    }

    [Fact]
    public void TryExcess_HandComputedFixture_SelfExcludedEqualWeightMean()
    {
        var benchmark = HandComputedUniverse();

        var result = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.True(result.IsDefined);
        Assert.Equal(BenchmarkExcessUnavailableReason.None, result.Reason);
        Assert.Equal(43, result.EligiblePeers);
        Assert.Equal(43, result.ResolvedPeers);
        Assert.Equal(40, result.RequiredResolvedPeers);

        // Hand-computed: peers are +0.05, −0.05, +0.10 and forty 0s ⇒ mean = 0.10 / 43 (self excluded).
        Assert.Equal(0.10 / 43, result.PeerMeanForwardReturn, 12);
        Assert.Equal(0.10 - (0.10 / 43), result.Excess, 12);
    }

    [Fact]
    public void TryExcess_SelfExclusionIsPerTarget_NotAGlobalMean()
    {
        var benchmark = HandComputedUniverse();

        // Peer C (+10%) excludes ITSELF: its peers are the target (+10%), +5%, −5% and forty 0s.
        var forPeerC = benchmark.TryExcess(PeerC, 0.10, AsOf, Horizon, Tolerance);
        var forTarget = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.True(forPeerC.IsDefined);
        Assert.Equal((0.10 + 0.05 - 0.05) / 43, forPeerC.PeerMeanForwardReturn, 12);

        // Same raw return, same pond — but each mean excludes its OWN return, so the two are computed over
        // different peer sets (here numerically equal sums by construction; the sets differ by identity).
        Assert.Equal(43, forPeerC.ResolvedPeers);
        Assert.Equal(43, forTarget.ResolvedPeers);
    }

    [Fact]
    public void TryExcess_TargetOutsideTheFrozenUniverse_IsNotInBenchmarkUniverse()
    {
        var benchmark = HandComputedUniverse();

        var result = benchmark.TryExcess(
            new Guid("bbbbbbbb-0000-0000-0000-000000000099"), 0.10, AsOf, Horizon, Tolerance);

        Assert.False(result.IsDefined);
        Assert.Equal(BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse, result.Reason);
    }

    [Fact]
    public void TryExcess_ThirtyNineMembers_FailsTheFloorEvenAtFullResolution()
    {
        // 39 members, every one resolving: eligible peers 38 < the 40 floor, so the coverage rule can NEVER
        // pass — a 39-member pond is a different pond, and no excess is computed from it.
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>();
        for (var m = 0; m < 39; m++)
        {
            members.Add((BenchmarkTestUniverse.PeerId(m), $"M{m:D2}", TwoBar(100m, 101m)));
        }

        var benchmark = BenchmarkTestUniverse.Of("benchmark-universe-v1", FrozenAt, members);

        var result = benchmark.TryExcess(
            BenchmarkTestUniverse.PeerId(0), 0.01, AsOf, Horizon, Tolerance);

        Assert.False(result.IsDefined);
        Assert.Equal(BenchmarkExcessUnavailableReason.BenchmarkUnavailable, result.Reason);
        Assert.Equal(38, result.EligiblePeers);
        Assert.Equal(38, result.ResolvedPeers);
        Assert.Equal(40, result.RequiredResolvedPeers);
    }

    [Fact]
    public void TryExcess_EightyNinePercentResolved_FailsTheProportion_WithFullProvenance()
    {
        // 100 members; the target plus 88 resolving peers and 11 with no price at all. Eligible 99,
        // required max(40, ceil(0.9 × 99) = 90) = 90, resolved 88 < 90 ⇒ BenchmarkUnavailable — and the
        // unresolved members stay in the denominator, each with its named reason.
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)> { (Target, "TGT", TwoBar(100m, 110m)) };
        for (var p = 0; p < 88; p++)
        {
            members.Add((BenchmarkTestUniverse.PeerId(p), $"R{p:D2}", TwoBar(100m, 101m)));
        }

        for (var p = 88; p < 99; p++)
        {
            members.Add((BenchmarkTestUniverse.PeerId(p), $"U{p:D2}", []));
        }

        var benchmark = BenchmarkTestUniverse.Of("benchmark-universe-v1", FrozenAt, members);

        var result = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.False(result.IsDefined);
        Assert.Equal(BenchmarkExcessUnavailableReason.BenchmarkUnavailable, result.Reason);
        Assert.Equal(99, result.EligiblePeers);
        Assert.Equal(88, result.ResolvedPeers);
        Assert.Equal(90, result.RequiredResolvedPeers);

        // The per-day provenance lists every unresolved member with its spec-152 reason.
        var day = benchmark.DayAt(AsOf, Horizon, Tolerance);
        Assert.Equal(100, day.MemberCount);
        Assert.Equal(89, day.ResolvedCount);
        Assert.Equal(11, day.Unresolved.Count);
        Assert.All(day.Unresolved, m => Assert.Equal(ForwardReturnUnavailableReason.NoForwardBar, m.Reason));
    }

    [Fact]
    public void RequiredResolvedPeers_UsesIntegerCeiling_SoAMultipleOfTenDoesNotRoundUp()
    {
        // 0.90 is not exactly representable in binary: 0.90 × 70 computes slightly ABOVE 63.0 and
        // Math.Ceiling would answer 64 — silently requiring one more peer than the declared rule. The
        // integer form cannot.
        Assert.Equal(63, UniverseBenchmark.RequiredResolvedPeers(70));
        Assert.Equal(66, UniverseBenchmark.RequiredResolvedPeers(73));
        Assert.Equal(43, UniverseBenchmark.RequiredResolvedPeers(47));
        Assert.Equal(40, UniverseBenchmark.RequiredResolvedPeers(10));   // the floor binds
        Assert.Equal(40, UniverseBenchmark.RequiredResolvedPeers(0));
    }

    [Fact]
    public void MemberResolution_NeverReadsABarAtOrBeforeD_PoisonBarsChangeNothing()
    {
        // The spec-152 poison-bar guarantee at the MEMBER level: two universes identical after D but with
        // wildly different at-or-before-D bars must produce byte-identical excess values, because member
        // windows resolve through the same ForwardReturn.TryCompute admission (bar.Date > D).
        static UniverseBenchmark WithPoison(decimal poisonPrice)
        {
            var members = new List<(Guid, string, IReadOnlyList<PriceBar>)> { (Target, "TGT", TwoBar(100m, 110m)) };
            for (var p = 0; p < 43; p++)
            {
                var bars = new List<PriceBar>
                {
                    // Poison: at D and before D, at a price that would wreck the mean if ever read.
                    new(AsOf.AddDays(-3), poisonPrice, poisonPrice, poisonPrice, poisonPrice, poisonPrice, 1),
                    new(AsOf, poisonPrice, poisonPrice, poisonPrice, poisonPrice, poisonPrice, 1),
                };
                bars.AddRange(TwoBar(100m, 102m));
                members.Add((BenchmarkTestUniverse.PeerId(p), $"P{p:D2}", bars));
            }

            return BenchmarkTestUniverse.Of("benchmark-universe-v1", FrozenAt, members);
        }

        var quiet = WithPoison(100m).TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);
        var poisoned = WithPoison(0.0001m).TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.True(quiet.IsDefined);
        Assert.Equal(quiet, poisoned);
    }

    [Fact]
    public void MemberWithAPartialWindow_IsOmittedFromTheMean_AndRecordedWithItsReason()
    {
        // 45 members: target + 43 full peers + ONE whose bars stop at D+10 (short of D+17 at tolerance 4).
        // That member is PartialWindow: omitted from the mean, kept in the denominator, reason recorded.
        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)> { (Target, "TGT", TwoBar(100m, 110m)) };
        for (var p = 0; p < 43; p++)
        {
            members.Add((BenchmarkTestUniverse.PeerId(p), $"P{p:D2}", TwoBar(100m, 100m)));
        }

        var partial = new Guid("cccccccc-0000-0000-0000-000000000001");
        members.Add((partial, "PART",
        [
            new(AsOf.AddDays(1), 100m, 100m, 100m, 100m, 100m, 1000),
            new(AsOf.AddDays(10), 500m, 500m, 500m, 500m, 500m, 1000),   // +400% — must never enter the mean
        ]));

        var benchmark = BenchmarkTestUniverse.Of("benchmark-universe-v1", FrozenAt, members);

        var result = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.True(result.IsDefined);
        Assert.Equal(44, result.EligiblePeers);                          // the partial member stays eligible
        Assert.Equal(43, result.ResolvedPeers);                          // …but does not resolve
        Assert.Equal(0.0, result.PeerMeanForwardReturn, 12);             // 43 flat peers; +400% never entered
        Assert.Equal(0.10, result.Excess, 12);

        var day = benchmark.DayAt(AsOf, Horizon, Tolerance);
        var recorded = Assert.Single(day.Unresolved);
        Assert.Equal("PART", recorded.Ticker);
        Assert.Equal(ForwardReturnUnavailableReason.PartialWindow, recorded.Reason);
    }

    [Fact]
    public void DayAt_IsComputedOncePerKey_AndSharedByEveryConsumer()
    {
        var benchmark = HandComputedUniverse();

        var first = benchmark.DayAt(AsOf, Horizon, Tolerance);
        var second = benchmark.DayAt(AsOf, Horizon, Tolerance);
        var differentKey = benchmark.DayAt(AsOf.AddDays(1), Horizon, Tolerance);

        Assert.Same(first, second);                                      // one computation per key
        Assert.NotSame(first, differentKey);
    }

    [Fact]
    public void SeedEdits_CannotMoveTheBenchmark_OnlyTheFrozenArtifactIsAnInput()
    {
        // "Adding a company to companies.json changes NO benchmark value": membership is an input ONLY via
        // the frozen artifact. A company that exists outside the artifact is simply not a member —
        // NotInBenchmarkUniverse for itself, invisible to every member's mean.
        var benchmark = HandComputedUniverse();
        var before = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        var stranger = new Guid("dddddddd-0000-0000-0000-000000000001");
        var strangerResult = benchmark.TryExcess(stranger, 0.42, AsOf, Horizon, Tolerance);
        var after = benchmark.TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);

        Assert.Equal(BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse, strangerResult.Reason);
        Assert.Equal(before, after);

        // …and reruns over identical inputs are byte-deterministic (AD-3): a freshly built, identical
        // universe reproduces the value exactly.
        var rebuilt = HandComputedUniverse().TryExcess(Target, 0.10, AsOf, Horizon, Tolerance);
        Assert.Equal(before, rebuilt);
    }
}
