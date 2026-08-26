using System.Globalization;

using Radar.Application.News;

namespace Radar.Application.NewsRisk;

/// <summary>
/// Whether the spec-177 observation archive PROVED it captured this run's observations (spec 182 §2).
/// <para>
/// The zero value is DELIBERATELY the degraded state: a persisted v1 assessment record
/// (<c>news-risk-assessment-v1</c>) carries none of the spec-182 dimension fields, so a missing JSON
/// property deserializes to <c>default</c> (0) — which must read as "cannot tell", never as best-state.
/// </para>
/// </summary>
public enum NewsRiskArchiveCapture
{
    /// <summary>No batch manifest was readable, or the batch recorded observation persistence failures.</summary>
    Unproven = 0,

    /// <summary>A batch manifest exists for the run and its capture is proven (no persistence failures).</summary>
    Proven,
}

/// <summary>
/// How completely the newssearch provider enumeration ran for this company this run (spec 182 §2).
/// <para>
/// The zero value is DELIBERATELY the degraded state (see <see cref="NewsRiskArchiveCapture"/>): a v1
/// record with no <c>searchEnumeration</c> field must deserialize as <see cref="Unproven"/>, never as
/// <see cref="Complete"/>.
/// </para>
/// </summary>
public enum NewsRiskSearchEnumeration
{
    /// <summary>No coverage evidence exists (missing batch/capture/coverage rows) — "cannot tell".</summary>
    Unproven = 0,

    /// <summary>A KNOWN failure: no declared feed, feed failures, or recorded coverage issues.</summary>
    Failed,

    /// <summary>
    /// The result list reached Radar's own EFFECTIVE/LOCAL retention limit (spec 190) — enumeration ran but
    /// is known POSSIBLY truncated. It is not a measured provider ceiling, and the converse is equally weak:
    /// observing no item beyond the limit still cannot prove the provider had no further results.
    /// </summary>
    Truncated,

    /// <summary>Every declared feed succeeded without reaching the local retention limit and with no recorded issue.</summary>
    Complete,
}

/// <summary>
/// Whether the model-input bundle held EVERY qualifying observation, or dropped some at the bundle cap
/// (spec 182 §2 — collection completeness and model-input completeness are independent dimensions).
/// <para>
/// The zero value is DELIBERATELY the degraded state (see <see cref="NewsRiskArchiveCapture"/>): a v1
/// record with no <c>assessmentBundle</c> field must deserialize as <see cref="Capped"/>, never as
/// <see cref="Complete"/>.
/// </para>
/// </summary>
public enum NewsRiskAssessmentBundle
{
    /// <summary>Qualifying observations were dropped by the bundle bound (<c>MaxArticlesPerCompany</c>).</summary>
    Capped = 0,

    /// <summary>The bundle holds every qualifying observation.</summary>
    Complete,
}

/// <summary>The typed coverage evaluation (spec 182 §2): two of the three dimensions plus the human-readable issue detail list.</summary>
public sealed record NewsRiskCoverageEvaluation(
    NewsRiskArchiveCapture ArchiveCapture,
    NewsRiskSearchEnumeration SearchEnumeration,
    IReadOnlyList<string> Issues);

/// <summary>
/// Pure, deterministic derivation of the archive-capture and search-enumeration dimensions from the
/// spec-177 batch manifest (spec 182 §2). This REPLACES spec 179 §4's boolean coverage gate: nothing here
/// blocks a reader — the dimensions are recorded facts, never admission criteria for presence claims.
/// <para>
/// The search-enumeration mapping is TOTAL over the states the coverage evaluation distinguishes:
/// <list type="bullet">
/// <item>batch manifest null → <c>Unproven</c> (and archive capture <c>Unproven</c>);</item>
/// <item>batch <c>CaptureProven</c> false → archive capture <c>Unproven</c> (search unaffected);</item>
/// <item>newssearch capture entry missing → <c>Unproven</c>;</item>
/// <item><c>CompanyCoverage</c> null → <c>Unproven</c>;</item>
/// <item>company coverage row missing → <c>Unproven</c>;</item>
/// <item><c>ExpectedFeedCount == 0</c> (no declared feed) → <c>Failed</c>;</item>
/// <item><c>SuccessfulFeedCount &lt; ExpectedFeedCount</c> (feed failures) → <c>Failed</c>;</item>
/// <item>row <c>Issues</c> non-empty (health mismatches / source failures) → <c>Failed</c>;</item>
/// <item><c>HitEffectiveResultLimit</c> → <c>Truncated</c>;</item>
/// <item>none of the above → <c>Complete</c>.</item>
/// </list>
/// When several states apply they combine through the ONE severity rule <see cref="Worse"/>:
/// <c>Failed</c> outranks <c>Unproven</c> outranks <c>Truncated</c> outranks <c>Complete</c> — a known
/// failure outranks "cannot tell", and both outrank mere truncation.
/// </para>
/// </summary>
public static class NewsRiskCoverageEvaluator
{
    /// <summary>
    /// The single documented combine rule: severity order Failed &gt; Unproven &gt; Truncated &gt; Complete.
    /// </summary>
    public static NewsRiskSearchEnumeration Worse(
        NewsRiskSearchEnumeration a, NewsRiskSearchEnumeration b) =>
        Severity(a) >= Severity(b) ? a : b;

