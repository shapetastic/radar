using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// Reads the per-company <c>trademarks</c> source feeds configured on the <see cref="CollectionContext"/>
/// (each feed's <c>Url</c> is an <c>owner=&lt;organization name&gt;</c> token) and turns each owner's recent
/// trademark-filing activity into exactly ONE raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.Trademark"/> carrying the fixed spec-130 trademark phrase
/// (<c>trademark activity (recent filings)</c>) plus the deterministic filing count + owner/window provenance
/// metadata (NO AI). The extractor (spec 130) maps that phrase to a NEUTRAL <c>TrademarkActivity</c> signal: a
/// single-window trademark-filing COUNT cannot tell genuine brand-activity acceleration from an
/// always-prolific filer, so v1 never misfires Trajectory; directional SURGE detection vs the accrued history
/// is deferred to slice B. Does not score, resolve, or persist. A feed whose token is malformed, whose API key
/// is missing, or whose read fails contributes no evidence and is logged as a Warning; an owner with zero
/// recent filings is a valid zero-filing snapshot, not an error. Company hints come only from the configured
/// feed→company binding — tickers are never invented (provenance is sacred). Evidence Title/RawText are
/// synthesized from the fixed phrase + real count + owner/window + retrieved timestamp only — <b>never raw
/// mark texts</b>: a wordmark like "LAUNCHPAD" or "NEW HORIZON" would otherwise trip the extractor's
/// <c>launches</c>/<c>new platform</c> rules (keyword contamination). Sample marks live in evidence METADATA
/// only, which the extractor never scans. Factual, advice-free (AD-9) — the direction is internal to scoring
/// (and is Neutral in v1).
/// </summary>
internal sealed class TrademarkActivityCollector : IEvidenceCollector
{
    private readonly ITrademarkSearchReader _reader;
    private readonly ILogger<TrademarkActivityCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TrademarkCollectorOptions _options;

    public TrademarkActivityCollector(
        ITrademarkSearchReader reader,
        ILogger<TrademarkActivityCollector> logger,
        TimeProvider timeProvider,
        TrademarkCollectorOptions options)
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

    public string CollectorName => "trademarks";

    public EvidenceSourceType SourceType => EvidenceSourceType.Trademark;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("trademarks");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = TrademarkFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed trademark feed token"));
                _logger.LogWarning(
                    "Trademarks feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'owner=<organization name>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // One instant per feed, from the injected TimeProvider (UTC): both the filing-date floor and the
            // snapshot's retrievedAt are derived from this single 'now' so they can never disagree by a day if a
            // run happens to cross a UTC date boundary between two GetUtcNow() calls.
            var now = _timeProvider.GetUtcNow();
            var filingFloor = DateOnly.FromDateTime(
                (now - TimeSpan.FromDays(_options.LookbackDays)).UtcDateTime);

            var result = await _reader.ReadAsync(target.OwnerName, filingFloor, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                // A MissingApiKey outcome is logged clearly so an operator sees WHY trademarks produced nothing.
                _logger.LogWarning(
                    "Trademarks feed '{FeedName}' (owner '{Owner}') could not be read: {Detail}; skipping.",
                    feed.Name,
                    target.OwnerName,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Exactly ONE snapshot evidence per feed per run — the filing count is already the aggregate.
            results.Add(MapToEvidence(feed, target, filingFloor, now, result.Result!, hints));
        }

        _logger.LogInformation(
            "Trademark activity collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
                + "{ItemsCollected} snapshot(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed,
        TrademarkFeedTarget target,
        DateOnly filingFloor,
        DateTimeOffset retrievedAtUtc,
        TrademarkSearchResult search,
        IReadOnlyList<string> hints)
    {
        // The whole window snapshot shares the caller's single per-feed instant: a bounded filing window has no
        // single publish date, so PublishedAt = CollectedAt = retrievedAtUtc (the same 'now' the filingFloor was
        // derived from, UTC, injected TimeProvider).
        var retrievedAtToken = retrievedAtUtc.ToString("o", CultureInfo.InvariantCulture);
        var filingFloorToken = filingFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var count = search.FilingCount;
        var owner = target.OwnerName;

        // NO-CONTAMINATION RULE (spec 130): Title/RawText carry ONLY the fixed phrase + numeric count +
        // owner/window — NEVER raw mark texts. A wordmark like "LAUNCHPAD" would otherwise trip the extractor's
        // 'launches'/'new platform' rules. Sample marks go in metadata ONLY (the extractor never scans metadata
        // for phrases).
        var title =
            $"Trademark activity (recent filings) — {count} trademark applications filed by '{owner}' "
                + $"in the last {_options.LookbackDays} days";

        // The RawText timestamp makes each run's Title+RawText ContentHash distinct, so every run persists a
        // distinct timestamped snapshot evidence — this accrued, timestamped filing-count history IS the record
        // the deferred slice-B surge detection will read (no separate history store is built).
        var rawText =
            $"Owner '{owner}': {count} trademark applications filed since {filingFloorToken}, as of "
                + $"{retrievedAtToken}. Signal: trademark activity (recent filings).";

        // Bounded provenance/debug sample — metadata only, NOT scanned by the extractor.
        var sampleMarks = string.Join(
            " | ",
            search.Filings.Take(_options.MaxSampleMarks).Select(f => $"{f.SerialNumber}: {f.MarkText}"));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. USPTO
            // trademark filings are an authoritative public record — on par with the SEC/USASpending High.
            ["quality"] = "High",
            ["trademarkFeedUrl"] = feed.Url,
            ["owner"] = owner,
            // The filing count is the accrued trademark history slice B will read.
            ["filingCount"] = count.ToString(CultureInfo.InvariantCulture),
            ["lookbackDays"] = _options.LookbackDays.ToString(CultureInfo.InvariantCulture),
            ["filingFloor"] = filingFloorToken,
            ["sampleMarks"] = sampleMarks,
            // The API's own grand total — a cross-check when it exceeds the bounded page count.
            ["apiReportedTotal"] = search.ApiReportedTotal.ToString(CultureInfo.InvariantCulture),
            ["retrievedAtUtc"] = retrievedAtToken,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            // Provenance: the USPTO trademark query URL (one builder produces both the fetched URL and this link).
            SourceUrl: _reader.QueryUrl(owner, filingFloor),
            Title: title,
            RawText: rawText,
            PublishedAt: retrievedAtUtc,
            CollectedAt: retrievedAtUtc,
            Metadata: metadata)
        {
            CompanyHints = hints,
        };
    }
}
