using System.Security.Cryptography;
using System.Text;

using Radar.Application.News;

namespace Radar.Application.NewsRisk;

/// <summary>
/// One article as supplied to the model (spec 179 §4/§6): exactly the fields listed here — headline,
/// optional RSS description text, and optional permitted publisher body — define the "archived text" the
/// §6 excerpt validation runs against. URLs, publisher and timestamps ride along as provenance for the
/// artifact, never as model-validatable text.
/// </summary>
public sealed record NewsRiskInputArticle(
    Guid ObservationId,
    string Headline,
    string? DescriptionText,
    string? BodyText,
    string Publisher,
    string Url,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset RetrievedAtUtc,
    NewsObservationCaptureMode CaptureMode,
    string PayloadHash,
    string? BodyContentHash,
    DateTimeOffset? BodyRetrievedAtUtc,
    string? BodyExtractorVersion,
    string? BodyRetrievalPolicy);

/// <summary>
/// The frozen point-in-time input bundle for one (candidate, run): ordered articles, the selection instant D,
/// the honest assessment cutoff, the ordered input-bundle hash the assessment cache keys on, and the count
/// of QUALIFYING observations (admitted by the window/cutoff filters AND surviving the duplicate-headline
/// collapse — spec 182 §2). <see cref="QualifyingArticleCount"/> exceeding <c>Articles.Count</c> means the
/// article cap dropped qualifying observations; it is deliberately a count rather than a bool because the
/// dropped volume is itself information. It is NOT a <see cref="BundleHash"/> input — the hash stays over
/// the supplied articles only, so the assessment cache key does not move.
/// </summary>
public sealed record NewsRiskInputBundle(
    IReadOnlyList<NewsRiskInputArticle> Articles,
    DateTimeOffset SelectionAsOfUtc,
    DateTimeOffset AssessmentCutoffUtc,
    string BundleHash,
    int QualifyingArticleCount)
{
    /// <summary>
    /// The spec-182 model-input completeness dimension: <c>Capped</c> when qualifying observations were
    /// dropped by the bundle bound. Computed, so a <c>with</c>-copy (e.g. live body attachment) can never
    /// carry a stale value.
    /// </summary>
    public NewsRiskAssessmentBundle Completeness =>
        QualifyingArticleCount > Articles.Count
            ? NewsRiskAssessmentBundle.Capped
            : NewsRiskAssessmentBundle.Complete;
}

