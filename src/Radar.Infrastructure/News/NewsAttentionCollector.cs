using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.News;

/// <summary>
/// Reads the per-company <c>newssearch</c> source feeds configured on the <see cref="CollectionContext"/>
/// (each feed's <c>Url</c> is a token carrying that company's query phrase and optional ticker) and turns each
/// recent, relevance-confirmed Google News RSS article into a raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.NewsArticle"/> — the third-party market-attention source that is NOT per-IP
/// throttled (spec-80 verified), Radar's fix for GDELT's per-IP quota. Does not score, resolve, or persist —
/// it only answers "what recent news covered this company?" A feed that fails to read (or whose token is
/// malformed) contributes no evidence and is logged as a Warning (the reader reports the failure mode); a
/// company with zero recent coverage degrades to zero evidence, not an error.
/// <para>
/// Feeds are processed strictly sequentially (never fanned out) with a small configurable inter-request pacing
/// delay, and any non-<c>Success</c> read (incl. HTTP 429 → <c>RateLimited</c>) degrades that feed to a source
/// failure without aborting the run. <b>Provenance guard:</b> news phrase search has no exact-entity key
/// (unlike USASpending's <c>recipient_id</c>), so returned articles are CLIENT-SIDE-FILTERED to those whose
/// whitespace-normalised title — after stripping any Google News <c>" - Publisher"</c> suffix — references the
/// company query phrase or its ticker token; an off-topic loosely-matched article is dropped rather than
/// attached. Company hints come only from the configured feed→company binding — tickers are never invented.
/// Evidence Title/RawText are synthesized from real article metadata; no article body text is fabricated (a
/// news SEARCH returns headlines only). All HTTP/XML/source specifics stay behind the injected
/// <see cref="INewsSearchReader"/> (AD-5) — this collector contains no <c>HttpClient</c> and no XML parsing.
/// </para>
/// </summary>
internal sealed class NewsAttentionCollector : IEvidenceCollector
{
    private const int ApiMinRecords = 1;
    private const int ApiMaxRecords = 100;
    private const string TitleSuffixSeparator = " - ";

    private readonly INewsSearchReader _reader;
    private readonly ILogger<NewsAttentionCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly NewsCollectorOptions _options;

    public NewsAttentionCollector(
        INewsSearchReader reader,
        ILogger<NewsAttentionCollector> logger,
        TimeProvider timeProvider,
        NewsCollectorOptions options)
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

    public string CollectorName => "newssearch";

    public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;

    public async Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("newssearch");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        // Strictly sequential (never Task.WhenAll) + paced: a small polite pace between reads.
        var isFirstRequest = true;

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = QueryFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed news feed token"));
                _logger.LogWarning(
                    "News search feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'query=<phrase>' with an optional '&ticker=<TICKER>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // PACE: before each request AFTER the first, wait so successive feeds stay polite.
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
                    "News search feed '{FeedName}' (phrase '{QueryPhrase}') could not be read: {Detail}; skipping.",
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
                // CLIENT-SIDE RELEVANCE FILTER (the provenance guard): news phrase search has no exact-entity
                // key, so keep only articles whose title plausibly references the company phrase or ticker; an
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
                    // The reader returns items in feed order (Google News RSS sorts newest-first), so the first
                    // N survivors are the most recent coverage.
                    break;
                }

                results.Add(MapToEvidence(feed, article, hints));
                collectedForFeed++;
            }

            _logger.LogInformation(
                "News search feed '{FeedName}' (phrase '{QueryPhrase}'): kept {Kept} of {Returned} article(s).",
                feed.Name,
                target.QueryPhrase,
                collectedForFeed,
                result.Items.Count);
        }

        _logger.LogInformation(
            "News search collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
                + "{ItemsCollected} article(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private NewsSearchQuery BuildQuery(QueryFeedTarget target) => new(
        QueryPhrase: target.QueryPhrase,
        MaxRecords: Math.Clamp(_options.MaxRecordsPerCompany, ApiMinRecords, ApiMaxRecords),
        EnglishOnly: _options.EnglishOnly);

    /// <summary>
    /// True when the whitespace-normalised, case-insensitive article title contains the company query phrase
    /// or the (optional) ticker token. The Google News <c>" - Publisher"</c> title suffix is stripped BEFORE
    /// the check so a publisher name that happens to contain the ticker/phrase cannot produce a false match;
    /// both sides are whitespace-normalised first, so a spaced <c>"( RKLB )"</c> still matches an
    /// <c>RKLB</c> ticker and <c>"Rocket Lab USA , Inc ."</c> still matches the <c>Rocket Lab</c> phrase.
    /// </summary>
    private static bool IsRelevant(string? title, QueryFeedTarget target)
    {
        var normalizedTitle = NormalizeWhitespace(StripPublisherSuffix(title));
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

    /// <summary>
    /// Removes a trailing <c>" - Publisher"</c> suffix Google News appends to the headline (the outlet name),
    /// so the relevance check runs against the real headline only. Returns the input unchanged when no suffix
    /// is present.
    /// </summary>
    private static string? StripPublisherSuffix(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return title;
        }

        var separatorIndex = title.LastIndexOf(TitleSuffixSeparator, StringComparison.Ordinal);
        return separatorIndex >= 0 ? title[..separatorIndex] : title;
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
        CompanySourceFeed feed, NewsArticleItem article, IReadOnlyList<string> hints)
    {
        var pubDateText = article.PublishedAt?.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

        // Title: the article headline as-is (the Google News " - Publisher" suffix is kept for provenance; the
        // suffix strip is only performed for the relevance check, never on the stored title).
        var title = article.Title;

        var publisher = article.SourceName;

        // SourceName is the article's real OUTLET (Reuters, Yahoo Finance, ...), NOT the per-company feed:
        // AttentionScore's breadth term counts distinct third-party evidence SourceNames, so it must see how
        // many distinct outlets cover a company — the feed name is one constant value per company and would
        // pin breadth at 1. Fall back to feed.Name only when the publisher is blank so an unattributable
        // article still carries a human-readable source label for the report; that fallback never manufactures
        // false breadth — it is a single per-company-constant bucket the formula's Distinct() collapses, and a
        // blank publisher is skipped by the formula anyway (contributes 0 breadth, still counts as media).
        var sourceName = string.IsNullOrWhiteSpace(publisher) ? feed.Name : publisher;

        // RawText: synthesized from REAL fields only. The url + title + pubDate are included so two distinct
        // articles never collide under the mapper's Title+RawText ContentHash dedupe. No body text is fabricated.
        var rawText =
            $"{article.Title} — {publisher} ({pubDateText}). Source: {article.Url}";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality.
            // Third-party news is lower-integrity than primary filings/awards (aggregators, wires, listicles),
            // so it declares a Medium baseline — below the SEC/USASpending High, consistent with GDELT.
            ["quality"] = "Medium",
            ["newsSearchFeedUrl"] = feed.Url,
            ["url"] = article.Url,
            ["publisher"] = publisher,
            // The per-company feed attribution, still recoverable for provenance/display now that SourceName
            // carries the outlet.
            ["feedName"] = feed.Name,
            ["pubDate"] = pubDateText,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: sourceName,
            SourceUrl: article.Url,
            Title: title,
            RawText: rawText,
            // Observed instant = the article's pubDate (parsed UTC), null when unparseable; CollectedAt is the
            // TimeProvider now regardless, so windowing/recency work.
            PublishedAt: article.PublishedAt,
            CollectedAt: _timeProvider.GetUtcNow(),
            Metadata: metadata)
        {
            CompanyHints = hints,
        };
    }
}