    private static int Severity(NewsRiskSearchEnumeration value) => value switch
    {
        NewsRiskSearchEnumeration.Failed => 3,
        NewsRiskSearchEnumeration.Unproven => 2,
        NewsRiskSearchEnumeration.Truncated => 1,
        NewsRiskSearchEnumeration.Complete => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown search-enumeration state."),
    };

    public static NewsRiskCoverageEvaluation Evaluate(
        NewsObservationBatch? batch, Guid companyId, string newsSearchCollectorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newsSearchCollectorName);

        var issues = new List<string>();
        if (batch is null)
        {
            issues.Add("archive-batch-unavailable: no batch manifest is readable for this run");
            return new NewsRiskCoverageEvaluation(
                NewsRiskArchiveCapture.Unproven, NewsRiskSearchEnumeration.Unproven, issues);
        }

        // Archive capture is Proven ONLY when a batch exists AND it proved persistence.
        var archiveCapture = batch.CaptureProven
            ? NewsRiskArchiveCapture.Proven
            : NewsRiskArchiveCapture.Unproven;
        if (!batch.CaptureProven)
        {
            issues.Add("archive-batch-unproven: the batch recorded observation persistence failures");
        }

        var capture = batch.Collectors.FirstOrDefault(
            c => string.Equals(c.CollectorName, newsSearchCollectorName, StringComparison.Ordinal));
        if (capture is null)
        {
            issues.Add($"newssearch-capture-not-recorded: no '{newsSearchCollectorName}' capture in the batch");
            return new NewsRiskCoverageEvaluation(
                archiveCapture, NewsRiskSearchEnumeration.Unproven, issues);
        }

        if (capture.CompanyCoverage is null)
        {
            issues.Add("newssearch-coverage-not-recorded: coverage rows are absent (unproven)");
            return new NewsRiskCoverageEvaluation(
                archiveCapture, NewsRiskSearchEnumeration.Unproven, issues);
        }

        var row = capture.CompanyCoverage.FirstOrDefault(r => r.CompanyId == companyId);
        if (row is null)
        {
            issues.Add("company-coverage-missing: this company has no newssearch coverage row");
            return new NewsRiskCoverageEvaluation(
                archiveCapture, NewsRiskSearchEnumeration.Unproven, issues);
        }

        var enumeration = NewsRiskSearchEnumeration.Complete;
        if (row.ExpectedFeedCount == 0)
        {
            issues.Add("no-newssearch-feed: this company declares no newssearch feed");
            enumeration = Worse(enumeration, NewsRiskSearchEnumeration.Failed);
        }

        if (row.SuccessfulFeedCount < row.ExpectedFeedCount)
        {
            issues.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"feed-failures: {row.SuccessfulFeedCount}/{row.ExpectedFeedCount} newssearch feeds succeeded"));
            enumeration = Worse(enumeration, NewsRiskSearchEnumeration.Failed);
        }

        if (row.HitEffectiveResultLimit)
        {
            issues.Add(
                "result-limit-reached: the newssearch result list reached Radar's effective local retention "
                    + "limit (possible truncation; not a proven provider ceiling)");
            enumeration = Worse(enumeration, NewsRiskSearchEnumeration.Truncated);
        }

        if (row.Issues.Count > 0)
        {
            issues.AddRange(row.Issues.Select(i => "coverage-issue: " + i));
            enumeration = Worse(enumeration, NewsRiskSearchEnumeration.Failed);
        }

        return new NewsRiskCoverageEvaluation(archiveCapture, enumeration, issues);
    }
}

