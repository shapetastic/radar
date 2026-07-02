using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// Reads the per-company <c>usaspending</c> source feeds configured on the <see cref="CollectionContext"/>
/// (each feed's <c>Url</c> is a token carrying that company's exact <c>recipient_id</c> and fuzzy
/// <c>recipient_search_text</c>) and turns each recent federal contract award into a raw
/// <see cref="CollectedEvidence"/> of type <see cref="EvidenceSourceType.GovernmentContract"/>. Does not
/// score, resolve, or persist — it only answers "what recent contract awards did this recipient win?" A
/// feed that fails to read (or whose token is malformed) contributes no evidence and is logged as a Warning
/// (the reader reports the failure mode); a recipient with zero matching awards degrades to zero evidence,
/// not an error. Because <c>recipient_search_text</c> is fuzzy (it can match subsidiaries/unrelated
/// entities and the API silently firehoses an unsupported key), results are CLIENT-SIDE-FILTERED to the
/// feed's exact <c>recipient_id</c>. Company hints come only from the configured feed→company binding —
/// tickers are never invented (provenance is sacred). Evidence Title/RawText are synthesized from real award
/// metadata; no award body text is fabricated.
/// </summary>
internal sealed class UsaSpendingContractCollector : IEvidenceCollector
{
    // The API only searches back to 2007-10-01; never send an earlier start_date.
    private static readonly DateTimeOffset TimePeriodFloor =
        new(2007, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private const int ApiMaxLimit = 100;

    private readonly IUsaSpendingAwardReader _reader;
    private readonly ILogger<UsaSpendingContractCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UsaSpendingCollectorOptions _options;

    public UsaSpendingContractCollector(
        IUsaSpendingAwardReader reader,
        ILogger<UsaSpendingContractCollector> logger,
        TimeProvider timeProvider,
        UsaSpendingCollectorOptions options)
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

    public string CollectorName => "usaspending";

    public EvidenceSourceType SourceType => EvidenceSourceType.GovernmentContract;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("usaspending");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = UsaSpendingFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed usaspending feed token"));
                _logger.LogWarning(
                    "USASpending feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'recipientId=<id>&recipientSearchText=<name>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            var query = BuildQuery(target);

            var result = await _reader.ReadAsync(query, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "USASpending feed '{FeedName}' (recipient '{SearchText}', id {RecipientId}) could not be "
                        + "read: {Detail}; skipping.",
                    feed.Name,
                    target.RecipientSearchText,
                    target.RecipientId,
                    result.Detail);
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Dedupe within this feed by generated_internal_id so an award appears at most once.
            var seenAwards = new HashSet<string>(StringComparer.Ordinal);
            var collectedForFeed = 0;

            foreach (var award in result.Items)
            {
                // CLIENT-SIDE-FILTER: keep only rows whose recipient_id is the feed's exact seeded key.
                // The fuzzy search can return subsidiaries/unrelated entities; this equality check is what
                // guarantees we never attach another entity's awards to our company.
                if (!string.Equals(award.RecipientId, target.RecipientId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (collectedForFeed >= _options.MaxAwardsPerCompany)
                {
                    // The API sorts by Last Modified Date desc, so the first N surviving rows are the ones
                    // with the most recent activity — the awards most likely to fall in the scoring window.
                    break;
                }

                if (!seenAwards.Add(award.GeneratedInternalId))
                {
                    continue;
                }

                results.Add(MapToEvidence(feed, award, hints));
                collectedForFeed++;
            }
        }

        _logger.LogInformation(
            "USASpending contract collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
                + "{ItemsCollected} award(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private UsaSpendingAwardQuery BuildQuery(UsaSpendingFeedTarget target)
    {
        var now = _timeProvider.GetUtcNow();

        // Clamp the effective lookback before AddDays: a config-driven LookbackDays that is negative
        // or absurdly large would otherwise overflow DateTimeOffset.AddDays and crash the collector.
        // The earliest valid start is the API floor (2007-10-01), so cap the lookback at that distance.
        var maxLookbackDays = (now - TimePeriodFloor).TotalDays;
        var effectiveLookbackDays = Math.Clamp((double)_options.LookbackDays, 0d, maxLookbackDays);

        var start = now.AddDays(-effectiveLookbackDays);
        if (start < TimePeriodFloor)
        {
            start = TimePeriodFloor;
        }

        return new UsaSpendingAwardQuery(
            SearchText: target.RecipientSearchText,
            StartDate: start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate: now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AwardTypeCodes: _options.AwardTypeCodes,
            Limit: Math.Min(_options.MaxAwardsPerCompany, ApiMaxLimit));
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed, UsaSpendingAwardItem award, IReadOnlyList<string> hints)
    {
        var amount = award.AwardAmount.ToString("N0", CultureInfo.InvariantCulture);

        // Title: synthesized from REAL fields only.
        var title =
            $"Federal contract award {award.AwardId} — {award.AwardingAgency} → {award.RecipientName} "
                + $"(${amount}, {award.StartDate})";

        // RawText: synthesized from REAL metadata only. The Award ID, generated_internal_id and recipient_id
        // are included so two distinct awards never collide under the mapper's Title+RawText ContentHash
        // dedupe. No award body text is fabricated.
        var description = string.IsNullOrWhiteSpace(award.Description)
            ? string.Empty
            : $" Description: {award.Description}.";

        var rawText =
            $"Federal contract award {award.AwardId} (generated_internal_id {award.GeneratedInternalId}, "
                + $"recipient_id {award.RecipientId}): {award.AwardingAgency} awarded {award.RecipientName} "
                + $"${amount} starting {award.StartDate}.{description}";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. A
            // federal contract award is an official primary record, so it declares a High baseline (above
            // the press-release Medium) — reinforcing the source-diversity/confidence story like SEC filings.
            ["quality"] = "High",
            ["usaSpendingFeedUrl"] = feed.Url,
            ["awardId"] = award.AwardId,
            ["generatedInternalId"] = award.GeneratedInternalId,
            ["recipientId"] = award.RecipientId,
            ["awardingAgency"] = award.AwardingAgency,
            ["awardAmount"] = award.AwardAmount.ToString(CultureInfo.InvariantCulture),
            ["startDate"] = award.StartDate,
            ["lastModifiedDate"] = award.LastModifiedDate ?? string.Empty,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            SourceUrl: award.AwardUrl,
            Title: title,
            RawText: rawText,
            // Observed instant = the award's Last Modified Date (most recent activity), falling back to the
            // period-of-performance Start Date. The PoP start is often years old for multi-year vehicles, so
            // using it as PublishedAt dropped recently-active awards out of the scoring window; Last Modified
            // Date reflects recent activity and keeps windowing/recency correct.
            PublishedAt: ParseObservedInstant(award.LastModifiedDate) ?? ParseObservedInstant(award.StartDate),
            CollectedAt: _timeProvider.GetUtcNow(),
            Metadata: metadata)
        {
            CompanyHints = hints,
        };
    }

    /// <summary>
    /// Parses a USASpending date to a UTC instant, accepting both the <c>Last Modified Date</c> form
    /// (<c>yyyy-MM-dd HH:mm:ss</c>) and the date-only <c>Start Date</c> form (<c>yyyy-MM-dd</c>), invariant
    /// culture. Returns <see langword="null"/> for an absent/unparseable value rather than throwing.
    /// </summary>
    private static DateTimeOffset? ParseObservedInstant(string? value)
    {
        if (DateTime.TryParseExact(
                value,
                ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        }

        return null;
    }
}
