using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Reads the per-company <c>sec</c> source feeds configured on the <see cref="CollectionContext"/> (each
/// feed's <c>Url</c> is that company's EDGAR submissions JSON endpoint) and turns each recent filing of a
/// configured form into a raw <see cref="CollectedEvidence"/> of type <see cref="EvidenceSourceType.Filing"/>.
/// Does not score, resolve, or persist — it only answers "what recent filings did this issuer make?" A feed
/// that fails to read contributes no evidence and is logged as a Warning (the reader reports the failure
/// mode); a delisted/quiet issuer degrades to zero evidence, not an error. Company hints come only from the
/// configured feed→company binding — tickers are never invented (provenance is sacred). Evidence Title/RawText
/// are synthesized from real filing metadata; no filing body text is fabricated.
/// </summary>
internal sealed class SecEdgarFilingCollector : IEvidenceCollector
{
    private readonly ISecFilingReader _reader;
    private readonly ILogger<SecEdgarFilingCollector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SecCollectorOptions _options;
    private readonly HashSet<string> _forms;

    public SecEdgarFilingCollector(
        ISecFilingReader reader,
        ILogger<SecEdgarFilingCollector> logger,
        TimeProvider timeProvider,
        SecCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _reader = reader;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options;
        _forms = new HashSet<string>(options.Forms ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public string CollectorName => "sec-edgar";

    public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feeds = context.FeedsOfType("sec");

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
                    "SEC feed '{FeedName}' ({FeedUrl}) could not be read: {Detail}; skipping.",
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
                if (!_forms.Contains(filing.Form))
                {
                    continue;
                }

                if (collectedForFeed >= _options.MaxFilingsPerCompany)
                {
                    // Items are newest-first, so the first N of the desired forms are the most recent.
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
            "SEC filing collection complete: {FeedsChecked} feed(s) checked, {FeedsFailed} failed, {ItemsCollected} filing(s) collected.",
            feedsChecked,
            feedsFailed,
            results.Count);

        var summary = new CollectionSummary(
            feedsChecked, feedsChecked - feedsFailed, feedsFailed, results.Count, failures.ToArray());
        return new CollectionResult(results.ToArray(), summary);
    }

    private CollectedEvidence MapToEvidence(
        CompanySourceFeed feed, SecFilingItem filing, IReadOnlyList<string> hints)
    {
        var description = string.IsNullOrWhiteSpace(filing.PrimaryDocDescription)
            ? filing.Form
            : filing.PrimaryDocDescription;

        var hasItems = !string.IsNullOrWhiteSpace(filing.Items);

        // Resolve recognised 8-K item codes to their official SEC item titles (real semantics, not
        // fabricated). Only mapped codes contribute a title; unmapped codes (e.g. 9.01) stay bare so no
        // title is ever invented. The titles are legitimate business phrases the shared keyword extractor
        // can match, which is how a Filing produces a signal (and thus a source-diversity lift).
        var itemTitles = hasItems ? SecFormItemTitles.ResolveTitles(filing.Items) : [];
        var itemsClause = itemTitles.Count > 0
            ? $" Items: {string.Join("; ", itemTitles)}."
            : string.Empty;

        // Title: "{form} — {description} ({filingDate})", with 8-K item codes appended when present, plus
        // the resolved official item titles alongside the raw codes (both retained).
        var title = hasItems
            ? $"{filing.Form} — {description} ({filing.FilingDate}) [items: {filing.Items}]{itemsClause}"
            : $"{filing.Form} — {description} ({filing.FilingDate})";

        // RawText: synthesized from REAL metadata only (form + human description + item codes + official
        // item titles). The accession number and filing date are included so two same-form filings on
        // different dates hash differently under the mapper's Title+RawText ContentHash dedupe. Raw codes
        // are kept for provenance and the official titles are appended for matchable text; no filing body
        // text is fabricated.
        var rawText = hasItems
            ? $"{filing.Form} filing accession {filing.Accession} filed {filing.FilingDate}: {description}. "
                + $"8-K item codes: {filing.Items}.{itemsClause}"
            : $"{filing.Form} filing accession {filing.Accession} filed {filing.FilingDate}: {description}.";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Declared baseline evidence quality (AD-7), read by CollectedEvidenceMapper.ParseQuality. SEC
            // filings are the highest-integrity primary source, so they declare a High baseline (above the
            // press-release Medium) — this reinforces the source-diversity/confidence story.
            ["quality"] = "High",
            ["secFeedUrl"] = feed.Url,
            ["accessionNumber"] = filing.Accession,
            ["form"] = filing.Form,
            ["filingDate"] = filing.FilingDate,
        };

        if (!string.IsNullOrWhiteSpace(filing.PrimaryDocument))
        {
            metadata["primaryDocument"] = filing.PrimaryDocument;
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
