using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.News;
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

    /// <summary>
    /// THE definition of this collector's stable provenance name (spec 147). A const so the composition
    /// root's kind→collector table can name it without a magic string — that table is what lets a standalone
    /// <c>score</c> pass (which registers no collector at all) still know the collector VOCABULARY.
    /// </summary>
    public const string Name = "newssearch";

    /// <summary>
    /// The metadata key ONLY this collector writes — the spec-151 discriminator
    /// <c>LegacyCollectorAttributionInference</c> re-derives collector attribution from, for evidence
    /// collected before spec 146 began recording the producing collector. Renaming or dropping it
    /// un-attributes this collector's accrued history.
    /// </summary>
    internal const string MetadataMarkerKey = "newsSearchFeedUrl";

    /// <inheritdoc />
    public string CollectorName => Name;

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

        // SPEC 177 OBSERVATION SIDECAR: one row per SURVIVING article (post relevance filter, post
        // within-feed URL dedupe, post per-feed cap), carrying the bounded provider payload + provenance.
        // The collector only ACCUMULATES rows — the collection orchestration owns identity minting and the
        // archive write; nothing here touches a filesystem store. Off-topic drops are deliberately NOT
        // archived against the company. The evidence mapping below is byte-identical to pre-177: the
        // description payload feeds ONLY this sidecar, never Title/RawText/metadata/ContentHash.
        var observations = new List<NewsObservationCandidate>();

        // PER-COMPANY COVERAGE (spec 169 / AD-16's 2026-08-03 amendment). Accumulated HERE, inside the feed
        // loop, because this is the only place that knows both the feed→company binding and the RAW returned
        // item count. Reconstructing it later from ItemsCollected is invalid: the merge discards per-collector
        // attribution, the aggregate carries no company, and the KEPT count is post-relevance-filter — so it
        // cannot reveal a censored result set, which is precisely the failure the evaluator must catch.
        //
        // Seeded with EVERY company in the context, not only those holding a feed, so a company with no
        // configured newssearch feed is recorded as MissingFeed rather than silently absent — an absent row
        // and a clean row must never be the same thing.
        var coverage = context.Companies.ToDictionary(
            c => c.Id, c => new CompanyCoverageAccumulator(EffectiveMaxRecords));

        // The EFFECTIVE clamped LOCAL retention limit — the same value BuildQuery sends, not the unclamped
        // config value. A raw result count that REACHES it means Radar stopped retaining there, so more may
        // have been available; it is never a measured provider ceiling.
        var effectiveLimit = EffectiveMaxRecords;

        // SPEC 190 audit input: one entry per SUCCESSFUL feed, holding the number of structurally valid items
        // that feed's response held (bounded by the reader's absolute parse ceiling). Feed order is
        // deterministic, and the summary below only takes a max/median, so the audit line is run-stable (AD-3).
        var successfulFeedObservedSizes = new List<int>();

        // Strictly sequential (never Task.WhenAll) + paced: a small polite pace between reads.
        var isFirstRequest = true;

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            // A feed bound to a company that is not in the context universe still counts as a checked feed,
            // but has no coverage row to record against (it belongs to no company Radar is watching here).
            var companyCoverage = coverage.GetValueOrDefault(feed.CompanyId);
            companyCoverage?.RecordExpectedFeed();

            var target = QueryFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                // A malformed token is a FEED FAILURE for coverage purposes: the company's configured source
                // was not read, so the window is not provably complete.
                companyCoverage?.RecordFeedFailure();
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
                companyCoverage?.RecordFeedFailure();
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
                observations.Add(MapToObservation(feed, target, article, companiesById));
                collectedForFeed++;
            }

            // SPEC 190 DIAGNOSTIC-ONLY PASS over the already-fetched response tail. It maps nothing: the
            // evidence/observation loop above ran over exactly the retained prefix, as it always has. This
            // only records which ADDITIONAL company-relevant items the same response held beyond Radar's own
            // local retention limit, so "the response contained exactly N valid items" can be told apart from
            // "the response contained more and Radar stopped reading".
            //
            // SPEC 195 §3: the URLs are handed to the COMPANY-level accumulator rather than deduped and
            // counted per feed. Per-feed counting overcounted twice — the same relevant tail URL returned by
            // two feeds counted twice, and a URL in feed A's tail counted as unadmitted even when feed B
            // admitted it in its retained prefix — because the per-feed integers were then SUMMED. The
            // accumulator now unions the retained prefixes and the relevant tails across every feed and takes
            // the difference once, which is also what makes the answer independent of feed iteration order.
            // Both sets live on the accumulator, deliberately SEPARATE from the evidence loop's `seenUrls`,
            // so the admission path's dedupe state is untouched. The URL strings, the OrdinalIgnoreCase
            // comparer and the `IsRelevant` predicate are exactly spec 190's — no canonicalization, no
            // tracking-query stripping, no wider semantic duplicate rule.
            var relevantTailUrls = new List<string>();
            foreach (var article in result.DiagnosticTail)
            {
                if (!IsRelevant(article.Title, target))
                {
                    continue;
                }

                relevantTailUrls.Add(article.Url);
            }

            // Coverage is recorded from the RAW reader counts, BEFORE the relevance filter and the per-feed
            // dedupe/cap loop. Reaching the effective limit means POSSIBLY truncated — Radar stopped
            // retaining at the limit IT asked for, so articles beyond it are unadmitted even when relevance
            // filtering later keeps far fewer items. `ObservedValidItemBeyondLocalLimit` upgrades that to
            // CONFIRMED local truncation; it never downgrades the fail-closed possible-truncation token, and
            // it is never a statement about the provider's own result set.
            companyCoverage?.RecordFeedSuccess(
                hitEffectiveResultLimit: result.Items.Count >= effectiveLimit,
                validItemsObserved: result.ValidItemsObserved,
                confirmedLocalTruncation: result.ObservedValidItemBeyondLocalLimit,
                // EVERY retained-prefix URL (result.Items, not the evidence loop's `seenUrls` — that set is
                // incomplete because the loop breaks once the per-feed cap is met).
                observedPrefixUrls: result.Items.Select(a => a.Url),
                relevantTailUrls: relevantTailUrls);

            successfulFeedObservedSizes.Add(result.ValidItemsObserved);

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

        // SPEC 190 §4 — ONE aggregated, deterministic, advice-free audit line. Everything in it is read off
        // the SAME already-fetched responses: no extra request, page or article fetch produced any of it,
        // and not one tail item became evidence or an observation candidate. "At the effective local limit"
        // is Radar's own retention limit being reached (possible truncation), NOT a measured provider
        // ceiling; "confirmed a response tail" is the stronger, observed fact.
        _logger.LogInformation(
            "News search local-limit audit (diagnostic only; no response-tail item was admitted): "
                + "{AtLimitCompanies} company/companies reached the effective LOCAL retention limit of "
                + "{EffectiveLimit}; {ConfirmedTailCompanies} confirmed a response tail beyond it; "
                + "{UnadmittedRelevantTailItems} additional unique company-relevant tail item(s) observed but "
                + "not admitted; observed valid response size across {SuccessfulFeeds} successful feed(s): "
                + "max {MaxObservedResponseSize}, median {MedianObservedResponseSize}; admitted under the "
                + "retained prefix (unchanged): {EvidenceItems} evidence item(s), "
                + "{ObservationCandidates} observation candidate(s).",
            coverage.Values.Count(a => a.HitEffectiveResultLimit),
            effectiveLimit,
            coverage.Values.Count(a => a.ConfirmedLocalTruncation),
            coverage.Values.Sum(a => a.UnadmittedRelevantTailItemCount),
            successfulFeedObservedSizes.Count,
            FormatStat(
                successfulFeedObservedSizes.Count == 0 ? null : (double)successfulFeedObservedSizes.Max()),
            FormatStat(Median(successfulFeedObservedSizes)),
            results.Count,
            observations.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());

        // Ordered by CompanyId so two runs over the same universe record byte-identical coverage (AD-3).
        var companyCoverageRows = coverage
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value.ToCoverage(kvp.Key))
            .ToArray();

        return new CollectionResult(results.ToArray(), summary, companyCoverageRows, observations.ToArray());
    }

    /// <summary>
    /// Projects one surviving article into its observation sidecar row. Purely a field projection — the
    /// bounded description, the publisher-site URL and the retrieval instant all come from the reader; the
    /// company/feed/query provenance from the configured binding; nothing is fetched or invented. The
    /// ticker prefers the seed company's own ticker, falling back to the feed token's ticker hint. A
    /// defaulted <see cref="NewsArticleItem.RetrievedAt"/> (a hand-built item) falls back to the collector's
    /// TimeProvider so an observation can never carry the meaningless default instant.
    /// </summary>
    private NewsObservationCandidate MapToObservation(
        CompanySourceFeed feed,
        QueryFeedTarget target,
        NewsArticleItem article,
        IReadOnlyDictionary<Guid, Company> companiesById)
    {
        var ticker = companiesById.TryGetValue(feed.CompanyId, out var company)
            && !string.IsNullOrWhiteSpace(company.Ticker)
                ? company.Ticker
                : target.Ticker;

        return new NewsObservationCandidate(
            CompanyId: feed.CompanyId,
            Ticker: ticker,
            Collector: Name,
            QueryPhrase: target.QueryPhrase,
            FeedId: feed.Id,
            FeedName: feed.Name,
            GoogleLandingUrl: article.Url,
            Publisher: article.SourceName,
            PublisherSiteUrl: article.PublisherSiteUrl,
            Headline: article.Title,
            DescriptionRaw: article.DescriptionRaw,
            DescriptionText: article.DescriptionText,
            DescriptionTruncated: article.DescriptionTruncated,
            PublishedAtUtc: article.PublishedAt,
            RetrievedAtUtc: article.RetrievedAt == default
                ? _timeProvider.GetUtcNow()
                : article.RetrievedAt);
    }

    /// <summary>
    /// The effective clamped per-feed request limit — the value <see cref="BuildQuery"/> actually sends. It
    /// is the ONE definition, read by both the query builder and the result-limit censoring test, so a
    /// clamped-vs-unclamped mismatch between "what we asked for" and "what we call truncated" is not
    /// expressible.
    /// </summary>
    private int EffectiveMaxRecords =>
        Math.Clamp(_options.MaxRecordsPerCompany, ApiMinRecords, ApiMaxRecords);

    private NewsSearchQuery BuildQuery(QueryFeedTarget target) => new(
        QueryPhrase: target.QueryPhrase,
        MaxRecords: EffectiveMaxRecords,
        EnglishOnly: _options.EnglishOnly);

    /// <summary>
    /// Accumulates one company's per-feed coverage facts across the feed loop and projects them into the
    /// durable <see cref="CollectorCompanyCoverage"/> row. Kept as a tiny mutable accumulator (rather than
    /// rebuilding an immutable record per feed) so the loop reads as what it is: three counters plus a flag.
    /// </summary>
    private sealed class CompanyCoverageAccumulator(int effectiveResultLimit)
    {
        private int _expected;
        private int _succeeded;
        private bool _failed;
        private bool _hitLimit;
        private int _maxValidItemsObserved;
        private bool _confirmedLocalTruncation;

        // Spec 195 §3: company-scoped, NOT per-feed. `_observedPrefixUrls` is the union of every successful
        // feed's retained reader prefix; `_relevantTailUrls` is the union of every company-relevant URL seen
        // in any diagnostic tail. The unadmitted count is their set difference, taken ONCE at projection
        // time, so it counts each URL once across the whole company and cannot depend on feed iteration
        // order. Both are diagnostic-only: nothing here is shared with, or mutates, the evidence/observation
        // admission loop's own dedupe set. The comparer is spec 190's OrdinalIgnoreCase URL equality.
        private readonly HashSet<string> _observedPrefixUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _relevantTailUrls = new(StringComparer.OrdinalIgnoreCase);

        public void RecordExpectedFeed() => _expected++;

        public void RecordFeedFailure() => _failed = true;

        /// <summary>
        /// Records one SUCCESSFUL feed. <paramref name="hitEffectiveResultLimit"/> keeps its exact pre-190
        /// fail-closed meaning (possible truncation); the spec-190 diagnostics are additive and
        /// observational — the MAX observed valid response size across the company's feeds, whether any feed
        /// CONFIRMED local truncation, and (spec 195 §3) the company-wide union of retained-prefix URLs and of
        /// company-relevant diagnostic-tail URLs, differenced once at projection time.
        /// </summary>
        public void RecordFeedSuccess(
            bool hitEffectiveResultLimit,
            int validItemsObserved,
            bool confirmedLocalTruncation,
            IEnumerable<string> observedPrefixUrls,
            IEnumerable<string> relevantTailUrls)
        {
            _succeeded++;
            _hitLimit |= hitEffectiveResultLimit;
            _maxValidItemsObserved = Math.Max(_maxValidItemsObserved, validItemsObserved);
            _confirmedLocalTruncation |= confirmedLocalTruncation;

            foreach (var url in observedPrefixUrls)
            {
                _observedPrefixUrls.Add(url);
            }

            foreach (var url in relevantTailUrls)
            {
                _relevantTailUrls.Add(url);
            }
        }

        /// <summary>True when at least one of this company's feeds reached the effective LOCAL retention limit.</summary>
        public bool HitEffectiveResultLimit => _hitLimit;

        /// <summary>True when at least one feed's response held a valid item BEYOND that local limit.</summary>
        public bool ConfirmedLocalTruncation => _confirmedLocalTruncation;

        /// <summary>
        /// Unique company-relevant tail URLs observed for this company but deliberately not admitted:
        /// <c>relevantTailUrls EXCEPT observedPrefixUrls</c>, counted once across the whole company (spec
        /// 195 §3). A tail URL that ANY feed retained in its prefix is NOT unadmitted — some feed admitted
        /// it — and a URL seen in two feeds' tails counts once.
        /// </summary>
        public int UnadmittedRelevantTailItemCount =>
            _relevantTailUrls.Count(url => !_observedPrefixUrls.Contains(url));

        public CollectorCompanyCoverage ToCoverage(Guid companyId)
        {
            var issues = new List<string>(CollectionCoverageIssues.All.Count);

            // No configured feed at all: recorded, never omitted. A company Radar never asked about must not
            // be mistakable for a company Radar asked about and heard nothing from.
            if (_expected == 0)
            {
                issues.Add(CollectionCoverageIssues.MissingFeed);
            }

            // Defensive on BOTH the flag and the counts: a future edit that forgets one still records the
            // failure rather than silently certifying a window as complete.
            if (_failed || _succeeded < _expected)
            {
                issues.Add(CollectionCoverageIssues.SourceFailure);
            }

            if (_hitLimit)
            {
                issues.Add(CollectionCoverageIssues.ResultLimitReached);
            }

            return new CollectorCompanyCoverage(
                CompanyId: companyId,
                ExpectedFeedCount: _expected,
                SuccessfulFeedCount: _succeeded,
                HitEffectiveResultLimit: _hitLimit,
                Issues: CollectionCoverageIssues.Canonicalize(issues),
                // Spec 190 diagnostics. Recorded on EVERY row this collector writes (including a
                // MissingFeed/failed row, where the observed counts are honestly zero) — this collector does
                // record them, so `null` stays reserved for rows written by something that does not.
                EffectiveResultLimit: effectiveResultLimit,
                MaxValidItemsObserved: _maxValidItemsObserved,
                ConfirmedLocalTruncation: _confirmedLocalTruncation,
                UnadmittedRelevantTailItemCount: UnadmittedRelevantTailItemCount);
        }
    }

    /// <summary>
    /// The median observed response size, deterministic by construction: the input is sorted and, on an EVEN
    /// count, the answer is the MEAN of the two central values (stated because the convention is otherwise
    /// ambiguous). An empty input has no median.
    /// <para>
    /// It deliberately does NOT reuse <c>AttentionArrivalScreenEvaluator.Median</c>: that helper is
    /// <c>internal</c> to <c>Radar.Application</c> — Infrastructure cannot reach it — and it is defined over
    /// the efficacy screen's <c>double</c> δ values, a different quantity from a count of response items.
    /// Sharing it would mean either widening an Application internal for a log line or moving an efficacy
    /// primitive into Infrastructure; both are worse than eight documented lines. The two definitions agree
    /// on the even-count convention on purpose.
    /// </para>
    /// </summary>
    private static double? Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Renders an audit statistic invariantly, or <c>n/a</c> when there was nothing to measure — a measured
    /// zero and an unmeasured one are different facts, so the line never prints one as the other.
    /// </summary>
    private static string FormatStat(double? value) =>
        value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";

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
    /// so the relevance check runs against the real headline only — via the ONE shared rule
    /// (<see cref="GoogleNewsHeadline"/>, extracted by spec 179; the spec-179 duplicate-headline collapse
    /// uses the same rule, so the two cannot drift).
    /// </summary>
    private static string? StripPublisherSuffix(string? title) =>
        GoogleNewsHeadline.StripPublisherSuffix(title);

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
        // false breadth — feed.Name is a single per-company-constant bucket, so every blank-publisher article
        // collapses to that same one value under the formula's Distinct() and together they add at most 1 to
        // breadth (not one per article — and not 0: the non-blank fallback IS counted, unlike a truly blank name).
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
            [MetadataMarkerKey] = feed.Url,
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
