using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Reads the per-company <c>sec13dg</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is that company's EDGAR submissions JSON endpoint) and turns each recent SEC Schedule
/// 13D/13G beneficial-ownership filing into a raw <see cref="CollectedEvidence"/> of type
/// <see cref="EvidenceSourceType.Filing"/> carrying a deterministically-chosen fixed ownership phrase (spec
/// 99's contract): an original <c>SC 13D</c> → the activist phrase, an original <c>SC 13G</c> → the passive
/// phrase, any <c>/A</c> amendment → the routine-amendment phrase. The extractor (spec 99) maps those phrases
/// to <c>InstitutionalOwnership</c> Positive (13D) / Neutral (13G, amendment) signals with no further
/// extractor change. Does not score, resolve, or persist. A feed that fails to read contributes no evidence
/// and is logged as a Warning; a delisted/quiet issuer degrades to zero evidence, not an error. Company hints
/// come only from the configured feed→company binding — tickers are never invented (provenance is sacred).
/// Evidence Title/RawText are synthesized from real filing metadata only (form, filing date, accession); no
/// filing body text is fabricated, and the text is advice-free (AD-9) — the direction is internal to scoring.
/// </summary>
internal sealed class Sec13DGCollector : IEvidenceCollector
{
    private readonly ISec13DGReader _reader;
    private readonly ILogger<Sec13DGCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Sec13DGCollectorOptions _options;

    public Sec13DGCollector(
        ISec13DGReader reader,
        ILogger<Sec13DGCollector> logger,
        TimeProvider timeProvider,
        Sec13DGCollectorOptions options)
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

    public string CollectorName => "sec-13dg";

    public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("sec13dg");

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
                    "SEC 13D/13G feed '{FeedName}' ({FeedUrl}) could not be read: {Detail}; skipping.",
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
            "SEC 13D/13G collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, {ItemsCollected} filing(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed, Sec13DGFiling filing, IReadOnlyList<string> hints)
    {
        // Fixed ownership phrase the KeywordSignalExtractor matches (spec 99 — VERBATIM). Title/RawText are
        // synthesized from REAL metadata only (form, filing date, accession); the accession + filing date are
        // included so distinct filings hash distinctly under the mapper's Title+RawText ContentHash. Factual,
        // advice-free — the direction is internal to scoring.
        string title;
        string rawText;
        switch (filing.Category)
        {
            case Sec13DGCategory.Activist13D:
                title = $"Schedule 13D — activist beneficial-ownership stake (13d) filed {filing.FilingDate} "
                    + $"(accession {filing.Accession})";
                rawText = $"SEC Schedule 13D ({filing.Form}) accession {filing.Accession} filed {filing.FilingDate}: "
                    + "activist beneficial-ownership stake (13d).";
                break;
            case Sec13DGCategory.Passive13G:
                title = $"Schedule 13G — passive beneficial-ownership stake (13g) filed {filing.FilingDate} "
                    + $"(accession {filing.Accession})";
                rawText = $"SEC Schedule 13G ({filing.Form}) accession {filing.Accession} filed {filing.FilingDate}: "
                    + "passive beneficial-ownership stake (13g).";
                break;
            default:
                title = $"Schedule 13D/13G — beneficial-ownership amendment (routine) filed {filing.FilingDate} "
                    + $"(accession {filing.Accession})";
                rawText = $"SEC Schedule 13D/13G ({filing.Form}) accession {filing.Accession} filed {filing.FilingDate}: "
                    + "beneficial-ownership amendment (routine).";
                break;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7): SEC primary source declares High (matching the
            // spec-56/93 SEC collectors).
            ["quality"] = "High",
            ["secFeedUrl"] = feed.Url,
            ["accessionNumber"] = filing.Accession,
            // The real EDGAR form string (e.g. "SC 13D") — NOT the classifier category.
            ["form"] = filing.Form,
            ["filingDate"] = filing.FilingDate,
            // Debug/traceability only — NOT read by the extractor (direction rides the fixed phrase).
            ["ownershipCategory"] = filing.Category.ToString(),
        };

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
