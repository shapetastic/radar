using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Domain.Evidence;

namespace Radar.Application.News;

/// <summary>Which passes the one-shot migration runs (resolved by the composition root; never inferred).</summary>
public sealed class NewsObservationMigrationOptions
{
    /// <summary>
    /// Whether, AFTER the legacy headline migration, saved landing URLs are re-visited through the safe
    /// content reader to produce <see cref="NewsObservationCaptureMode.RetrospectiveUrlFetch"/> records.
    /// OFF by default; requires the reader to be registered (the composition root fails fast otherwise).
    /// </summary>
    public bool RetrospectiveFetch { get; init; }
}

/// <summary>What one migration run did — logged by the Worker, never scored.</summary>
public sealed record NewsObservationMigrationResult(
    int EvidenceScanned,
    int LegacyWritten,
    int LegacyDeduped,
    int LegacyFailed,
    int LegacySkipped,
    int RetrospectiveAttempted,
    int RetrospectiveWritten,
    int RetrospectiveDeduped,
    int RetrospectiveFailed);

/// <summary>The explicitly-invoked, one-shot migration seam (spec 177 §7). Replaces the pipeline run when enabled.</summary>
public interface INewsObservationMigration
{
    Task<NewsObservationMigrationResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// The honest migration of accrued news history into the observation archive (spec 177 §7).
/// <para>
/// <b>Legacy pass:</b> every accrued raw <see cref="EvidenceSourceType.NewsArticle"/> evidence item becomes
/// one <see cref="NewsObservationCaptureMode.LegacyHeadlineOnly"/> observation carrying exactly what was
/// genuinely persisted at the time — headline, publisher, Google landing URL, <c>PublishedAtUtc</c> — with
/// <c>FirstObservedAtUtc</c> = the original <c>CollectedAtUtc</c> (that headline/URL really WAS known then)
/// and description/body <c>null</c> forever (the RSS description was discarded before spec 177 and cannot be
/// honestly reconstructed). Source evidence is never rewritten; the migration only READS the repository.
/// Idempotent by identity: a second run re-derives the same payload hashes/ids and the archive's hydrated
/// index dedupes every one of them, so it writes nothing new.
/// </para>
/// <para>
/// <b>Retrospective pass (explicit opt-in):</b> re-visits each distinct archived landing URL through the
/// safe §6 content reader. Every result — fetched, vanished, paywalled, disallowed — is a durable
/// <see cref="NewsObservationCaptureMode.RetrospectiveUrlFetch"/> record whose <c>RetrievedAtUtc</c> and
/// <c>FirstObservedAtUtc</c> are the ACTUAL fetch instant, never inherited publication/collection time:
/// retrospectively fetched content cannot establish what was knowable historically and must never be
/// backdated into looking like it could.
/// </para>
/// </summary>
public sealed class NewsObservationMigration : INewsObservationMigration
{
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly ICompanyRepository _companyRepository;
    private readonly INewsObservationArchive _archive;
    private readonly NewsObservationMigrationOptions _options;
    private readonly ILogger<NewsObservationMigration> _logger;
    private readonly INewsArticleContentReader? _contentReader;

    public NewsObservationMigration(
        IEvidenceRepository evidenceRepository,
        ICompanyRepository companyRepository,
        INewsObservationArchive archive,
        NewsObservationMigrationOptions options,
        ILogger<NewsObservationMigration> logger,
        INewsArticleContentReader? contentReader = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.RetrospectiveFetch && contentReader is null)
        {
            // The composition root fails fast earlier with a config-keyed message; this guard keeps the
            // invariant local so a test composition cannot silently run a fetch-less "retrospective" pass.
            throw new ArgumentException(
                "RetrospectiveFetch requires an INewsArticleContentReader; enable the safe article-fetch "
                    + "seam (Radar:NewsResearch:ArticleFetch) or disable the retrospective pass.",
                nameof(contentReader));
        }

        _evidenceRepository = evidenceRepository;
        _companyRepository = companyRepository;
        _archive = archive;
        _options = options;
        _logger = logger;
        _contentReader = contentReader;
    }

