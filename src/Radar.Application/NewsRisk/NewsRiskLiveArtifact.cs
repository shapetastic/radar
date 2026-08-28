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
    DateTimeOffset GeneratedAtUtc,
    // Spec 194 §1.2 (additive, TRAILING and NULLABLE): what the judgment-signal materializer did this run.
    // `null` means the step was NOT ATTEMPTED — a pre-194 document, or a run with no materializer registered
    // — never "attempted and produced nothing", which is an all-zero summary. The schema tag deliberately
    // does NOT move: no field is removed or re-meant, and the member's own nullability carries the whole
    // "not recorded" story (the spec-142 EvidenceQuality / spec-148 EffectiveScoringConfig.Window
    // trailing-nullable precedent). A v3 consumer reading the existing fields BY NAME is unaffected.
    NewsJudgmentSignalMaterializationSummary? SignalMaterialization = null)
{
    // v3 (spec 185): additive per-company two-stage judgment sections + the presentation-cohort marker
    // state. A v2 JSON document deserializes safely — the new members are trailing and nullable.
    //
    // v4 (spec 195 §§2): the per-company pre-collapse syndication measurement. The tag DOES move here,
    // unlike the spec-194 SignalMaterialization addition, because the new members are the artifact's only
    // record of a measurement the run performed and then discarded: a reader has to be able to tell a v3
    // document (measurement NOT RECORDED, hydrating null) from a v4 document that measured an honest zero.
    // Nothing is removed or re-meant, so a by-name v3 consumer is unaffected.
    //
    // v5 (spec 197 §1.2): the run-level observation-to-evidence JOIN measurement nested inside
    // SignalMaterialization, plus the prior-version-occupancy count. The tag moves for the same reason v4's
    // did: the buckets are the artifact's only record of a measurement the run performed, so a reader must
    // be able to tell a v4 document (join measurement NOT RECORDED, hydrating null) from a v5 document that
    // measured an honest zero. Nothing is removed or re-meant; a by-name v4 consumer is unaffected. The
    // measurement is current-run diagnostic provenance only — it enters no bundle hash, cache key, cohort
    // key, judgment, signal or score.
    public const string CurrentSchemaVersion = "news-risk-live-v5";

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
    string? JudgmentMarker = null,
    // Spec 195 §2 (v4, additive, TRAILING and NULLABLE): THIS RUN's pre-collapse syndication measurement,
    // taken from the freshly built input bundle — never from a cached assessment record, whose surviving
    // supplied articles (and therefore BundleHash) can be identical while syndication breadth has changed;
    // reusing it would display an old run's breadth as current.
    //
    // `null` means NOT RECORDED (a pre-v4 document, hydrated) and is deliberately NOT the same fact as a
    // measured 0, which means "this run enumerated the articles and nothing collapsed". Every company row a
    // v4 run writes carries measured integers, including that honest zero.
    //
    // Neither value enters BundleHash, an assessment id, a cohort key, completeness, the model request,
    // scoring or any fingerprint: it is enumeration provenance sitting BESIDE a possibly cached reader
    // result, and it is never a reason to call the model again.
    int? SyndicatedDuplicateCount = null,
    int? SyndicatedDistinctPublisherCount = null);

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
    IReadOnlyList<NewsJudgmentFamilyRef> Families,
    // Spec 187 §1: the supplied FactIds the judge said ESTABLISH the trajectory. NULL means "not recorded
    // under news-judgment-v1" — never an empty v2 evidence set and never proof of invalidity.
    IReadOnlyList<Guid>? TrajectoryFactIds = null,
    // Spec 197 §2.2: how many raw FactId citations this judgment's response had shortened and the shared
    // resolver deterministically expanded. TRAILING and NULLABLE, mirroring the record's own three states:
    // null = no validated response was examined under this contract (or a pre-197 record), 0 = a response
    // was examined and every accepted citation was already complete, positive = that many expansions. The
    // renderer states all three DISTINCTLY — a defaulted zero must never read as a measured one.
    int? FactIdPrefixExpansionCount = null);

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
