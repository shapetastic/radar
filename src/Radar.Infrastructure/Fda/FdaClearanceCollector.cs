using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fda;

/// <summary>
/// Reads the per-company <c>fda</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is an <c>applicant=&lt;organization name&gt;</c> token) and turns each applicant's recent
/// FDA device clearance/approval activity (510(k) + PMA) into exactly ONE raw <see cref="CollectedEvidence"/>
/// of type <see cref="EvidenceSourceType.RegulatoryApproval"/> carrying the fixed spec-129 clearance phrase
/// (<c>fda clearance or approval (recent)</c>) plus the deterministic clearance count + applicant/window
/// provenance metadata (NO AI). The extractor (spec 129) maps that phrase to a POSITIVE
/// <c>RegulatoryApproval</c> signal at ROUTINE strength, ONE signal per run (NOT count-proportional): an FDA
/// clearance/approval is a discrete, market-relevant gate with clear positive valence, but a fixed
/// single-signal routine strength means an always-prolific medtech incumbent cannot dominate — it
/// corroborates, it does not flip a label alone (specs 111/121). Directional SURGE detection vs the accrued
/// history is deferred to slice B. Does not score, resolve, or persist. A feed whose token is malformed or
/// whose read fails contributes no evidence and is logged as a Warning; an applicant with zero recent
/// clearances (including openFDA's empty-search 404) is a valid zero-clearance snapshot, not an error. Company
/// hints come only from the configured feed→company binding — tickers are never invented (provenance is
/// sacred). Evidence Title/RawText are synthesized from the fixed phrase + real count + applicant/window +
/// retrieved timestamp only — <b>never raw device names</b>: a device name like "cardiac partnership system"
/// would otherwise trip the extractor's <c>partnership</c> rule (keyword contamination). Sample clearances
/// live in evidence METADATA only, which the extractor never scans. Factual, advice-free (AD-9) — the
/// direction is internal to scoring.
/// </summary>
internal sealed class FdaClearanceCollector : IEvidenceCollector
{
    private readonly IFdaClearanceReader _reader;
    private readonly ILogger<FdaClearanceCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FdaCollectorOptions _options;

    public FdaClearanceCollector(
        IFdaClearanceReader reader,
        ILogger<FdaClearanceCollector> logger,
        TimeProvider timeProvider,
        FdaCollectorOptions options)
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

    public string CollectorName => "fda";

    public EvidenceSourceType SourceType => EvidenceSourceType.RegulatoryApproval;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("fda");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            feedsChecked++;

            var target = FdaFeedTarget.Parse(feed.Url);
            if (target is null)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(feed.Name, feed.Url, "malformed fda feed token"));
                _logger.LogWarning(
                    "FDA feed '{FeedName}' ({FeedUrl}) has a malformed token "
                        + "(expected 'applicant=<organization name>'); skipping.",
                    feed.Name,
                    feed.Url);
                continue;
            }

            // The decision-date floor: today minus the lookback window, from the injected TimeProvider (UTC).
            var decisionFloor = DateOnly.FromDateTime(
                (_timeProvider.GetUtcNow() - TimeSpan.FromDays(_options.LookbackDays)).UtcDateTime);

            var result = await _reader.ReadAsync(target.ApplicantName, decisionFloor, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "FDA feed '{FeedName}' (applicant '{Applicant}') could not be read: {Detail}; skipping.",
                    feed.Name,
                    target.ApplicantName,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Exactly ONE snapshot evidence per feed per run — the clearance count is already the aggregate.
            results.Add(MapToEvidence(feed, target, decisionFloor, result.Result!, hints));
        }

        _logger.LogInformation(
            "FDA clearance collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, "
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
        FdaFeedTarget target,
        DateOnly decisionFloor,
        FdaClearanceResult clearances,
        IReadOnlyList<string> hints)
    {
        // One instant for the whole window snapshot: a bounded clearance window has no single publish date, so
        // PublishedAt = CollectedAt = now (UTC, injected TimeProvider).
        var retrievedAtUtc = _timeProvider.GetUtcNow();
        var retrievedAtToken = retrievedAtUtc.ToString("o", CultureInfo.InvariantCulture);
        var decisionFloorToken = decisionFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var count = clearances.ClearanceCount;
        var applicant = target.ApplicantName;

        // NO-CONTAMINATION RULE (spec 129): Title/RawText carry ONLY the fixed phrase + numeric count +
        // applicant/window — NEVER raw device names. A device name like "cardiac partnership system" would
        // otherwise trip the extractor's 'partnership' rule. Sample clearances go in metadata ONLY (the
        // extractor never scans metadata for phrases).
        var title =
            $"FDA clearance or approval (recent) — {count} device clearances/approvals for '{applicant}' "
                + $"in the last {_options.LookbackDays} days";

        // The RawText timestamp makes each run's Title+RawText ContentHash distinct, so every run persists a
        // distinct timestamped snapshot evidence — this accrued, timestamped clearance-count history IS the
        // record the deferred slice-B surge detection will read (no separate history store is built).
        var rawText =
            $"Applicant '{applicant}': {count} FDA device clearances/approvals since {decisionFloorToken}, as of "
                + $"{retrievedAtToken}. Signal: fda clearance or approval (recent).";

        // Bounded provenance/debug sample — metadata only, NOT scanned by the extractor.
        var sampleClearances = string.Join(
            " | ",
            clearances.Clearances
                .Take(_options.MaxSampleClearances)
                .Select(c => $"{c.SubmissionNumber} [{c.Track}]: {c.DeviceName}"));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. FDA
            // decisions are an authoritative public record — on par with the SEC/USASpending High.
            ["quality"] = "High",
            ["fdaFeedUrl"] = feed.Url,
            ["applicant"] = applicant,
            ["clearanceCount"] = count.ToString(CultureInfo.InvariantCulture),
            ["lookbackDays"] = _options.LookbackDays.ToString(CultureInfo.InvariantCulture),
            ["decisionFloor"] = decisionFloorToken,
            ["sampleClearances"] = sampleClearances,
            // Each endpoint's own reported total — a cross-check when it exceeds the bounded page count.
            ["reportedTotal510k"] = clearances.ReportedTotal510k.ToString(CultureInfo.InvariantCulture),
            ["reportedTotalPma"] = clearances.ReportedTotalPma.ToString(CultureInfo.InvariantCulture),
            ["retrievedAtUtc"] = retrievedAtToken,
        };

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            // Provenance: the openFDA query URL (one builder produces both the fetched URL and this link).
            SourceUrl: _reader.QueryUrl(applicant, decisionFloor),
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
