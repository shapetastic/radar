using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Patents;

/// <summary>
/// Reads the per-company <c>patents</c> source feeds configured on the <see cref="CollectionContext"/>
/// (each feed's <c>Url</c> is an <c>assignee=&lt;organization name&gt;</c> token) and turns each assignee's
/// recent granted-patent activity into exactly ONE raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.Patent"/> carrying the fixed spec-127 patent phrase
/// (<c>patent activity (recent grants)</c>) plus the deterministic grant count + assignee/window provenance
/// metadata (NO AI). The extractor (spec 127) maps that phrase to a NEUTRAL <c>PatentActivity</c> signal: a
/// single-window granted-patent COUNT cannot tell genuine acceleration from an always-prolific filer, so v1
/// never misfires Trajectory; directional SURGE detection vs the accrued history is deferred to slice B.
/// Does not score, resolve, or persist. A feed whose token is malformed, whose API key is missing, or whose
/// read fails contributes no evidence and is logged as a Warning; an assignee with zero recent grants is a
/// valid zero-grant snapshot, not an error. Company hints come only from the configured feed→company
/// binding — tickers are never invented (provenance is sacred). Evidence Title/RawText are synthesized from
/// the fixed phrase + real count + assignee/window + retrieved timestamp only — <b>never raw patent
/// titles</b>: a title like "System for autonomous launch integration" would otherwise trip the extractor's
/// <c>launches</c>/<c>integrates</c>/<c>new platform</c> rules (keyword contamination). Sample titles live
/// in evidence METADATA only, which the extractor never scans. Factual, advice-free (AD-9) — the direction
/// is internal to scoring (and is Neutral in v1).
/// </summary>
internal sealed class PatentActivityCollector : IEvidenceCollector
{
    private readonly IPatentSearchReader _reader;
    private readonly ILogger<PatentActivityCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PatentCollectorOptions _options;

    public PatentActivityCollector(
        IPatentSearchReader reader,
        ILogger<PatentActivityCollector> logger,
        TimeProvider timeProvider,
        PatentCollectorOptions options)
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

    public string CollectorName => "patents";

    public EvidenceSourceType SourceType => EvidenceSourceType.Patent;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("patents");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = PatentFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed patents feed token"));
                _logger.LogWarning(
                    "Patents feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'assignee=<organization name>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // The grant-date floor: today minus the lookback window, from the injected TimeProvider (UTC).
            var grantFloor = DateOnly.FromDateTime(
                (_timeProvider.GetUtcNow() - TimeSpan.FromDays(_options.LookbackDays)).UtcDateTime);

            var result = await _reader.ReadAsync(target.AssigneeName, grantFloor, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                // A MissingApiKey outcome is logged clearly so an operator sees WHY patents produced nothing.
                _logger.LogWarning(
                    "Patents feed '{FeedName}' (assignee '{Assignee}') could not be read: {Detail}; skipping.",
                    feed.Name,
                    target.AssigneeName,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Exactly ONE snapshot evidence per feed per run — the grant count is already the aggregate.
            results.Add(MapToEvidence(feed, target, grantFloor, result.Result!, hints));
        }

        _logger.LogInformation(
            "Patent activity collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
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
        PatentFeedTarget target,
        DateOnly grantFloor,
        PatentSearchResult search,
        IReadOnlyList<string> hints)
    {
        // One instant for the whole window snapshot: a bounded grant window has no single publish date, so
        // PublishedAt = CollectedAt = now (UTC, injected TimeProvider).
        var retrievedAtUtc = _timeProvider.GetUtcNow();
        var retrievedAtToken = retrievedAtUtc.ToString("o", CultureInfo.InvariantCulture);
        var grantFloorToken = grantFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var count = search.GrantCount;
        var assignee = target.AssigneeName;

        // NO-CONTAMINATION RULE (spec 127): Title/RawText carry ONLY the fixed phrase + numeric count +
        // assignee/window — NEVER raw patent titles. A title like "System for autonomous launch integration"
        // would otherwise trip the extractor's 'launches'/'new platform' rules. Sample titles go in metadata
        // ONLY (the extractor never scans metadata for phrases).
        var title =
            $"Patent activity (recent grants) — {count} patents granted to '{assignee}' "
                + $"in the last {_options.LookbackDays} days";

        // The RawText timestamp makes each run's Title+RawText ContentHash distinct, so every run persists a
        // distinct timestamped snapshot evidence — this accrued, timestamped grant-count history IS the record
        // the deferred slice-B surge detection will read (no separate history store is built).
        var rawText =
            $"Assignee '{assignee}': {count} patents granted since {grantFloorToken}, as of "
                + $"{retrievedAtToken}. Signal: patent activity (recent grants).";

        // Bounded provenance/debug sample — metadata only, NOT scanned by the extractor.
        var sampleTitles = string.Join(
            " | ",
            search.Grants.Take(_options.MaxSampleTitles).Select(g => $"{g.PatentId}: {g.Title}"));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. Granted
            // patents are an authoritative public record — on par with the SEC/USASpending High.
            ["quality"] = "High",
            ["patentsFeedUrl"] = feed.Url,
            ["assignee"] = assignee,
            // The grant count is the accrued patent history slice B will read.
            ["grantCount"] = count.ToString(CultureInfo.InvariantCulture),
            ["lookbackDays"] = _options.LookbackDays.ToString(CultureInfo.InvariantCulture),
            ["grantFloor"] = grantFloorToken,
            ["sampleTitles"] = sampleTitles,
            // The API's own grand total (total_hits) — a cross-check when it exceeds the bounded page count.
            ["apiReportedTotal"] = search.ApiReportedTotal.ToString(CultureInfo.InvariantCulture),
            ["retrievedAtUtc"] = retrievedAtToken,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            // Provenance: the PatentsView query URL (one builder produces both the fetched URL and this link).
            SourceUrl: _reader.QueryUrl(assignee, grantFloor),
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
