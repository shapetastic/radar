using Microsoft.Extensions.Logging;

namespace Radar.Application.Efficacy;

/// <summary>
/// Builds the score↔price efficacy dataset and, for every series that has BOTH ≥1 score point AND ≥1 price
/// bar, renders the SVG + CSV and writes them via <see cref="IEfficacyArtifactStore"/>. A series missing a side
/// is skipped with an honest logged reason ("no price data" / "no score history"). Read-only over score
/// history + price (AD-14): no evidence/signal/scoring dependency, writes only efficacy artifacts, runs OUTSIDE
/// <c>IRadarPipeline</c>. A per-run summary (rendered / skipped) is logged; caller cancellation propagates.
/// </summary>
public sealed class EfficacyReportGenerator : IEfficacyReportGenerator
{
    private readonly EfficacyDatasetBuilder _builder;
    private readonly EfficacySvgRenderer _svgRenderer;
    private readonly EfficacyCsvRenderer _csvRenderer;
    private readonly IEfficacyArtifactStore _artifactStore;
    private readonly ILogger<EfficacyReportGenerator> _logger;

    public EfficacyReportGenerator(
        EfficacyDatasetBuilder builder,
        EfficacySvgRenderer svgRenderer,
        EfficacyCsvRenderer csvRenderer,
        IEfficacyArtifactStore artifactStore,
        ILogger<EfficacyReportGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(svgRenderer);
        ArgumentNullException.ThrowIfNull(csvRenderer);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(logger);

        _builder = builder;
        _svgRenderer = svgRenderer;
        _csvRenderer = csvRenderer;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task GenerateAsync(CancellationToken ct)
    {
        var series = await _builder.BuildAsync(ct).ConfigureAwait(false);

        var rendered = 0;
        var skipped = 0;

        foreach (var company in series)
        {
            ct.ThrowIfCancellationRequested();

            if (company.Points.Count == 0)
            {
                skipped++;
                _logger.LogInformation(
                    "Efficacy: skipping '{Ticker}' — no score history.", company.Ticker);
                continue;
            }

            if (company.PriceBars.Count == 0)
            {
                skipped++;
                _logger.LogInformation(
                    "Efficacy: skipping '{Ticker}' — no price data.", company.Ticker);
                continue;
            }

            var svg = _svgRenderer.Render(company);
            var csv = _csvRenderer.Render(company);
            await _artifactStore.WriteAsync(company.Ticker, svg, csv, ct).ConfigureAwait(false);
            rendered++;
        }

        _logger.LogInformation(
            "Efficacy report complete: {Rendered} company/companies rendered, {Skipped} skipped.",
            rendered,
            skipped);
    }
}
