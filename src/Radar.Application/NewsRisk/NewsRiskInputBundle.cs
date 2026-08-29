using System.Text;

using Radar.Application.Identity;
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
/// <param name="SyndicatedDuplicateCount">
/// SPEC 193 §3: how many ADMITTED observations the duplicate-normalized-headline collapse removed — i.e.
/// N − 1 for each headline with N admitted copies, summed over headlines. Population: the observations that
/// passed the window/cutoff admission filters, BEFORE the article cap (the collapse runs before the cap, so
/// this number is independent of it). Zero means nothing collapsed. Without it, a company with 40 syndicated
/// copies of one story is indistinguishable from one with a single article, and syndication breadth is
/// itself a presence measurement.
/// </param>
/// <param name="SyndicatedDistinctPublisherCount">
/// SPEC 193 §3: the number of distinct <c>Publisher</c> values (ordinal) across the admitted observations
/// belonging to a normalized-headline group with MORE THAN ONE copy — i.e. the syndication breadth of the
/// stories that collapsed. Population: those groups' observations INCLUDING the surviving copy, so N copies
/// from M publishers reports M — the story's full syndication breadth — rather than M − 1. That is the
/// deliberate choice: the question this answers is "how widely was the collapsed story carried", and the
/// survivor's own publisher is part of that breadth. It is 0 when nothing collapsed, and it is NOT a count
/// of publishers whose article was dropped.
/// <para>
/// NEITHER count is a <see cref="BundleHash"/> input, for exactly the reason
/// <paramref name="QualifyingArticleCount"/> is not: the hash stays over the SUPPLIED articles only, so the
/// assessment cache key does not move and no cohort forks. Neither feeds
/// <see cref="Completeness"/> either — <c>Capped</c> is about the bundle bound, and a dedupe collapse is not
/// a cap drop (spec 182 §2).
/// </para>
/// </param>
public sealed record NewsRiskInputBundle(
    IReadOnlyList<NewsRiskInputArticle> Articles,
    DateTimeOffset SelectionAsOfUtc,
    DateTimeOffset AssessmentCutoffUtc,
    string BundleHash,
    int QualifyingArticleCount,
    int SyndicatedDuplicateCount,
    int SyndicatedDistinctPublisherCount)
{
    /// <summary>
    /// The spec-182 model-input completeness dimension: <c>Capped</c> when qualifying observations were
    /// dropped by the bundle bound. Computed, so a <c>with</c>-copy (e.g. live body attachment) can never
    /// carry a stale value. Spec 193 §3 deliberately does NOT fold the syndication counts in: a
    /// duplicate-headline collapse is not a cap drop, and <c>Capped</c> keeps its exact pre-193 meaning.
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

        // Spec 193 §3: measure the syndication the collapse below is about to discard, BEFORE discarding it.
        // Only the surviving article's own Publisher travels into the bundle — the earlier comment here
        // claimed "publisher diversity and exact ids stay visible on the surviving article", which was not
        // accurate: one article carries one publisher, and every other copy's publisher and id are gone. So
        // the two facts worth keeping are counted here: how many copies collapsed, and across how many
        // distinct publishers the collapsed STORIES were carried (see NewsRiskInputBundle for the exact
        // populations). Pure and deterministic — a grouping over the already-ordered admitted list.
        var syndicatedDuplicateCount = 0;
        var syndicatedPublishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in admitted.GroupBy(o => NormalizeHeadline(o.Headline), StringComparer.Ordinal))
        {
            var copies = 0;
            foreach (var o in group)
            {
                copies++;
            }

            if (copies <= 1)
            {
                continue;
            }

            syndicatedDuplicateCount += copies - 1;
            foreach (var o in group)
            {
                syndicatedPublishers.Add(o.Publisher);
            }
        }

        // Collapse exact duplicate normalized headlines: the comparison key is suffix-stripped + trimmed,
        // ordinal — the FIRST (newest) copy survives. Only the surviving article's OWN Publisher, id and
        // stored headline travel into the bundle; every collapsed copy's publisher and id are dropped, which
        // is why the two counts above exist. Nothing else is normalized. Enumeration continues PAST the
        // article cap (without adding) so the bundle can report how many QUALIFYING observations exist — a
        // dedupe-collapsed duplicate is not a cap drop (spec 182 §2).
        var seenHeadlines = new HashSet<string>(StringComparer.Ordinal);
        var articles = new List<NewsRiskInputArticle>();
        var qualifying = 0;
        var fetchedAttached = 0;
        foreach (var o in admitted)
        {
            var normalized = NormalizeHeadline(o.Headline);
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
            // Unchanged: the hash is over the SUPPLIED articles only, so neither syndication count below can
            // move the assessment cache key (spec 193 §3).
            BundleHash: ComputeBundleHash(articles),
            QualifyingArticleCount: qualifying,
            SyndicatedDuplicateCount: syndicatedDuplicateCount,
            SyndicatedDistinctPublisherCount: syndicatedPublishers.Count);
    }

    /// <summary>
    /// The ONE duplicate-collapse comparison key: the already-defined Google publisher-suffix strip plus a
    /// trim, ordinal. Shared by the syndication measurement and the collapse itself so the two can never
    /// group differently — a divergent second copy would make the counts describe a collapse that did not
    /// happen.
    /// </summary>
    private static string NormalizeHeadline(string headline) =>
        (GoogleNewsHeadline.StripPublisherSuffix(headline) ?? string.Empty).Trim();

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

        return CanonicalHash.Sha256Hex(canonical);
    }
}
