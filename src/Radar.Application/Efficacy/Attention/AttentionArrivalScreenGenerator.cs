using System.Globalization;

using Microsoft.Extensions.Logging;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// Composes the AD-16 attention-arrival screen (spec 169): evaluate, render, write. Read-only over score,
/// signal, evidence and run history; it writes only its own three artifacts and promotes nothing.
/// </summary>
public interface IAttentionArrivalScreenGenerator
{
    Task<AttentionArrivalScreenResult> GenerateAsync(CancellationToken ct);
}

/// <inheritdoc cref="IAttentionArrivalScreenGenerator"/>
public sealed class AttentionArrivalScreenGenerator : IAttentionArrivalScreenGenerator
{
    private readonly AttentionArrivalScreenEvaluator _evaluator;
    private readonly AttentionArrivalRenderer _renderer;
    private readonly IAttentionArrivalArtifactStore _artifacts;
    private readonly ILogger<AttentionArrivalScreenGenerator> _logger;

    public AttentionArrivalScreenGenerator(
        AttentionArrivalScreenEvaluator evaluator,
        AttentionArrivalRenderer renderer,
        IAttentionArrivalArtifactStore artifacts,
        ILogger<AttentionArrivalScreenGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);

        _evaluator = evaluator;
        _renderer = renderer;
        _artifacts = artifacts;
        _logger = logger;
    }

    public async Task<AttentionArrivalScreenResult> GenerateAsync(CancellationToken ct)
    {
        var result = await _evaluator.EvaluateAsync(ct).ConfigureAwait(false);

        // An early run still writes a useful artifact — Pending, with its exclusion counts — so the coverage
        // instrumentation is exercised and readable long before the first outcome matures.
        var artifactWrite = await _artifacts
            .WriteAsync(
                _renderer.RenderJson(result),
                _renderer.RenderCsv(result),
                _renderer.RenderMarkdown(result),
                ct)
            .ConfigureAwait(false);
        if (artifactWrite.NotPersistedCount > 0)
        {
            // Spec 201 §1: an AD-16 screen artifact that never landed must not read as evaluated on disk.
            _logger.LogWarning(
                "Attention-arrival screen: {FilesNotPersisted} of 3 artifact file(s) could NOT be durably "
                    + "persisted ({JsonPath}, {CsvPath}, {MarkdownPath}); the on-disk screen is missing or "
                    + "STALE.",
                artifactWrite.NotPersistedCount,
                artifactWrite.JsonPath,
                artifactWrite.CsvPath,
                artifactWrite.MarkdownPath);
        }

        if (result.Availability == AttentionEvaluationAvailability.Unavailable)
        {
            _logger.LogWarning(
                "Attention-arrival screen NOT evaluated: {Reason}. {Detail}",
                result.UnavailableReason,
                result.UnavailableDetail);
            return result;
        }

        _logger.LogInformation(
            "Attention-arrival screen (AD-16): {Status}. {EligibleDates} of {RequiredDates} eligible as-of "
                + "date(s) over {CandidateDates} candidate(s); median delta {MedianDelta}. First eligible "
                + "date {FirstEligible:yyyy-MM-dd}. Exploratory cohort reported separately over "
                + "{ExploratoryDates} candidate date(s).",
            result.ScreenStatus,
            result.Primary.EligibleDates,
            result.MinimumEligibleDates,
            result.Primary.CandidateDates,
            result.Primary.IsMedianDeltaDefined
                ? result.Primary.MedianDelta.ToString("0.000000", CultureInfo.InvariantCulture)
                : "(undefined)",
            result.FirstEligibleAsOfDateUtc,
            result.Exploratory.CandidateDates);

        return result;
    }
}
