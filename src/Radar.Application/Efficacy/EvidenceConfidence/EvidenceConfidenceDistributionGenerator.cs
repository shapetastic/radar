using Microsoft.Extensions.Logging;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>
/// Composes the spec-225 EvidenceConfidence measurement: build, render, write. Read-only over persisted
/// snapshots, stored links, signals, evidence and companies; it writes only its own three artifacts and
/// promotes nothing.
/// </summary>
public interface IEvidenceConfidenceDistributionGenerator
{
    Task GenerateAsync(CancellationToken ct);
}

/// <inheritdoc cref="IEvidenceConfidenceDistributionGenerator"/>
public sealed class EvidenceConfidenceDistributionGenerator : IEvidenceConfidenceDistributionGenerator
{
    private readonly EvidenceConfidenceDistributionReporter _reporter;
    private readonly EvidenceConfidenceDistributionRenderer _renderer;
    private readonly IEvidenceConfidenceArtifactStore _artifacts;
    private readonly ILogger<EvidenceConfidenceDistributionGenerator> _logger;

    public EvidenceConfidenceDistributionGenerator(
        EvidenceConfidenceDistributionReporter reporter,
        EvidenceConfidenceDistributionRenderer renderer,
        IEvidenceConfidenceArtifactStore artifacts,
        ILogger<EvidenceConfidenceDistributionGenerator> logger)
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
                    "EvidenceConfidence distribution: {FilesNotPersisted} of 3 artifact file(s) could NOT be "
                        + "durably persisted ({JsonPath}, {CsvPath}, {MarkdownPath}); the on-disk measurement "
                        + "is missing or STALE.",
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
            // Read-only measurement: a failure here must never abort the surrounding run (the spec-218
            // generator's posture). Nothing is written, and the run continues.
            _logger.LogError(ex, "EvidenceConfidence distribution measurement failed; no artifact was written.");
        }
    }
}
