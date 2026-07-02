using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// Reads the per-company <c>news</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is a token carrying that company's query phrase and optional ticker) and turns each
/// recent, relevance-confirmed news article into a raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.NewsArticle"/> — Radar's first third-party market-attention source. Does not
/// score, resolve, or persist — it only answers "what recent news covered this company?" A feed that fails to
/// read (or whose token is malformed) contributes no evidence and is logged as a Warning (the reader reports
/// the failure mode); a company with zero recent coverage degrades to zero evidence, not an error.
/// <para>
/// <b>GDELT throttles hard</b>, so feeds are processed strictly sequentially (never fanned out) with a
/// configurable inter-request pacing delay, and an HTTP 429 degrades that feed to a source failure without
/// aborting the run. <b>Provenance guard:</b> GDELT phrase search has no exact-entity key (unlike
/// USASpending's <c>recipient_id</c>), so returned articles are CLIENT-SIDE-FILTERED to those whose
/// whitespace-normalised title references the company name or its ticker token — an off-topic loosely-matched
/// article is dropped rather than attached. Company hints come only from the configured feed→company binding —
/// tickers are never invented. Evidence Title/RawText are synthesized from real article metadata; no article
/// body text is fabricated (DOC <c>ArtList</c> returns none).
/// </para>
/// </summary>
internal sealed class GdeltNewsCollector : IEvidenceCollector
{
    private const int ApiMinRecords = 1;
    private const int ApiMaxRecords = 250;

    private readonly IGdeltNewsReader _reader;
    private readonly ILogger<GdeltNewsCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly GdeltCollectorOptions _options;

    public GdeltNewsCollector(
        IGdeltNewsReader reader,
        ILogger<GdeltNewsCollector> logger,
        TimeProvider timeProvider,
        GdeltCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _reader = reader;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options;
    }

    public string CollectorName => "news";

    public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;

    public async Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("news");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        // Strictly sequential (never Task.WhenAll) + paced: GDELT 429s on back-to-back requests.
        var isFirstRequest = true;

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = GdeltFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed news feed token"));
                _logger.LogWarning(
                    "News feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'query=<phrase>' with an optional '&ticker=<TICKER>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // PACE: before each request AFTER the first, wait so successive feeds do not trip the throttle.
            if (!isFirstRequest)
            {
                await Task.Delay(_options.InterRequestDelay, ct).ConfigureAwait(false);
            }

            isFirstRequest = false;

            var query = BuildQuery(target);

            var result = await _reader.ReadAsync(query, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "News feed '{FeedName}' (phrase '{QueryPhrase}') could not be read: {Detail}; skipping.",
                    feed.Name,
                    target.QueryPhrase,
                    result.Detail);
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Dedupe within this feed by url so an article appears at most once.
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectedForFeed = 0;

            foreach (var article in result.Items)
            {
                // CLIENT-SIDE RELEVANCE FILTER (the provenance guard): GDELT phrase search has no exact-entity
                // key, so keep only articles whose title plausibly references the company name or ticker; an
                // off-topic loosely-matched article is dropped rather than attributed to this company.
                if (!IsRelevant(article.Title, target))
                {
                    continue;
                }

                if (!seenUrls.Add(article.Url))
                {
                    continue;
                }

                if (collectedForFeed >= _options.MaxRecordsPerCompany)
                {
                    // The API sorts DateDesc, so the first N survivors are the most recent coverage.
                    break;
                }

                results.Add(MapToEvidence(feed, article, hints));
                collectedForFeed++;
            }

            _logger.LogInformation(
                "News feed '{FeedName}' (phrase '{QueryPhrase}'): kept {Kept} of {Returned} article(s).",
                feed.Name,
                target.QueryPhrase,
                collectedForFeed,
                result.Items.Count);
        }

        _logger.LogInformation(
            "GDELT news collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
                + "{ItemsCollected} article(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private GdeltNewsQuery BuildQuery(GdeltFeedTarget target) => new(
        QueryPhrase: target.QueryPhrase,
        Timespan: _options.Timespan,
        MaxRecords: Math.Clamp(_options.MaxRecordsPerCompany, ApiMinRecords, ApiMaxRecords),
        EnglishOnly: _options.EnglishOnly)
    {
        MaxRetriesOn429 = _options.MaxRetriesOn429,
        // A 429 needs a much longer cool-down than ordinary pacing (GDELT recommends ≈60s/120s), so the
        // backoff base is its own option — the reader grows it exponentially per retry.
        RetryDelay = _options.RetryBackoff,
    };

    /// <summary>
    /// True when the whitespace-normalised, case-insensitive article title contains the company query phrase
    /// or the (optional) ticker token. GDELT spaces out punctuation in titles, so both sides are
    /// whitespace-normalised first — that is what lets a spaced <c>"( MRCY )"</c> still match a <c>MRCY</c>
    /// ticker and <c>"Mercury Systems , Inc ."</c> still match the <c>Mercury Systems</c> phrase.
    /// </summary>
    private static bool IsRelevant(string? title, GdeltFeedTarget target)
    {
        var normalizedTitle = NormalizeWhitespace(title);
        if (normalizedTitle.Length == 0)
        {
            return false;
        }

        var phrase = NormalizeWhitespace(target.QueryPhrase);
        if (phrase.Length > 0
            && normalizedTitle.Contains(phrase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ticker = NormalizeWhitespace(target.Ticker);
        return ticker.Length > 0
            && normalizedTitle.Contains(ticker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapses every run of whitespace to a single space and trims; null/blank becomes empty.</summary>
    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed, GdeltArticleItem article, IReadOnlyList<string> hints)
    {
        var seenDateText = article.SeenDate?.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

        // Title: the article headline as-is (GDELT's cosmetic punctuation spacing is left untouched).
        var title = article.Title;

        // RawText: synthesized from REAL fields only. The url + title + seendate are included so two distinct
        // articles never collide under the mapper's Title+RawText ContentHash dedupe. No body text is fabricated.
        var rawText =
            $"{article.Title} — {article.Domain} ({seenDateText}). Source: {article.Url}";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality.
            // Third-party news is lower-integrity than primary filings/awards (aggregators, wires, listicles),
            // so it declares a Medium baseline — below the SEC/USASpending High.
            ["quality"] = "Medium",
            ["gdeltFeedUrl"] = feed.Url,
            ["url"] = article.Url,
            ["domain"] = article.Domain,
            ["seendate"] = seenDateText,
            ["language"] = article.Language,
            ["sourcecountry"] = article.SourceCountry,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            SourceUrl: article.Url,
            Title: title,
            RawText: rawText,
            // Observed instant = the article's seendate (parsed UTC), null when unparseable; CollectedAt is
            // the TimeProvider now regardless, so windowing/recency work.
            PublishedAt: article.SeenDate,
            CollectedAt: _timeProvider.GetUtcNow(),
            Metadata: metadata)
        {
            CompanyHints = hints,
        };
    }
}
