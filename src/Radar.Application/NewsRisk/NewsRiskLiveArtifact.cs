using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk;

/// <summary>The JSON side of the live artifact (spec 179 §7) — one document per run day, mirrored by the rendered markdown.</summary>
public sealed record NewsRiskLiveDocument(
    string SchemaVersion,
    Guid? RunId,
    DateTimeOffset? SelectionAsOfUtc,
    string Caveat,
    IReadOnlyList<string> Readers,
    string? Diagnostic,
    IReadOnlyList<NewsRiskLiveCompany> Companies,
    DateTimeOffset GeneratedAtUtc)
{
    // v3 (spec 185): additive per-company two-stage judgment sections + the presentation-cohort marker
    // state. A v2 JSON document deserializes safely — the new members are trailing and nullable.
    public const string CurrentSchemaVersion = "news-risk-live-v3";

    /// <summary>The §1 live caveat, verbatim — carried by every live artifact.</summary>
    public const string LiveCaveat =
        "News-risk assessments are shadow diagnostics over the cited text available at the stated cutoff. "
            + "They do not alter Radar scores or labels, and absence of a detected risk is not evidence that "
            + "a company is safe.";

    /// <summary>The named §2 diagnostic for a run whose report produced no multi-strategy sections — rows are never invented.</summary>
    public const string NoLiveStrategySections = "NoLiveStrategySections";
}

/// <summary>
/// One selected company's live entry: frozen selection provenance, supplied inputs, the three spec-182
/// completeness dimensions (per company per run — identical across readers of one company, so they live
/// here rather than on each reader result), and every reader's own result. v2 replaced the v1
/// <c>CoverageComplete</c> boolean with the dimensions plus the qualifying-observation count that makes the
/// bundle dimension's arithmetic visible.
/// </summary>
public sealed record NewsRiskLiveCompany(
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsRiskCandidateSelection> Selections,
    IReadOnlyList<NewsRiskLiveArticle> Articles,
    NewsRiskArchiveCapture ArchiveCapture,
    NewsRiskSearchEnumeration SearchEnumeration,
    NewsRiskAssessmentBundle AssessmentBundle,
    int QualifyingArticleCount,
    IReadOnlyList<string> CoverageIssues,
    IReadOnlyList<NewsRiskLiveReaderResult> ReaderResults,
    // Spec 185 (v3, additive): the two-stage judgment cohorts' results for this company — every cohort
    // rendered independently, never pooled — and the PRESENTATION cohort's semantic-read marker text as
    // rendered on the leaders. Null when the judgment step did not run this pass.
    IReadOnlyList<NewsRiskLiveJudgment>? Judgments = null,
    string? JudgmentMarker = null);

/// <summary>
/// One stage-2 judgment cohort's result for one company (spec 185 §5): the judge and its upstream stage-1
/// extractor cohort, the validated result with drop accounting (the judgment side of the
/// extraction-vs-judgment error split, rendered beside stage 1's fact-drop count), and ALL FIVE
/// completeness dimensions. Labelled by judge name AND exact model id; never merged across cohorts.
/// </summary>
public sealed record NewsRiskLiveJudgment(
    string JudgeName,
    string Provider,
    string ModelId,
    string Stage1CohortKey,
    Guid JudgmentId,
    NewsJudgmentStatus Status,
    NewsJudgmentTrajectory? BusinessTrajectory,
    int? ChallengeStrength,
    IReadOnlyList<NewsJudgmentValidatedFinding> Findings,
    string? Rationale,
    int FindingsTotal,
    int FindingsAccepted,
    int FindingsDropped,
    IReadOnlyList<string> FindingDropReasons,
    int Stage1FactsDroppedInWindow,
    NewsRiskArchiveCapture ArchiveCapture,
    NewsRiskSearchEnumeration SearchEnumeration,
    NewsRiskAssessmentBundle ObservationSupply,
    NewsTypingCompleteness TypingCompleteness,
    NewsJudgmentFamilyBundle FamilyBundle,
    IReadOnlyList<NewsJudgmentFamilyRef> Families);

/// <summary>One supplied article's display row: headline, publisher, URL and which text fields were supplied.</summary>
public sealed record NewsRiskLiveArticle(
    Guid ObservationId,
    string Headline,
    string Publisher,
    string Url,
    NewsObservationCaptureMode CaptureMode,
    string InputKind);

/// <summary>One reader's own assessment of one company — labelled by reader name AND exact model id; never merged.</summary>
public sealed record NewsRiskLiveReaderResult(
    string ReaderName,
    string Provider,
    string ModelId,
    Guid AssessmentId,
    NewsRiskAssessmentStatus Status,
    DateTimeOffset AssessmentCutoffUtc,
    int? RiskScore,
    IReadOnlyList<NewsRiskCategory> Categories,
    IReadOnlyList<NewsRiskValidatedClaim> Claims,
    string? Rationale,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The artifact write seam (spec 179 §7/§9), implemented in Infrastructure over the shared graceful writer.
/// Live: <c>{root}/live/news-risk-{asOfDate}.md|.json</c>; a shadow failure writes the NAMED failed artifact
/// <c>{root}/live/news-risk-{asOfDate}-FAILED.md</c> and never rolls back or relabels the already-durable
/// Radar run. Evaluation: <c>{root}/evaluation/news-risk-evaluation.md|.csv</c>.
/// </summary>
public interface INewsRiskArtifactStore
{
    Task WriteLiveAsync(
        string asOfDateToken, string markdown, NewsRiskLiveDocument document, CancellationToken ct);

    Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct);

    Task WriteEvaluationAsync(string markdown, string csv, CancellationToken ct);
}
