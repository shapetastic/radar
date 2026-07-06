using System.Globalization;
using System.Text;

using Radar.Application.Prices;

namespace Radar.Application.Efficacy;

/// <summary>
/// Pure, deterministic renderer of a per-company score-vs-price overlay as a <b>self-contained inline SVG</b>
/// (no <c>&lt;script&gt;</c>, no external assets, no web fonts, no remote references). A left Y-axis 0–100
/// carries the chosen numeric score component (default <see cref="EfficacyScoreComponent.Opportunity"/>); a
/// right Y-axis is scaled to the price min/max over the window; the dense price series is drawn as one
/// continuous polyline over adjusted close.
/// <para>
/// <b>Fingerprint segmentation (hard correctness requirement, AD-10).</b> The sparse score points are
/// partitioned into contiguous segments of equal <c>ScoringConfigVersion</c> (Ordinal equality; <c>null</c> is
/// its own segment). A connecting line is drawn ONLY within a segment — never across a fingerprint boundary,
/// because those scores came from different formulas/weights. Each boundary is marked with a thin dashed
/// vertical rule + the short fingerprint suffix. A length-1 segment renders as an isolated dot.
/// </para>
/// <para>
/// Determinism (AD-3): <see cref="CultureInfo.InvariantCulture"/> formatting, fixed coordinate precision, no
/// embedded wall-clock, stable element order — identical input yields byte-identical output. Framing stays
/// AD-9-clean: a score-vs-price overlay is a research statistic, never advice.
/// </para>
/// </summary>
public sealed class EfficacySvgRenderer
{
    // Fixed canvas (AD-3: no dynamic sizing that could vary output).
    private const int Width = 900;
    private const int Height = 340;
    private const double PlotLeft = 55;
    private const double PlotRight = 840;
    private const double PlotTop = 40;
    private const double PlotBottom = 285;

    private static double PlotWidth => PlotRight - PlotLeft;
    private static double PlotHeight => PlotBottom - PlotTop;

    /// <summary>Which numeric score component to plot. Defaults to the headline OpportunityScore.</summary>
    public EfficacyScoreComponent Component { get; init; } = EfficacyScoreComponent.Opportunity;

    public string Render(CompanyEfficacySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var points = series.Points;
        var bars = series.PriceBars;

        // X domain: earliest→latest date across BOTH series.
        var minDay = int.MaxValue;
        var maxDay = int.MinValue;
        foreach (var b in bars)
        {
            minDay = Math.Min(minDay, b.Date.DayNumber);
            maxDay = Math.Max(maxDay, b.Date.DayNumber);
        }

        foreach (var p in points)
        {
            minDay = Math.Min(minDay, p.ScoreDate.DayNumber);
            maxDay = Math.Max(maxDay, p.ScoreDate.DayNumber);
        }

        var hasDomain = minDay != int.MaxValue;
        var daySpan = hasDomain ? Math.Max(1, maxDay - minDay) : 1;

        // Price domain (adjusted close). Guard a flat/empty series against divide-by-zero.
        var hasPrice = bars.Count > 0;
        var priceMin = double.MaxValue;
        var priceMax = double.MinValue;
        foreach (var b in bars)
        {
            var v = (double)b.AdjClose;
            priceMin = Math.Min(priceMin, v);
            priceMax = Math.Max(priceMax, v);
        }

        var priceSpan = hasPrice ? Math.Max(1e-9, priceMax - priceMin) : 1;

        double X(int dayNumber) =>
            hasDomain && daySpan > 0
                ? PlotLeft + ((dayNumber - minDay) / (double)daySpan * PlotWidth)
                : PlotLeft + (PlotWidth / 2);

        double YScore(int value) => PlotTop + ((100 - Clamp01To100(value)) / 100.0 * PlotHeight);

        double YPrice(double value) => PlotTop + ((priceMax - value) / priceSpan * PlotHeight);

        var sb = new StringBuilder();

        // NOTE: no xmlns http URI is emitted — the artifact is a SELF-CONTAINED INLINE SVG (AD: no "http"/no
        // external reference). It renders inline in HTML; it deliberately carries no remote namespace/asset.
        sb.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {Width} {Height}\" width=\"{Width}\" height=\"{Height}\" font-family=\"monospace\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<title>{Escape(series.CompanyName)} ({Escape(series.Ticker)}) score vs price</title>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{Width}\" height=\"{Height}\" fill=\"#ffffff\" stroke=\"#cccccc\"/>\n");