    public async Task<NewsObservationMigrationResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var companiesByTicker = companies
            .Where(c => !string.IsNullOrWhiteSpace(c.Ticker))
            .GroupBy(c => c.Ticker!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var evidence = await _evidenceRepository.GetAllAsync(ct).ConfigureAwait(false);

        var scanned = 0;
        var written = 0;
        var deduped = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var item in evidence)
        {
            ct.ThrowIfCancellationRequested();

            if (item.SourceType != EvidenceSourceType.NewsArticle)
            {
                continue;
            }

            scanned++;

            var record = TryBuildLegacyRecord(item, companiesByTicker);
            if (record is null)
            {
                // A news item with no landing URL cannot be identified or ever re-fetched; skipping is the
                // honest outcome (logged per item at Debug, counted here).
                skipped++;
                continue;
            }

            switch (await _archive.WriteAsync(record, ct).ConfigureAwait(false))
            {
                case NewsObservationWriteOutcome.Written:
                    written++;
                    break;
                case NewsObservationWriteOutcome.CrossRunDeduped:
                    deduped++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        _logger.LogInformation(
            "News observation legacy migration: {Scanned} news evidence item(s) scanned, {Written} written, "
                + "{Deduped} already archived, {Failed} failed, {Skipped} skipped (no landing URL).",
            scanned,
            written,
            deduped,
            failed,
            skipped);

        var retrospective = _options.RetrospectiveFetch
            ? await RunRetrospectiveFetchAsync(ct).ConfigureAwait(false)
            : (Attempted: 0, Written: 0, Deduped: 0, Failed: 0);

        return new NewsObservationMigrationResult(
            EvidenceScanned: scanned,
            LegacyWritten: written,
            LegacyDeduped: deduped,
            LegacyFailed: failed,
            LegacySkipped: skipped,
            RetrospectiveAttempted: retrospective.Attempted,
            RetrospectiveWritten: retrospective.Written,
            RetrospectiveDeduped: retrospective.Deduped,
            RetrospectiveFailed: retrospective.Failed);
    }

    /// <summary>
    /// Projects one accrued news evidence item into its legacy observation, or <c>null</c> when it carries
    /// no landing URL. Publisher/feed/company attribution is RECOVERED from what the collector genuinely
    /// persisted (the metadata bag and the collector hints), never invented: an unmatched company stays
    /// <c>null</c>, an unrecorded collector stays <c>null</c> (legacy evidence predates spec 146's
    /// <c>collector</c> stamp), and the query phrase — which was never persisted — stays <c>null</c>.
    /// </summary>
    private NewsObservationRecord? TryBuildLegacyRecord(
        EvidenceItem item,
        IReadOnlyDictionary<string, Domain.Companies.Company> companiesByTicker)
    {
        EvidenceMetadata.TryRead(item.MetadataJson, out var metadata, out var hints);

        var landingUrl = FirstNonBlank(item.SourceUrl, metadata.GetValueOrDefault("url"));
        if (landingUrl is null)
        {
            _logger.LogDebug(
                "News evidence {EvidenceId} carries no landing URL; skipping migration for it.", item.Id);
            return null;
        }

        // metadata.publisher is the parsed outlet the collector recorded (it may be blank for an
        // unattributable article — preserved as-is); SourceName is the display fallback.
        var publisher = metadata.TryGetValue("publisher", out var recordedPublisher)
            ? recordedPublisher
            : item.SourceName;

        // Company attribution: the collector's hints are the persisted feed→company binding echo. A hint
        // that names a seed ticker resolves the company; otherwise the ticker is kept as a hint-only fact.
        Guid? companyId = null;
        string? ticker = null;
        foreach (var hint in hints)
        {
            if (companiesByTicker.TryGetValue(hint.Trim(), out var company))
            {
                companyId = company.Id;
                ticker = company.Ticker;
                break;
            }
        }

        var payloadHash = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.LegacyHeadlineOnly,
            landingUrl,
            item.Title,
            publisher,
            descriptionRaw: null);

        return new NewsObservationRecord(
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            ObservationId: NewsObservationIdentity.ObservationIdFor(landingUrl, payloadHash),
            CompanyId: companyId,
            Ticker: ticker,
            Collector: CollectionProvenanceMetadata.Read(item),
            QueryPhrase: null,
            FeedId: null,
            FeedName: metadata.GetValueOrDefault("feedName"),
            GoogleLandingUrl: landingUrl,
            Publisher: publisher,
            PublisherSiteUrl: null,
            Headline: item.Title,
            DescriptionRaw: null,
            DescriptionText: null,
            DescriptionTruncated: false,
            PublishedAtUtc: item.PublishedAtUtc,
            RetrievedAtUtc: item.CollectedAtUtc,
            FirstObservedAtUtc: item.CollectedAtUtc,
            PayloadHash: payloadHash,
            CaptureMode: NewsObservationCaptureMode.LegacyHeadlineOnly,
            ArticleFetch: null);
    }

