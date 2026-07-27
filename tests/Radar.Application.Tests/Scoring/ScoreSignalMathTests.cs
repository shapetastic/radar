using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 146 — the per-signal primitives EXTRACTED from <c>radar-formula-v8</c> so <c>radar-formula-v9</c>
/// composes its channels from the same machinery instead of a second copy.
/// <para>
/// These are BIT-EXACTNESS guards, not behavioural tests: v8's own characterization tests already pin its
/// output, but they round to ints, so a one-ULP change from a re-associated expression could hide there and
/// only surface later at a midpoint. Each test below reproduces v8's ORIGINAL expression literally and
/// asserts the helper matches it exactly.
/// </para>
/// </summary>
public sealed class ScoreSignalMathTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

    private static ScoringSignal Signal(
        int strength, SignalDirection direction, decimal confidence, DateTimeOffset observedAt) =>
        new(
            new SignalBuilder()
                .WithStrength(strength)
                .WithDirection(direction)
                .WithConfidence(confidence)
                .WithObservedAtUtc(observedAt)
                .Build(),
            new EvidenceBuilder().Build());

    private static IReadOnlyList<ScoringSignal> Fixture() =>
    [
        Signal(7, SignalDirection.Positive, 0.83m, WindowStart.AddDays(3)),
        Signal(3, SignalDirection.Negative, 0.61m, WindowStart.AddDays(11)),
        Signal(9, SignalDirection.Positive, 0.77m, WindowStart.AddDays(19)),
        Signal(5, SignalDirection.Neutral, 0.55m, WindowStart.AddDays(27)),
    ];

    [Fact]
    public void RecencyFactors_ReproduceV8sOriginalExpression_Exactly()
    {
        var signals = Fixture();
        const double recencyFloor = 0.5;

        var actual = ScoreSignalMath.RecencyFactors(signals, WindowStart, WindowEnd, recencyFloor);

        // v8's original loop, verbatim.
        var windowLength = WindowEnd - WindowStart;
        var expected = new double[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            var age = (WindowEnd - signals[i].Signal.ObservedAtUtc).TotalSeconds / windowLength.TotalSeconds;
            age = Math.Clamp(age, 0, 1);
            expected[i] = 1 - recencyFloor * age;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RecencyFactors_NonPositiveWindow_DegradeToOne()
    {
        // v8's divide-by-zero guard, preserved: age 0 ⇒ recency 1.0 for every signal.
        var actual = ScoreSignalMath.RecencyFactors(Fixture(), WindowEnd, WindowEnd, 0.5);

        Assert.All(actual, r => Assert.Equal(1.0, r));
    }

    [Fact]
    public void DirectionalMasses_AndPreponderance_ReproduceV8sOriginalExpressions_Exactly()
    {
        var signals = Fixture();
        var weights = new ScoringWeights();
        var recency = ScoreSignalMath.RecencyFactors(
            signals, WindowStart, WindowEnd, weights.RecencyFloor);

        // v8's original mass loop, verbatim (no quality factor — v8's trajectory weight is
        // confidence·recency alone).
        var mPos = 0.0;
        var mNeg = 0.0;
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var sign = signal.Direction switch
            {
                SignalDirection.Positive => +1,
                SignalDirection.Negative => -1,
                _ => 0,
            };

            if (sign == 0)
            {
                continue;
            }

            var w = (double)signal.Confidence * recency[i];
            var mass = signal.Strength * w;
            if (sign > 0)
            {
                mPos += mass;
            }
            else
            {
                mNeg += mass;
            }
        }

        var actual = ScoreSignalMath.DirectionalMasses(signals, recency);

        Assert.Equal(mPos, actual.Positive);
        Assert.Equal(mNeg, actual.Negative);
        Assert.Equal(mPos + mNeg, actual.Total);

        // THE ULP GUARD. v8 computes `TrajectoryBand * (mPos - mNeg) / (sumMass + k)`, which associates as
        // `(band * (p - n)) / (t + k)`. Passing the band as a PARAMETER preserves that association; had the
        // helper returned a raw ratio for the caller to scale, the result could differ by an ULP — enough to
        // flip an AwayFromZero midpoint in Score().
        var sumMass = mPos + mNeg;
        var expected = sumMass <= 0
            ? 0
            : 10.0 * (mPos - mNeg) / (sumMass + weights.TrajectoryCorroborationK);

        Assert.Equal(expected, ScoreSignalMath.Preponderance(actual, weights.TrajectoryCorroborationK, 10.0));
    }

    [Fact]
    public void Preponderance_KeepsV8sAssociation_OnInputsWhereTheAlternativeDiffersByAnUlp()
    {
        // THE MUTATION-PROOF FORM of the ULP guard. The masses below were found by search: they are inputs
        // where `(band·(p−n))/(t+k)` — v8's association, which passing the band as a PARAMETER preserves —
        // and `band·((p−n)/(t+k))` — the association a "return the raw ratio and let the caller scale it"
        // refactor would produce — differ in the last bit. Rewriting Preponderance that way fails here, which
        // v8's own int-rounded characterization tests would not reliably catch.
        const double positive = 39.056068382391224;
        const double negative = 4.346177200052566;
        const double k = 10.0;
        const double band = 10.0;

        var total = positive + negative;
        var v8Association = band * (positive - negative) / (total + k);
        var refactoredAssociation = band * ((positive - negative) / (total + k));

        // The two really do differ on this input — if this ever stops holding, the guard below is vacuous
        // and needs new masses.
        Assert.NotEqual(v8Association, refactoredAssociation);

        Assert.Equal(
            v8Association,
            ScoreSignalMath.Preponderance(new DirectionalMass(positive, negative), k, band));
    }

    [Fact]
    public void Preponderance_WithNoDirectionalMass_IsExactlyZero()
    {
        Assert.Equal(0.0, ScoreSignalMath.Preponderance(new DirectionalMass(0, 0), 10.0, 10.0));
    }

    [Theory]
    [InlineData(EvidenceQuality.PrimarySource, 1.00)]
    [InlineData(EvidenceQuality.High, 0.85)]
    [InlineData(EvidenceQuality.Medium, 0.60)]
    [InlineData(EvidenceQuality.Low, 0.35)]
    [InlineData(EvidenceQuality.Unknown, 0.40)]
    public void QualityWeight_MapsEachQualityToItsConfiguredMagnitude(EvidenceQuality quality, double expected)
    {
        Assert.Equal(expected, ScoreSignalMath.QualityWeight(new ScoringWeights(), quality));
    }

    [Theory]
    [InlineData(-5.0, 0)]
    [InlineData(0.4, 0)]
    [InlineData(0.5, 1)]      // AwayFromZero, not banker's rounding
    [InlineData(49.5, 50)]
    [InlineData(100.4, 100)]
    [InlineData(1000.0, 100)]
    public void Clamp0To100_RoundsAwayFromZero_AndClamps(double value, int expected)
    {
        Assert.Equal(expected, ScoreSignalMath.Clamp0To100(value));
    }

    [Fact]
    public void AttentionReach_ReproducesV8sTermExactly_IncludingTheCollapsedBreadthCredit()
    {
        var weights = new ScoringWeights();
        IAttentionSourceWeights sourceWeights = new TieredWeights();

        ScoringSignal Publisher(string name, EvidenceSourceType type, SignalType signalType) => new(
            new SignalBuilder().WithType(signalType).Build(),
            new EvidenceBuilder().WithSourceType(type).WithSourceName(name).Build());

        var survivors = new[]
        {
            Publisher("genuine-reuters", EvidenceSourceType.NewsArticle, SignalType.MediaAttention),
            Publisher("mill-zacks", EvidenceSourceType.NewsArticle, SignalType.MediaAttention),
            // First-party: not market attention, so it must not add breadth.
            Publisher("Acme Newsroom", EvidenceSourceType.PressRelease, SignalType.CustomerWin),
        };
        var preCollapse = survivors.Concat(
        [
            Publisher("genuine-bloomberg", EvidenceSourceType.NewsArticle, SignalType.MediaAttention),
            // Already a survivor: must not be credited twice.
            Publisher("genuine-reuters", EvidenceSourceType.NewsArticle, SignalType.MediaAttention),
        ]).ToArray();

        // survivors: genuine 1.0 + mill 0.1 = 1.1; collapsed-away extra: genuine-bloomberg 1.0;
        // mediaCount (post-collapse) = 2 × MediaReachWeight 0.10 = 0.2.
        var expected = (1.0 + 0.1) + (weights.CollapsedBreadthCredit * 1.0) + (weights.MediaReachWeight * 2);

        Assert.Equal(
            expected,
            ScoreSignalMath.AttentionReach(survivors, preCollapse, weights, sourceWeights),
            12);

        // An empty pre-collapse set drops the credit entirely — the radar-formula-v7-equivalent reach.
        Assert.Equal(
            1.1 + (weights.MediaReachWeight * 2),
            ScoreSignalMath.AttentionReach(survivors, [], weights, sourceWeights),
            12);
    }

    [Fact]
    public void ContributesToReach_IsTrueForThirdPartyPublishersAndMediaSignals_Only()
    {
        ScoringSignal Build(EvidenceSourceType type, string name, SignalType signalType) => new(
            new SignalBuilder().WithType(signalType).Build(),
            new EvidenceBuilder().WithSourceType(type).WithSourceName(name).Build());

        Assert.True(ScoreSignalMath.ContributesToReach(
            Build(EvidenceSourceType.NewsArticle, "reuters", SignalType.CustomerWin)));
        Assert.True(ScoreSignalMath.ContributesToReach(
            Build(EvidenceSourceType.PressRelease, "Acme", SignalType.MediaAttention)));
        Assert.False(ScoreSignalMath.ContributesToReach(
            Build(EvidenceSourceType.PressRelease, "Acme", SignalType.CustomerWin)));
        // A third-party source with no publisher name names nobody, so it adds no breadth.
        Assert.False(ScoreSignalMath.ContributesToReach(
            Build(EvidenceSourceType.NewsArticle, "  ", SignalType.CustomerWin)));
    }

    private sealed class TieredWeights : IAttentionSourceWeights
    {
        public double WeightFor(string? sourceName) => sourceName switch
        {
            null => 0.5,
            var n when n.StartsWith("mill", StringComparison.OrdinalIgnoreCase) => 0.1,
            var n when n.StartsWith("genuine", StringComparison.OrdinalIgnoreCase) => 1.0,
            _ => 0.5,
        };

        public string CanonicalDescriptor() => "test-tiered";
    }
}
