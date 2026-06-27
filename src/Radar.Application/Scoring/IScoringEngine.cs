namespace Radar.Application.Scoring;

/// <summary>
/// Stage 6 engine: computes and persists a <c>CompanyScoreSnapshot</c> for one company over the
/// recent-signal window ending at <paramref name="windowEndUtc"/>, plus the <c>ScoreEvidenceLink</c>
/// rows tracing it back to the contributing signals and evidence.
/// </summary>
public interface IScoringEngine
{
    Task<CompanyScoreResult> ScoreCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct);
}
