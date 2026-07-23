using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fcc;

/// <summary>
/// Reads the per-company <c>fccauth</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is a <c>grantee=&lt;organization name&gt;</c> token) and turns each grantee's recent FCC
/// equipment-authorization activity into exactly ONE raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.EquipmentAuthorization"/> carrying the fixed spec-128 authorization phrase
/// (<c>fcc equipment authorization (recent grants)</c>) plus the deterministic grant count + grantee/window
/// provenance metadata (NO AI). A company must obtain FCC certification BEFORE it may sell a wireless/electronic
/// device in the US, so a new authorization leads product shipment by weeks to months — a "before the market
/// notices" hardware signal. The extractor (spec 128) maps that phrase to a NEUTRAL
/// <c>EquipmentAuthorization</c> signal: a single-window authorization COUNT cannot tell genuine product-cadence
/// acceleration from an always-prolific filer, so v1 never misfires Trajectory; directional NEW-authorization /
/// surge detection vs the accrued history is deferred to slice B. Does not score, resolve, or persist. A feed
/// whose token is malformed or whose read fails contributes no evidence and is logged as a Warning; a grantee
/// with zero recent authorizations is a valid zero-grant snapshot, not an error. Company hints come only from
/// the configured feed→company binding — tickers are never invented (provenance is sacred). Evidence
/// Title/RawText are synthesized from the fixed phrase + real count + grantee/window + retrieved timestamp only
/// — <b>never raw product descriptions or FCC-ID free text</b>: a description like "wireless launch controller"
/// would otherwise trip the extractor's <c>launches</c> rule (keyword contamination). Sample authorizations live
/// in evidence METADATA only, which the extractor never scans. Factual, advice-free (AD-9) — the direction is
/// internal to scoring (and is Neutral in v1).
/// </summary>
internal sealed class FccEquipmentAuthorizationCollector : IEvidenceCollector
{
    private readonly IFccAuthReader _reader;
    private readonly ILogger<FccEquipmentAuthorizationCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FccCollectorOptions _options;

    public FccEquipmentAuthorizationCollector(
        IFccAuthReader reader,
        ILogger<FccEquipmentAuthorizationCollector> logger,
        TimeProvider timeProvider,
        FccCollectorOptions options)
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

    public string CollectorName => "fccauth";

    public EvidenceSourceType SourceType => EvidenceSourceType.EquipmentAuthorization;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("fccauth");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = FccFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed fcc feed token"));
                _logger.LogWarning(
                    "FCC auth feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'grantee=<organization name>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // The grant-date floor: today minus the lookback window, from the injected TimeProvider (UTC).
            var grantFloor = DateOnly.FromDateTime(
                (_timeProvider.GetUtcNow() - TimeSpan.FromDays(_options.LookbackDays)).UtcDateTime);

            var result = await _reader.ReadAsync(target.GranteeName, grantFloor, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "FCC auth feed '{FeedName}' (grantee '{Grantee}') could not be read: {Detail}; skipping.",
                    feed.Name,
                    target.GranteeName,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Exactly ONE snapshot evidence per feed per run — the grant count is already the aggregate.
            results.Add(MapToEvidence(feed, target, grantFloor, result.Result!, hints));
        }

        _logger.LogInformation(
            "FCC equipment-authorization collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
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
        FccFeedTarget target,
        DateOnly grantFloor,
        FccAuthResult read,
        IReadOnlyList<string> hints)
    {
        // One instant for the whole window snapshot: a bounded grant window has no single publish date, so
        // PublishedAt = CollectedAt = now (UTC, injected TimeProvider).
        var retrievedAtUtc = _timeProvider.GetUtcNow();
        var retrievedAtToken = retrievedAtUtc.ToString("o", CultureInfo.InvariantCulture);
        var grantFloorToken = grantFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var count = read.GrantCount;
        var grantee = target.GranteeName;

        // When the reader hit its page cap with more valid grants remaining, the count is a FLOOR, not an exact
        // total — render it as "{count}+" so Title/RawText don't imply a full count the parse never established.
        var countToken = read.Truncated
            ? $"{count.ToString(CultureInfo.InvariantCulture)}+"
            : count.ToString(CultureInfo.InvariantCulture);

        // NO-CONTAMINATION RULE (spec 128): Title/RawText carry ONLY the fixed phrase + numeric count +
        // grantee/window — NEVER raw product descriptions or FCC-ID free text. A description like "wireless
        // launch controller" would otherwise trip the extractor's 'launches' rule. Sample authorizations go in
        // metadata ONLY (the extractor never scans metadata for phrases).
        var title =
            $"FCC equipment authorization (recent grants) — {countToken} authorizations granted to '{grantee}' "
                + $"in the last {_options.LookbackDays} days";

        // The RawText timestamp makes each run's Title+RawText ContentHash distinct, so every run persists a
        // distinct timestamped snapshot evidence — this accrued, timestamped authorization-count history IS the
        // record the deferred slice-B surge detection will read (no separate history store is built). The grant
        // floor uses the same grantFloorToken as the metadata so the two can never disagree.
        var rawText =
            $"Grantee '{grantee}': {countToken} FCC equipment authorizations granted since {grantFloorToken}, as "
                + $"of {retrievedAtToken}. Signal: fcc equipment authorization (recent grants).";

        // Bounded provenance/debug sample — metadata only, NOT scanned by the extractor.
        var sampleAuthorizations = string.Join(
            " | ",
            read.Grants.Take(_options.MaxSampleAuthorizations).Select(g => $"{g.FccId}: {g.Description}"));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. FCC grants
            // are an authoritative public record — on par with the SEC/USASpending High.
            ["quality"] = "High",
            ["fccFeedUrl"] = feed.Url,
            ["grantee"] = grantee,
            // The grant count is the accrued authorization history slice B will read.
            ["grantCount"] = count.ToString(CultureInfo.InvariantCulture),
            // "true" when the reader hit its page cap with more valid grants left: grantCount is then a FLOOR,
            // not an exact total, so slice-B surge detection must treat it as a lower bound.
            ["grantCountTruncated"] = read.Truncated ? "true" : "false",
            ["lookbackDays"] = _options.LookbackDays.ToString(CultureInfo.InvariantCulture),
            ["grantFloor"] = grantFloorToken,
            ["sampleAuthorizations"] = sampleAuthorizations,
            ["retrievedAtUtc"] = retrievedAtToken,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            // Provenance: the EAS GenericSearch query URL (one builder produces both the fetched URL and this link).
            SourceUrl: _reader.QueryUrl(grantee, grantFloor),
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
