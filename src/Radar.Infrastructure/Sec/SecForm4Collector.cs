using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Reads the per-company <c>secform4</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is that company's EDGAR submissions JSON endpoint) and turns each recent SEC Form 4
/// (insider-transaction) filing into a raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.Filing"/> carrying a deterministically-determined insider-activity direction
/// (Positive for open-market purchases, Negative for discretionary open-market sales, Neutral for
/// grants/exercises/withholding/gifts, anything under a 10b5-1 plan, and mixed same-filing buy+sell). Does not
/// score, resolve, or persist. A feed that fails to read contributes no evidence and is logged as a Warning;
/// a delisted/quiet issuer degrades to zero evidence, not an error. Company hints come only from the
/// configured feed→company binding — tickers are never invented (provenance is sacred). Evidence Title/RawText
/// are synthesized from real filing metadata only (owner, shares, $ value, date, accession); no filing body
/// text is fabricated, and the text is advice-free (AD-9) — the direction is internal to scoring.
/// </summary>
internal sealed class SecForm4Collector : IEvidenceCollector
{
    private readonly ISecForm4Reader _reader;
    private readonly ILogger<SecForm4Collector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SecForm4CollectorOptions _options;

    public SecForm4Collector(
        ISecForm4Reader reader,
        ILogger<SecForm4Collector> logger,
        TimeProvider timeProvider,
        SecForm4CollectorOptions options)
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

    public string CollectorName => "sec-form4";

    public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("secform4");

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var feedsChecked = 0;
        var feedsFailed = 0;
        var failures = new List<SourceFailure>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _reader.ReadAsync(feed.Url, ct).ConfigureAwait(false);
            feedsChecked++;

            if (!result.IsSuccess)
            {
                feedsFailed++;
                failures.Add(new SourceFailure(
                    feed.Name, feed.Url, result.Detail ?? result.Outcome.ToString()));
                _logger.LogWarning(
                    "SEC Form 4 feed '{FeedName}' ({FeedUrl}) could not be read: {Detail}; skipping.",
                    feed.Name,
                    feed.Url,
                    result.Detail);
                continue;
            }

            var hints = CollectorCompanyHints.For(feed.CompanyId, companiesById);

            // Dedupe within this feed by accession number so a filing appears at most once.
            var seenAccessions = new HashSet<string>(StringComparer.Ordinal);
            var collectedForFeed = 0;

            foreach (var filing in result.Items)
            {
                if (collectedForFeed >= _options.MaxFilingsPerCompany)
                {
                    // Items are newest-first, so the first N are the most recent.
                    break;
                }

                if (!seenAccessions.Add(filing.Accession))
                {
                    continue;
                }

                results.Add(MapToEvidence(feed, filing, hints));
                collectedForFeed++;
            }
        }

        _logger.LogInformation(
            "SEC Form 4 collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, {ItemsCollected} filing(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed, SecForm4Filing filing, IReadOnlyList<string> hints)
    {
        var owner = string.IsNullOrWhiteSpace(filing.PrimaryOwnerName) ? "An insider" : filing.PrimaryOwnerName;
        var netValueText = filing.NetValue.ToString("N0", CultureInfo.InvariantCulture);
        var sharesText = filing.Shares.ToString("N0", CultureInfo.InvariantCulture);

        // Fixed direction phrase the KeywordSignalExtractor matches. Title/RawText are synthesized from REAL
        // metadata only (owner, shares, $ value, date, accession); the accession + filing date are included so
        // distinct filings hash distinctly under the mapper's Title+RawText ContentHash. Factual, advice-free.
        string title;
        string rawText;
        switch (filing.Direction)
        {
            case SignalDirection.Positive:
                title = $"Form 4 — insider open-market purchase: {owner} bought {sharesText} shares "
                    + $"(~${netValueText}) ({filing.FilingDate})";
                rawText = $"Form 4 accession {filing.Accession} filed {filing.FilingDate}: insider open-market "
                    + $"purchase — {owner} bought {sharesText} shares (~${netValueText}).";
                break;
            case SignalDirection.Negative:
                title = $"Form 4 — insider open-market sale: {owner} sold {sharesText} shares "
                    + $"(~${netValueText}) ({filing.FilingDate})";
                rawText = $"Form 4 accession {filing.Accession} filed {filing.FilingDate}: insider open-market "
                    + $"sale — {owner} sold {sharesText} shares (~${netValueText}).";
                break;
            default:
                title = $"Form 4 — insider stock transaction (routine): {owner} ({filing.FilingDate})";
                rawText = $"Form 4 accession {filing.Accession} filed {filing.FilingDate}: insider stock "
                    + $"transaction (routine) — {owner}.";
                break;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7): SEC primary source declares High (matching the
            // spec-56 SEC filing collector).
            ["quality"] = "High",
            ["secFeedUrl"] = feed.Url,
            ["accessionNumber"] = filing.Accession,
            ["form"] = "4",
            ["filingDate"] = filing.FilingDate,
            // Debug/traceability only — NOT read by the extractor (direction rides the fixed phrase).
            ["insiderDirection"] = filing.Direction.ToString(),
        };

        // The extractor's materiality key: written ONLY when the discretionary $ value is positive, so a
        // Neutral no-value filing omits it and the InsiderBuying signal keeps its baseline Strength.
        if (filing.NetValue > 0m)
        {
            metadata["insiderNetValue"] = filing.NetValue.ToString(CultureInfo.InvariantCulture);
        }

        // Multi-insider cluster (>= 2 distinct reporting owners transacting the same direction) — the reader
        // gates HasCluster to directional (Positive/Negative) filings, so this is only set for those. The
        // extractor adds +1 to the materiality tier Strength (capped at 10). Written only when true, mirroring
        // insiderNetValue (a single-insider filing omits the key).
        if (filing.HasCluster)
        {
            metadata["insiderCluster"] = "true";
        }

        if (!string.IsNullOrWhiteSpace(filing.IssuerTicker))
        {
            metadata["issuerTicker"] = filing.IssuerTicker;
        }

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: feed.Name,
            SourceUrl: filing.IndexUrl,
            Title: title,
            RawText: rawText,
            // The acceptance instant is the observed/published moment, so windowing/recency work correctly.
            PublishedAt: filing.AcceptanceDateTimeUtc,
            CollectedAt: _timeProvider.GetUtcNow(),
            Metadata: metadata)
        {
            CompanyHints = hints,
        };
    }
}
