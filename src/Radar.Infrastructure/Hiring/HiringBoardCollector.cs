using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Hiring;

/// <summary>
/// Reads the per-company <c>hiringats</c> source feeds configured on the <see cref="CollectionContext"/>
/// (each feed's <c>Url</c> is a <c>platform=…&amp;board=…</c> token naming that company's public ATS job
/// board) and turns each board snapshot into exactly ONE raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.JobPosting"/> carrying the fixed spec-103 hiring phrase
/// (<c>hiring activity (open roles)</c>) plus the deterministic open-role counts (total,
/// senior/leadership, engineering/R&amp;D via <see cref="JobTitleClassifier"/> — NO AI). The extractor
/// (spec 103) maps that phrase to a NEUTRAL <c>HiringActivity</c> signal: a single-snapshot open-role
/// COUNT cannot tell genuine expansion from an always-large hirer, so v1 never misfires Trajectory;
/// directional SURGE detection vs the accrued history is deferred to slice B. Does not score, resolve, or
/// persist. A feed whose token is malformed, whose platform has no reader, or whose board fails to read
/// contributes no evidence and is logged as a Warning; a board with zero openings is a valid zero-role
/// snapshot, not an error. Company hints come only from the configured feed→company binding — tickers are
/// never invented (provenance is sacred). Evidence Title/RawText are synthesized from the fixed phrase +
/// real counts + platform/board only — <b>never raw job titles</b>: a title like "VP, Strategic
/// Partnerships" would otherwise trip the extractor's <c>partnership</c> rule (keyword contamination).
/// Sample titles live in evidence METADATA only, which the extractor never scans. Factual, advice-free
/// (AD-9) — the direction is internal to scoring (and is Neutral in v1).
/// </summary>
internal sealed class HiringBoardCollector : IEvidenceCollector
{
    private readonly IReadOnlyDictionary<string, IJobBoardReader> _readersByPlatform;
    private readonly ILogger<HiringBoardCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly HiringCollectorOptions _options;

    public HiringBoardCollector(
        IEnumerable<IJobBoardReader> readers,
        ILogger<HiringBoardCollector> logger,
        TimeProvider timeProvider,
        HiringCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        // Build the platform→reader map once (case-insensitive, matching the feed token's platform value).
        // Duplicate platforms fail fast: silently keeping the last-registered reader would make behaviour
        // depend on DI registration order and hide a misconfiguration.
        var readersByPlatform = new Dictionary<string, IJobBoardReader>(StringComparer.OrdinalIgnoreCase);
        foreach (var reader in readers)
        {
            if (!readersByPlatform.TryAdd(reader.Platform, reader))
            {
                throw new InvalidOperationException(
                    $"Multiple {nameof(IJobBoardReader)} registrations claim hiring platform "
                        + $"'{reader.Platform}'; each platform must have exactly one reader.");
            }
        }

        _readersByPlatform = readersByPlatform;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options;
    }

    public string CollectorName => "hiring-ats";

    public EvidenceSourceType SourceType => EvidenceSourceType.JobPosting;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("hiringats");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = HiringFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed hiringats feed token"));
                _logger.LogWarning(
                    "Hiring feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'platform=<greenhouse|lever>&board=<token>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            if (!_readersByPlatform.TryGetValue(target.Platform, out var reader))
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, $"unsupported hiring platform '{target.Platform}'"));
                _logger.LogWarning(
                    "Hiring feed '{FeedName}' names unsupported platform '{Platform}' "
                        + "(supported: {SupportedPlatforms}); skipping.",
                    feed.Name,
                    target.Platform,
                    string.Join(", ", _readersByPlatform.Keys.OrderBy(k => k, StringComparer.Ordinal)));
                continue;
            }

            var result = await reader.ReadAsync(target.BoardToken, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "Hiring feed '{FeedName}' ({Platform} board '{BoardToken}') could not be read: "
                        + "{Detail}; skipping.",
                    feed.Name,
                    target.Platform,
                    target.BoardToken,
                    result.Detail);
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Exactly ONE snapshot evidence per feed per run — the board read is already the aggregate.
            results.Add(MapToEvidence(feed, target, reader, result.Result!, hints));
        }

        _logger.LogInformation(
            "Hiring board collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
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
        HiringFeedTarget target,
        IJobBoardReader reader,
        JobBoardResult board,
        IReadOnlyList<string> hints)
    {
        // One instant for the whole snapshot: a live board has no per-role publish date, so
        // PublishedAt = CollectedAt = now (UTC, injected TimeProvider).
        var retrievedAtUtc = _timeProvider.GetUtcNow();
        var retrievedAtToken = retrievedAtUtc.ToString("o", CultureInfo.InvariantCulture);

        var total = board.TotalRoles;
        var (senior, engineering) = JobTitleClassifier.Classify(board.Titles);

        // Canonical platform name from the reader, NOT the feed token's verbatim casing: the reader lookup
        // is case-insensitive, so a feed configured as 'Greenhouse' must stamp the same Title/RawText/
        // metadata (and therefore the same ContentHash) as one configured 'greenhouse'.
        var platform = reader.Platform;

        // NO-CONTAMINATION RULE (spec 103): Title/RawText carry ONLY the fixed phrase + numeric counts +
        // platform/board — NEVER raw job titles. A title like "VP, Strategic Partnerships" would otherwise
        // trip the extractor's 'partnership' rule. Sample titles go in metadata ONLY (the extractor never
        // scans metadata for phrases).
        var title =
            $"Hiring activity (open roles) — {total} open roles ({senior} senior/leadership, "
                + $"{engineering} engineering/R&D) via {platform} board '{target.BoardToken}'";

        // The RawText timestamp makes each run's Title+RawText ContentHash distinct, so every run persists
        // a distinct timestamped snapshot evidence — this accrued, timestamped open-role history IS the
        // record the deferred slice-B surge detection will read (no separate history store is built).
        var rawText =
            $"{platform} job board '{target.BoardToken}': {total} open roles as of "
                + $"{retrievedAtToken}; {senior} senior/leadership, {engineering} engineering/R&D. "
                + "Signal: hiring activity (open roles).";

        // Bounded provenance/debug sample — metadata only, NOT scanned by the extractor.
        var sampleTitles = string.Join(" | ", board.Titles.Take(_options.MaxSampleTitles));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. A
            // company's own careers page is primary but unaudited — below the SEC/USASpending High,
            // matching the news Medium.
            ["quality"] = "Medium",
            ["hiringFeedUrl"] = feed.Url,
            ["platform"] = platform,
            ["board"] = target.BoardToken,
            // The three counts are the accrued hiring history slice B will read.
            ["totalRoles"] = total.ToString(CultureInfo.InvariantCulture),
            ["seniorRoles"] = senior.ToString(CultureInfo.InvariantCulture),
            ["engRoles"] = engineering.ToString(CultureInfo.InvariantCulture),
            ["sampleTitles"] = sampleTitles,
            ["retrievedAtUtc"] = retrievedAtToken,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            // Provenance: the exact board API URL the reader fetched (one builder, one URL).
            SourceUrl: reader.BoardUrl(target.BoardToken),
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