        // Heading.
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotLeft)}\" y=\"24\" font-size=\"14\" fill=\"#222222\">{Escape(series.CompanyName)} ({Escape(series.Ticker)}) — {ComponentLabel()} vs price</text>\n");

        // Plot frame + axes.
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotLeft)}\" y1=\"{Num(PlotTop)}\" x2=\"{Num(PlotLeft)}\" y2=\"{Num(PlotBottom)}\" stroke=\"#888888\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotRight)}\" y1=\"{Num(PlotTop)}\" x2=\"{Num(PlotRight)}\" y2=\"{Num(PlotBottom)}\" stroke=\"#888888\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotLeft)}\" y1=\"{Num(PlotBottom)}\" x2=\"{Num(PlotRight)}\" y2=\"{Num(PlotBottom)}\" stroke=\"#888888\"/>\n");

        // Left score axis: 0 / 50 / 100 ticks + labels.
        foreach (var tick in new[] { 0, 50, 100 })
        {
            var y = YScore(tick);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotLeft - 4)}\" y1=\"{Num(y)}\" x2=\"{Num(PlotLeft)}\" y2=\"{Num(y)}\" stroke=\"#888888\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotLeft - 8)}\" y=\"{Num(y + 3)}\" font-size=\"10\" text-anchor=\"end\" fill=\"#555555\">{tick.ToString(CultureInfo.InvariantCulture)}</text>\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"14\" y=\"{Num(PlotTop + (PlotHeight / 2))}\" font-size=\"10\" fill=\"#3366cc\" transform=\"rotate(-90 14 {Num(PlotTop + (PlotHeight / 2))})\">score 0-100</text>\n");

        // Right price axis: min/max labels.
        if (hasPrice)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotRight + 6)}\" y=\"{Num(YPrice(priceMax) + 3)}\" font-size=\"10\" fill=\"#cc6633\">{Price(priceMax)}</text>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotRight + 6)}\" y=\"{Num(YPrice(priceMin) + 3)}\" font-size=\"10\" fill=\"#cc6633\">{Price(priceMin)}</text>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(Width - 6)}\" y=\"{Num(PlotTop + (PlotHeight / 2))}\" font-size=\"10\" fill=\"#cc6633\" transform=\"rotate(90 {Num(Width - 6)} {Num(PlotTop + (PlotHeight / 2))})\">price (adj close)</text>\n");
        }

        // Dense price polyline over adjusted close.
        if (hasPrice)
        {
            sb.Append("<polyline fill=\"none\" stroke=\"#cc6633\" stroke-width=\"1\" points=\"");
            var first = true;
            foreach (var b in bars)
            {
                if (!first)
                {
                    sb.Append(' ');
                }

                first = false;
                sb.Append(CultureInfo.InvariantCulture, $"{Num(X(b.Date.DayNumber))},{Num(YPrice((double)b.AdjClose))}");
            }

            sb.Append("\"/>\n");
        }

        // Score points, segmented by ScoringConfigVersion. Draw a connecting polyline ONLY within a segment;
        // never across a fingerprint boundary (AD-10). Then dots for every point. Then boundary markers.
        RenderScoreSegments(sb, points, X, YScore);

        // Legend + caption (a research statistic segmented by scoring-config fingerprint — NOT advice, AD-9).
        var legendY = Height - 28;
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotLeft)}\" y1=\"{Num(legendY - 4)}\" x2=\"{Num(PlotLeft + 20)}\" y2=\"{Num(legendY - 4)}\" stroke=\"#3366cc\" stroke-width=\"2\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotLeft + 26)}\" y=\"{Num(legendY)}\" font-size=\"10\" fill=\"#3366cc\">{ComponentLabel()} (score)</text>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(PlotLeft + 200)}\" y1=\"{Num(legendY - 4)}\" x2=\"{Num(PlotLeft + 220)}\" y2=\"{Num(legendY - 4)}\" stroke=\"#cc6633\" stroke-width=\"2\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotLeft + 226)}\" y=\"{Num(legendY)}\" font-size=\"10\" fill=\"#cc6633\">price (adj close)</text>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(PlotLeft)}\" y=\"{Num(Height - 10)}\" font-size=\"10\" fill=\"#777777\">Research statistic: score vs price, segmented by scoring-config fingerprint. Not advice.</text>\n");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private void RenderScoreSegments(
        StringBuilder sb, IReadOnlyList<EfficacyPoint> points, Func<int, double> x, Func<int, double> yScore)
    {
        if (points.Count == 0)
        {
            return;
        }

        // Walk contiguous equal-ScoringConfigVersion runs. A run of length >= 2 gets a connecting polyline;
        // a run of length 1 gets no line (isolated dot). A boundary is where the segment key changes.
        var segmentStart = 0;
        for (var i = 1; i <= points.Count; i++)
        {
            var isBoundary = i == points.Count
                || !SameSegment(points[i - 1].ScoringConfigVersion, points[i].ScoringConfigVersion);

            if (!isBoundary)
            {
                continue;
            }

            var segmentLength = i - segmentStart;
            if (segmentLength >= 2)
            {
                sb.Append("<polyline fill=\"none\" stroke=\"#3366cc\" stroke-width=\"1.5\" points=\"");
                for (var j = segmentStart; j < i; j++)
                {
                    if (j > segmentStart)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(CultureInfo.InvariantCulture, $"{Num(x(points[j].ScoreDate.DayNumber))},{Num(yScore(SelectScore(points[j])))}");
                }

                sb.Append("\"/>\n");
            }

            // Mark the boundary (except the final terminator) with a dashed vertical rule + fingerprint label
            // at the FIRST point of the NEW segment.
            if (i < points.Count)
            {
                var bx = x(points[i].ScoreDate.DayNumber);
                sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Num(bx)}\" y1=\"{Num(PlotTop)}\" x2=\"{Num(bx)}\" y2=\"{Num(PlotBottom)}\" stroke=\"#aaaaaa\" stroke-width=\"1\" stroke-dasharray=\"4 3\"/>\n");
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Num(bx + 2)}\" y=\"{Num(PlotTop + 10)}\" font-size=\"9\" fill=\"#999999\">{Escape(ShortFingerprint(points[i].ScoringConfigVersion))}</text>\n");
            }

            segmentStart = i;
        }

        // Dots for every score point (drawn after lines so they sit on top).
        foreach (var p in points)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{Num(x(p.ScoreDate.DayNumber))}\" cy=\"{Num(yScore(SelectScore(p)))}\" r=\"2.5\" fill=\"#3366cc\"/>\n");
        }
    }

    private int SelectScore(EfficacyPoint p) => Component switch
    {
        EfficacyScoreComponent.Trajectory => p.TrajectoryScore,
        EfficacyScoreComponent.Opportunity => p.OpportunityScore,
        EfficacyScoreComponent.Attention => p.AttentionScore,
        EfficacyScoreComponent.EvidenceConfidence => p.EvidenceConfidenceScore,
        EfficacyScoreComponent.SignalVelocity => p.SignalVelocityScore,
        _ => p.OpportunityScore,
    };

    private string ComponentLabel() => Component switch
    {
        EfficacyScoreComponent.Trajectory => "trajectory",
        EfficacyScoreComponent.Opportunity => "opportunity",
        EfficacyScoreComponent.Attention => "attention",
        EfficacyScoreComponent.EvidenceConfidence => "evidence-confidence",
        EfficacyScoreComponent.SignalVelocity => "signal-velocity",
        _ => "opportunity",
    };

    // Two points share a segment iff their fingerprint keys are Ordinal-equal (null == null; null != any value).
    private static bool SameSegment(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    // The short suffix used as a boundary label; null = the unknown/pre-stamp segment.
    private static string ShortFingerprint(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "unknown";
        }

        var dash = version.LastIndexOf('-');
        var suffix = dash >= 0 && dash < version.Length - 1 ? version[(dash + 1)..] : version;
        return suffix.Length > 12 ? suffix[^12..] : suffix;
    }

    private static int Clamp01To100(int value) => Math.Clamp(value, 0, 100);

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Price(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // XML-escape text/attribute content so a company name/ticker/fingerprint can never break the SVG.
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}
