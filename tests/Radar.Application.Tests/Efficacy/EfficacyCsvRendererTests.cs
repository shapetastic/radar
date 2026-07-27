using Radar.Application.Efficacy;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy;

public sealed class EfficacyCsvRendererTests
{
    // Spec 141: the series key (strategy name) leads, with the fingerprint retained beside it as provenance.
    private const string ExpectedHeader =
        "scoreDate,seriesKey,scoringConfigVersion,trajectory,opportunity,attention,evidenceConfidence,velocity,"
            + "priceAsOfDate,priceClose,priceAdjClose";

    [Fact]
    public void Render_HeaderPlusOneRowPerPoint_InvariantFormatting_NullPriceCellsEmpty()
    {
        var paired = new EfficacyPoint(
            ScoreDate: new DateOnly(2026, 6, 12),
            TrajectoryScore: 50,
            OpportunityScore: 60,
            AttentionScore: 55,
            EvidenceConfidenceScore: 70,
            SignalVelocityScore: 40,
            SeriesKey: "default",
            ScoringConfigVersion: "radar-scoring-fp-abc",
            PriceAsOfDate: new DateOnly(2026, 6, 12),
            PriceClose: 102.5m,
            PriceAdjClose: 101.25m);

        var unpaired = new EfficacyPoint(
            ScoreDate: new DateOnly(2026, 6, 5),
            TrajectoryScore: 10,
            OpportunityScore: 20,
            AttentionScore: 30,
            EvidenceConfidenceScore: 40,
            SignalVelocityScore: 50,
            SeriesKey: "insider-only",
            ScoringConfigVersion: null,
            PriceAsOfDate: null,
            PriceClose: null,
            PriceAdjClose: null);

        var series = new CompanyEfficacySeries(
            Guid.NewGuid(), "Acme Corp", "MRCY", [paired, unpaired], Array.Empty<PriceBar>());

        var csv = new EfficacyCsvRenderer().Render(series);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header + exactly one row per point.
        Assert.Equal(3, lines.Length);
        Assert.Equal(ExpectedHeader, lines[0]);

        // Rows follow point order; invariant decimal formatting; ISO dates.
        Assert.Equal(
            "2026-06-12,default,radar-scoring-fp-abc,50,60,55,70,40,2026-06-12,102.5,101.25", lines[1]);

        // Null price fields (and a null fingerprint) render as empty cells; the series key never does — a
        // legacy/blank strategy name is canonicalised to "default" upstream, so grouping by it is total.
        Assert.Equal("2026-06-05,insider-only,,10,20,30,40,50,,,", lines[2]);
    }

    [Fact]
    public void Render_EmptySeries_IsHeaderOnly()
    {
        var series = new CompanyEfficacySeries(
            Guid.NewGuid(), "Acme Corp", "MRCY", Array.Empty<EfficacyPoint>(), Array.Empty<PriceBar>());

        var csv = new EfficacyCsvRenderer().Render(series);

        Assert.Equal(ExpectedHeader + "\n", csv);
    }

    [Fact]
    public void Render_IsByteUnchangedByTheSpec140AsOfDateAddition()
    {
        // EfficacyPoint gained a trailing AsOfDate for the strategy-comparison harness. The per-company CSV
        // is an EXISTING artifact and must not move: no new column, and two points differing ONLY in AsOfDate
        // must render byte-identically.
        var point = new EfficacyPoint(
            ScoreDate: new DateOnly(2026, 6, 12),
            TrajectoryScore: 50,
            OpportunityScore: 60,
            AttentionScore: 55,
            EvidenceConfidenceScore: 70,
            SignalVelocityScore: 40,
            SeriesKey: "default",
            ScoringConfigVersion: "radar-scoring-fp-abc",
            PriceAsOfDate: new DateOnly(2026, 6, 12),
            PriceClose: 102.5m,
            PriceAdjClose: 101.25m);

        var renderer = new EfficacyCsvRenderer();

        string Render(EfficacyPoint p) => renderer.Render(new CompanyEfficacySeries(
            Guid.Empty, "Acme Corp", "MRCY", [p], Array.Empty<PriceBar>()));

        var withoutAsOf = Render(point);
        var withAsOf = Render(point with { AsOfDate = new DateOnly(2026, 6, 12) });
        var withDifferentAsOf = Render(point with { AsOfDate = new DateOnly(2020, 1, 1) });

        Assert.Null(point.AsOfDate);
        Assert.Equal(withoutAsOf, withAsOf);
        Assert.Equal(withoutAsOf, withDifferentAsOf);
        Assert.StartsWith(ExpectedHeader + "\n", withoutAsOf, StringComparison.Ordinal);
        Assert.Equal(2, withoutAsOf.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
