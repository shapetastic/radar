using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 158 §4 — the two PROSPECTIVE v11 primitives extracted into <see cref="ScoreSignalMath"/>
/// (<c>DirectionalActivityMass</c>, <c>PositiveAttentionReach</c>) and the breadth-reach delegate seam
/// added to <see cref="ScoringChannelComposition.Compose"/>. No shipped formula consumes these yet; the
/// tests pin their SEMANTICS (spec 157 §1/§3's normative rules) so the eventual v11 and the spec-158 audit
/// share one verified definition:
/// <list type="bullet">
/// <item>directional activity is exactly <c>DirectionalMasses(...).Total</c> — Neutral contributes 0;</item>
/// <item>positive-only breadth filters <c>Direction == Positive</c> from BOTH input sets BEFORE the
/// existing reach terms; publisher inclusion stays BINARY and DISTINCT; Negative is excluded alongside
/// Neutral, from the media-count term too;</item>
/// <item>the delegate seam is byte-identical for the shipped formulas: passing
/// <see cref="ScoreSignalMath.AttentionReach"/> reproduces the exact breadth score the pass previously
/// computed inline (the v8/v9/v10 golden pins additionally hold unmodified).</item>
/// </list>
/// </summary>
public sealed class ProspectiveV11ScoringPrimitivesTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);

    private sealed class FuncWeights(Func<string?, double> fn) : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(fn(sourceName), sourceName ?? string.Empty);
        public string CanonicalDescriptor() => "test-func-weights";
    }

    private static readonly IAttentionSourceWeights AllGenuine = new FuncWeights(_ => 1.0);

    private static ScoringSignal Signal(
        SignalDirection direction,
        SignalType type = SignalType.ProductLaunch,
        EvidenceSourceType sourceType = EvidenceSourceType.PressRelease,
        string sourceName = "Company IR",
        int strength = 5,
        decimal confidence = 0.8m,
        DateTimeOffset? observedAt = null) =>
        new(
            new SignalBuilder()
                .WithDirection(direction)
                .WithType(type)
                .WithStrength(strength)
                .WithConfidence(confidence)
                .WithObservedAtUtc(observedAt ?? WindowStart.AddDays(10))
                .Build(),
            new EvidenceBuilder()
                .WithSourceType(sourceType)
                .WithSourceName(sourceName)
                .WithQuality(EvidenceQuality.High)
                .Build());

    private static ScoringSignal NewsSignal(
        SignalDirection direction, string publisher, SignalType type = SignalType.MediaAttention) =>
        Signal(direction, type, EvidenceSourceType.NewsArticle, publisher);

    // ---------------------------------------------------------------------------------------------
    // DirectionalActivityMass
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DirectionalActivityMass_IsExactly_DirectionalMassesTotal()
    {
        var signals = new List<ScoringSignal>
        {
            Signal(SignalDirection.Positive, strength: 7),
            Signal(SignalDirection.Negative, strength: 3),
            Signal(SignalDirection.Neutral, strength: 9),
            Signal(SignalDirection.Mixed, strength: 4),
        };
        var weights = new ScoringWeights();
        var recency = ScoreSignalMath.RecencyFactors(signals, WindowStart, WindowEnd, weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, weights);

        var actual = ScoreSignalMath.DirectionalActivityMass(signals, recency, quality);

        // The spec fixes the definition verbatim: exactly DirectionalMasses(...).Total — no other value,
        // and bit-equal (no re-associated arithmetic).
        Assert.Equal(ScoreSignalMath.DirectionalMasses(signals, recency, quality).Total, actual);
        Assert.True(actual > 0);
    }

    [Fact]
    public void DirectionalActivityMass_AllNeutral_IsExactlyZero_WhileActivityMassIsNot()
    {
        var signals = new List<ScoringSignal>
        {
            Signal(SignalDirection.Neutral, strength: 9),
            Signal(SignalDirection.Neutral, strength: 5),
            Signal(SignalDirection.Mixed, strength: 4),
        };
        var weights = new ScoringWeights();
        var recency = ScoreSignalMath.RecencyFactors(signals, WindowStart, WindowEnd, weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, weights);

        // The whole point of the v11 term: neutral volume cannot raise a saturation built on it, while the
        // v9/v10 ActivityMass DOES rise on the same set (the AD-16 property being corrected).
        Assert.Equal(0.0, ScoreSignalMath.DirectionalActivityMass(signals, recency, quality));
        Assert.True(ScoreSignalMath.ActivityMass(signals, recency, quality) > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // PositiveAttentionReach
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PositiveAttentionReach_EqualsAttentionReach_OverPositiveFilteredInputs()
    {
        var post = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
            NewsSignal(SignalDirection.Neutral, "Outlet B"),
            NewsSignal(SignalDirection.Negative, "Outlet C"),
            NewsSignal(SignalDirection.Positive, "Outlet D"),
        };
        var pre = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet E"),
            NewsSignal(SignalDirection.Neutral, "Outlet F"),
        };
        var weights = new ScoringWeights();

        var actual = ScoreSignalMath.PositiveAttentionReach(post, pre, weights, AllGenuine);

        // §3's rule stated as code: the filter is applied to the INPUTS; the reach terms are unchanged and
        // simply see a smaller set.
        var expected = ScoreSignalMath.AttentionReach(
            post.Where(s => s.Signal.Direction == SignalDirection.Positive).ToList(),
            pre.Where(s => s.Signal.Direction == SignalDirection.Positive).ToList(),
            weights,
            AllGenuine);

        Assert.Equal(expected, actual);
        Assert.True(actual > 0);
    }

    [Fact]
    public void PositiveAttentionReach_PublisherInclusion_IsBinaryAndDistinct()
    {
        var weights = new ScoringWeights();

        var onePositive = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
        };
        var threePositive = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
            NewsSignal(SignalDirection.Positive, "outlet a", SignalType.CustomerWin),
            NewsSignal(SignalDirection.Positive, "OUTLET A", SignalType.CustomerWin),
        };

        // A publisher qualifies on AT LEAST ONE Positive signal and is counted ONCE — several Positive
        // signals (case-insensitively same publisher) earn no extra publisher reach. Non-media types keep
        // the media-count term at 0 so this isolates the publisher term.
        Assert.Equal(
            ScoreSignalMath.PositiveAttentionReach(onePositive, [], weights, AllGenuine),
            ScoreSignalMath.PositiveAttentionReach(threePositive, [], weights, AllGenuine));
    }

    [Fact]
    public void PositiveAttentionReach_NegativeIsExcludedAlongsideNeutral_IncludingMediaCount()
    {
        var weights = new ScoringWeights();

        // A publisher carrying ONLY Negative signals earns nothing — from the publisher term or the media
        // term — and neither does a Neutral MediaAttention. Both sets therefore measure exactly zero.
        var negativeOnly = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Negative, "Bad News Daily"),
            NewsSignal(SignalDirection.Negative, "Bad News Daily", SignalType.CustomerWin),
        };
        var neutralOnly = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Neutral, "Wire Copy Weekly"),
        };

        Assert.Equal(0.0, ScoreSignalMath.PositiveAttentionReach(negativeOnly, [], weights, AllGenuine));
        Assert.Equal(0.0, ScoreSignalMath.PositiveAttentionReach(neutralOnly, [], weights, AllGenuine));

        // The FULL-set reach (which AttentionScore keeps consuming, unfiltered) is non-zero over the same
        // inputs — the two must not be conflated (spec 157 §3's boxed note).
        Assert.True(ScoreSignalMath.AttentionReach(negativeOnly, [], weights, AllGenuine) > 0);
        Assert.True(ScoreSignalMath.AttentionReach(neutralOnly, [], weights, AllGenuine) > 0);
    }

    [Fact]
    public void PositiveAttentionReach_NeutralMediaFromQualifyingPublisher_ContributesNothing()
    {
        var weights = new ScoringWeights();

        var withoutNeutral = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
        };
        var withNeutral = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
            // Same, already-qualifying publisher: its Neutral MediaAttention still adds NOTHING — not to
            // publisher reach (binary, already counted) and not to the media-count term (Neutral excluded).
            NewsSignal(SignalDirection.Neutral, "Outlet A"),
        };

        Assert.Equal(
            ScoreSignalMath.PositiveAttentionReach(withoutNeutral, [], weights, AllGenuine),
            ScoreSignalMath.PositiveAttentionReach(withNeutral, [], weights, AllGenuine));
    }

    [Fact]
    public void PositiveAttentionReach_PositiveMediaCount_IsCounted()
    {
        var weights = new ScoringWeights();

        var withoutMedia = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
        };
        var withPositiveMedia = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
            NewsSignal(SignalDirection.Positive, "Outlet A"),
        };

        // The media-count term is filtered, not removed: a POSITIVE MediaAttention signal still counts.
        Assert.Equal(
            ScoreSignalMath.PositiveAttentionReach(withoutMedia, [], weights, AllGenuine)
                + weights.MediaReachWeight,
            ScoreSignalMath.PositiveAttentionReach(withPositiveMedia, [], weights, AllGenuine));
    }

    [Fact]
    public void PositiveAttentionReach_PreCollapseSplit_CreditsPositiveOnly()
    {
        var weights = new ScoringWeights();
        var post = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Survivor Outlet"),
        };
        var pre = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Survivor Outlet"),
            NewsSignal(SignalDirection.Positive, "Collapsed Positive Outlet"),
            NewsSignal(SignalDirection.Neutral, "Collapsed Neutral Outlet"),
        };

        // Positive survivor publisher (1.0) + its own positive media signal + CollapsedBreadthCredit for the
        // pre-collapse-only POSITIVE publisher; the pre-collapse-only NEUTRAL publisher earns nothing.
        var expected = 1.0
            + weights.CollapsedBreadthCredit * 1.0
            + weights.MediaReachWeight * 1;

        Assert.Equal(expected, ScoreSignalMath.PositiveAttentionReach(post, pre, weights, AllGenuine));
    }

    [Fact]
    public void PositiveAttentionReach_FirstPartySources_NeverCountAsPublishers()
    {
        var weights = new ScoringWeights();

        // A first-party RSS press release (PressRelease) and a filing are NOT third-party publishers, even
        // when Positive — the §5 "does a first-party feed count" question, answered by the existing
        // IsBreadthPublisher whitelist the filtered term reuses unchanged.
        var firstPartyOnly = new List<ScoringSignal>
        {
            Signal(SignalDirection.Positive, SignalType.ProductLaunch,
                EvidenceSourceType.PressRelease, "Company Newsroom"),
            Signal(SignalDirection.Positive, SignalType.GuidanceChange,
                EvidenceSourceType.Filing, "SEC EDGAR"),
        };

        Assert.Equal(0.0, ScoreSignalMath.PositiveAttentionReach(firstPartyOnly, [], weights, AllGenuine));
    }

    // ---------------------------------------------------------------------------------------------
    // The Compose breadth-reach delegate seam
    // ---------------------------------------------------------------------------------------------

    private static ScoringInput InputOf(
        IReadOnlyList<ScoringSignal> signals, IReadOnlyList<ScoringSignal>? preCollapse = null) =>
        new(Guid.NewGuid(), WindowStart, WindowEnd, signals, [])
        {
            PreCollapseSignals = preCollapse ?? [],
        };

    private static ChannelComposition ComposeBreadth(
        ScoringInput input, BreadthChannelReach breadthReach, double saturation = 3.0)
    {
        var weights = new ScoringWeights();
        var recency = ScoreSignalMath.RecencyFactors(
            input.Signals, input.WindowStartUtc, input.WindowEndUtc, weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(input.Signals, weights);
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Breadth("breadth", 1.0, saturation)], "seam-test");

        return ScoringChannelComposition.Compose(
            input,
            recency,
            quality,
            channels,
            weights,
            AllGenuine,
            RecordedOnlyCollectorAttributionResolver.Instance,
            ScoreSignalMath.ActivityMass,
            (saturationValue, preponderance) => saturationValue * Math.Max(0.0, preponderance),
            breadthReach);
    }

    [Fact]
    public void ComposeSeam_WithAttentionReach_ReproducesTheInlineBreadthScoreExactly()
    {
        var signals = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Neutral, "Outlet A"),
            NewsSignal(SignalDirection.Positive, "Outlet B", SignalType.CustomerWin),
        };
        var pre = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Neutral, "Collapsed Outlet"),
        };
        var input = InputOf(signals, pre);
        var weights = new ScoringWeights();

        var composition = ComposeBreadth(input, ScoreSignalMath.AttentionReach);
        var breadth = Assert.Single(composition.Channels);

        // Byte-identity of the seam: routing the SAME static method through the delegate produces exactly
        // the expression the pass used to compute inline — Saturate(AttentionReach(...), S).
        var expectedReach = ScoreSignalMath.AttentionReach(signals, pre, weights, AllGenuine);
        Assert.Equal(ScoreSignalMath.Saturate(expectedReach, 3.0), breadth.Score);
        Assert.False(breadth.Dark);
    }

    [Fact]
    public void ComposeSeam_WithPositiveAttentionReach_NarrowsReach_AndDarkensWithoutPositives()
    {
        var weights = new ScoringWeights();

        // Mixed set: the positive-only reach is strictly smaller than the full reach, and the breadth score
        // saturates the NARROWED value.
        var mixed = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Positive, "Outlet A", SignalType.CustomerWin),
            NewsSignal(SignalDirection.Neutral, "Outlet B"),
        };
        var mixedComposition = ComposeBreadth(InputOf(mixed), ScoreSignalMath.PositiveAttentionReach);
        var mixedBreadth = Assert.Single(mixedComposition.Channels);
        var expectedPositiveReach = ScoreSignalMath.PositiveAttentionReach(mixed, [], weights, AllGenuine);
        Assert.Equal(ScoreSignalMath.Saturate(expectedPositiveReach, 3.0), mixedBreadth.Score);
        Assert.True(expectedPositiveReach < ScoreSignalMath.AttentionReach(mixed, [], weights, AllGenuine));

        // All-neutral set: positive-only reach is 0 ⇒ the channel is DARK under the narrowed measure (it has
        // nothing to measure), even though the full-set measure would not be.
        var neutralOnly = new List<ScoringSignal>
        {
            NewsSignal(SignalDirection.Neutral, "Outlet A"),
            NewsSignal(SignalDirection.Neutral, "Outlet B"),
        };
        var neutralComposition = ComposeBreadth(InputOf(neutralOnly), ScoreSignalMath.PositiveAttentionReach);
        var neutralBreadth = Assert.Single(neutralComposition.Channels);
        Assert.Equal(0.0, neutralBreadth.Score);
        Assert.True(neutralBreadth.Dark);
    }
}