/// <summary>
/// The ONE wording source for completeness dimensions (spec 182 §3): the generator's warnings and the live
/// renderer both route through these pure functions, so a cached raw verdict replayed under different
/// dimensions always gets THIS run's presentation. No output here — or anywhere — may ever read as an
/// "all-clear": the absence wording is permanently scoped to the supplied text.
/// </summary>
public static class NewsRiskCompletenessDescription
{
    /// <summary>All three dimensions at their best — the ONLY state in which an absence claim carries evidential weight.</summary>
    public static bool IsBestState(
        NewsRiskArchiveCapture archiveCapture,
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle) =>
        archiveCapture == NewsRiskArchiveCapture.Proven
            && searchEnumeration == NewsRiskSearchEnumeration.Complete
            && assessmentBundle == NewsRiskAssessmentBundle.Complete;

    /// <summary>All three dimensions rendered explicitly — none collapsed into or hidden behind another.</summary>
    public static string Describe(
        NewsRiskArchiveCapture archiveCapture,
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle,
        int suppliedArticleCount,
        int qualifyingArticleCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"archive capture {archiveCapture} · search enumeration {searchEnumeration} · assessment "
                + $"bundle {assessmentBundle} ({suppliedArticleCount} supplied of {qualifyingArticleCount} "
                + $"qualifying available)");

    /// <summary>
    /// Whether any degraded dimension states a KNOWN incompleteness (search failed/truncated, bundle
    /// capped) as opposed to merely unproven ("cannot tell"). Archive capture never contributes — its
    /// only degraded state is <see cref="NewsRiskArchiveCapture.Unproven"/>.
    /// </summary>
    public static bool HasKnownIncompleteness(
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle) =>
        searchEnumeration is NewsRiskSearchEnumeration.Failed or NewsRiskSearchEnumeration.Truncated
            || assessmentBundle == NewsRiskAssessmentBundle.Capped;

    /// <summary>The degraded dimensions, each named — empty at best-state.</summary>
    public static IReadOnlyList<string> DegradedParts(
        NewsRiskArchiveCapture archiveCapture,
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle,
        int suppliedArticleCount,
        int qualifyingArticleCount)
    {
        var parts = new List<string>();
        if (archiveCapture != NewsRiskArchiveCapture.Proven)
        {
            parts.Add($"archive capture {archiveCapture}");
        }

        if (searchEnumeration != NewsRiskSearchEnumeration.Complete)
        {
            parts.Add($"search enumeration {searchEnumeration}");
        }

        if (assessmentBundle != NewsRiskAssessmentBundle.Complete)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"bundle capped at {suppliedArticleCount} of {qualifyingArticleCount} qualifying available"));
        }

        return parts;
    }

    /// <summary>
    /// The permanently-narrow absence wording (spec 182 §3), a pure function of (dimensions, counts): at
    /// best-state it is still ONLY a statement about the supplied text; under any degraded dimension the
    /// degradation is stated beside it. There is no wording, in any state, that asserts a company is clean.
    /// </summary>
    public static string NoRiskWording(
        NewsRiskArchiveCapture archiveCapture,
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle,
        int suppliedArticleCount,
        int qualifyingArticleCount)
    {
        var degraded = DegradedParts(
            archiveCapture, searchEnumeration, assessmentBundle,
            suppliedArticleCount, qualifyingArticleCount);
        // A KNOWN incompleteness is stated as fact; an unproven-only degradation must not be — "cannot
        // tell" overstated as "known incomplete" is its own kind of false certainty.
        var caveat = HasKnownIncompleteness(searchEnumeration, assessmentBundle)
            ? "known to be incomplete"
            : "not proven complete";
        return degraded.Count == 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"No risk was supported by the supplied text. This is a statement about the "
                    + $"{suppliedArticleCount} supplied article(s), not about the company.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"No risk was supported by the supplied text. Supplied text is {caveat} "
                    + $"({string.Join("; ", degraded)}) — this is a statement about "
                    + $"{suppliedArticleCount} article(s), not about the company.");
    }
}