/// <summary>
/// Deterministic point-in-time input-bundle construction (spec 179 §4). Pure — no clock, no I/O:
/// <list type="bullet">
/// <item>admits only observations for the candidate company whose <c>FirstObservedAtUtc</c> and
/// <c>RetrievedAtUtc</c> are at/before the selection instant D, whose <c>PublishedAtUtc</c> is null or at/
/// before D, and whose observation time (<c>FirstObservedAtUtc</c>) falls in <c>(D − lookback, D]</c>;</item>
/// <item>collapses exact duplicate normalized headlines — normalization is ONLY the already-defined Google
/// publisher-suffix strip (<see cref="GoogleNewsHeadline"/>) plus a trim, applied for comparison only (the
/// kept article's stored headline is untouched);</item>
/// <item>orders newest first (<c>FirstObservedAtUtc</c> descending) then observation id ascending, and takes
/// at most the article cap;</item>
/// <item>attaches an observation's own ARCHIVED permitted body (a stored <c>Fetched</c> result), newest
/// first, up to the fetched-article cap — a body retrieved at E moves the assessment cutoff to
/// <c>max(D, E)</c>, never backward. Live fetching (when spec 177's ArticleFetch is enabled) is the
/// generator's concern, not this pure builder's.</item>
/// </list>
/// </summary>
public static class NewsRiskInputBundleBuilder
{
    public static NewsRiskInputBundle Build(
        Guid companyId,
        IReadOnlyList<NewsObservationRecord> observations,
        DateTimeOffset selectionAsOfUtc,
        int lookbackDays,
        int maxArticlesPerCompany,
        int maxFetchedArticlesPerCompany)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticlesPerCompany, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFetchedArticlesPerCompany);

        var windowStartExclusive = selectionAsOfUtc.AddDays(-lookbackDays);

        var admitted = observations
            .Where(o => o.CompanyId == companyId)
            .Where(o => o.FirstObservedAtUtc <= selectionAsOfUtc)
            .Where(o => o.RetrievedAtUtc <= selectionAsOfUtc)
            .Where(o => o.PublishedAtUtc is null || o.PublishedAtUtc <= selectionAsOfUtc)
            .Where(o => o.FirstObservedAtUtc > windowStartExclusive)
            // Newest first, then observation id — the deterministic order (AD-3) the caps and the
            // duplicate collapse below both run over, so which duplicate survives is a pure function of
            // the data.
            .OrderByDescending(o => o.FirstObservedAtUtc)
            .ThenBy(o => o.ObservationId)
            .ToList();

        // Collapse exact duplicate normalized headlines: the comparison key is suffix-stripped + trimmed,
        // ordinal — the FIRST (newest) copy survives. Publisher diversity and exact ids stay visible on the
        // surviving article; nothing else is normalized. Enumeration continues PAST the article cap (without
        // adding) so the bundle can report how many QUALIFYING observations exist — a dedupe-collapsed
        // duplicate is not a cap drop (spec 182 §2).
        var seenHeadlines = new HashSet<string>(StringComparer.Ordinal);
        var articles = new List<NewsRiskInputArticle>();
        var qualifying = 0;
        var fetchedAttached = 0;
        foreach (var o in admitted)
        {
            var normalized = (GoogleNewsHeadline.StripPublisherSuffix(o.Headline) ?? string.Empty).Trim();
            if (!seenHeadlines.Add(normalized))
            {
                continue;
            }

            qualifying++;
            if (articles.Count >= maxArticlesPerCompany)
            {
                continue;
            }

            // A stored permitted body (spec 177 §6: BodyText is non-null ONLY for a Fetched outcome from an
            // allowlisted source) is attached newest-first up to the fetched cap.
            var storedFetch = o.ArticleFetch;
            var attachBody = fetchedAttached < maxFetchedArticlesPerCompany
                && storedFetch is { Outcome: NewsArticleFetchOutcome.Fetched, BodyText: not null };
            if (attachBody)
            {
                fetchedAttached++;
            }

            articles.Add(new NewsRiskInputArticle(
                ObservationId: o.ObservationId,
                Headline: o.Headline,
                DescriptionText: o.DescriptionText,
                BodyText: attachBody ? storedFetch!.BodyText : null,
                Publisher: o.Publisher,
                Url: o.GoogleLandingUrl,
                PublishedAtUtc: o.PublishedAtUtc,
                RetrievedAtUtc: o.RetrievedAtUtc,
                CaptureMode: o.CaptureMode,
                PayloadHash: o.PayloadHash,
                BodyContentHash: attachBody ? storedFetch!.ContentHash : null,
                BodyRetrievedAtUtc: attachBody ? storedFetch!.RetrievedAtUtc : null,
                BodyExtractorVersion: attachBody ? storedFetch!.ExtractorVersion : null,
                BodyRetrievalPolicy: attachBody ? storedFetch!.RetrievalPolicy : null));
        }

        return new NewsRiskInputBundle(
            Articles: articles,
            SelectionAsOfUtc: selectionAsOfUtc,
            AssessmentCutoffUtc: ComputeCutoff(selectionAsOfUtc, articles),
            BundleHash: ComputeBundleHash(articles),
            QualifyingArticleCount: qualifying);
    }

    /// <summary>
    /// The honest assessment cutoff (spec 179 §4): <c>max(D, every supplied input's retrieval instant)</c>.
    /// RSS-only input known by D keeps D; a supplied body retrieved at E &gt; D moves the cutoff forward to
    /// E. A cutoff can NEVER move backward — the max over instants that include D makes that structural.
    /// </summary>
    public static DateTimeOffset ComputeCutoff(
        DateTimeOffset selectionAsOfUtc, IReadOnlyList<NewsRiskInputArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var cutoff = selectionAsOfUtc;
        foreach (var article in articles)
        {
            if (article.RetrievedAtUtc > cutoff)
            {
                cutoff = article.RetrievedAtUtc;
            }

            if (article.BodyRetrievedAtUtc is { } bodyAt && bodyAt > cutoff)
            {
                cutoff = bodyAt;
            }
        }

        return cutoff;
    }

    /// <summary>
    /// The ordered input-bundle hash the assessment cache keys on (spec 179 §6): SHA-256 over each article's
    /// identity, payload hash and WHICH text fields were supplied (description presence and the exact body
    /// content hash) — so adding a body, dropping a description, or reordering the bundle is a different
    /// cache entry, never a silent reuse.
    /// </summary>
    public static string ComputeBundleHash(IReadOnlyList<NewsRiskInputArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var canonical = new StringBuilder("radar:news-risk-bundle:");
        foreach (var article in articles)
        {
            canonical
                .Append(article.ObservationId.ToString("D"))
                .Append('|')
                .Append(article.PayloadHash)
                .Append('|')
                .Append(article.DescriptionText is null ? "d0" : "d1")
                .Append('|')
                .Append(article.BodyText is null ? "b0" : "b1:" + (article.BodyContentHash ?? string.Empty))
                .Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