    /// <summary>
    /// Re-visits each distinct archived landing URL (deterministic order) through the safe content reader.
    /// The knowledge cutoff of every produced record is the fetch's OWN retrieval instant — asserted here by
    /// construction: both timestamps come from the reader's result, and nothing from the source observation's
    /// timeline is copied onto them.
    /// </summary>
    private async Task<(int Attempted, int Written, int Deduped, int Failed)> RunRetrospectiveFetchAsync(
        CancellationToken ct)
    {
        var archived = await _archive.GetAllAsync(ct).ConfigureAwait(false);

        // One fetch per distinct URL; the ordinal-first (earliest) non-retrospective observation supplies
        // the descriptive fields. GetAllAsync is already deterministically ordered (AD-3).
        var candidates = archived
            .Where(o => o.CaptureMode != NewsObservationCaptureMode.RetrospectiveUrlFetch)
            .GroupBy(o => o.GoogleLandingUrl, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var attempted = 0;
        var written = 0;
        var deduped = 0;
        var failed = 0;

        foreach (var source in candidates)
        {
            ct.ThrowIfCancellationRequested();
            attempted++;

            var fetch = await _contentReader!.FetchAsync(source.GoogleLandingUrl, ct).ConfigureAwait(false);

            var payloadHash = NewsObservationIdentity.ComputePayloadHash(
                NewsObservationCaptureMode.RetrospectiveUrlFetch,
                source.GoogleLandingUrl,
                source.Headline,
                source.Publisher,
                descriptionRaw: null,
                fetchedContentHash: fetch.ContentHash);

            var record = new NewsObservationRecord(
                SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
                ObservationId: NewsObservationIdentity.ObservationIdFor(source.GoogleLandingUrl, payloadHash),
                CompanyId: source.CompanyId,
                Ticker: source.Ticker,
                Collector: source.Collector,
                QueryPhrase: source.QueryPhrase,
                FeedId: source.FeedId,
                FeedName: source.FeedName,
                GoogleLandingUrl: source.GoogleLandingUrl,
                Publisher: source.Publisher,
                PublisherSiteUrl: source.PublisherSiteUrl,
                Headline: source.Headline,
                DescriptionRaw: null,
                DescriptionText: null,
                DescriptionTruncated: false,
                PublishedAtUtc: source.PublishedAtUtc,
                // ACTUAL retrieval time, both fields — never the source observation's timeline (§7).
                RetrievedAtUtc: fetch.RetrievedAtUtc,
                FirstObservedAtUtc: fetch.RetrievedAtUtc,
                PayloadHash: payloadHash,
                CaptureMode: NewsObservationCaptureMode.RetrospectiveUrlFetch,
                ArticleFetch: fetch);

            switch (await _archive.WriteAsync(record, ct).ConfigureAwait(false))
            {
                case NewsObservationWriteOutcome.Written:
                    written++;
                    break;
                case NewsObservationWriteOutcome.CrossRunDeduped:
                    deduped++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        _logger.LogInformation(
            "News observation retrospective fetch: {Attempted} URL(s) attempted, {Written} written, "
                + "{Deduped} unchanged since last fetch, {Failed} failed.",
            attempted,
            written,
            deduped,
            failed);

        return (attempted, written, deduped, failed);
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
