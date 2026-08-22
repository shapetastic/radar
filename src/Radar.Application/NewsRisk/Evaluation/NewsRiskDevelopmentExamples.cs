namespace Radar.Application.NewsRisk.Evaluation;

/// <summary>
/// One committed known-development-example declaration (spec 179 §8): a company examined BEFORE the feature
/// existed, which may exercise it but can never serve as validation evidence. Read directly from
/// <c>docs/cohorts/news-risk-development.json</c> — the file, not git history, is the declaration mechanism.
/// </summary>
public sealed record NewsRiskDevelopmentExample(string Ticker, string InspectedOnUtc, string Reason);

/// <summary>
/// The declaration read seam, implemented in Infrastructure. <c>null</c> (as opposed to an empty list) means
/// the declaration file is missing/unreadable — the evaluator must then produce NO clean prospective table
/// at all (fail closed: without the declarations, a development example could silently leak into it).
/// </summary>
public interface INewsRiskDevelopmentExampleSource
{
    Task<IReadOnlyList<NewsRiskDevelopmentExample>?> GetAllAsync(CancellationToken ct);
}
