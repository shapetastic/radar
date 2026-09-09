using Microsoft.Extensions.Logging;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>
/// Composes the spec-218 directional-filing-read measurement: build, render, write. Read-only over the
/// accrued read corpus, evidence, companies, typed news and price; it writes only its own three artifacts and
/// promotes nothing.
/// </summary>
public interface IDirectionalFilingReadReportGenerator
{
    Task GenerateAsync(CancellationToken ct);
}

/// <inheritdoc cref="IDirectionalFilingReadReportGenerator"/>
public sealed class DirectionalFilingReadReportGenerator : IDirectionalFilingReadReportGenerator
{
    private readonly DirectionalFilingReadReporter _reporter;
    private readonly DirectionalFilingReadRenderer _renderer;
    private readonly IDirectionalFilingReadArtifactStore _artifacts;
    private readonly ILogger<DirectionalFilingReadReportGenerator> _logger;

    public DirectionalFilingReadReportGenerator(
        DirectionalFilingReadReporter reporter,
        DirectionalFilingReadRenderer renderer,
        IDirectionalFilingReadArtifactStore artifacts,
        ILogger<DirectionalFilingReadReportGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);

        _reporter = reporter;
        _renderer = renderer;
        _artifacts = artifacts;
        _logger = logger;
    }

    public async Task GenerateAsync(CancellationToken ct)
    {
        try
        {
            var report = await _reporter.BuildAsync(ct).ConfigureAwait(false);

            var write = await _artifacts
                .WriteAsync(
                    _renderer.RenderJson(report),
                    _renderer.RenderCsv(report),
                    _renderer.RenderMarkdown(report),
                    ct)
                .ConfigureAwait(false);

            if (write.NotPersistedCount > 0)
            {
                // Spec 201 §1: a measurement that never landed must not read as measured on disk.
                _logger.LogWarning(
                    "Directional filing-read measurement: {FilesNotPersisted} of 3 artifact file(s) could "
                        + "NOT be durably persisted ({JsonPath}, {CsvPath}, {MarkdownPath}); the on-disk "
                        + "measurement is missing or STALE.",
                    write.NotPersistedCount,
                    write.JsonPath,
                    write.CsvPath,
                    write.MarkdownPath);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Read-only measurement: a failure here must never abort the surrounding run (the
            // NewsRiskEvaluationGenerator posture). Nothing is written, and the run continues.
            _logger.LogError(
                ex, "Directional filing-read measurement failed; no artifact was written.");
        }
    }
}
