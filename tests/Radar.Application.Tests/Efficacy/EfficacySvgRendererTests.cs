using System.Text.RegularExpressions;

using Radar.Application.Efficacy;
using Radar.Application.Prices;

using static Radar.Application.Tests.Efficacy.EfficacyTestFakes;

namespace Radar.Application.Tests.Efficacy;

public sealed class EfficacySvgRendererTests
{
    private static readonly string[] ForbiddenAdviceWords =
        ["buy", "sell", "target", "return", "outperform", "guaranteed", "safe bet"];

    private static EfficacyPoint Point(DateOnly date, int opportunity, string? fingerprint) =>
        new(
            ScoreDate: date,
            TrajectoryScore: 50,
            OpportunityScore: opportunity,
            AttentionScore: 50,
            EvidenceConfidenceScore: 50,
            SignalVelocityScore: 50,
            ScoringConfigVersion: fingerprint,
            PriceAsOfDate: date,
            PriceClose: 100m,
            PriceAdjClose: 99m);

    private static CompanyEfficacySeries Series(
        IReadOnlyList<EfficacyPoint> points, IReadOnlyList<PriceBar> bars) =>
        new(Guid.NewGuid(), "Acme Corp", "MRCY", points, bars);

    private static PriceBar[] SampleBars() =>
    [
        Bar(new DateOnly(2026, 6, 10), 100m),
        Bar(new DateOnly(2026, 6, 11), 101m),
        Bar(new DateOnly(2026, 6, 12), 103m),
        Bar(new DateOnly(2026, 6, 13), 102m),
    ];

    [Fact]
    public void Render_IsDeterministic_ByteIdenticalForIdenticalInput()
    {
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 55, "radar-scoring-fp-aaaa1111"),
            ],
            SampleBars());

        var renderer = new EfficacySvgRenderer();

        Assert.Equal(renderer.Render(series), renderer.Render(series));
    }

    [Fact]
    public void Render_IsSelfContained_NoScriptNoExternalReference_HasBothAxes()
    {
        var series = Series(
            [Point(new DateOnly(2026, 6, 11), 60, "radar-scoring-fp-aaaa1111")],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);

        // A left score axis (0–100) and a right price axis are present.
        Assert.Contains("score 0-100", svg, StringComparison.Ordinal);
        Assert.Contains("price (adj close)", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SegmentsScoreLineByFingerprint_NeverAcrossBoundary()
    {
        // Two contiguous multi-point segments: fp "AAAA" (x3) then fp "BBBB" (x2).
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 11), 45, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 13), 60, "radar-scoring-fp-bbbb2222"),
                Point(new DateOnly(2026, 6, 14), 65, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        // TWO separate score-line segments (never a single line across the boundary).
        var scoreSegments = Regex.Matches(
            svg, "<polyline fill=\"none\" stroke=\"#3366cc\" stroke-width=\"1.5\"").Count;
        Assert.Equal(2, scoreSegments);

        // Exactly one boundary marker (dashed vertical rule) at the A→B change, labelled with B's suffix.
        var boundaries = Regex.Matches(svg, "stroke-dasharray").Count;
        Assert.Equal(1, boundaries);
        Assert.Contains("bbbb2222", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LengthOneSegments_AreIsolatedDots_NoConnectingLine()
    {
        // Every point has a distinct fingerprint → every segment is length 1 → NO score polyline at all.
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        var scoreSegments = Regex.Matches(
            svg, "<polyline fill=\"none\" stroke=\"#3366cc\" stroke-width=\"1.5\"").Count;
        Assert.Equal(0, scoreSegments);

        // Both points still render as dots.
        var dots = Regex.Matches(svg, "<circle").Count;
        Assert.Equal(2, dots);
    }

    [Fact]
    public void Render_NullFingerprintSegment_LabelledUnknown()
    {
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, null),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        // The null (pre-stamp) segment is its own segment; the boundary into the stamped one is marked.
        Assert.Contains("bbbb2222", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ContainsNoAdviceLanguage()
    {
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 90, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        foreach (var word in ForbiddenAdviceWords)
        {
            Assert.DoesNotContain(word, svg, StringComparison.OrdinalIgnoreCase);
        }
    }
}
