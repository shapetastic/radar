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

    // Independently set trajectory and opportunity so a component-scoped test can hold one flat while the
    // other moves across a fingerprint boundary.
    private static EfficacyPoint PointTO(
        DateOnly date, int trajectory, int opportunity, string? fingerprint) =>
        new(
            ScoreDate: date,
            TrajectoryScore: trajectory,
            OpportunityScore: opportunity,
            AttentionScore: 50,
            EvidenceConfidenceScore: 50,
            SignalVelocityScore: 50,
            ScoringConfigVersion: fingerprint,
            PriceAsOfDate: date,
            PriceClose: 100m,
            PriceAdjClose: 99m);

    private static int ScorePolylineCount(string svg) =>
        Regex.Matches(svg, "<polyline fill=\"none\" stroke=\"#3366cc\" stroke-width=\"1.5\"").Count;

    private static int BoundaryTickCount(string svg) => Regex.Matches(svg, "stroke-dasharray").Count;

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
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);

        // The root carries the standard SVG namespace (required for standalone .svg viewers); it must be the
        // ONLY URI in the document — no other external reference is allowed.
        Assert.Contains($"<svg xmlns=\"{EfficacySvgRenderer.SvgNamespace}\"", svg, StringComparison.Ordinal);
        var withoutNamespace = svg.Replace(EfficacySvgRenderer.SvgNamespace, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("http", withoutNamespace, StringComparison.OrdinalIgnoreCase);

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
    public void Render_CosmeticReStamp_SameValueAcrossBoundary_IsBridged_ButStillTicked()
    {
        // Three points, all OpportunityScore 50; the fingerprint re-stamps at the middle point (aaaa→bbbb).
        // The plotted value is unchanged across the boundary => a cosmetic re-stamp: the line bridges it.
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 50, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 11), 50, "radar-scoring-fp-bbbb2222"),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        // ONE continuous score polyline spanning all three points (no break at the cosmetic boundary).
        Assert.Equal(1, ScorePolylineCount(svg));

        // Provenance preserved: the dashed config-change tick + fingerprint label is STILL drawn at the boundary.
        Assert.Equal(1, BoundaryTickCount(svg));
        Assert.Contains("bbbb2222", svg, StringComparison.Ordinal);

        // Paint order (SVG paints in document order): the dashed tick markup must appear AFTER the spanning
        // score polyline so the provenance marker paints on top of the bridged line, not under it. The bridged
        // tick is buffered and flushed only when the spanning run closes — pin that layering here.
        var polylineIndex = svg.IndexOf(
            "<polyline fill=\"none\" stroke=\"#3366cc\" stroke-width=\"1.5\"", StringComparison.Ordinal);
        var tickIndex = svg.IndexOf("stroke-dasharray", StringComparison.Ordinal);
        Assert.True(polylineIndex >= 0 && tickIndex > polylineIndex,
            "Bridged-boundary tick must be emitted after the spanning score polyline (tick paints on top).");
    }

    [Fact]
    public void Render_RealScoreChangeAcrossBoundary_StillBreaks_AndTicks()
    {
        // Different fingerprint AND different plotted value => a real score change: the line must still break.
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 60, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        // No connecting line across the boundary (both runs are length 1) — today's behaviour, unchanged.
        Assert.Equal(0, ScorePolylineCount(svg));

        // The config-change tick is still present at the boundary.
        Assert.Equal(1, BoundaryTickCount(svg));
        Assert.Contains("bbbb2222", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SameFingerprintRun_StillConnects_NoSpuriousTick()
    {
        // Regression: the added "same value" clause must not drop existing same-fingerprint connections, and a
        // run with no fingerprint change must emit no config tick.
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 40, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 11), 45, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-aaaa1111"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        Assert.Equal(1, ScorePolylineCount(svg));
        Assert.Equal(0, BoundaryTickCount(svg));
    }

    [Fact]
    public void Render_EqualValueSameFingerprint_OnePolyline_NoTick()
    {
        // Equal value AND equal fingerprint: connected via the fingerprint clause; the OR must not double-mark.
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 50, "radar-scoring-fp-aaaa1111"),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-aaaa1111"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        Assert.Equal(1, ScorePolylineCount(svg));
        Assert.Equal(0, BoundaryTickCount(svg));
    }

    [Fact]
    public void Render_NonDefaultComponent_BridgeFollowsSelectedComponent()
    {
        // TrajectoryScore is flat (50) across the boundary while OpportunityScore moves (40→70). A renderer
        // plotting Trajectory must bridge (value unchanged for the plotted component); a renderer plotting
        // Opportunity must break (value moved) — proving the rule is component-scoped.
        var points = new[]
        {
            PointTO(new DateOnly(2026, 6, 10), trajectory: 50, opportunity: 40, "radar-scoring-fp-aaaa1111"),
            PointTO(new DateOnly(2026, 6, 12), trajectory: 50, opportunity: 70, "radar-scoring-fp-bbbb2222"),
        };
        var series = Series(points, SampleBars());

        var trajectorySvg = new EfficacySvgRenderer { Component = EfficacyScoreComponent.Trajectory }.Render(series);
        var opportunitySvg = new EfficacySvgRenderer { Component = EfficacyScoreComponent.Opportunity }.Render(series);

        // Trajectory: flat value => bridged into one polyline; tick still drawn at the fingerprint boundary.
        Assert.Equal(1, ScorePolylineCount(trajectorySvg));
        Assert.Equal(1, BoundaryTickCount(trajectorySvg));

        // Opportunity: value moved => the line breaks (no connecting polyline).
        Assert.Equal(0, ScorePolylineCount(opportunitySvg));
        Assert.Equal(1, BoundaryTickCount(opportunitySvg));
    }

    [Fact]
    public void Render_NullToStampedFingerprint_EqualValue_IsBridged_AndTicked()
    {
        // null (pre-stamp) => stamped transition with an equal plotted value bridges the line but still ticks
        // the boundary; the null side's ShortFingerprint stays "unknown".
        var series = Series(
            [
                Point(new DateOnly(2026, 6, 10), 50, null),
                Point(new DateOnly(2026, 6, 12), 50, "radar-scoring-fp-bbbb2222"),
            ],
            SampleBars());

        var svg = new EfficacySvgRenderer().Render(series);

        Assert.Equal(1, ScorePolylineCount(svg));
        Assert.Equal(1, BoundaryTickCount(svg));
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
