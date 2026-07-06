using Radar.Application.Efficacy;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy;

public sealed class EfficacyCsvRendererTests
{
    private const string ExpectedHeader =
        "scoreDate,scoringConfigVersion,trajectory,opportunity,attention,evidenceConfidence,velocity,"
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
            "2026-06-12,radar-scoring-fp-abc,50,60,55,70,40,2026-06-12,102.5,101.25", lines[1]);

        // Null price fields (and null fingerprint) render as empty cells.
        Assert.Equal("2026-06-05,,10,20,30,40,50,,,", lines[2]);
    }

    [Fact]
    public void Render_EmptySeries_IsHeaderOnly()
    {
        var series = new CompanyEfficacySeries(
            Guid.NewGuid(), "Acme Corp", "MRCY", Array.Empty<EfficacyPoint>(), Array.Empty<PriceBar>());

        var csv = new EfficacyCsvRenderer().Render(series);

        Assert.Equal(ExpectedHeader + "\n", csv);
    }
}
