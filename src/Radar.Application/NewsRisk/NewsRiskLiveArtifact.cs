using Radar.Application.News;

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
    public const string CurrentSchemaVersion = "news-risk-live-v1";

    /// <summary>The §1 live caveat, verbatim — carried by every live artifact.</summary>
    public const string LiveCaveat =
        "News-risk assessments are shadow diagnostics over the cited text available at the stated cutoff. "
            + "They do not alter Radar scores or labels, and absence of a detected risk is not evidence that "
            + "a company is safe.";

    /// <summary>The named §2 diagnostic for a run whose report produced no multi-strategy sections — rows are never invented.</summary>
    public const string NoLiveStrategySections = "NoLiveStrategySections";
}

/// <summary>One selected company's live entry: frozen selection provenance, supplied inputs, and every reader's own result.</summary>
public sealed record NewsRiskLiveCompany(
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsRiskCandidateSelection> Selections,
    IReadOnlyList<NewsRiskLiveArticle> Articles,
    bool CoverageComplete,
    IReadOnlyList<string> CoverageIssues,
    IReadOnlyList<NewsRiskLiveReaderResult> ReaderResults);

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
